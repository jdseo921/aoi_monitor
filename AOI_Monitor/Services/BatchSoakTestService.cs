using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Headless Stage 1 stability soak that loops the real batch-inspection pipeline
/// (image enumeration, optional manifest, engine analysis, batch rows/metrics,
/// optional SQLite batch-run persistence) over an uploaded-image folder for a
/// configured duration, capturing per-pass timing, managed memory, handle counts,
/// SQLite file growth, and error/alarm events. Fails the run on unhandled
/// exceptions, stuck iterations, sustained memory-growth trend, or an unusable
/// dataset, while still producing HTML/JSON/CSV evidence with truthful
/// uploaded-image scope labeling (see <see cref="BatchSoakReportService"/>).
/// </summary>
public static class BatchSoakTestService
{
    /// <summary>Truthful evidence boundary embedded in every artifact and the console banner.</summary>
    public const string ScopeStatement =
        "Stage 1 uploaded-image batch-inspection soak evidence. Frames come from a local " +
        "image folder processed by the offline batch-inspection pipeline. This is not live " +
        "camera acquisition, lighting, robot/PLC, safety, or MES evidence, and it does not " +
        "satisfy Stage 2-4 hardware readiness gates.";

    public const string FailReasonUnhandledException = "UnhandledException";
    public const string FailReasonStuckIteration = "StuckIteration";
    public const string FailReasonMemoryGrowthTrend = "MemoryGrowthTrend";
    public const string FailReasonNoImagesFound = "NoImagesFound";
    public const string FailReasonEveryImageFailed = "EveryImageFailed";
    public const string FailReasonNoPassesCompleted = "NoPassesCompleted";

    /// <summary>File that receives full exception traces; operator-facing artifacts carry type + message only.</summary>
    public const string EngineerDebugFileName = "soak_debug.txt";

    private const int MaxStoredErrors = 200;
    private const int MinimumTrendSamples = 8;

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    /// <summary>Runs the batch soak. Throws ArgumentException for unusable options; never throws for in-run pipeline failures.</summary>
    public static async Task<BatchSoakResult> RunAsync(
        BatchSoakOptions options,
        IProgress<BatchSoakProgress>? progress,
        CancellationToken cancellationToken,
        IInspectionEngine? engineOverride = null)
    {
        ValidateOptions(options);
        AoiDatabase.Initialize();
        Directory.CreateDirectory(options.OutputFolder);

        var context = CreateContext(options, progress, engineOverride);
        var manifest = BatchValidationService.LoadValidationManifest(
            string.IsNullOrWhiteSpace(options.ManifestPath) ? null : options.ManifestPath,
            options.ImageFolder);
        foreach (var warning in manifest.Warnings)
            AddError(context.Result, warning);

        while (ShouldContinue(context))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                MarkCanceled(context.Result, "Cancellation requested before next pass.");
                break;
            }

