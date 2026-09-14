using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class PixelDifferenceInspectionEngine : IInspectionEngine
{
    /// <summary>
    /// Prototype engine version. Bumped to 0.2 when the verdict decision statistic changed from
    /// the whole-frame mean difference to the worst-region mean difference; results produced by
    /// the two versions are not comparable, so persisted history must stay distinguishable
    /// (AGENTS.md rule 10). This constant is the single source of truth — do not re-literal it.
    /// </summary>
    public const string EngineVersion = "PIXEL_DIFF_0.2";

    public string Name => "Pixel Difference Prototype Engine";
    public string Version => EngineVersion;

    // Reject implausibly large images before decode. A valid-but-enormous PNG/JPEG (a
    // "decompression bomb", or an accidental gigapixel scan) would otherwise force a multi-GB
    // BGRA allocation on the inspection thread and abort the batch run. 100 MPix covers any real
    // PCB image with wide margin; oversized images take the graceful SAMPLE_IMAGE_LOAD_FAILED path.
    internal const long MaxDecodePixels = 100_000_000;

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var timing = new InspectionTiming();
        var timestamp = DateTime.Now;
        var result = CreateBaseResult(samplePath, goldenPath, priority, timing, timestamp);

        var loadWatch = Stopwatch.StartNew();
        var sample = LoadBgra32(samplePath);
        loadWatch.Stop();
        timing.ImageLoadMilliseconds += loadWatch.Elapsed.TotalMilliseconds;
        if (sample is null)
        {
            result.ErrorCode = "SAMPLE_IMAGE_LOAD_FAILED";
            result.ErrorMessage = $"Unable to load sample image: {samplePath}";
            result.DecisionReason = result.ErrorMessage;
            result.Evidence = new List<string>
            {
                "Sample image could not be loaded; decision remains REVIEW by policy.",
                result.ErrorMessage,
            };
            result.Defects.Add(CreateDefectResult(result, "Sample Image Load Failed", "ROI-SAMPLE-ERROR", 1, 0, 0));
            result.Timing.RecalculateTotal();
            return result;
        }

        var preprocessingWatch = Stopwatch.StartNew();
        var meanBrightness = CalculateBrightness(sample);
        preprocessingWatch.Stop();
        timing.PreprocessingMilliseconds += preprocessingWatch.Elapsed.TotalMilliseconds;
        result.MeanBrightness = meanBrightness;

        if (string.IsNullOrWhiteSpace(goldenPath))
        {
            result.Defects.Add(CreateDefectResult(result, "Reference Missing", "ROI-REFERENCE", 1, sample.PixelWidth, sample.PixelHeight));
            result.Timing.RecalculateTotal();
            return result;
        }

        // Golden references repeat across inspections (golden-compare loops, batch runs,
        // benchmarks), so the decoded + normalized + grayscaled golden is served from a
        // keyed cache. Cached bytes are bit-identical to the uncached pipeline; scores,
        // verdicts, and hotspots are unchanged (cache-equivalence tests pin this).
        loadWatch.Restart();
        var goldenPixels = PixelDifferenceGoldenCache.GetOrCreate(goldenPath, () => BuildGoldenNormalizedPixels(goldenPath));
        loadWatch.Stop();
        timing.ImageLoadMilliseconds += loadWatch.Elapsed.TotalMilliseconds;
        if (goldenPixels is null)
        {
            result.ErrorCode = "GOLDEN_IMAGE_LOAD_FAILED";
            result.ErrorMessage = $"Unable to load golden image: {goldenPath}";
            result.SuggestedDefect = "Golden Reference Load Failed";
            result.DecisionReason = result.ErrorMessage;
            result.Evidence = new List<string>
            {
                "Golden reference could not be loaded; decision remains REVIEW by policy.",
                result.ErrorMessage,
            };
            result.Defects.Add(CreateDefectResult(result, "Golden Reference Load Failed", "ROI-GOLDEN-ERROR", 1, sample.PixelWidth, sample.PixelHeight));
            result.Timing.RecalculateTotal();
            return result;
        }

        preprocessingWatch.Restart();
        var sampleNorm = Resize(sample, 384, 384);
        preprocessingWatch.Stop();
        timing.PreprocessingMilliseconds += preprocessingWatch.Elapsed.TotalMilliseconds;

        var recipeLoad = RecipeService.LoadLatestRecipe(result.BoardProgram);
        if (recipeLoad.HasEnabledRois)
        {
            var recipeWatch = Stopwatch.StartNew();
            ApplyRecipeRoiAnalysis(result, sampleNorm, goldenPixels.ToBitmapSource(), priority, recipeLoad);
            recipeWatch.Stop();
            timing.InferenceMilliseconds = recipeWatch.Elapsed.TotalMilliseconds;
            result.Timing.RecalculateTotal();
            return result;
        }

        var inferenceWatch = Stopwatch.StartNew();
        var diff = Compare(sampleNorm, goldenPixels, out var hotspot, out var frameMeanDiff);
        inferenceWatch.Stop();
        timing.InferenceMilliseconds = inferenceWatch.Elapsed.TotalMilliseconds;
        // Everything from here until the defect record is built is overlay-data
        // preparation: the verdict, thresholds, evidence lines, and bounding-box records
        // the overlay layer consumes. Timed as OverlayRenderingMilliseconds so the
        // benchmark's frame-to-overlay figure covers the full operator-visible data path
        // (the on-screen WPF draw itself is timed in-app by the latency service).
        var overlayWatch = Stopwatch.StartNew();
        result.DifferenceScore = diff;
        result.FrameMeanDifferenceScore = frameMeanDiff;
        result.Hotspot = hotspot;

        var frameThreshold = ResolveFrameThreshold(result, recipeLoad, priority);
        var ngThreshold = frameThreshold.NgThreshold;
        var reviewThreshold = frameThreshold.ReviewThreshold;
        result.NgThreshold = ngThreshold;
        result.ReviewThreshold = reviewThreshold;

        if (diff >= ngThreshold)
        {
            result.Verdict = "NG";
            result.SuggestedDefect = "Possible Solder Bridge";
            result.DecisionMargin = diff - ngThreshold;
            result.DecisionReason = "Difference score exceeds NG threshold under current policy.";
        }
        else if (diff >= reviewThreshold)
        {
            result.Verdict = "REVIEW";
            result.SuggestedDefect = "Alignment / Reflection Difference";
            result.DecisionMargin = Math.Min(diff - reviewThreshold, ngThreshold - diff);
            result.DecisionReason = "Difference score is in the review band; human confirmation required.";
        }
        else
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Significant Difference";
            result.DecisionMargin = reviewThreshold - diff;
            result.DecisionReason = "Difference score is below review threshold for this policy.";
        }

        result.Confidence = ComputeConfidence(result.Verdict, diff, reviewThreshold, ngThreshold);
        if (!string.IsNullOrEmpty(frameThreshold.ProfileId))
            result.DecisionReason += $" (deployed threshold profile {frameThreshold.ProfileId}/{frameThreshold.Revision})";
        result.Evidence = BuildEvidence(result, priority);
        result.Evidence.Add(string.IsNullOrEmpty(frameThreshold.ProfileId)
            ? $"Threshold profile fallback (full frame): no active deployed profile rule matched board={result.BoardId}, program={result.BoardProgram}, recipe={recipeLoad.Recipe?.RecipeName ?? "ANY"}, view={result.ViewType}, roiType={ThresholdScopes.FullFrame}."
            : $"Threshold profile (full frame): {frameThreshold.ProfileId}/{frameThreshold.Revision} (Review >= {frameThreshold.ReviewThreshold:F1}%, NG >= {frameThreshold.NgThreshold:F1}%).");
        AppendRecipeWarnings(result, recipeLoad);
        result.Defects.Add(CreateDefectResult(result, result.SuggestedDefect, "ROI-HOTSPOT-001", 1, sampleNorm.PixelWidth, sampleNorm.PixelHeight));
        overlayWatch.Stop();
        timing.OverlayRenderingMilliseconds = overlayWatch.Elapsed.TotalMilliseconds;
        result.Timing.RecalculateTotal();

        return result;
    }

    private static void ApplyRecipeRoiAnalysis(
        AnalysisResult result,
        BitmapSource sample,
        BitmapSource golden,
        DetectionPriority priority,
        RecipeLoadResult recipeLoad)
    {
        var recipe = recipeLoad.Recipe!;
        var rois = recipe.Rois.Where(roi => roi.Enabled).ToArray();
        var (defaultNgThreshold, defaultReviewThreshold) = GetThresholds(priority);
        result.RecipeName = recipe.RecipeName;
        result.RecipeRevision = recipe.Revision;
        result.NgThreshold = defaultNgThreshold;
        result.ReviewThreshold = defaultReviewThreshold;
        result.Defects.Clear();
        result.Evidence.Clear();
        AppendRecipeWarnings(result, recipeLoad);

        var checkedCount = 0;
        double maxScore = 0;
        RecipeRoi? maxRoi = null;
        var sequence = 1;

        foreach (var roi in rois)
        {
            checkedCount++;
            var score = CompareRegion(sample, golden, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
            if (score > maxScore)
            {
                maxScore = score;
                maxRoi = roi;
            }

            var threshold = ResolveRoiThreshold(result, recipe, priority, roi);
            var roiNgThreshold = threshold.NgThreshold;
            var roiReviewThreshold = threshold.ReviewThreshold;
            if (score < roiReviewThreshold)
                continue;

            var judgment = score >= roiNgThreshold ? "NG" : "REVIEW";
            var confidence = ComputeConfidence(judgment, score, roiReviewThreshold, roiNgThreshold);
            var defect = CreateDefectResult(
                result,
                $"{roi.RoiType} ROI Difference",
                roi.RoiId,
                sequence++,
                sample.PixelWidth,
                sample.PixelHeight,
                new Rect(roi.X, roi.Y, roi.Width, roi.Height),
                confidence,
                judgment);
            defect.RoiName = roi.DisplayName;
            defect.RoiType = roi.RoiType;
            defect.SourceRoiId = roi.RoiId;
            result.Defects.Add(defect);
        }

        result.DifferenceScore = maxScore;
        if (maxRoi is not null)
            result.Hotspot = new Rect(maxRoi.X, maxRoi.Y, maxRoi.Width, maxRoi.Height);

        result.Evidence.Add($"Recipe '{recipe.RecipeName}' revision {recipe.Revision} applied.");
        result.Evidence.Add($"Checked {checkedCount} enabled recipe ROI(s).");
        result.Evidence.Add($"Maximum ROI difference score: {maxScore:F1}%.");

        if (result.Defects.Count == 0)
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Recipe ROI Difference Above Threshold";
            result.Confidence = ComputeConfidence("OK", maxScore, result.ReviewThreshold, result.NgThreshold);
            result.DecisionMargin = result.ReviewThreshold - maxScore;
            result.DecisionReason = $"All {checkedCount} enabled recipe ROI(s) were below review threshold.";
            return;
        }

        var topDefect = result.Defects.OrderByDescending(defect => defect.Confidence).First();
        result.Verdict = result.Defects.Any(defect => defect.JudgmentStatus == "NG") ? "NG" : "REVIEW";
        result.SuggestedDefect = topDefect.DefectType;
        result.Confidence = result.Defects.Max(defect => defect.Confidence);
        result.DecisionMargin = result.Defects.Count;
        result.DecisionReason = $"{result.Defects.Count} recipe ROI(s) crossed review/NG threshold.";
        result.Evidence.Add($"Recipe ROI findings: {string.Join("; ", result.Defects.Select(d => $"{d.RoiId}/{d.RoiType}={d.JudgmentStatus} {d.Confidence:P0}"))}.");
    }

    private AnalysisResult CreateBaseResult(
        string samplePath,
        string? goldenPath,
        DetectionPriority priority,
        InspectionTiming timing,
        DateTime timestamp)
    {
        var result = new AnalysisResult
        {
            SchemaVersion = AnalysisResult.CurrentSchemaVersion,
            InspectionId = InspectionContractIds.NewInspectionId(timestamp.ToUniversalTime()),
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            MeanBrightness = 0,
            Timestamp = timestamp,
            SuggestedDefect = "Solder Bridge",
            Verdict = "REVIEW",
            DifferenceScore = 0,
            ReviewThreshold = 0,
            NgThreshold = 0,
            Confidence = 0.55,
            DecisionMargin = 0,
            DecisionReason = "Golden reference is required for differential judgement.",
            PolicyName = ToPolicyDisplay(priority),
            Hotspot = new Rect(0.45, 0.4, 0.14, 0.12),
            SourceKind = "File",
            SourceFrameId = Path.GetFileName(samplePath),
            IsSimulatedSource = false,
            Evidence = new List<string>
            {
                "No golden image was supplied; decision remains REVIEW by policy.",
                "Run comparison against a verified golden image for actionable classification.",
            },
            Timing = timing,
        };

        ApplyWorkflowContext(result);
        return result;
    }

    private static void ApplyWorkflowContext(AnalysisResult result)
    {
        try
        {
            var state = WorkflowState.Instance;
            result.StationId = state.StationId;
            result.BoardProgram = state.BoardProgram;
            result.BoardId = state.BoardProgram;
            result.OperatorId = state.OperatorWithRole;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Pixel-difference analysis workflow context could not be applied: {ex.Message}");
        }
    }

    private static DefectResult CreateDefectResult(
        AnalysisResult result,
        string defectType,
        string roiId,
        int sequence,
        double imageWidthPixels,
        double imageHeightPixels)
        => CreateDefectResult(
            result,
            defectType,
            roiId,
            sequence,
            imageWidthPixels,
            imageHeightPixels,
            result.Hotspot,
            result.Confidence,
            result.Verdict);

    private static DefectResult CreateDefectResult(
        AnalysisResult result,
        string defectType,
        string roiId,
        int sequence,
        double imageWidthPixels,
        double imageHeightPixels,
        Rect boundingBox,
        double confidence,
        string judgmentStatus)
    {
        var box = boundingBox;
        var widthPixels = imageWidthPixels > 0 ? box.Width * imageWidthPixels : 0;
        var heightPixels = imageHeightPixels > 0 ? box.Height * imageHeightPixels : 0;
        return new DefectResult
        {
            DefectId = InspectionContractIds.NewDefectId(result.InspectionId, sequence),
            DefectType = defectType,
            Confidence = confidence,
            BoundingBox = box,
            XPosition = box.X + box.Width / 2.0,
            YPosition = box.Y + box.Height / 2.0,
            WidthPixels = widthPixels,
            HeightPixels = heightPixels,
            AreaPixels = widthPixels * heightPixels,
            Severity = ToDefectSeverity(judgmentStatus),
            SourceRoiId = roiId,
            SideOrViewType = result.ViewType,
            RoiId = roiId,
            JudgmentStatus = judgmentStatus,
        };
    }

    private static string ToDefectSeverity(string judgmentStatus)
        => judgmentStatus.ToUpperInvariant() switch
        {
            "NG" => "Major",
            "OK" => "Info",
            _ => "Review",
        };

    private static string ToPolicyDisplay(DetectionPriority priority) => priority switch
    {
        DetectionPriority.MinimizeFalsePositives => "Minimize False Positives",
        DetectionPriority.Balanced => "Balanced",
        DetectionPriority.MaximizeDefectRecall => "Maximize Defect Recall",
        _ => "Balanced",
    };

    private static double ComputeConfidence(string verdict, double diff, double reviewThreshold, double ngThreshold)
    {
        if (verdict == "NG")
        {
            var normalized = Math.Clamp((diff - ngThreshold) / Math.Max(2.0, ngThreshold * 0.5), 0, 1);
            return 0.72 + normalized * 0.27;
        }

        if (verdict == "OK")
        {
            var normalized = Math.Clamp((reviewThreshold - diff) / Math.Max(2.0, reviewThreshold * 0.6), 0, 1);
            return 0.68 + normalized * 0.3;
        }

        var mid = (reviewThreshold + ngThreshold) / 2.0;
        var halfBand = Math.Max(1.0, (ngThreshold - reviewThreshold) / 2.0);
        var centered = 1.0 - Math.Clamp(Math.Abs(diff - mid) / halfBand, 0, 1);
        return 0.52 + centered * 0.22;
    }

    private static List<string> BuildEvidence(AnalysisResult result, DetectionPriority priority)
    {
        return new List<string>
        {
            $"Difference score (worst region): {result.DifferenceScore:F1}% (Review >= {result.ReviewThreshold:F1}%, NG >= {result.NgThreshold:F1}%).",
            $"Whole-frame mean difference: {result.FrameMeanDifferenceScore:F2}% (context only; a localized defect is diluted by frame area and is not the decision statistic).",
            $"Threshold source: {result.ThresholdSource}.",
            $"Policy: {ToPolicyDisplay(priority)}.",
            $"Hotspot: x={result.Hotspot.X:P0}, y={result.Hotspot.Y:P0}, w={result.Hotspot.Width:P0}, h={result.Hotspot.Height:P0}.",
            $"Mean brightness (sample): {result.MeanBrightness:F1}.",
            $"Decision margin: {result.DecisionMargin:F2}.",
        };
    }

    private static (double ngThreshold, double reviewThreshold) GetThresholds(DetectionPriority priority)
    {
        return priority switch
        {
            DetectionPriority.MinimizeFalsePositives => (24, 12),
            DetectionPriority.Balanced => (18, 8),
            DetectionPriority.MaximizeDefectRecall => (14, 5),
            _ => (18, 8),
        };
    }

    private static EffectiveThresholdRule ResolveFrameThreshold(
        AnalysisResult result,
        RecipeLoadResult recipeLoad,
        DetectionPriority priority)
    {
        // The real recipe name is load-bearing: the frame path also runs when a loaded
        // recipe has no enabled ROIs, and drafts created from the AI / Models screen are
        // scoped to that recipe's name — an empty name would orphan those deployments.
        var profileRule = ThresholdProfileService.GetEffectiveThreshold(
            result.BoardId,
            result.BoardProgram,
            recipeLoad.Recipe?.RecipeName ?? string.Empty,
            result.ViewType,
            ThresholdScopes.FullFrame,
            ThresholdScopes.FullFrame);
        if (profileRule is not null)
        {
            ApplyThresholdProfile(result, profileRule);
            return profileRule;
        }

        var (ngThreshold, reviewThreshold) = GetThresholds(priority);
        result.ThresholdSource = "Built-in policy default";
        return new EffectiveThresholdRule
        {
            Source = "Built-in policy default",
            ReviewThreshold = reviewThreshold,
            NgThreshold = ngThreshold,
            ConfidenceThreshold = Math.Clamp(reviewThreshold / 100.0, 0.0, 1.0),
        };
    }

    private static (double ngThreshold, double reviewThreshold) GetRoiThresholds(DetectionPriority priority, RecipeRoi roi)
    {
        var (defaultNgThreshold, defaultReviewThreshold) = GetThresholds(priority);
        if (roi.Thresholds.AiScoreThreshold <= 0)
            return (defaultNgThreshold, defaultReviewThreshold);

        var roiReviewThreshold = Math.Clamp(roi.Thresholds.AiScoreThreshold * 100.0, 0.1, 100.0);
        roiReviewThreshold = Math.Min(defaultReviewThreshold, roiReviewThreshold);
        var roiNgThreshold = Math.Min(defaultNgThreshold, Math.Max(roiReviewThreshold, roiReviewThreshold * 2.0));
        return (roiNgThreshold, roiReviewThreshold);
    }

    private static EffectiveThresholdRule ResolveRoiThreshold(
        AnalysisResult result,
        RecipeDefinition recipe,
        DetectionPriority priority,
        RecipeRoi roi)
    {
        var profileRule = ThresholdProfileService.GetEffectiveThreshold(
            result.BoardId,
            recipe.BoardProgram,
            recipe.RecipeName,
            result.ViewType,
            roi.RoiType,
            roi.RoiType);
        if (profileRule is not null)
        {
            ApplyThresholdProfile(result, profileRule);
            result.Evidence.Add($"Threshold source for ROI {roi.RoiId}: Active threshold profile {profileRule.ProfileId}/{profileRule.Revision} (Review >= {profileRule.ReviewThreshold:F1}%, NG >= {profileRule.NgThreshold:F1}%).");
            return profileRule;
        }

        var (ngThreshold, reviewThreshold) = GetRoiThresholds(priority, roi);
        var source = roi.Thresholds.AiScoreThreshold > 0 ? "Recipe ROI threshold" : "Built-in policy default";
        result.ThresholdSource = source;
        result.Evidence.Add($"Threshold profile fallback for ROI {roi.RoiId}: no active deployed profile rule matched board={result.BoardId}, recipe={recipe.RecipeName}, view={result.ViewType}, roiType={roi.RoiType}.");
        result.Evidence.Add($"Threshold source for ROI {roi.RoiId}: {source} (Review >= {reviewThreshold:F1}%, NG >= {ngThreshold:F1}%).");
        return new EffectiveThresholdRule
        {
            Source = source,
            ReviewThreshold = reviewThreshold,
            NgThreshold = ngThreshold,
            ConfidenceThreshold = Math.Clamp(reviewThreshold / 100.0, 0.0, 1.0),
        };
    }

    private static void ApplyThresholdProfile(AnalysisResult result, EffectiveThresholdRule threshold)
    {
        result.ThresholdSource = threshold.Source;
        result.ThresholdProfileId = threshold.ProfileId;
        result.ThresholdProfileRevision = threshold.Revision;
        result.ReviewThreshold = threshold.ReviewThreshold;
        result.NgThreshold = threshold.NgThreshold;
        result.ConfidenceThreshold = threshold.ConfidenceThreshold;
    }

    private static void AppendRecipeWarnings(AnalysisResult result, RecipeLoadResult recipeLoad)
    {
        foreach (var warning in recipeLoad.Warnings)
            result.Evidence.Add($"Recipe warning: {warning}");
    }

    private static BitmapSource? LoadBgra32(string path, bool ignoreImageCache = false)
    {
        if (!File.Exists(path)) return null;

        if (ExceedsMaxDecodePixels(path))
            return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (ignoreImageCache)
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        if (bmp.Format == PixelFormats.Bgra32)
            return bmp;

        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    // Reads only the frame header (no full decode) to reject oversized images cheaply.
    private static bool ExceedsMaxDecodePixels(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return false;

            var frame = decoder.Frames[0];
            return (long)frame.PixelWidth * frame.PixelHeight > MaxDecodePixels;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or OverflowException)
        {
            // A header we cannot even read is not treatable as "oversized"; let the normal decode
            // path attempt it and surface a proper load failure.
            return false;
        }
    }

    private static BitmapSource Resize(BitmapSource source, int maxW, int maxH)
    {
        var scale = Math.Min(maxW / (double)source.PixelWidth, maxH / (double)source.PixelHeight);
        scale = Math.Min(1.0, scale);

        var transform = new ScaleTransform(scale, scale);
        var resized = new TransformedBitmap(source, transform);
        resized.Freeze();

        if (resized.Format == PixelFormats.Bgra32)
            return resized;

        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static double CalculateBrightness(BitmapSource src)
    {
        var stride = src.PixelWidth * 4;
        // 64-bit product so a large full-resolution sample cannot overflow int32 into a negative
        // count (the sample is measured before the 384x384 downscale). LoadBgra32 already caps
        // dimensions, so this branch is a defence-in-depth backstop, not the primary guard.
        var count = (long)stride * src.PixelHeight;
        if (count > int.MaxValue)
            return 0;

        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent((int)count);

        try
        {
            src.CopyPixels(pixels, stride, 0);

            double sum = 0;
            for (int i = 0; i < count; i += 4)
            {
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            }

            var n = count / 4.0;
            return n == 0 ? 0 : sum / n;
        }
        finally
        {
            pool.Return(pixels);
        }
    }

    private static GoldenNormalizedPixels? BuildGoldenNormalizedPixels(string goldenPath)
    {
        // Decode bypassing WPF's process-wide URI image cache: the golden cache keys on
        // file size + last-write time, and a stale WPF-cached decode of an overwritten
        // golden file would silently defeat that invalidation guarantee.
        var golden = LoadBgra32(goldenPath, ignoreImageCache: true);
        if (golden is null)
            return null;

        var goldenNorm = Resize(golden, 384, 384);
        var width = goldenNorm.PixelWidth;
        var height = goldenNorm.PixelHeight;
        var stride = width * 4;
        var bytes = new byte[stride * height];
        goldenNorm.CopyPixels(bytes, stride, 0);
        return new GoldenNormalizedPixels(width, height, bytes, ToGray(bytes, width, height, stride));
    }

    private static (byte[] Bgra, double[] Gray) CropGolden(GoldenNormalizedPixels golden, int width, int height)
    {
        if (width == golden.Width && height == golden.Height)
            return (golden.Bgra, golden.Gray);

        // Top-left crop of the cached full-size planes, byte-identical to what a
        // CroppedBitmap(0,0,w,h) CopyPixels of the normalized golden would produce.
        var stride = width * 4;
        var goldenStride = golden.Width * 4;
        var bytes = new byte[stride * height];
        var gray = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(golden.Bgra, y * goldenStride, bytes, y * stride, stride);
            Array.Copy(golden.Gray, y * golden.Width, gray, y * width, width);
        }

        return (bytes, gray);
    }

    /// <summary>
    /// Compares a sample against the golden reference and returns the **decision statistic**: the
    /// mean absolute BGR difference of the worst region of the board, as a percentage of full
    /// scale (0-100), matching the units of <see cref="GetThresholds"/>.
    ///
    /// The statistic is deliberately regional, not frame-wide. A real PCB defect — a solder
    /// bridge, a missing 0402, a shifted connector — occupies a small fraction of the frame, so a
    /// whole-image mean dilutes it by roughly the ratio of frame area to defect area and can never
    /// reach a sensible NG threshold. Judging the worst region keeps the threshold units meaningful
    /// while making localized defects detectable. The frame-wide mean is still returned through
    /// <paramref name="frameMeanDifference"/> and recorded as evidence, because it remains the
    /// right statistic for gross whole-board problems (wrong board loaded, gross mis-registration).
    /// </summary>
    private static double Compare(BitmapSource a, GoldenNormalizedPixels golden, out Rect hotspot, out double frameMeanDifference)
    {
        var w = Math.Min(a.PixelWidth, golden.Width);
        var h = Math.Min(a.PixelHeight, golden.Height);

        var stride = w * 4;
        var count = stride * h;
        var pool = ArrayPool<byte>.Shared;
        var pa = pool.Rent(count);

        try
        {
            var ra = new CroppedBitmap(a, new Int32Rect(0, 0, w, h));
            ra.CopyPixels(pa, stride, 0);
            var (pb, grayGolden) = CropGolden(golden, w, h);

            // Recover small sample-to-golden translation before diffing: unaligned captures
            // shift every high-contrast edge and inflate the difference score on good boards.
            // The search is deliberately tight (~2% of frame) and keeps (0,0) unless the best
            // offset is a clear improvement, so a gross defect cannot drag the alignment.
            var graySample = ToGray(pa, w, h, stride);
            var (dx, dy) = FindTranslation(grayGolden, graySample, w, h);

            double total = 0;
            long overlap = 0;
            const int gridX = 8;
            const int gridY = 8;
            var bins = new double[gridX * gridY];
            // Per-cell overlap counts: edge cells and the alignment-trimmed border carry fewer
            // pixels than interior cells, so a cell mean needs its own denominator.
            var binCounts = new long[gridX * gridY];
            int cw = Math.Max(1, w / gridX);
            int ch = Math.Max(1, h / gridY);

            for (int y = 0; y < h; y++)
            {
                int sy = y - dy;
                if (sy < 0 || sy >= h)
                    continue;

                int gy = Math.Min(gridY - 1, y / ch);
                int goldenRow = y * stride;
                int sampleRow = sy * stride;
                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx;
                    if (sx < 0 || sx >= w)
                        continue;

                    int gx = Math.Min(gridX - 1, x / cw);
                    int gi = goldenRow + x * 4;
                    int si = sampleRow + sx * 4;

                    double dr = Math.Abs(pa[si + 2] - pb[gi + 2]);
                    double dg = Math.Abs(pa[si + 1] - pb[gi + 1]);
                    double db = Math.Abs(pa[si] - pb[gi]);
                    double d = (dr + dg + db) / 3.0;

                    total += d;
                    overlap++;
                    bins[gy * gridX + gx] += d;
                    binCounts[gy * gridX + gx]++;
                }
            }

            // Worst region by mean, not by sum: a sum-ranked search favours whichever cell simply
            // has the most overlapping pixels, which is a size artefact rather than a defect.
            int idx = 0;
            double worstCellMean = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                if (binCounts[i] == 0)
                    continue;

                var cellMean = bins[i] / binCounts[i];
                if (cellMean > worstCellMean)
                {
                    worstCellMean = cellMean;
                    idx = i;
                }
            }

            int bx = idx % gridX;
            int by = idx / gridX;
            hotspot = new Rect(
                bx / (double)gridX,
                by / (double)gridY,
                1.0 / gridX,
                1.0 / gridY);

            var frameMad = overlap == 0 ? 0 : total / overlap;
            frameMeanDifference = Math.Min(100.0, frameMad / 255.0 * 100.0);
            return Math.Min(100.0, worstCellMean / 255.0 * 100.0);
        }
        finally
        {
            // Only the sample buffer is pool-rented; the golden planes are cache-owned
            // (or exact-size crops) and must never be handed to the ArrayPool.
            pool.Return(pa);
        }
    }

    private static double[] ToGray(byte[] bgra, int width, int height, int stride)
    {
        var gray = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                gray[y * width + x] = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
            }
        }

        return gray;
    }

    private static (int OffsetX, int OffsetY) FindTranslation(double[] golden, double[] sample, int width, int height)
    {
        // Coarse-to-fine integer translation search, mirroring the learned-model pipeline.
        // Radius stays tight (~2% of a 384px frame): this engine has no brightness
        // normalization, so wide searches would chase lighting differences.
        var radius = Math.Min(8, Math.Min(width, height) / 16);
        if (radius <= 0)
            return (0, 0);

        var sampleStep = Math.Max(1, Math.Max(width, height) / 96);
        var bestX = 0;
        var bestY = 0;
        var bestError = double.PositiveInfinity;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var error = TranslationError(golden, sample, dx, dy, width, height, sampleStep);
                if (error < bestError - 0.000001 ||
                    (Math.Abs(error - bestError) <= 0.000001 &&
                     Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bestX) + Math.Abs(bestY)))
                {
                    bestX = dx;
                    bestY = dy;
                    bestError = error;
                }
            }
        }

        if (sampleStep > 1)
        {
            var refinedX = bestX;
            var refinedY = bestY;
            var refinedError = double.PositiveInfinity;
            for (var dy = Math.Max(-radius, bestY - 2); dy <= Math.Min(radius, bestY + 2); dy++)
            {
                for (var dx = Math.Max(-radius, bestX - 2); dx <= Math.Min(radius, bestX + 2); dx++)
                {
                    var error = TranslationError(golden, sample, dx, dy, width, height, 1);
                    if (error < refinedError - 0.000001 ||
                        (Math.Abs(error - refinedError) <= 0.000001 &&
                         Math.Abs(dx) + Math.Abs(dy) < Math.Abs(refinedX) + Math.Abs(refinedY)))
                    {
                        refinedX = dx;
                        refinedY = dy;
                        refinedError = error;
                    }
                }
            }

            bestX = refinedX;
            bestY = refinedY;
            bestError = refinedError;
        }

        if (bestX == 0 && bestY == 0)
            return (0, 0);

        // Keep (0,0) unless the shifted match is a clear improvement: a gross defect can bias
        // the search toward "eating" its own edge, and marginal gains are not worth that risk.
        var zeroError = TranslationError(golden, sample, 0, 0, width, height, 1);
        return bestError < zeroError * 0.98 ? (bestX, bestY) : (0, 0);
    }

    private static double TranslationError(double[] golden, double[] sample, int offsetX, int offsetY, int width, int height, int sampleStep)
    {
        var sum = 0.0;
        var count = 0;
        for (var y = 0; y < height; y += sampleStep)
        {
            var sourceY = y - offsetY;
            if (sourceY < 0 || sourceY >= height)
                continue;

            for (var x = 0; x < width; x += sampleStep)
            {
                var sourceX = x - offsetX;
                if (sourceX < 0 || sourceX >= width)
                    continue;

                var delta = golden[y * width + x] - sample[sourceY * width + sourceX];
                sum += delta * delta;
                count++;
            }
        }

        return count == 0 ? double.PositiveInfinity : sum / count;
    }

    private static double CompareRegion(BitmapSource a, BitmapSource b, Rect normalizedRegion)
    {
        var w = Math.Min(a.PixelWidth, b.PixelWidth);
        var h = Math.Min(a.PixelHeight, b.PixelHeight);
        if (w <= 0 || h <= 0)
            return 0;

        var left = Math.Clamp((int)Math.Floor(normalizedRegion.X * w), 0, w - 1);
        var top = Math.Clamp((int)Math.Floor(normalizedRegion.Y * h), 0, h - 1);
        var right = Math.Clamp((int)Math.Ceiling((normalizedRegion.X + normalizedRegion.Width) * w), left + 1, w);
        var bottom = Math.Clamp((int)Math.Ceiling((normalizedRegion.Y + normalizedRegion.Height) * h), top + 1, h);
        var regionWidth = right - left;
        var regionHeight = bottom - top;
        var fullStride = w * 4;
        var count = fullStride * h;
        var pool = ArrayPool<byte>.Shared;
        var pa = pool.Rent(count);
        var pb = pool.Rent(count);

        try
        {
            var ra = new CroppedBitmap(a, new Int32Rect(0, 0, w, h));
            var rb = new CroppedBitmap(b, new Int32Rect(0, 0, w, h));
            ra.CopyPixels(pa, fullStride, 0);
            rb.CopyPixels(pb, fullStride, 0);

            double total = 0;
            for (var y = top; y < bottom; y++)
            {
                var row = y * fullStride;
                for (var x = left; x < right; x++)
                {
                    var i = row + x * 4;
                    var dr = Math.Abs(pa[i + 2] - pb[i + 2]);
                    var dg = Math.Abs(pa[i + 1] - pb[i + 1]);
                    var db = Math.Abs(pa[i] - pb[i]);
                    total += (dr + dg + db) / 3.0;
                }
            }

            var mad = total / (regionWidth * regionHeight);
            return Math.Min(100.0, mad / 255.0 * 100.0);
        }
        finally
        {
            pool.Return(pa);
            pool.Return(pb);
        }
    }
}
