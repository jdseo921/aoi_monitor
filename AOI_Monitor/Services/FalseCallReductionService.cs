using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class FalseCallReductionService
{
    public static FalseCallReductionRun Analyze(
        IReadOnlyCollection<BatchTestRow> rows,
        string engineName,
        string modelVersion,
        string modelId,
        string modelSha256,
        long? batchRunId,
        FalseCallReductionCriteria? criteria = null)
    {
        criteria = NormalizeCriteria(criteria ?? new FalseCallReductionCriteria());
        var rowArray = rows.ToArray();
        var knownOk = rowArray.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "OK");
        var knownNg = rowArray.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG");
        var points = BuildSweep(rowArray, criteria).ToArray();
        var recommendation = Recommend(points, criteria, knownOk, knownNg);

        return new FalseCallReductionRun
        {
            BatchRunId = batchRunId,
            CreatedAtUtc = DateTime.UtcNow,
            EngineName = string.IsNullOrWhiteSpace(engineName) ? "UNKNOWN" : engineName,
            ModelVersion = string.IsNullOrWhiteSpace(modelVersion) ? "UNKNOWN" : modelVersion,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? "Not selected" : modelId,
            ModelSha256 = string.IsNullOrWhiteSpace(modelSha256) ? "Not available" : modelSha256,
            Criteria = criteria,
            Recommendation = recommendation,
            Points = points,
        };
    }

    public static FalseCallReductionRun AnalyzeAndPersist(
        IReadOnlyCollection<BatchTestRow> rows,
        string engineName,
        string modelVersion,
        string modelId,
        string modelSha256,
        long? batchRunId,
        FalseCallReductionCriteria? criteria = null,
        string? operatorId = null)
    {
        var run = Analyze(rows, engineName, modelVersion, modelId, modelSha256, batchRunId, criteria);
        run.Id = AoiDatabase.RecordFalseCallReductionRun(run, operatorId);
        return run;
    }

    /// <summary>
    /// Sweep criteria for interactive/CLI threshold calibration. The 0.01 floor and 0.005
    /// step let the sweep see the sub-5% boundaries golden-compare imagery produces (a grid
    /// that cannot see the OK/NG boundary can never emit a VALID recommendation), and the
    /// review band equals the step so a fine grid does not silently sweep the whole
    /// population into predicted REVIEW. Class defaults stay unchanged for other consumers.
    /// </summary>
    public static FalseCallReductionCriteria CreateSweepCriteria(FalseCallReductionMode mode)
        => new()
        {
            MinimumThreshold = 0.01,
            MaximumThreshold = 0.95,
            ThresholdStep = 0.005,
            ReviewBand = 0.005,
            MaximumFalseCallRate = 0.10,
            MaximumPossibleEscapeRate = mode == FalseCallReductionMode.MaximizeDefectRecall ? 0.0 : 0.05,
            MinimumKnownOk = 1,
            MinimumKnownNg = 1,
            ManualReviewMinutesPerImage = 2.0,
            Mode = mode,
        };

    public static void ApplyRecommendedThreshold(FalseCallReductionRun run, string operatorId)
        => ApplyRecommendedThreshold(run, UserRole.Admin, operatorId);

    public static void ApplyRecommendedThreshold(FalseCallReductionRun run, UserRole role, string operatorId)
    {
        if (!RoleAuthorization.CanChangeThresholds(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "applying false-call threshold recommendations"));

        if (!string.Equals(run.Recommendation.Status, "VALID", StringComparison.OrdinalIgnoreCase) || run.Recommendation.Point is null)
            throw new InvalidOperationException("Only valid false-call recommendations can be applied.");

        var configuration = InspectionModelConfigurationService.Load();
        configuration.ConfidenceThreshold = run.Recommendation.Point.ConfidenceThreshold;
        InspectionModelConfigurationService.Save(configuration);

        AoiDatabase.RecordAuditEvent(
            "FALSE_CALL_THRESHOLD_APPLIED",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Applied false-call recommendation run {run.Id}; threshold={configuration.ConfidenceThreshold:F3}; mode={run.Recommendation.Mode}. Stage 1 labeled-data recommendation only, not production accuracy proof."),
            operatorWithRole: operatorId,
            relatedEntityType: "FalseCallReductionRun",
            relatedEntityId: run.Id.ToString(CultureInfo.InvariantCulture),
            relatedPath: InspectionModelConfigurationService.ConfigurationPath);
    }

    public static FalseCallRecommendationSummary ToSummary(FalseCallReductionRun? run)
    {
        if (run?.Recommendation.Point is not { } point)
        {
            return new FalseCallRecommendationSummary
            {
                Status = run?.Recommendation.Status ?? "Not available",
                Mode = run?.Recommendation.Mode ?? string.Empty,
                Limitations = BuildLimitations(run?.Recommendation.Messages ?? new List<string>()),
            };
        }

        return new FalseCallRecommendationSummary
        {
            RunId = run.Id > 0 ? run.Id : null,
            Status = run.Recommendation.Status,
            Mode = run.Recommendation.Mode,
            SelectedThreshold = point.ConfidenceThreshold,
            FalseCallRate = point.FalseCallRate,
            PossibleEscapeRate = point.PossibleEscapeRate,
            PossibleEscapeCount = point.FalseNegative,
            ReviewRate = point.ReviewRate,
            EstimatedManualReviewMinutes = point.EstimatedManualReviewMinutes,
            Limitations = BuildLimitations(run.Recommendation.Messages),
        };
    }

    private static IEnumerable<ThresholdSweepPoint> BuildSweep(IReadOnlyList<BatchTestRow> rows, FalseCallReductionCriteria criteria)
    {
        for (var threshold = criteria.MinimumThreshold; threshold <= criteria.MaximumThreshold + 0.0000001; threshold += criteria.ThresholdStep)
        {
            yield return Evaluate(rows, Math.Round(threshold, 4), criteria);
        }
    }

    private static ThresholdSweepPoint Evaluate(IReadOnlyList<BatchTestRow> rows, double threshold, FalseCallReductionCriteria criteria)
    {
        var point = new ThresholdSweepPoint
        {
            ConfidenceThreshold = threshold,
            DifferenceThreshold = threshold,
        };

        var reviewLowerBound = Math.Max(0, threshold - criteria.ReviewBand);
        foreach (var row in rows)
        {
            var truth = BatchValidationService.NormalizeBinaryLabel(row.GroundTruth);
            if (truth == "UNKNOWN")
            {
                point.UnknownGroundTruthCount++;
                continue;
            }

            point.KnownGroundTruthCount++;
            var normalizedScore = NormalizeScore(row.Score);
            var predictedNg = normalizedScore >= threshold;
            var predictedReview = normalizedScore >= reviewLowerBound && normalizedScore < threshold;

            if (predictedReview)
                point.ReviewCount++;
            if (predictedNg)
                point.NgCount++;

            if (truth == "NG" && predictedNg)
                point.TruePositive++;
            else if (truth == "OK" && !predictedNg)
                point.TrueNegative++;
            else if (truth == "OK" && predictedNg)
                point.FalsePositive++;
            else if (truth == "NG" && !predictedNg)
                point.FalseNegative++;
        }

        point.Precision = Divide(point.TruePositive, point.TruePositive + point.FalsePositive);
        point.Recall = Divide(point.TruePositive, point.TruePositive + point.FalseNegative);
        point.FalseCallRate = Divide(point.FalsePositive, point.FalsePositive + point.TrueNegative);
        point.PossibleEscapeRate = Divide(point.FalseNegative, point.FalseNegative + point.TruePositive);
        point.ReviewRate = Divide(point.ReviewCount, point.KnownGroundTruthCount);
        point.NgRate = Divide(point.NgCount, point.KnownGroundTruthCount);
        point.EstimatedManualReviewMinutes = point.ReviewCount * criteria.ManualReviewMinutesPerImage;
        point.MeetsConstraints = point.FalseCallRate <= criteria.MaximumFalseCallRate &&
            point.PossibleEscapeRate <= criteria.MaximumPossibleEscapeRate;
        point.Status = point.MeetsConstraints ? "VALID" : "CONDITIONAL";
        return point;
    }

    private static OperatingPointRecommendation Recommend(
        IReadOnlyList<ThresholdSweepPoint> points,
        FalseCallReductionCriteria criteria,
        int knownOk,
        int knownNg)
    {
        var messages = new List<string>();
        if (knownOk < criteria.MinimumKnownOk)
            messages.Add($"Insufficient known OK ground truth: {knownOk}; required {criteria.MinimumKnownOk}.");
        if (knownNg < criteria.MinimumKnownNg)
            messages.Add($"Insufficient known NG ground truth: {knownNg}; required {criteria.MinimumKnownNg}.");
        if (points.Count == 0)
            messages.Add("No threshold sweep points were generated.");

        if (messages.Count > 0)
        {
            messages.Add("Recommendation is invalid because labeled evidence is insufficient. This does not prove production accuracy.");
            return new OperatingPointRecommendation
            {
                Status = "INVALID",
                Mode = criteria.Mode.ToString(),
                Messages = messages,
            };
        }

        var constrained = points.Where(point => point.MeetsConstraints).ToArray();
        if (criteria.Mode == FalseCallReductionMode.MaximizeDefectRecall)
            constrained = constrained.Where(point => point.PossibleEscapeRate <= criteria.MaximumPossibleEscapeRate).ToArray();

        var candidates = constrained.Length > 0 ? constrained : points.ToArray();
        var selected = criteria.Mode switch
        {
            FalseCallReductionMode.MinimizeFalsePositives => candidates
                .OrderBy(point => point.FalsePositive)
                .ThenBy(point => point.FalseCallRate)
                .ThenBy(point => point.EstimatedManualReviewMinutes)
                .ThenByDescending(point => point.Recall)
                .First(),
            FalseCallReductionMode.MaximizeDefectRecall => candidates
                .OrderBy(point => point.FalseNegative)
                .ThenBy(point => point.PossibleEscapeRate)
                .ThenBy(point => point.FalsePositive)
                .ThenBy(point => point.EstimatedManualReviewMinutes)
                .First(),
            _ => candidates
                .OrderBy(point => point.FalsePositive + (point.FalseNegative * 2))
                .ThenBy(point => point.EstimatedManualReviewMinutes)
                .ThenByDescending(point => point.Precision + point.Recall)
                .First(),
        };

        messages.Add(constrained.Length > 0
            ? "Recommendation selected from points meeting configured false-call and possible-escape constraints."
            : "No point met all configured constraints; recommendation is conditional and requires engineering review.");
        messages.Add("Stage 1 labeled-data threshold recommendation only; it is not production accuracy proof.");

        return new OperatingPointRecommendation
        {
            Status = selected.MeetsConstraints ? "VALID" : "CONDITIONAL",
            Mode = criteria.Mode.ToString(),
            Point = selected,
            Messages = messages,
        };
    }

    private static FalseCallReductionCriteria NormalizeCriteria(FalseCallReductionCriteria criteria)
    {
        criteria.MinimumThreshold = Math.Clamp(criteria.MinimumThreshold, 0, 1);
        criteria.MaximumThreshold = Math.Clamp(criteria.MaximumThreshold, criteria.MinimumThreshold, 1);
        criteria.ThresholdStep = Math.Clamp(criteria.ThresholdStep, 0.001, 1);
        criteria.ReviewBand = Math.Clamp(criteria.ReviewBand, 0, 1);
        criteria.MinimumKnownOk = Math.Max(0, criteria.MinimumKnownOk);
        criteria.MinimumKnownNg = Math.Max(0, criteria.MinimumKnownNg);
        criteria.MaximumPossibleEscapeRate = Math.Clamp(criteria.MaximumPossibleEscapeRate, 0, 1);
        criteria.MaximumFalseCallRate = Math.Clamp(criteria.MaximumFalseCallRate, 0, 1);
        criteria.ManualReviewMinutesPerImage = Math.Max(0, criteria.ManualReviewMinutesPerImage);
        return criteria;
    }

    /// <summary>
    /// Rows arrive with DifferenceScore on the 0-100 percent scale from every engine
    /// (pixel-diff worst-region percent, ONNX confidence x 100), so the sweep always
    /// divides by 100. The previous "already normalized" guess for values &lt;= 1.0
    /// mis-scaled genuine sub-1% difference scores by 100x.
    /// </summary>
    private static double NormalizeScore(double score)
        => Math.Clamp(score / 100.0, 0.0, 1.0);

    private static double Divide(int numerator, int denominator)
        => denominator <= 0 ? 0 : numerator / (double)denominator;

    private static List<string> BuildLimitations(IReadOnlyList<string> messages)
    {
        var limitations = messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        limitations.Add("False-call reduction uses customer-labeled Stage 1 batch rows only and does not prove production accuracy.");
        limitations.Add("Manual review burden is an estimate based on configured minutes per review item.");
        return limitations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