            var keepRunning = await RunPassAsync(context, manifest, cancellationToken) &&
                AfterPass(context, cancellationToken) &&
                await DelayBetweenPassesAsync(context, cancellationToken);
            if (!keepRunning)
                break;
        }

        if (!context.Result.WasCanceled && context.Result.TotalPasses == 0 && context.Result.FailReasons.Count == 0)
            context.Result.FailReasons.Add(FailReasonNoPassesCompleted);

        FinalizeResult(context);
        return context.Result;
    }

    /// <summary>
    /// Evaluates the managed-memory series for an unbounded-growth trend: least-squares
    /// slope over the second half of the samples must exceed the slope limit AND total
    /// growth across the full series must exceed the floor.
    /// </summary>
    public static BatchSoakMemoryTrend EvaluateMemoryTrend(
        IReadOnlyList<BatchSoakMemoryTrendSample> samples,
        double slopeFailMegabytesPerHour,
        double growthFloorMegabytes)
    {
        if (samples.Count < MinimumTrendSamples)
        {
            return new BatchSoakMemoryTrend(
                samples.Count,
                0,
                samples.Count > 0 ? samples[0].ManagedMegabytes : 0,
                samples.Count > 0 ? samples[^1].ManagedMegabytes : 0,
                samples.Count > 0 ? samples[^1].ManagedMegabytes - samples[0].ManagedMegabytes : 0,
                false,
                $"Insufficient samples for trend evaluation ({samples.Count} < {MinimumTrendSamples}).");
        }

        var window = samples.Skip(samples.Count / 2).ToArray();
        var meanX = window.Average(sample => sample.ElapsedHours);
        var meanY = window.Average(sample => sample.ManagedMegabytes);
        var denominator = window.Sum(sample => Math.Pow(sample.ElapsedHours - meanX, 2));
        var slope = denominator <= double.Epsilon
            ? 0
            : window.Sum(sample => (sample.ElapsedHours - meanX) * (sample.ManagedMegabytes - meanY)) / denominator;
        var growth = samples[^1].ManagedMegabytes - samples[0].ManagedMegabytes;
        var exceeded = slope > slopeFailMegabytesPerHour && growth > growthFloorMegabytes;
        var description = exceeded
            ? FormattableString.Invariant($"Unbounded managed-memory growth trend: slope {slope:F1} MB/h over the last {window.Length} samples exceeds {slopeFailMegabytesPerHour:F0} MB/h and total growth {growth:F1} MB exceeds {growthFloorMegabytes:F0} MB.")
            : FormattableString.Invariant($"Managed-memory trend within bounds: slope {slope:F1} MB/h (limit {slopeFailMegabytesPerHour:F0} MB/h), total growth {growth:F1} MB (floor {growthFloorMegabytes:F0} MB).");
        return new BatchSoakMemoryTrend(window.Length, slope, samples[0].ManagedMegabytes, samples[^1].ManagedMegabytes, growth, exceeded, description);
    }

    /// <summary>
    /// Writes a crash marker into the run folder so a hard process crash leaves an
    /// evidence trail. Returns the marker path, or null when the write itself failed
    /// (the failure is reported to <paramref name="diagnostics"/> when provided).
    /// </summary>
    public static string? WriteCrashMarker(string runFolder, string detail, TextWriter? diagnostics = null)
    {
        try
        {
            Directory.CreateDirectory(runFolder);
            var path = Path.Combine(runFolder, "crash_marker.txt");
            File.WriteAllText(
                path,
                FormattableString.Invariant($"Batch soak run crashed at {DateTime.UtcNow:O}.{Environment.NewLine}{detail}"));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics?.WriteLine($"WARN could not write crash marker: {ex.Message}");
            return null;
        }
    }

    private static void ValidateOptions(BatchSoakOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ImageFolder))
            throw new ArgumentException("Image folder is required.", nameof(options));
        if (!Directory.Exists(options.ImageFolder))
            throw new ArgumentException($"Image folder was not found: {options.ImageFolder}", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new ArgumentException("Output folder is required.", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.ManifestPath) && !File.Exists(options.ManifestPath))
            throw new ArgumentException($"Manifest CSV was not found: {options.ManifestPath}", nameof(options));
    }

    private static SoakRunContext CreateContext(
        BatchSoakOptions options,
        IProgress<BatchSoakProgress>? progress,
        IInspectionEngine? engineOverride)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var configuration = InspectionModelConfigurationService.Load();
        var engine = engineOverride ?? InspectionEngineFactory.Create(options.EngineKey);
        var resolvedEngineKey = InspectionEngineFactory.NormalizeEngineKey(
            string.IsNullOrWhiteSpace(options.EngineKey) ? configuration.SelectedEngineKey : options.EngineKey);
        var startImages = EnumerateImages(options.ImageFolder);

        var result = new BatchSoakResult
        {
            RequestedDuration = options.Duration,
            ImageFolder = Path.GetFullPath(options.ImageFolder),
            ManifestPath = string.IsNullOrWhiteSpace(options.ManifestPath) ? string.Empty : Path.GetFullPath(options.ManifestPath),
            OutputFolder = Path.GetFullPath(options.OutputFolder),
            OperatorId = options.OperatorId,
            BoardModel = string.IsNullOrWhiteSpace(options.BoardModel) ? "TBOX-MAIN" : options.BoardModel,
            LotId = string.IsNullOrWhiteSpace(options.LotId) ? "BATCH-SOAK" : options.LotId,
            BatchRunsPersisted = options.PersistBatchRuns,
            StartManagedMemoryMegabytes = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: true)),
            StartWorkingSetMegabytes = BytesToMegabytes(process.WorkingSet64),
            StartHandleCount = process.HandleCount,
            StartDatabaseSizeMegabytes = DatabaseSizeMegabytes(null),
            EngineConfig = new BatchSoakEngineConfig(
                resolvedEngineKey,
                engine.Name,
                engine.Version,
                options.DetectionPriority.ToString(),
                configuration.IsOnnxSelected,
                configuration.ActiveModelId,
                configuration.ActiveModelSha256,
                configuration.ConfidenceThreshold),
        };
        result.PeakManagedMemoryMegabytes = result.StartManagedMemoryMegabytes;
        result.PeakWorkingSetMegabytes = result.StartWorkingSetMegabytes;
        result.PeakHandleCount = result.StartHandleCount;
        result.DatasetImageCountAtStart = startImages.Length;
        result.DatasetFingerprintSha256 = FingerprintFileList(startImages);

        return new SoakRunContext(options, engine, result, process, progress);
    }

    private static bool ShouldContinue(SoakRunContext context)
        => context.Clock.Elapsed < context.Options.Duration &&
           (context.Options.MaxPasses is null || context.Result.TotalPasses < context.Options.MaxPasses.Value);

    /// <summary>Runs one batch pass. Returns false when the run must stop (fail, cancel, or dataset unusable).</summary>
    private static async Task<bool> RunPassAsync(SoakRunContext context, ValidationManifest manifest, CancellationToken cancellationToken)
    {
        var result = context.Result;
        var passNumber = result.TotalPasses + 1;
        var passStartedUtc = DateTime.UtcNow;
        var passStopwatch = Stopwatch.StartNew();

        var imageFiles = EnumerateImages(context.Options.ImageFolder);
        if (imageFiles.Length == 0)
        {
            AddError(result, $"Pass {passNumber}: no PNG/JPG/JPEG images found in {context.Options.ImageFolder}.");
            result.FailReasons.Add(FailReasonNoImagesFound);
            return false;
        }

        var rows = new List<BatchTestRow>(imageFiles.Length);
        var keepRunning = true;
        foreach (var item in BatchValidationService.BuildRunItems(imageFiles, manifest))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                MarkCanceled(result, "Cancellation requested during a pass.");
                keepRunning = false;
                break;
            }

            if (!File.Exists(item.ImagePath))
            {
                var missing = $"Missing image file: {item.ImagePath}";
                AddError(result, missing);
                rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, missing, context.Engine.Name, context.Engine.Version));
                continue;
            }

            if (!await InspectImageAsync(context, item, rows, passNumber, cancellationToken))
            {
                keepRunning = false;
                break;
            }
        }

        passStopwatch.Stop();
        if (rows.Count > 0)
            RecordPass(context, rows, passNumber, passStartedUtc, passStopwatch.Elapsed);
        return keepRunning;
    }

    /// <summary>Inspects one image under the stuck-iteration watchdog. Returns false when the run must stop.</summary>
    private static async Task<bool> InspectImageAsync(
        SoakRunContext context,
        RunItem item,
        List<BatchTestRow> rows,
        int passNumber,
        CancellationToken cancellationToken)
    {
        var result = context.Result;

        // The IInspectionEngine contract has no cancellation support, so a hung or
        // canceled-mid-flight analysis cannot be aborted cooperatively; the task is
        // abandoned (its late fault, if any, is appended to the engineer debug file)
        // and the headless driver's process teardown reclaims the thread.
        var analyzeTask = Task.Run(
            () =>
            {
                using (InspectionScope.ForBoardModel(item.Manifest.BoardModel))
                {
                    return context.Engine.Analyze(
                        item.ImagePath,
                        string.IsNullOrWhiteSpace(item.Manifest.GoldenPath) ? null : item.Manifest.GoldenPath,
                        context.Options.DetectionPriority);
                }
            },
            CancellationToken.None);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(context.Options.StuckImageTimeout, watchdog.Token);
        var completed = await Task.WhenAny(analyzeTask, delayTask);
        if (completed != analyzeTask)
        {
            // Race-free classification: the linked delay can only be canceled by the
            // outer token here, so IsCanceled means operator cancellation, and a delay
            // that ran to completion means the watchdog genuinely expired.
            if (delayTask.IsCanceled)
            {
                MarkCanceled(result, "Cancellation requested during an inspection; the in-flight image was abandoned.");
                return false;
            }

            ObserveAbandonedFault(result.OutputFolder, analyzeTask, item.ImagePath);
            AddError(result, FormattableString.Invariant(
                $"Pass {passNumber}: inspection of {Path.GetFileName(item.ImagePath)} exceeded the stuck-iteration timeout of {context.Options.StuckImageTimeout.TotalSeconds:F0} s."));
            result.FailReasons.Add(FailReasonStuckIteration);
            return false;
        }

        watchdog.Cancel();
        try
        {
            var analysis = await analyzeTask;
            rows.Add(BatchValidationService.ToRow(item.ImagePath, item.Manifest, analysis));
            if (analysis.Timing.TotalInspectionMilliseconds > 0)
                context.ImageTimings.Add(analysis.Timing.TotalInspectionMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            var message = $"Pass {passNumber}: {Path.GetFileName(item.ImagePath)}: {ex.GetType().Name} - {ex.Message}";
            AddError(result, message);
            rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, message, context.Engine.Name, context.Engine.Version));
            return true;
        }
        catch (Exception ex)
        {
            AddError(result, $"Pass {passNumber}: unhandled {ex.GetType().Name} while inspecting {Path.GetFileName(item.ImagePath)}: {ex.Message} (full trace: {EngineerDebugFileName})");
            AppendEngineerDebug(result.OutputFolder, $"Unhandled exception in pass {passNumber}, image {item.ImagePath}:{Environment.NewLine}{ex}");
            result.FailReasons.Add(FailReasonUnhandledException);
            return false;
        }
    }

    /// <summary>Post-pass bookkeeping: every-image-failed circuit, trend sampling/gating, progress. Returns false to stop.</summary>
    private static bool AfterPass(SoakRunContext context, CancellationToken cancellationToken)
    {
        var result = context.Result;
        if (result.WasCanceled || result.FailReasons.Count > 0 || result.Passes.Count == 0 || cancellationToken.IsCancellationRequested)
            return !result.WasCanceled && result.FailReasons.Count == 0;

        var lastPass = result.Passes[^1];
        if (lastPass.ImagesProcessed > 0 && lastPass.ErrorCount == lastPass.ImagesProcessed)
        {
            result.FailReasons.Add(FailReasonEveryImageFailed);
            return false;
        }

        if (context.TrendSamples.Count >= MinimumTrendSamples && context.Clock.Elapsed >= context.TrendWarmup)
        {
            var trend = EvaluateMemoryTrend(
                context.TrendSamples,
                context.Options.MemorySlopeFailMegabytesPerHour,
                context.Options.MemoryGrowthFailFloorMegabytes);
            if (trend.Exceeded)
            {
                result.MemoryTrend = trend;
                AddError(result, trend.Description);
                result.FailReasons.Add(FailReasonMemoryGrowthTrend);
                return false;
            }
        }

        var remaining = context.Options.Duration - context.Clock.Elapsed;
        context.Progress?.Report(new BatchSoakProgress(
            lastPass.PassNumber,
            context.Clock.Elapsed,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            result.TotalImagesProcessed,
            result.TotalErrorCount,
            lastPass.WorkingSetMegabytes,
            FormattableString.Invariant(
                $"Pass {lastPass.PassNumber}: images={lastPass.ImagesProcessed}, errors={lastPass.ErrorCount}, avg={lastPass.AverageInspectionMilliseconds:F0} ms, managed={lastPass.ManagedMemoryMegabytes:F1} MB, handles={lastPass.HandleCount}.")));
        return true;
    }

    private static async Task<bool> DelayBetweenPassesAsync(SoakRunContext context, CancellationToken cancellationToken)
    {
        if (context.Options.DelayBetweenPasses <= TimeSpan.Zero || context.Clock.Elapsed >= context.Options.Duration)
            return true;

        try
        {
            await Task.Delay(context.Options.DelayBetweenPasses, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(context.Result, "Cancellation requested during the delay between passes.");
            return false;
        }
    }

    private static void RecordPass(
        SoakRunContext context,
        List<BatchTestRow> rows,
        int passNumber,
        DateTime passStartedUtc,
        TimeSpan passDuration)
    {
        var result = context.Result;
        var options = context.Options;
        var okCount = rows.Count(row => string.Equals(row.EngineResult, "OK", StringComparison.OrdinalIgnoreCase));
        var ngCount = rows.Count(row => string.Equals(row.EngineResult, "NG", StringComparison.OrdinalIgnoreCase));
        var errorCount = rows.Count(row => string.Equals(row.FailureCategory, "ERROR", StringComparison.OrdinalIgnoreCase));
        var reviewCount = Math.Max(0, rows.Count(row => string.Equals(row.EngineResult, "REVIEW", StringComparison.OrdinalIgnoreCase)) - errorCount);
        var timings = rows.Select(row => row.TotalInspectionMilliseconds).Where(value => value > 0).ToArray();
        if (string.IsNullOrWhiteSpace(result.ThresholdProfileId))
        {
            result.ThresholdProfileId = rows.Select(row => row.ThresholdProfileId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;
            result.ThresholdProfileRevision = rows.Select(row => row.ThresholdProfileRevision).FirstOrDefault(revision => !string.IsNullOrWhiteSpace(revision)) ?? string.Empty;
        }

        var batchRunId = PersistBatchRun(context, rows, passNumber);

        // Full collection before sampling so the trend series reflects objects that
        // survive GC (a leak signal) instead of collector timing noise.
        var managedMb = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: true));
        context.Process.Refresh();
        var workingSetMb = BytesToMegabytes(context.Process.WorkingSet64);

        result.TotalPasses = passNumber;
        result.TotalImagesProcessed += rows.Count;
        result.TotalOkCount += okCount;
        result.TotalNgCount += ngCount;
        result.TotalReviewCount += reviewCount;
        result.TotalErrorCount += errorCount;
        result.CountOverOneSecond += timings.Count(value => value > 1000.0);
        result.PeakManagedMemoryMegabytes = Math.Max(result.PeakManagedMemoryMegabytes, managedMb);
        result.PeakWorkingSetMegabytes = Math.Max(result.PeakWorkingSetMegabytes, workingSetMb);
        result.PeakHandleCount = Math.Max(result.PeakHandleCount, context.Process.HandleCount);
        context.PassDurations.Add(passDuration.TotalMilliseconds);
        context.TrendSamples.Add(new BatchSoakMemoryTrendSample(context.Clock.Elapsed.TotalHours, managedMb));

        result.Passes.Add(new BatchSoakPassRecord(
            passNumber,
            passStartedUtc,
            passDuration.TotalMilliseconds,
            rows.Count,
            okCount,
            ngCount,
            reviewCount,
            errorCount,
            timings.Length > 0 ? timings.Average() : 0,
            timings.Length > 0 ? timings.Max() : 0,
            timings.Count(value => value > 1000.0),
            managedMb,
            workingSetMb,
            context.Process.HandleCount,
            context.Process.Threads.Count,
            DatabaseSizeMegabytes(result),
            batchRunId,
            rows.Where(row => string.Equals(row.FailureCategory, "ERROR", StringComparison.OrdinalIgnoreCase))
                .Select(row => row.DefectType)
                .FirstOrDefault() ?? string.Empty));
    }

    private static long? PersistBatchRun(SoakRunContext context, List<BatchTestRow> rows, int passNumber)
    {
        if (!context.Options.PersistBatchRuns || !context.PersistenceHealthy)
            return null;

        try
        {
            var metrics = BatchValidationService.CalculateMetrics(rows);
            return AoiDatabase.RecordBatchTestRun(
                context.Options.ImageFolder,
                context.Options.ManifestPath ?? string.Empty,
                context.Engine.Name,
                context.Engine.Version,
                metrics.Accuracy,
                metrics.Precision,
                metrics.Recall,
                metrics.FalseCallRate,
                rows.Select(row => row.ToRecord()).ToArray(),
                context.Result.ThresholdProfileId,
                context.Result.ThresholdProfileRevision);
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException)
        {
            // A persistence failure is a recorded stability signal, not a run abort:
            // the soak keeps producing evidence, and persistence stays disabled so an
            // 8-hour run does not spam the same failure every pass.
            context.PersistenceHealthy = false;
            AddError(context.Result, $"Pass {passNumber}: SQLite batch-run persistence failed and is disabled for the rest of the run: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    private static void FinalizeResult(SoakRunContext context)
    {
        var result = context.Result;
        result.CompletedAtUtc = DateTime.UtcNow;
        result.ActualDuration = context.Clock.Elapsed;
        result.EndManagedMemoryMegabytes = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: true));
        context.Process.Refresh();
        result.EndWorkingSetMegabytes = BytesToMegabytes(context.Process.WorkingSet64);
        result.EndHandleCount = context.Process.HandleCount;
        result.PeakManagedMemoryMegabytes = Math.Max(result.PeakManagedMemoryMegabytes, result.EndManagedMemoryMegabytes);
        result.PeakWorkingSetMegabytes = Math.Max(result.PeakWorkingSetMegabytes, result.EndWorkingSetMegabytes);
        result.PeakHandleCount = Math.Max(result.PeakHandleCount, result.EndHandleCount);
        result.EndDatabaseSizeMegabytes = DatabaseSizeMegabytes(result);

        if (context.ImageTimings.Count > 0)
        {
            result.AverageInspectionMilliseconds = context.ImageTimings.Average();
            result.MaxInspectionMilliseconds = context.ImageTimings.Max();
            result.P95InspectionMilliseconds = SoakTestService.Percentile(context.ImageTimings, 0.95);
        }
        if (context.PassDurations.Count > 0)
        {
            result.AveragePassMilliseconds = context.PassDurations.Average();
            result.MaxPassMilliseconds = context.PassDurations.Max();
        }

        EvaluateFinalTrend(context);
        CaptureAlarmEvents(result);
    }

    private static void EvaluateFinalTrend(SoakRunContext context)
    {
        var result = context.Result;
        if (result.FailReasons.Contains(FailReasonMemoryGrowthTrend, StringComparer.Ordinal))
            return;

        var trend = EvaluateMemoryTrend(
            context.TrendSamples,
            context.Options.MemorySlopeFailMegabytesPerHour,
            context.Options.MemoryGrowthFailFloorMegabytes);
        if (context.Clock.Elapsed < context.TrendWarmup)
        {
            // Same warm-up gate as the in-run check: a short run (e.g. --max-passes) spans
            // minutes, so an MB/hour slope extrapolated from it is amplified noise in either
            // direction. The annotation is applied whether or not the slope happened to look
            // bad, because reporting a bare "within bounds" for a run too short to evaluate
            // overstates the evidence exactly as much as failing on it would. A single GC
            // inside the sampled window is enough to swing the slope by tens of thousands of
            // MB/h when the window is milliseconds wide.
            trend = trend with
            {
                Exceeded = false,
                Description = trend.Description + " Informational only: the run ended before the memory-trend warm-up gate, so this is not evaluated as a failure condition.",
            };
        }

        result.MemoryTrend = trend;
        if (trend.Exceeded)
        {
            AddError(result, trend.Description);
            result.FailReasons.Add(FailReasonMemoryGrowthTrend);
        }
    }

    private static void CaptureAlarmEvents(BatchSoakResult result)
    {
        try
        {
            var newAlarms = AlarmEventService
                .GetEvents(new AlarmEventQuery { OperatorVisibleOnly = false, SortOrder = AlarmSortOrder.NewestFirst })
                .Where(alarm => alarm.TimestampUtc >= result.StartedAtUtc)
                .ToArray();
            result.NewAlarmEventCount = newAlarms.Length;
            result.ActiveCriticalAlarmCountAtEnd = AlarmEventService.GetActiveCriticalAlarms().Count;
            result.AlarmSummaries.AddRange(newAlarms.Take(50).Select(alarm => alarm.Summary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DbException)
        {
            AddError(result, $"Alarm-event capture failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static void ObserveAbandonedFault(string outputFolder, Task abandonedTask, string imagePath)
    {
        // Best-effort: observe a late fault of the abandoned analysis so the hang's root
        // cause is not lost as an unobserved task exception. May race process teardown.
        _ = abandonedTask.ContinueWith(
            task => AppendEngineerDebug(
                outputFolder,
                $"Abandoned stuck inspection of {imagePath} later faulted:{Environment.NewLine}{task.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void AppendEngineerDebug(string outputFolder, string text)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            File.AppendAllText(
                Path.Combine(outputFolder, EngineerDebugFileName),
                FormattableString.Invariant($"[{DateTime.UtcNow:O}] {text}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Batch soak engineer-debug write failed: {ex.Message}");
        }
    }

    private static string[] EnumerateImages(string imageFolder)
        => Directory.EnumerateFiles(imageFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FingerprintFileList(string[] imageFiles)
    {
        var sb = new StringBuilder();
        foreach (var path in imageFiles)
        {
            long length = 0;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                Trace.WriteLine($"Batch soak dataset fingerprint skipped length of {path}: {ex.Message}");
            }

            sb.Append(Path.GetFileName(path)).Append('|').Append(length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static double DatabaseSizeMegabytes(BatchSoakResult? result)
    {
        double total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = AoiDatabase.DatabasePath + suffix;
            try
            {
                if (File.Exists(path))
                    total += new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // WAL checkpoints can delete sidecars between the existence check and the
                // length read; a missed sample must not abort the soak.
                if (result is not null)
                    AddError(result, $"Database size sampling skipped {Path.GetFileName(path)}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        return total / 1024.0 / 1024.0;
    }

    private static void MarkCanceled(BatchSoakResult result, string reason)
    {
        result.WasCanceled = true;
        if (string.IsNullOrWhiteSpace(result.CancellationReason))
            result.CancellationReason = reason;
    }

    private static void AddError(BatchSoakResult result, string message)
    {
        if (result.Errors.Count < MaxStoredErrors)
            result.Errors.Add(message);
    }

    private static double BytesToMegabytes(long bytes)
        => bytes / 1024.0 / 1024.0;

    private sealed class SoakRunContext
    {
        public SoakRunContext(
            BatchSoakOptions options,
            IInspectionEngine engine,
            BatchSoakResult result,
            Process process,
            IProgress<BatchSoakProgress>? progress)
        {
            Options = options;
            Engine = engine;
            Result = result;
            Process = process;
            Progress = progress;
            TrendWarmup = options.MemoryTrendWarmup ?? TimeSpan.FromTicks(options.Duration.Ticks / 4);
        }

        public BatchSoakOptions Options { get; }
        public IInspectionEngine Engine { get; }
        public BatchSoakResult Result { get; }
        public Process Process { get; }
        public IProgress<BatchSoakProgress>? Progress { get; }
        public TimeSpan TrendWarmup { get; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public List<BatchSoakMemoryTrendSample> TrendSamples { get; } = new();
        public List<double> ImageTimings { get; } = new();
        public List<double> PassDurations { get; } = new();
        public bool PersistenceHealthy { get; set; } = true;
    }
}
