using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CustomerValidationPackageRequest
{
    public string? OutputRoot { get; init; }
    public long? RunId { get; init; }
    public DateTime? RunCreatedAtUtc { get; init; }
    public string StationId { get; init; } = "AOI-LIB-01";
    public string OperatorId { get; init; } = "UNKNOWN";
    public string OperatorRole { get; init; } = "UNKNOWN";
    public string BoardModel { get; init; } = "Not provided";
    public string LotId { get; init; } = "Not provided";
    public string ModelId { get; init; } = "Not selected";
    public string ModelSha256 { get; init; } = "Not available";
    public string ModelValidationStatus { get; init; } = "Not Tested";
    public string EngineName { get; init; } = "Pixel Difference Prototype Engine";
    public string ModelVersion { get; init; } = PixelDifferenceInspectionEngine.EngineVersion;
    public string ModelFileName { get; init; } = "Not configured";
    public double ConfidenceThreshold { get; init; }
    public string DatasetFolder { get; init; } = string.Empty;
    public string GroundTruthCsvPath { get; init; } = string.Empty;
    public bool IsFormalManifest { get; init; }
    public BatchMetrics Metrics { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public BatchPerformanceSummary PerformanceSummary { get; init; } = new(0, 0, 0, 0, 0);
    public IReadOnlyCollection<BatchTestRow> Rows { get; init; } = Array.Empty<BatchTestRow>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ValidationAcceptanceCriteria Criteria { get; init; } = new();
    public ValidationDatasetQualityCriteria DatasetQualityCriteria { get; init; } = new();
    public DatasetQualitySummary? DatasetQualitySummary { get; init; }
    public CustomerDatasetPreflightResult? DatasetPreflightResult { get; init; }
    public FalseCallReductionRun? FalseCallReductionRun { get; init; }
    public LearnedVisualModelEvidenceSummary? LearnedVisualModel { get; init; }
    public InspectionLatencySummary? LatencySummary { get; init; }
}

public sealed class CustomerValidationPackageResult
{
    public CustomerValidationPackageResult(
        string packageId,
        string packageFolder,
        string manifestPath,
        string reportPath,
        string csvPath,
        string annotatedImagesFolder,
        ValidationPackageManifest manifest,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings)
    {
        PackageId = packageId;
        PackageFolder = packageFolder;
        ManifestPath = manifestPath;
        ReportPath = reportPath;
        CsvPath = csvPath;
        AnnotatedImagesFolder = annotatedImagesFolder;
        Manifest = manifest;
        Acceptance = acceptance;
        Warnings = warnings;
    }

    public string PackageId { get; }
    public string PackageFolder { get; }
    public string ManifestPath { get; }
    public string ReportPath { get; }
    public string CsvPath { get; }
    public string AnnotatedImagesFolder { get; }
    public ValidationPackageManifest Manifest { get; }
    public ValidationAcceptanceSummary Acceptance { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public static class CustomerValidationPackageService
{
    private const string ManifestSchemaVersion = "stage1-validation-package/v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ValidationAcceptanceSummary EvaluateAcceptance(
        BatchMetrics metrics,
        BatchPerformanceSummary performance,
        int totalRows,
        bool isFormalManifest,
        ValidationAcceptanceCriteria? criteria = null,
        DatasetQualitySummary? datasetQuality = null)
    {
        criteria ??= new ValidationAcceptanceCriteria();
        var messages = new List<string>();
        var failures = new List<string>();
        var knownRows = Math.Max(0, totalRows - metrics.Unknown);
        var accuracyComputable = knownRows > 0;
        var precisionComputable = metrics.TruePositive + metrics.FalsePositive > 0;
        var recallComputable = metrics.TruePositive + metrics.FalseNegative > 0;
        var falseCallRateComputable = metrics.FalsePositive + metrics.TrueNegative > 0;
        var metricsComputed = accuracyComputable && precisionComputable && recallComputable && falseCallRateComputable;

        if (!accuracyComputable)
            messages.Add("Ground-truth labels are missing or unknown, so accuracy cannot be computed.");
        if (!precisionComputable)
            messages.Add("Precision cannot be computed because the run has no predicted NG samples.");
        if (!recallComputable)
            messages.Add("Recall cannot be computed because the run has no known NG ground-truth samples.");
        if (!falseCallRateComputable)
            messages.Add("False call rate cannot be computed because the run has no known OK ground-truth samples.");

        if (accuracyComputable && metrics.Accuracy < criteria.MinimumAccuracy)
            failures.Add($"Accuracy {FormatPercent(metrics.Accuracy)} is below the {FormatPercent(criteria.MinimumAccuracy)} gate.");
        if (precisionComputable && metrics.Precision < criteria.MinimumPrecision)
            failures.Add($"Precision {FormatPercent(metrics.Precision)} is below the {FormatPercent(criteria.MinimumPrecision)} gate.");
        if (recallComputable && metrics.Recall < criteria.MinimumRecall)
            failures.Add($"Recall {FormatPercent(metrics.Recall)} is below the {FormatPercent(criteria.MinimumRecall)} gate.");
        if (falseCallRateComputable && metrics.FalseCallRate > criteria.MaximumFalseCallRate)
            failures.Add($"False call rate {FormatPercent(metrics.FalseCallRate)} is above the {FormatPercent(criteria.MaximumFalseCallRate)} gate.");
        if (performance.CountOverOneSecond > criteria.MaximumImagesOverOneSecond)
            failures.Add($"{performance.CountOverOneSecond} image(s) exceeded the one-second target; maximum allowed is {criteria.MaximumImagesOverOneSecond}.");
        if (!isFormalManifest)
            messages.Add("Formal validation manifest was not present; acceptance is conditional until manifest evidence is supplied.");
        if (criteria.RequireFormalManifest && !isFormalManifest)
            failures.Add("Formal validation manifest is required by the active criteria.");
        if (datasetQuality is not null)
        {
            if (string.Equals(datasetQuality.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("Dataset quality gate failed; validation package cannot be accepted as PASS.");
                messages.AddRange(datasetQuality.BlockingFailures);
            }
            else if (string.Equals(datasetQuality.Status, "CONDITIONAL", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("Dataset quality gate is conditional; validation package cannot be accepted as PASS.");
                messages.AddRange(datasetQuality.Warnings);
            }
        }

        var status = failures.Count > 0
            ? "FAIL"
            : messages.Count > 0
                ? "CONDITIONAL"
                : "PASS";

        var allMessages = failures.Concat(messages).ToList();
        if (allMessages.Count == 0)
            allMessages.Add("All configured numeric gates passed and formal manifest evidence was present.");

        return new ValidationAcceptanceSummary
        {
            Status = status,
            MetricsComputed = metricsComputed,
            FormalManifestPresent = isFormalManifest,
            NumericGatesPassed = failures.Count == 0,
            DatasetQualityStatus = datasetQuality?.Status ?? "CONDITIONAL",
            Messages = allMessages,
        };
    }

    public static async Task<CustomerValidationPackageResult> CreatePackageAsync(
        CustomerValidationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => CreatePackage(request, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static CustomerValidationPackageResult CreatePackage(
        CustomerValidationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        var rows = request.Rows.ToArray();
        var criteria = request.Criteria ?? new ValidationAcceptanceCriteria();
        var generatedAtUtc = DateTime.UtcNow;
        var packageId = BuildPackageId(generatedAtUtc);
        var outputRoot = string.IsNullOrWhiteSpace(request.OutputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "packages")
            : request.OutputRoot;
        var packageFolder = Path.Combine(outputRoot, $"stage1_validation_{generatedAtUtc:yyyyMMdd_HHmmss}");
        packageFolder = EnsureUniqueFolder(packageFolder);
        Directory.CreateDirectory(packageFolder);

        var csvPath = Path.Combine(packageFolder, "validation_results.csv");
        var breakdownCsvPath = Path.Combine(packageFolder, "validation_breakdown.csv");
        var reportPath = Path.Combine(packageFolder, "customer_validation_report.html");
        var reportPdfPath = Path.Combine(packageFolder, "customer_validation_report.pdf");
        var summaryHtmlPath = Path.Combine(packageFolder, "validation_summary.html");
        var summaryPdfPath = Path.Combine(packageFolder, "validation_summary.pdf");
        var benchmarkCsvPath = Path.Combine(packageFolder, "benchmark_results.csv");
        var sourceManifestCopyPath = Path.Combine(packageFolder, "customer_validation_manifest.csv");
        var limitationsPath = Path.Combine(packageFolder, "limitations.txt");
        var instructionsPath = Path.Combine(packageFolder, "print_to_pdf_instructions.txt");
        var readmePath = Path.Combine(packageFolder, "README.txt");
        var manifestPath = Path.Combine(packageFolder, "validation_manifest.json");
        var preflightPath = Path.Combine(packageFolder, "dataset_preflight_summary.json");
        var issueSummaryPath = Path.Combine(packageFolder, "pilot_issue_summary.json");
        var annotatedFolder = Path.Combine(packageFolder, "annotated_images");
        var learnedVisualModelFolder = Path.Combine(packageFolder, "learned_visual_model");
        var learnedVisualModelSummaryPath = Path.Combine(learnedVisualModelFolder, "learned_visual_model_summary.json");

        File.WriteAllText(csvPath, BatchValidationService.BuildResultsCsv(rows), Encoding.UTF8);
        var breakdownSummary = ClassMetricsService.Calculate(rows);
        var datasetQuality = request.DatasetQualitySummary ?? DatasetQualityService.Analyze(rows, null, request.DatasetQualityCriteria);
        var cameraAcceptance = CameraAcceptanceTestService.ToSummary(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true));
        var robotAcceptance = RobotAcceptanceTestService.ToSummary(AoiDatabase.GetLatestRobotAcceptanceRun());
        var mesReadiness = MesSpoolService.EvaluateReadiness();
        var thresholdProfileEvidence = ThresholdProfileService.GetActiveEvidenceSummary(request.BoardModel, request.BoardModel, "ANY");
        var preflight = request.DatasetPreflightResult ?? BuildPreflight(request);
        var latencySummary = request.LatencySummary ?? InspectionLatencyService.SummarizeBatchRows(rows);
        var pilotIssueSummary = new
        {
            summary = PilotIssueService.Summarize(new PilotIssueFilter
            {
                BoardModel = request.BoardModel,
                LotId = request.LotId,
            }),
            openIssues = PilotIssueService.Search(new PilotIssueFilter
            {
                BoardModel = request.BoardModel,
                LotId = request.LotId,
                OpenOnly = true,
            }).Select(issue => new
            {
                issue.IssueId,
                category = issue.Category.ToString(),
                issue.Severity,
                status = issue.Status.ToString(),
                issue.BoardModel,
                issue.LotId,
                issue.RelatedInspectionId,
                issue.RelatedAcceptanceRunId,
                issue.Owner,
                issue.Notes,
            }).ToArray(),
        };
        File.WriteAllText(breakdownCsvPath, ClassMetricsService.BuildCsv(breakdownSummary), Encoding.UTF8);
        File.WriteAllText(preflightPath, JsonSerializer.Serialize(preflight, JsonOptions), Encoding.UTF8);
        File.WriteAllText(issueSummaryPath, JsonSerializer.Serialize(pilotIssueSummary, JsonOptions), Encoding.UTF8);
        cancellationToken.ThrowIfCancellationRequested();

        var assetResult = ValidationReportAssetService.ExportSampleAnnotatedImages(
            rows,
            annotatedFolder,
            "annotated_images",
            maxCount: 25,
            cancellationToken);
        var latestBenchmark = BenchmarkInspectionService.GetLatestBenchmark();
        var packageWarnings = new List<string>();
        CopyLatestBenchmarkCsv(latestBenchmark, benchmarkCsvPath, packageWarnings);
        CopySourceManifest(request.GroundTruthCsvPath, sourceManifestCopyPath, packageWarnings);
        var learnedVisualModel = CopyLearnedVisualModelEvidence(
            request.LearnedVisualModel ?? LearnedVisualModelRegistryService.GetActiveSummary(),
            learnedVisualModelFolder,
            learnedVisualModelSummaryPath,
            packageWarnings);
        File.WriteAllText(limitationsPath, BuildLimitationsText(), Encoding.UTF8);
        var warnings = request.Warnings
            .Concat(packageWarnings)
            .Concat(preflight.Warnings)
            .Concat(preflight.BlockingFailures.Select(failure => $"Dataset preflight FAIL: {failure}"))
            .Concat(assetResult.Warnings)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acceptance = EvaluateAcceptance(
            request.Metrics,
            request.PerformanceSummary,
            rows.Length,
            request.IsFormalManifest,
            criteria,
            datasetQuality);

        var reportContext = new CustomerValidationReportContext
        {
            StationId = request.StationId,
            UserId = request.OperatorId,
            UserRole = request.OperatorRole,
            RunId = request.RunId?.ToString(CultureInfo.InvariantCulture) ?? "Not available",
            TestTimestamp = ToReportTimestamp(request.RunCreatedAtUtc, generatedAtUtc),
            BoardModel = request.BoardModel,
            LotId = request.LotId,
            ModelId = request.ModelId,
            ModelSha256 = request.ModelSha256,
            ModelValidationStatus = request.ModelValidationStatus,
            EngineName = request.EngineName,
            ModelVersion = request.ModelVersion,
            ModelFileName = request.ModelFileName,
            ConfidenceThreshold = request.ConfidenceThreshold,
            DatasetFolder = string.IsNullOrWhiteSpace(request.DatasetFolder) ? "Not available" : request.DatasetFolder,
            GroundTruthFile = string.IsNullOrWhiteSpace(request.GroundTruthCsvPath) ? "Not selected" : request.GroundTruthCsvPath,
            Metrics = request.Metrics,
            PerformanceSummary = request.PerformanceSummary,
            AcceptanceStatus = acceptance.Status,
            AcceptanceMessages = acceptance.Messages,
            AcceptanceCriteria = criteria,
            Rows = rows,
            SampleAnnotatedImages = assetResult.Images,
            Warnings = warnings,
            FalseCallRecommendation = FalseCallReductionService.ToSummary(request.FalseCallReductionRun),
            BreakdownSummary = breakdownSummary,
            DatasetQualitySummary = datasetQuality,
            DatasetPreflightResult = preflight,
            CameraAcceptanceSummary = cameraAcceptance,
            RobotAcceptanceSummary = robotAcceptance,
            MesReadinessSummary = mesReadiness,
            ThresholdProfileEvidence = thresholdProfileEvidence,
            LearnedVisualModel = learnedVisualModel,
            LatencySummary = latencySummary,
        };

        File.WriteAllText(summaryHtmlPath, BuildValidationSummaryHtml(reportContext, acceptance, latestBenchmark), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(summaryHtmlPath, summaryPdfPath, "Validation Summary");
        File.WriteAllText(reportPath, CustomerValidationReportService.BuildHtml(reportContext), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(reportPath, reportPdfPath, "Customer Validation Report");
        File.WriteAllText(instructionsPath, CustomerValidationReportService.BuildPrintToPdfInstructions(reportPath), Encoding.UTF8);
        File.WriteAllText(readmePath, BuildReadme(packageId, acceptance, warnings), Encoding.UTF8);

        var manifest = BuildManifest(request, criteria, acceptance, warnings, generatedAtUtc, packageId, breakdownSummary, datasetQuality, cameraAcceptance, robotAcceptance, mesReadiness, thresholdProfileEvidence, preflight, latencySummary, learnedVisualModel);
        manifest.IncludedFiles = EnumerateIncludedFiles(packageFolder).ToList();
        WriteManifest(manifestPath, manifest);
        manifest.IncludedFiles = EnumerateIncludedFiles(packageFolder).ToList();
        WriteManifest(manifestPath, manifest);

        var summary = BuildDatabaseSummary(request.Metrics, request.PerformanceSummary, acceptance);
        AoiDatabase.RecordValidationPackage(
            packageId,
            packageFolder,
            manifestPath,
            acceptance.Status,
            summary,
            request.RunId,
            request.OperatorId);
        ExportVerificationService.RecordVerifiedExport("Stage1ValidationPackage", packageFolder, acceptance.Status, request.OperatorId);

        return new CustomerValidationPackageResult(
            packageId,
            packageFolder,
            manifestPath,
            reportPath,
            csvPath,
            annotatedFolder,
            manifest,
            acceptance,
            warnings);
    }

    private static ValidationPackageManifest BuildManifest(
        CustomerValidationPackageRequest request,
        ValidationAcceptanceCriteria criteria,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings,
        DateTime generatedAtUtc,
        string packageId,
        ValidationBreakdownSummary breakdownSummary,
        DatasetQualitySummary datasetQuality,
        CameraAcceptanceSummary cameraAcceptance,
        RobotAcceptanceSummary robotAcceptance,
        MesReadinessSummary mesReadiness,
        ThresholdProfileEvidenceSummary thresholdProfileEvidence,
        CustomerDatasetPreflightResult preflight,
        InspectionLatencySummary latencySummary,
        LearnedVisualModelEvidenceSummary? learnedVisualModel)
    {
        return new ValidationPackageManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            PackageId = packageId,
            GeneratedAtUtc = generatedAtUtc,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            DeploymentProfile = DeploymentProfileSettingsService.Load().ToString(),
            StationId = request.StationId,
            OperatorId = request.OperatorId,
            BoardModel = request.BoardModel,
            LotId = request.LotId,
            ModelId = request.ModelId,
            ModelSha256 = request.ModelSha256,
            ModelValidationStatus = request.ModelValidationStatus,
            ModelVersion = request.ModelVersion,
            EngineName = request.EngineName,
            ActiveConfidenceThreshold = request.ConfidenceThreshold,
            DatasetFolderHashOrName = DatasetName(request.DatasetFolder),
            GroundTruthCsvName = string.IsNullOrWhiteSpace(request.GroundTruthCsvPath) ? "Not selected" : Path.GetFileName(request.GroundTruthCsvPath),
            RunId = request.RunId?.ToString(CultureInfo.InvariantCulture) ?? "Not available",
            MetricSummary = new ValidationMetricSummary
            {
                TotalImages = request.Rows.Count,
                KnownGroundTruthImages = Math.Max(0, request.Rows.Count - request.Metrics.Unknown),
                UnknownGroundTruthImages = request.Metrics.Unknown,
                Accuracy = request.Metrics.Accuracy,
                Precision = request.Metrics.Precision,
                Recall = request.Metrics.Recall,
                FalseCallRate = request.Metrics.FalseCallRate,
                TruePositive = request.Metrics.TruePositive,
                TrueNegative = request.Metrics.TrueNegative,
                FalsePositive = request.Metrics.FalsePositive,
                FalseNegative = request.Metrics.FalseNegative,
                FalseCall = request.Metrics.FalseCall,
                PossibleEscape = request.Metrics.PossibleEscape,
                VerifiedNg = request.Metrics.VerifiedNg,
                OkCount = request.Metrics.OkCount,
                NgCount = request.Metrics.NgCount,
                ReviewCount = request.Metrics.ReviewCount,
            },
            PerformanceSummary = new ValidationPackagePerformanceSummary
            {
                AverageMilliseconds = request.PerformanceSummary.AverageMilliseconds,
                MaxMilliseconds = request.PerformanceSummary.MaxMilliseconds,
                MinMilliseconds = request.PerformanceSummary.MinMilliseconds,
                CountOverOneSecond = request.PerformanceSummary.CountOverOneSecond,
                TimedImageCount = request.PerformanceSummary.TimedImageCount,
            },
            LatencySummary = latencySummary,
            BreakdownSummary = breakdownSummary,
            DatasetQualitySummary = datasetQuality,
            CameraAcceptanceSummary = cameraAcceptance,
            RobotAcceptanceSummary = robotAcceptance,
            MesReadinessSummary = mesReadiness,
            AcceptanceStatus = acceptance.Status,
            Criteria = criteria,
            FalseCallRecommendation = FalseCallReductionService.ToSummary(request.FalseCallReductionRun),
            LearnedVisualModel = learnedVisualModel,
            ThresholdProfileEvidence = thresholdProfileEvidence,
            DatasetPreflightStatus = preflight.Status,
            DatasetPreflightFailures = preflight.BlockingFailures,
            DatasetPreflightWarnings = preflight.Warnings,
            Warnings = warnings.Concat(acceptance.Messages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Limitations = CustomerValidationReportContext.DefaultPrototypeLimitations.ToList(),
        };
    }

    private static CustomerDatasetPreflightResult BuildPreflight(CustomerValidationPackageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetFolder) || string.IsNullOrWhiteSpace(request.GroundTruthCsvPath))
        {
            return new CustomerDatasetPreflightResult
            {
                Status = "CONDITIONAL",
                DatasetFolder = request.DatasetFolder,
                ManifestPath = request.GroundTruthCsvPath,
                Warnings = { "Dataset preflight was not run because the dataset folder or manifest path is unavailable." },
            };
        }

        return CustomerDatasetPreflightService.Validate(request.DatasetFolder, request.GroundTruthCsvPath);
    }

    private static void CopyLatestBenchmarkCsv(
        BenchmarkInspectionResult? latestBenchmark,
        string benchmarkCsvPath,
        IList<string> warnings)
    {
        if (latestBenchmark is not null &&
            !string.IsNullOrWhiteSpace(latestBenchmark.CsvPath) &&
            File.Exists(latestBenchmark.CsvPath))
        {
            try
            {
                File.Copy(latestBenchmark.CsvPath, benchmarkCsvPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"Latest benchmark CSV could not be copied into the validation package ({ex.GetType().Name}); a NOT_RUN placeholder was written.");
            }
        }
        else
        {
            warnings.Add("No recent benchmark_results.csv was available; run Readiness & QA > Performance Benchmark before package export to attach benchmark evidence.");
        }

        File.WriteAllText(
            benchmarkCsvPath,
            "Metric,Value" + Environment.NewLine +
            "Status,NOT_RUN" + Environment.NewLine +
            "\"Message\",\"No recent performance benchmark CSV was available when this validation package was generated.\"" + Environment.NewLine,
            Encoding.UTF8);
    }

    private static void CopySourceManifest(
        string groundTruthCsvPath,
        string sourceManifestCopyPath,
        IList<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(groundTruthCsvPath) || !File.Exists(groundTruthCsvPath))
        {
            warnings.Add("No source customer validation manifest CSV was available to copy into the package.");
            return;
        }

        try
        {
            File.Copy(groundTruthCsvPath, sourceManifestCopyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            warnings.Add($"Source customer validation manifest could not be copied into the package ({ex.GetType().Name}).");
        }
    }

    private static LearnedVisualModelEvidenceSummary? CopyLearnedVisualModelEvidence(
        LearnedVisualModelEvidenceSummary? source,
        string learnedVisualModelFolder,
        string summaryPath,
        IList<string> warnings)
    {
        if (source is null)
            return null;

        Directory.CreateDirectory(learnedVisualModelFolder);
        var summary = CloneLearnedVisualModelSummary(source);
        summary.LearnedReferenceArtifactPath = CopyLearnedArtifact(
            source.LearnedReferenceArtifactPath,
            "learned_reference.png",
            learnedVisualModelFolder,
            warnings);
        summary.ToleranceMapArtifactPath = CopyLearnedArtifact(
            source.ToleranceMapArtifactPath,
            "tolerance_map.png",
            learnedVisualModelFolder,
            warnings);
        summary.AnomalyThresholdMapArtifactPath = CopyLearnedArtifact(
            source.AnomalyThresholdMapArtifactPath,
            "anomaly_threshold_map.png",
            learnedVisualModelFolder,
            warnings);
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOptions), Encoding.UTF8);
        return summary;
    }

    private static string CopyLearnedArtifact(
        string sourcePath,
        string fileName,
        string learnedVisualModelFolder,
        IList<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            warnings.Add($"Learned PCB Visual Model artifact was not copied because it was missing: {fileName}.");
            return string.Empty;
        }

        var destinationPath = Path.Combine(learnedVisualModelFolder, fileName);
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Path.Combine("learned_visual_model", fileName).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            warnings.Add($"Learned PCB Visual Model artifact could not be copied: {fileName} ({ex.GetType().Name}).");
            return string.Empty;
        }
    }

    private static LearnedVisualModelEvidenceSummary CloneLearnedVisualModelSummary(LearnedVisualModelEvidenceSummary source)
        => new()
        {
            ModelId = source.ModelId,
            ModelVersion = source.ModelVersion,
            CreatedAtUtc = source.CreatedAtUtc,
            ProjectId = source.ProjectId,
            ProjectName = source.ProjectName,
            BoardModel = source.BoardModel,
            EvidenceMode = source.EvidenceMode,
            GoldenCount = source.GoldenCount,
            OkLearningCount = source.OkLearningCount,
            OkValidationCount = source.OkValidationCount,
            ImagesLearnedFrom = source.ImagesLearnedFrom,
            InputWidth = source.InputWidth,
            InputHeight = source.InputHeight,
            AlignmentMode = source.AlignmentMode,
            BrightnessNormalizationMode = source.BrightnessNormalizationMode,
            LearnedThreshold = source.LearnedThreshold,
            FalseCallTarget = source.FalseCallTarget,
            FalseCallRate = source.FalseCallRate,
            PossibleEscapeRate = source.PossibleEscapeRate,
            LearnedReferenceArtifactPath = source.LearnedReferenceArtifactPath,
            ToleranceMapArtifactPath = source.ToleranceMapArtifactPath,
            AnomalyThresholdMapArtifactPath = source.AnomalyThresholdMapArtifactPath,
            EvidenceLines = source.EvidenceLines.ToList(),
            BoundaryNote = source.BoundaryNote,
        };

    private static IEnumerable<ValidationIncludedFile> EnumerateIncludedFiles(string packageFolder)
    {
        return Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(packageFolder, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = Path.GetRelativePath(packageFolder, path).Replace('\\', '/'),
                FileType = ClassifyFile(packageFolder, path),
                Bytes = new FileInfo(path).Length,
            });
    }

    private static string ClassifyFile(string packageFolder, string path)
    {
        var relative = Path.GetRelativePath(packageFolder, path).Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "validation_manifest.json", StringComparison.OrdinalIgnoreCase))
            return "Manifest";
        if (string.Equals(fileName, "customer_validation_manifest.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV source manifest";
        if (string.Equals(fileName, "validation_results.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV results";
        if (string.Equals(fileName, "validation_breakdown.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV validation breakdown";
        if (string.Equals(fileName, "benchmark_results.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV performance benchmark";
        if (string.Equals(fileName, "validation_summary.html", StringComparison.OrdinalIgnoreCase))
            return "HTML validation summary";
        if (string.Equals(fileName, "validation_summary.pdf", StringComparison.OrdinalIgnoreCase))
            return "PDF validation summary";
        if (string.Equals(fileName, "customer_validation_report.html", StringComparison.OrdinalIgnoreCase))
            return "HTML report";
        if (string.Equals(fileName, "customer_validation_report.pdf", StringComparison.OrdinalIgnoreCase))
            return "PDF report";
        if (string.Equals(fileName, "dataset_preflight_summary.json", StringComparison.OrdinalIgnoreCase))
            return "Dataset preflight summary";
        if (string.Equals(fileName, "pilot_issue_summary.json", StringComparison.OrdinalIgnoreCase))
            return "Pilot issue summary";
        if (string.Equals(fileName, "limitations.txt", StringComparison.OrdinalIgnoreCase))
            return "Limitations";
        if (string.Equals(fileName, "print_to_pdf_instructions.txt", StringComparison.OrdinalIgnoreCase))
            return "Print-to-PDF instructions";
        if (string.Equals(fileName, "README.txt", StringComparison.OrdinalIgnoreCase))
            return "README";
        if (relative.StartsWith("annotated_images/", StringComparison.OrdinalIgnoreCase))
            return "Annotated image";
        return "Package evidence";
    }

    private static void WriteManifest(string manifestPath, ValidationPackageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json, Encoding.UTF8);
    }

    private static string BuildReadme(
        string packageId,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings)
    {
        var warningText = warnings.Count == 0
            ? "No export warnings were recorded."
            : string.Join(Environment.NewLine, warnings.Select(warning => $"- {warning}"));

        return $"""
        AOI Monitor Stage 1 Customer Validation Package

        Package ID: {packageId}
        Acceptance status: {acceptance.Status}

        Contents:
        - validation_manifest.json: versioned manifest and acceptance summary.
        - validation_summary.html: concise client-readable summary with metrics, p95 timing evidence, and limitations.
        - validation_summary.pdf: native PDF rendering of the concise summary.
        - validation_results.csv: per-image validation results exported from the batch run.
        - validation_breakdown.csv: per-class, per-side, and per-ROI validation breakdown.
        - benchmark_results.csv: latest performance benchmark CSV when available, or a NOT_RUN placeholder.
        - customer_validation_manifest.csv: copy of the selected source manifest when available.
        - pilot_issue_summary.json: open customer pilot issue counts and non-image issue details for this board/lot.
        - customer_validation_report.html: browser-readable customer validation report.
        - customer_validation_report.pdf: native PDF rendering of the customer validation report.
        - limitations.txt: plain-language Stage 1 prototype and integration limitations.
        - print_to_pdf_instructions.txt: browser print-to-PDF workflow.
        - annotated_images/: generated overlays only. Raw source customer datasets are not copied into this package.

        How to inspect:
        1. Open validation_manifest.json and confirm packageId, runId, acceptanceStatus, criteria, and includedFiles.
        2. Open validation_summary.html for the non-technical summary.
        3. Open customer_validation_report.html to review metrics, acceptance notes, limitations, and sample overlays.
        4. Open validation_results.csv to audit row-level results against the ground-truth manifest.

        Prototype limitations:
        {string.Join(Environment.NewLine, CustomerValidationReportContext.DefaultPrototypeLimitations.Select(item => $"- {item}"))}

        Warnings:
        {warningText}

        """;
    }

    private static string BuildValidationSummaryHtml(
        CustomerValidationReportContext context,
        ValidationAcceptanceSummary acceptance,
        BenchmarkInspectionResult? benchmark)
    {
        var rows = context.Rows.ToArray();
        var benchmarkAvailable = benchmark is { CompletedCount: > 0 };
        var overOneSecond = benchmarkAvailable
            ? benchmark!.OverOneSecondCount.ToString(CultureInfo.InvariantCulture)
            : context.LatencySummary.OverOneSecondCount.ToString(CultureInfo.InvariantCulture);
        var p95 = benchmarkAvailable
            ? FormatMilliseconds(benchmark!.P95FrameToOverlayMs)
            : context.LatencySummary.TraceCount > 0
                ? $"{FormatMilliseconds(context.LatencySummary.P95FrameToOverlayMs)} batch latency"
                : "Not available";

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Validation Summary</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:28px;color:#1d252c;line-height:1.45}h1{margin-bottom:4px}.notice{border-left:5px solid #d9951b;background:#fff8e8;padding:10px 12px;margin:16px 0}table{border-collapse:collapse;width:100%;margin:12px 0 20px}td,th{border:1px solid #c4cdd3;padding:8px;text-align:left;vertical-align:top}th{background:#edf2f5}.k{width:260px;font-weight:700;background:#f7fafb}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Stage 1 Validation Summary</h1>");
        sb.AppendLine($"<p>Generated {Html(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))} for package review.</p>");
        sb.AppendLine("<div class=\"notice\"><strong>Prototype boundary:</strong> This package documents local Stage 1 image-validation evidence only. It does not prove live camera, lighting, robot, PLC, production MES, safety, or production model readiness.</div>");
        sb.AppendLine("<table><tr><th colspan=\"2\">Result Overview</th></tr>");
        AppendSummaryRow("Acceptance status", acceptance.Status);
        AppendSummaryRow("Total images", rows.Length.ToString(CultureInfo.InvariantCulture));
        AppendSummaryRow("OK / NG / REVIEW", $"{context.Metrics.OkCount} / {context.Metrics.NgCount} / {context.Metrics.ReviewCount}");
        AppendSummaryRow("Accuracy", FormatPercent(context.Metrics.Accuracy));
        AppendSummaryRow("Precision", FormatPercent(context.Metrics.Precision));
        AppendSummaryRow("Recall", FormatPercent(context.Metrics.Recall));
        AppendSummaryRow("False calls / possible escapes", $"{context.Metrics.FalseCall} / {context.Metrics.PossibleEscape}");
        AppendSummaryRow("Engine / model", $"{context.EngineName} / {context.ModelVersion}");
        AppendSummaryRow("Dataset folder", context.DatasetFolder);
        AppendSummaryRow("Ground-truth manifest", context.GroundTruthFile);
        sb.AppendLine("</table>");

        sb.AppendLine("<table><tr><th colspan=\"2\">Performance Evidence</th></tr>");
        AppendSummaryRow("Latest benchmark status", benchmarkAvailable ? $"{benchmark!.Status} ({benchmark.SourceKind})" : "Not attached");
        AppendSummaryRow("P50 frame-to-overlay", benchmarkAvailable ? FormatMilliseconds(benchmark!.P50FrameToOverlayMs) : "Not available");
        AppendSummaryRow("P95 frame-to-overlay", p95);
        AppendSummaryRow("Max frame-to-overlay", benchmarkAvailable ? FormatMilliseconds(benchmark!.MaxFrameToOverlayMs) : FormatMilliseconds(context.LatencySummary.MaxFrameToOverlayMs));
        AppendSummaryRow("Images per minute", benchmarkAvailable ? benchmark!.ThroughputImagesPerMinute.ToString("F1", CultureInfo.InvariantCulture) : "Not available");
        AppendSummaryRow("Over 1 second", overOneSecond);
        AppendSummaryRow(
            "P95 load/preprocess/inference/overlay/persist",
            benchmarkAvailable
                ? $"{FormatMilliseconds(benchmark!.P95LoadMs)} / {FormatMilliseconds(benchmark.P95PreprocessingMs)} / {FormatMilliseconds(benchmark.P95InferenceMs)} / {FormatMilliseconds(benchmark.P95OverlayMs)} / {FormatMilliseconds(benchmark.P95PersistenceMs)}"
                : "Not available");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Acceptance Notes</h2>");
        AppendSummaryList(acceptance.Messages);
        sb.AppendLine("<h2>Limitations</h2>");
        AppendSummaryList(context.PrototypeLimitations);
        sb.AppendLine("</body></html>");
        return sb.ToString();

        void AppendSummaryRow(string label, string value)
        {
            sb.AppendLine($"<tr><td class=\"k\">{Html(label)}</td><td>{Html(value)}</td></tr>");
        }

        void AppendSummaryList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                sb.AppendLine("<p>None recorded.</p>");
                return;
            }

            sb.AppendLine("<ul>");
            foreach (var value in values)
                sb.AppendLine($"<li>{Html(value)}</li>");
            sb.AppendLine("</ul>");
        }
    }

    private static string BuildLimitationsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("AOI Monitor Stage 1 Prototype Limitations");
        sb.AppendLine();
        foreach (var limitation in CustomerValidationReportContext.DefaultPrototypeLimitations)
            sb.AppendLine($"- {limitation}");
        sb.AppendLine();
        sb.AppendLine("Generated sample datasets and Folder Camera Simulation are not real camera, lighting, robot, PLC, MES, ERP, safety, cybersecurity, or factory acceptance evidence.");
        return sb.ToString();
    }

    private static string BuildDatabaseSummary(
        BatchMetrics metrics,
        BatchPerformanceSummary performance,
        ValidationAcceptanceSummary acceptance)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"status={acceptance.Status}; accuracy={metrics.Accuracy:P1}; precision={metrics.Precision:P1}; recall={metrics.Recall:P1}; falseCallRate={metrics.FalseCallRate:P1}; overOneSecond={performance.CountOverOneSecond}");
    }

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "Not available" : $"{value:F1} ms";

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static DateTime ToReportTimestamp(DateTime? runCreatedAtUtc, DateTime generatedAtUtc)
    {
        var timestamp = runCreatedAtUtc ?? generatedAtUtc;
        return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
    }

    private static string BuildPackageId(DateTime generatedAtUtc)
        => $"STAGE1-{generatedAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private static string EnsureUniqueFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return folder;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{folder}_{i:D2}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{folder}_{Guid.NewGuid():N}";
    }

    private static string DatasetName(string datasetFolder)
    {
        if (string.IsNullOrWhiteSpace(datasetFolder))
            return "Not provided";

        try
        {
            var trimmed = datasetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Unusable dataset path";
        }
    }

    private static string FormatPercent(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);
}
