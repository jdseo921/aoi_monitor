using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class Stage1ReadinessGateOptions
{
    public string RepositoryRoot { get; init; } = string.Empty;
    public string DatasetFolder { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public double P95FrameToOverlayTargetMs { get; init; } = 1000;
}

public sealed class Stage1ReadinessCheck
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = Stage1ReadinessGateService.Fail;
    public string Evidence { get; init; } = string.Empty;
    public string NextAction { get; init; } = string.Empty;
}

public sealed class Stage1ReadinessReport
{
    public string SchemaVersion { get; init; } = "stage1-readiness-gate/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string AppVersion { get; init; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public string OverallStatus { get; set; } = Stage1ReadinessGateService.Fail;
    public string DatasetFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string LatestPreflightStatus { get; set; } = "NOT RUN";
    public string LatestBatchRunSummary { get; set; } = "No Stage 1 batch validation run found.";
    public string LatestBenchmarkSummary { get; set; } = "No benchmark found.";
    public string LatestValidationPackagePath { get; set; } = string.Empty;
    public string NextRecommendedAction { get; set; } = "Run the Stage 1 validation workflow.";
    public int TotalImages { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int ReviewCount { get; set; }
    public int FalseCallCount { get; set; }
    public int PossibleEscapeCount { get; set; }
    public double P95FrameToOverlayMs { get; set; }
    public int OverOneSecondCount { get; set; }
    public double ImagesPerMinute { get; set; }
    public List<string> MissingEvidence { get; set; } = new();
    public List<string> ReportsGenerated { get; set; } = new();
    public List<string> Limitations { get; set; } = CustomerValidationReportContext.DefaultPrototypeLimitations.ToList();
    public List<string> WhatRemainsForStage2 { get; set; } = new()
    {
        "Run real camera acceptance with non-simulated image frames.",
        "Validate lighting control and synchronization against real equipment.",
        "Keep robot, PLC safety, production MES, and full factory automation out of the Stage 1 PASS claim until their own evidence exists.",
    };
    public List<Stage1ReadinessCheck> Checks { get; set; } = new();
}

public sealed record Stage1ReadinessReportExport(
    string Folder,
    string HtmlPath,
    string PdfPath,
    string JsonPath,
    Stage1ReadinessReport Report);

public static class Stage1ReadinessGateService
{
    public const string Pass = "PASS";
    public const string Conditional = "CONDITIONAL";
    public const string Fail = "FAIL";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Stage1ReadinessReport Evaluate(Stage1ReadinessGateOptions? options = null)
    {
        options ??= new Stage1ReadinessGateOptions();
        AoiDatabase.Initialize();

        var report = new Stage1ReadinessReport();
        var repositoryRoot = FindRepositoryRoot(options.RepositoryRoot);
        var latestRun = AoiDatabase.GetLatestBatchTestRun();
        var rows = latestRun is null
            ? Array.Empty<BatchTestRow>()
            : AoiDatabase.GetBatchTestResults(latestRun.Id).Select(BatchTestRow.FromRecord).ToArray();
        var metrics = rows.Length == 0
            ? new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            : BatchValidationService.CalculateMetrics(rows);
        var latestPackage = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestBenchmark = BenchmarkInspectionService.GetLatestBenchmark();
        var buildEvidence = BuildTestEvidenceService.GetSummary();
        var effectivePaths = ResolveDatasetPaths(options, repositoryRoot, latestRun);

        report.DatasetFolder = effectivePaths.DatasetFolder;
        report.ManifestPath = effectivePaths.ManifestPath;
        report.TotalImages = rows.Length;
        report.OkCount = metrics.OkCount;
        report.NgCount = metrics.NgCount;
        report.ReviewCount = metrics.ReviewCount;
        report.FalseCallCount = metrics.FalseCall;
        report.PossibleEscapeCount = metrics.PossibleEscape;

        AddBuildEvidence(report.Checks, buildEvidence);
        AddDatasetEvidence(report.Checks, repositoryRoot, effectivePaths);
        AddManifestTemplate(report.Checks, repositoryRoot);
        AddPreflight(report, effectivePaths, latestPackage);
        AddBatchRun(report, latestRun);
        AddValidationRows(report, latestRun, rows);
        AddMetrics(report, rows, metrics);
        AddFalseCallEvidence(report, latestRun, rows, metrics);
        AddBenchmark(report, latestBenchmark, options.P95FrameToOverlayTargetMs);
        AddValidationPackage(report, latestPackage);
        AddExportVerification(report.Checks, latestPackage);
        AddCrashReports(report.Checks);
        AddPilotIssues(report.Checks);
        AddPrototypeWording(report.Checks, latestPackage);
        AddStage1HardwareScope(report.Checks);
        AddReportArtifacts(report, latestPackage, latestBenchmark);

        FinalizeReport(report);
        return report;
    }

    public static Stage1ReadinessReportExport ExportReport(string? outputRoot = null, Stage1ReadinessGateOptions? options = null)
    {
        var report = Evaluate(options);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "stage1_readiness")
            : outputRoot.Trim();
        var folder = EnsureUniqueFolder(Path.Combine(root, $"stage1_readiness_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(folder);

        var jsonPath = Path.Combine(folder, "stage1_readiness_report.json");
        var htmlPath = Path.Combine(folder, "stage1_readiness_report.html");
        var pdfPath = Path.Combine(folder, "stage1_readiness_report.pdf");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Stage 1 Readiness Report");

        ExportVerificationService.RecordVerifiedExport(
            "Stage1ReadinessReport",
            folder,
            report.OverallStatus == Fail ? "WARN" : "OK");
        AoiDatabase.RecordAuditEvent(
            "STAGE1_READINESS_EXPORT",
            $"Stage 1 readiness report exported: status={report.OverallStatus}; folder={folder}.",
            relatedEntityType: "Stage1ReadinessReport",
            relatedPath: folder);

        return new Stage1ReadinessReportExport(folder, htmlPath, pdfPath, jsonPath, report);
    }

    public static string BuildHtml(Stage1ReadinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Stage 1 Readiness Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:28px;color:#1d252c;line-height:1.45}h1{margin-bottom:4px}.status{font-size:24px;font-weight:800}.PASS{color:#176b3a}.CONDITIONAL{color:#8a5a00}.FAIL{color:#a12626}.notice{border-left:5px solid #d9951b;background:#fff8e8;padding:10px 12px;margin:16px 0}table{border-collapse:collapse;width:100%;margin:12px 0 22px}td,th{border:1px solid #c4cdd3;padding:8px;text-align:left;vertical-align:top}th{background:#edf2f5}.k{font-weight:700;background:#f7fafb;width:260px}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>AOI Monitor Stage 1 Readiness Report</h1>");
        sb.AppendLine($"<p class=\"status {Html(report.OverallStatus)}\">{Html(report.OverallStatus)}</p>");
        sb.AppendLine($"<p>Generated UTC: {report.GeneratedAtUtc:O}<br>Application version: {Html(report.AppVersion)}</p>");
        sb.AppendLine("<div class=\"notice\"><strong>Prototype boundary:</strong> This report evaluates Stage 1 customer validation readiness only. It does not claim real camera, lighting, robot, PLC safety, production MES, ERP, or full factory automation readiness.</div>");

        sb.AppendLine("<h2>What Was Tested</h2><table>");
        Row("Scope", "Generated sample data or selected customer image folder, dataset preflight, Stage 1 batch validation, row-level metrics, false-call and possible-escape evidence, performance benchmark, validation package, export verification, crash/critical issue status, and prototype wording.");
        Row("Data used", string.IsNullOrWhiteSpace(report.DatasetFolder) ? "Not available" : report.DatasetFolder);
        Row("Manifest", string.IsNullOrWhiteSpace(report.ManifestPath) ? "Not available" : report.ManifestPath);
        Row("Latest preflight", report.LatestPreflightStatus);
        Row("Latest batch run", report.LatestBatchRunSummary);
        Row("Latest benchmark", report.LatestBenchmarkSummary);
        Row("Latest validation package", string.IsNullOrWhiteSpace(report.LatestValidationPackagePath) ? "Not exported" : report.LatestValidationPackagePath);
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Client Metrics</h2><table>");
        Row("Images", report.TotalImages.ToString(CultureInfo.InvariantCulture));
        Row("OK / NG / REVIEW", $"{report.OkCount} / {report.NgCount} / {report.ReviewCount}");
        Row("False calls", report.FalseCallCount.ToString(CultureInfo.InvariantCulture));
        Row("Possible escapes", report.PossibleEscapeCount.ToString(CultureInfo.InvariantCulture));
        Row("P95 frame-to-overlay", FormatMilliseconds(report.P95FrameToOverlayMs));
        Row("Over one second", report.OverOneSecondCount.ToString(CultureInfo.InvariantCulture));
        Row("Images per minute", report.ImagesPerMinute <= 0 ? "Not available" : report.ImagesPerMinute.ToString("F1", CultureInfo.InvariantCulture));
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Gate Checks</h2><table><tr><th>Check</th><th>Status</th><th>Evidence</th><th>Next action</th></tr>");
        foreach (var check in report.Checks)
            sb.AppendLine($"<tr><td>{Html(check.Name)}</td><td class=\"{Html(check.Status)}\">{Html(check.Status)}</td><td>{Html(check.Evidence)}</td><td>{Html(check.NextAction)}</td></tr>");
        sb.AppendLine("</table>");

        AppendList(sb, "Missing Or Conditional Evidence", report.MissingEvidence, "None.");
        AppendList(sb, "Reports Generated Or Expected", report.ReportsGenerated, "No report artifacts were found.");
        AppendList(sb, "Limitations", report.Limitations, "No limitations were recorded.");
        AppendList(sb, "What Remains For Stage 2", report.WhatRemainsForStage2, "No Stage 2 actions were recorded.");
        sb.AppendLine("</body></html>");
        return sb.ToString();

        void Row(string key, string value)
            => sb.AppendLine($"<tr><td class=\"k\">{Html(key)}</td><td>{Html(value)}</td></tr>");
    }

    private static void AddBuildEvidence(ICollection<Stage1ReadinessCheck> checks, BuildTestEvidenceSummary summary)
    {
        if (summary.Latest is null)
        {
            checks.Add(Check(
                "App build/test evidence",
                Conditional,
                "No local build/test evidence has been recorded or imported.",
                "Run the Release build/tests and import or record build/test evidence when preparing formal client handoff."));
            return;
        }

        checks.Add(summary.IsPassing
            ? Check("App build/test evidence", Pass, $"Latest evidence PASS; configuration={summary.Latest.Configuration}; generated={summary.Latest.GeneratedAtUtc:O}.", "No action required.")
            : Check("App build/test evidence", Fail, string.Join(" ", summary.Messages), "Fix failing build/test evidence before Stage 1 handoff."));
    }

    private static void AddDatasetEvidence(ICollection<Stage1ReadinessCheck> checks, string repositoryRoot, DatasetPaths paths)
    {
        var demoRoot = Path.Combine(repositoryRoot, "SampleData", "DemoSet_Quick");
        var demoOk = Directory.Exists(Path.Combine(demoRoot, "images")) &&
            Directory.Exists(Path.Combine(demoRoot, "golden")) &&
            File.Exists(Path.Combine(demoRoot, "customer_validation_manifest.csv"));
        var selectedOk = Directory.Exists(paths.DatasetFolder) && File.Exists(paths.ManifestPath);

        if (demoOk || selectedOk)
        {
            checks.Add(Check(
                "Generated or selected dataset",
                Pass,
                demoOk
                    ? $"Generated demo dataset present: {demoRoot}."
                    : $"Selected or latest customer dataset present: {paths.DatasetFolder}.",
                "No action required."));
            return;
        }

        checks.Add(Check(
            "Generated or selected dataset",
            Fail,
            "No generated DemoSet_Quick dataset or selected/persisted customer dataset was found.",
            "Run SampleData/demo_dataset_generator.ps1 or select a customer dataset and manifest in AI / Models."));
    }

    private static void AddManifestTemplate(ICollection<Stage1ReadinessCheck> checks, string repositoryRoot)
    {
        var template = Path.Combine(repositoryRoot, "SampleData", "customer_validation_manifest_template.csv");
        checks.Add(File.Exists(template)
            ? Check("Manifest template", Pass, $"Manifest template present: {template}.", "No action required.")
            : Check("Manifest template", Fail, $"Manifest template missing: {template}.", "Restore SampleData/customer_validation_manifest_template.csv before client handoff."));
    }

    private static void AddPreflight(Stage1ReadinessReport report, DatasetPaths paths, ValidationPackageRecord? latestPackage)
    {
        if (Directory.Exists(paths.DatasetFolder) && File.Exists(paths.ManifestPath))
        {
            try
            {
                var preflight = CustomerDatasetPreflightService.Validate(paths.DatasetFolder, paths.ManifestPath);
                report.LatestPreflightStatus = $"{preflight.Status}; rows={preflight.ManifestRows}; failures={preflight.BlockingFailures.Count}; warnings={preflight.Warnings.Count}";
                report.Checks.Add(preflight.Status switch
                {
                    Pass => Check("Latest dataset preflight", Pass, report.LatestPreflightStatus, "No action required."),
                    Conditional => Check("Latest dataset preflight", Conditional, report.LatestPreflightStatus, "Review warnings before client handoff."),
                    _ => Check("Latest dataset preflight", Fail, report.LatestPreflightStatus, "Fix blocking preflight failures before Stage 1 validation."),
                });
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                report.LatestPreflightStatus = $"FAIL; preflight could not run: {ex.Message}";
                report.Checks.Add(Check("Latest dataset preflight", Fail, report.LatestPreflightStatus, "Fix dataset file access or manifest formatting."));
                return;
            }
        }

        var packagePreflight = ReadPackagePreflightStatus(latestPackage?.ManifestPath);
        if (!string.IsNullOrWhiteSpace(packagePreflight))
        {
            report.LatestPreflightStatus = packagePreflight;
            report.Checks.Add(Check(
                "Latest dataset preflight",
                packagePreflight.StartsWith(Pass, StringComparison.OrdinalIgnoreCase) ? Pass : Conditional,
                $"Latest package manifest records dataset preflight: {packagePreflight}.",
                "Rerun dataset preflight in AI / Models when preparing fresh evidence."));
            return;
        }

        report.Checks.Add(Check(
            "Latest dataset preflight",
            Fail,
            "No dataset and manifest were available to evaluate preflight status.",
            "Run dataset preflight from AI / Models."));
    }

    private static void AddBatchRun(Stage1ReadinessReport report, BatchTestRunRecord? latestRun)
    {
        if (latestRun is null)
        {
            report.Checks.Add(Check("Latest batch validation run", Fail, "No persisted Stage 1 batch validation run exists.", "Run Batch Inspection in AI / Models."));
            return;
        }

        report.LatestBatchRunSummary =
            $"Run {latestRun.Id}; images={latestRun.TotalImages}; failed={latestRun.FailedCount}; engine={latestRun.EngineName}/{latestRun.ModelVersion}; created={latestRun.CreatedAtUtc:O}.";
        report.Checks.Add(Check("Latest batch validation run", Pass, report.LatestBatchRunSummary, "No action required."));
    }

    private static void AddValidationRows(Stage1ReadinessReport report, BatchTestRunRecord? latestRun, IReadOnlyCollection<BatchTestRow> rows)
    {
        if (latestRun is null)
            return;

        if (rows.Count == 0)
        {
            report.Checks.Add(Check("Validation rows persisted", Fail, $"Run {latestRun.Id} has no persisted validation rows.", "Rerun batch validation and confirm database persistence."));
            return;
        }

        report.Checks.Add(rows.Count == latestRun.TotalImages
            ? Check("Validation rows persisted", Pass, $"{rows.Count} row(s) persisted for run {latestRun.Id}.", "No action required.")
            : Check("Validation rows persisted", Conditional, $"Run {latestRun.Id} expected {latestRun.TotalImages} row(s), loaded {rows.Count}.", "Review database persistence before handoff."));
    }

    private static void AddMetrics(Stage1ReadinessReport report, IReadOnlyCollection<BatchTestRow> rows, BatchMetrics metrics)
    {
        if (rows.Count == 0)
        {
            report.Checks.Add(Check("Metrics computed", Fail, "No rows are available for accuracy, false-call, or possible-escape metrics.", "Run batch validation with a labeled manifest."));
            return;
        }

        var knownRows = rows.Count - metrics.Unknown;
        var knownOk = rows.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "OK");
        var knownNg = rows.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG");
        if (knownRows <= 0 || knownOk == 0 || knownNg == 0)
        {
            report.Checks.Add(Check(
                "Metrics computed",
                Fail,
                $"Known rows={knownRows}; OK={knownOk}; NG={knownNg}. Accuracy/precision/recall/false-call/possible-escape review needs both OK and NG labels.",
                "Use a manifest with known OK and NG rows."));
            return;
        }

        report.Checks.Add(Check(
            "Metrics computed",
            Pass,
            $"Accuracy={metrics.Accuracy:P1}; precision={metrics.Precision:P1}; recall={metrics.Recall:P1}; false-call rate={metrics.FalseCallRate:P1}; known rows={knownRows}.",
            "No action required."));
    }

    private static void AddFalseCallEvidence(Stage1ReadinessReport report, BatchTestRunRecord? latestRun, IReadOnlyCollection<BatchTestRow> rows, BatchMetrics metrics)
    {
        if (rows.Count == 0)
            return;

        var knownOk = rows.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "OK");
        var knownNg = rows.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG");
        if (knownOk == 0 || knownNg == 0)
        {
            report.Checks.Add(Check(
                "False-call / possible-escape evidence",
                Fail,
                $"False-call and possible-escape evidence cannot be reviewed because OK={knownOk}, NG={knownNg}.",
                "Use a labeled manifest with both known-good and known-defect boards."));
            return;
        }

        var falseCallRun = AoiDatabase.GetLatestFalseCallReductionRun(latestRun?.Id);
        var tradeoff = falseCallRun is null
            ? "No threshold tradeoff run recorded; row-level counts are still available."
            : $"Threshold tradeoff run {falseCallRun.Id}; recommendation={falseCallRun.Recommendation.Status}.";
        report.Checks.Add(Check(
            "False-call / possible-escape evidence",
            Pass,
            $"False calls={metrics.FalseCall}; possible escapes={metrics.PossibleEscape}; known OK={knownOk}; known NG={knownNg}. {tradeoff}",
            falseCallRun is null ? "Optional: run Analyze Review Tradeoff in AI / Models for threshold evidence." : "No action required."));
    }

    private static void AddBenchmark(Stage1ReadinessReport report, BenchmarkInspectionResult? benchmark, double p95TargetMs)
    {
        if (benchmark is null)
        {
            report.Checks.Add(Check("Inspection performance benchmark", Fail, "No benchmark result is available.", "Run Readiness & QA > Performance Benchmark against the Stage 1 image folder."));
            return;
        }

        report.P95FrameToOverlayMs = benchmark.P95FrameToOverlayMs;
        report.OverOneSecondCount = benchmark.OverOneSecondCount;
        report.ImagesPerMinute = benchmark.ThroughputImagesPerMinute;
        var workload = string.IsNullOrWhiteSpace(benchmark.GoldenImagePath)
            ? "workload=no-reference (lighter path; supply a golden reference to benchmark the golden-compare workload)"
            : $"workload=golden-compare ({benchmark.GoldenImagePath})";
        report.LatestBenchmarkSummary =
            $"Benchmark {benchmark.RunId}; status={benchmark.Status}; count={benchmark.CompletedCount}; p50={benchmark.P50FrameToOverlayMs:F1} ms; p95={benchmark.P95FrameToOverlayMs:F1} ms; max={benchmark.MaxFrameToOverlayMs:F1} ms; over-threshold={benchmark.OverOneSecondCount} (threshold={benchmark.AcceptanceThresholdMs:F0} ms); cold-start={benchmark.ColdStartSampleCount} sample(s), max {benchmark.ColdStartMaxFrameToOverlayMs:F1} ms, over-threshold {benchmark.ColdStartOverThresholdCount}; {workload}; source={benchmark.SourceKind}; folder={benchmark.ReportFolder}.";

        if (benchmark.CompletedCount <= 0)
        {
            report.Checks.Add(Check("Inspection performance benchmark", Fail, report.LatestBenchmarkSummary, "Rerun benchmark with a readable image folder."));
            return;
        }

        if (benchmark.AcceptanceThresholdMs > p95TargetMs)
        {
            // A run measured against a looser threshold than this gate's target cannot
            // satisfy the gate: its over-threshold count was computed with that looser value.
            report.Checks.Add(Check("Inspection performance benchmark", Fail, report.LatestBenchmarkSummary, $"Benchmark acceptance threshold ({benchmark.AcceptanceThresholdMs:F0} ms) is looser than the gate target ({p95TargetMs:F0} ms). Rerun the benchmark with the default threshold."));
            return;
        }

        if (benchmark.OverOneSecondCount > 0 || benchmark.P95FrameToOverlayMs > p95TargetMs)
        {
            report.Checks.Add(Check("Inspection performance benchmark", Fail, report.LatestBenchmarkSummary, "Investigate timing and rerun benchmark before Stage 1 readiness PASS."));
            return;
        }

        report.Checks.Add(Check(
            "Inspection performance benchmark",
            Pass,
            report.LatestBenchmarkSummary,
            benchmark.ColdStartOverThresholdCount > 0
                ? "Steady-state timing passes; cold-start sample(s) exceeded the threshold — review the cold-start figures in the benchmark report."
                : "No action required."));
    }

    private static void AddValidationPackage(Stage1ReadinessReport report, ValidationPackageRecord? latestPackage)
    {
        if (latestPackage is null)
        {
            report.Checks.Add(Check("Validation package export", Fail, "No Stage 1 validation package record exists.", "Export Validation Package from AI / Models."));
            return;
        }

        report.LatestValidationPackagePath = latestPackage.PackagePath;
        var packageExists = Directory.Exists(latestPackage.PackagePath) && File.Exists(latestPackage.ManifestPath);
        if (!packageExists)
        {
            report.Checks.Add(Check(
                "Validation package export",
                Fail,
                $"Latest package record exists but files are missing: package={latestPackage.PackagePath}; manifest={latestPackage.ManifestPath}.",
                "Regenerate the Stage 1 validation package."));
            return;
        }

        var status = NormalizeStatus(latestPackage.AcceptanceStatus);
        report.Checks.Add(status switch
        {
            Pass => Check("Validation package export", Pass, $"Package {latestPackage.PackageId}; status={latestPackage.AcceptanceStatus}; folder={latestPackage.PackagePath}.", "No action required."),
            Conditional => Check("Validation package export", Conditional, $"Package {latestPackage.PackageId}; status={latestPackage.AcceptanceStatus}; folder={latestPackage.PackagePath}.", "Review package warnings before client handoff."),
            _ => Check("Validation package export", Fail, $"Package {latestPackage.PackageId}; status={latestPackage.AcceptanceStatus}; folder={latestPackage.PackagePath}.", "Resolve package acceptance failures and regenerate."),
        });
    }

    private static void AddExportVerification(ICollection<Stage1ReadinessCheck> checks, ValidationPackageRecord? latestPackage)
    {
        var verifications = AoiDatabase.GetExportVerifications(250);
        var errors = verifications.Where(record => record.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (errors.Length > 0)
        {
            checks.Add(Check("Export verification records", Fail, $"{errors.Length} export verification ERROR record(s) exist. Latest={errors[0].ExportType}: {errors[0].ExportPath}.", "Fix failing exports and rerun verification."));
            return;
        }

        if (latestPackage is null)
        {
            checks.Add(Check("Export verification records", Fail, "No validation package exists, so package export verification cannot be confirmed.", "Export a Stage 1 validation package."));
            return;
        }

        var packageVerification = verifications.FirstOrDefault(record =>
            record.ExportType.Contains("Stage1ValidationPackage", StringComparison.OrdinalIgnoreCase) ||
            record.ExportPath.Equals(latestPackage.PackagePath, StringComparison.OrdinalIgnoreCase));
        if (packageVerification is null)
        {
            checks.Add(Check("Export verification records", Fail, "No Stage1ValidationPackage export verification record exists for the latest package.", "Regenerate or verify the Stage 1 validation package."));
            return;
        }

        checks.Add(packageVerification.Status.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            ? Check("Export verification records", Conditional, $"Latest package verification is WARN; total verification records={verifications.Count}.", "Review warning details before handoff.")
            : Check("Export verification records", Pass, $"Latest package verification is {packageVerification.Status}; total verification records={verifications.Count}.", "No action required."));
    }

    private static void AddCrashReports(ICollection<Stage1ReadinessCheck> checks)
    {
        checks.Add(!string.IsNullOrWhiteSpace(CrashReportService.LastCrashReportPath) && File.Exists(CrashReportService.LastCrashReportPath)
            ? Check("Critical crash reports", Fail, $"Crash report exists for the current session: {CrashReportService.LastCrashReportPath}.", "Investigate the crash report, restart, and rerun readiness.")
            : Check("Critical crash reports", Pass, "No crash report is recorded for the current app session.", "No action required."));
    }

    private static void AddPilotIssues(ICollection<Stage1ReadinessCheck> checks)
    {
        var open = PilotIssueService.Search(new PilotIssueFilter { OpenOnly = true });
        var critical = open.Where(issue => PilotIssueService.IsCritical(issue.Severity)).ToArray();
        var high = open.Where(issue => PilotIssueService.IsHighOrCritical(issue.Severity) && !PilotIssueService.IsCritical(issue.Severity)).ToArray();
        if (critical.Length > 0)
        {
            checks.Add(Check("Open critical pilot issues", Fail, $"{critical.Length} open Critical pilot issue(s) exist.", "Close, fix, verify, or explicitly waive Critical issues before Stage 1 handoff."));
            return;
        }

        checks.Add(high.Length > 0
            ? Check("Open critical pilot issues", Conditional, $"No open Critical issues, but {high.Length} open High issue(s) need review.", "Review High issues before client handoff.")
            : Check("Open critical pilot issues", Pass, $"No open Critical pilot issues. Open issues={open.Count}.", "No action required."));
    }

    private static void AddPrototypeWording(ICollection<Stage1ReadinessCheck> checks, ValidationPackageRecord? latestPackage)
    {
        var defaultText = string.Join(" ", CustomerValidationReportContext.DefaultPrototypeLimitations);
        var defaultsOk = defaultText.Contains("Pixel Difference Prototype Engine", StringComparison.OrdinalIgnoreCase) &&
            defaultText.Contains("not a trained production ML model", StringComparison.OrdinalIgnoreCase) &&
            defaultText.Contains("Folder Camera Simulation", StringComparison.OrdinalIgnoreCase) &&
            defaultText.Contains("Live AOI camera", StringComparison.OrdinalIgnoreCase);
        var packageLimitations = ReadPackageLimitations(latestPackage?.ManifestPath);
        var packageOk = latestPackage is null ||
            packageLimitations.Any(item => item.Contains("camera", StringComparison.OrdinalIgnoreCase)) &&
            packageLimitations.Any(item => item.Contains("robot", StringComparison.OrdinalIgnoreCase) || item.Contains("MES", StringComparison.OrdinalIgnoreCase)) &&
            packageLimitations.Any(item => item.Contains("Pixel Difference Prototype Engine", StringComparison.OrdinalIgnoreCase));

        if (defaultsOk && packageOk)
        {
            checks.Add(Check(
                "Prototype/simulation wording",
                Pass,
                "Default report/package limitations preserve Pixel Difference Prototype Engine, Folder Camera Simulation, and not-validated hardware/MES wording.",
                "No action required."));
            return;
        }

        checks.Add(Check(
            "Prototype/simulation wording",
            Fail,
            "Prototype/simulation limitations are missing or incomplete in default wording or latest package manifest.",
            "Restore explicit Stage 1 prototype, simulation, and not-real-hardware limitations before export."));
    }

    private static void AddStage1HardwareScope(ICollection<Stage1ReadinessCheck> checks)
    {
        checks.Add(Check(
            "Stage 1 hardware scope",
            Pass,
            "Stage 1 PASS does not require real camera, lighting, robot, PLC safety, production MES, ERP, or full factory automation evidence.",
            "Keep Stage 2/3/4 items labeled not validated until real evidence exists."));
    }

    private static void AddReportArtifacts(Stage1ReadinessReport report, ValidationPackageRecord? latestPackage, BenchmarkInspectionResult? latestBenchmark)
    {
        if (latestPackage is not null)
        {
            AddIfExists(report.ReportsGenerated, latestPackage.PackagePath);
            AddIfExists(report.ReportsGenerated, latestPackage.ManifestPath);
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "validation_summary.html"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "validation_summary.pdf"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "customer_validation_report.html"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "customer_validation_report.pdf"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "validation_results.csv"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "dataset_preflight_summary.json"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "benchmark_results.csv"));
            AddIfExists(report.ReportsGenerated, Path.Combine(latestPackage.PackagePath, "limitations.txt"));
        }

        if (latestBenchmark is not null)
        {
            AddIfExists(report.ReportsGenerated, latestBenchmark.ReportFolder);
            AddIfExists(report.ReportsGenerated, latestBenchmark.HtmlPath);
            AddIfExists(report.ReportsGenerated, latestBenchmark.PdfPath);
            AddIfExists(report.ReportsGenerated, latestBenchmark.JsonPath);
            AddIfExists(report.ReportsGenerated, latestBenchmark.CsvPath);
        }
    }

    private static void FinalizeReport(Stage1ReadinessReport report)
    {
        report.OverallStatus = report.Checks.Any(check => check.Status == Fail)
            ? Fail
            : report.Checks.Any(check => check.Status == Conditional)
                ? Conditional
                : Pass;
        report.MissingEvidence = report.Checks
            .Where(check => check.Status != Pass)
            .Select(check => $"{check.Name}: {check.Status}. {check.Evidence} Next: {check.NextAction}")
            .ToList();
        report.NextRecommendedAction = report.Checks.FirstOrDefault(check => check.Status == Fail)?.NextAction ??
            report.Checks.FirstOrDefault(check => check.Status == Conditional)?.NextAction ??
            "Stage 1 evidence is complete for customer validation review. Keep prototype and not-real-hardware limitations visible.";
    }

    private static DatasetPaths ResolveDatasetPaths(Stage1ReadinessGateOptions options, string repositoryRoot, BatchTestRunRecord? latestRun)
    {
        if (!string.IsNullOrWhiteSpace(options.DatasetFolder) || !string.IsNullOrWhiteSpace(options.ManifestPath))
            return new DatasetPaths(options.DatasetFolder.Trim(), options.ManifestPath.Trim());

        if (latestRun is not null)
            return new DatasetPaths(latestRun.ImageFolder, latestRun.GroundTruthCsvPath ?? string.Empty);

        var demoRoot = Path.Combine(repositoryRoot, "SampleData", "DemoSet_Quick");
        return new DatasetPaths(demoRoot, Path.Combine(demoRoot, "customer_validation_manifest.csv"));
    }

    private static string FindRepositoryRoot(string explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")) ||
                    File.Exists(Path.Combine(directory.FullName, "SampleData", "customer_validation_manifest_template.csv")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string? ReadPackagePreflightStatus(string? manifestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.TryGetProperty("datasetPreflightStatus", out var status))
                return status.GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static IReadOnlyList<string> ReadPackageLimitations(string? manifestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return Array.Empty<string>();
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("limitations", out var limitations) ||
                limitations.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return limitations.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizeStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Fail;

        var normalized = status.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PASS" or "OK" or "READY" => Pass,
            "CONDITIONAL" or "WARN" or "WARNING" or "SIMULATION_ONLY" => Conditional,
            _ => Fail,
        };
    }

    private static Stage1ReadinessCheck Check(string name, string status, string evidence, string nextAction)
        => new()
        {
            Name = name,
            Status = status,
            Evidence = evidence,
            NextAction = nextAction,
        };

    private static void AddIfExists(ICollection<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            paths.Add(path);
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyList<string> values, string emptyText)
    {
        sb.AppendLine($"<h2>{Html(title)}</h2>");
        if (values.Count == 0)
        {
            sb.AppendLine($"<p>{Html(emptyText)}</p>");
            return;
        }

        sb.AppendLine("<ul>");
        foreach (var value in values)
            sb.AppendLine($"<li>{Html(value)}</li>");
        sb.AppendLine("</ul>");
    }

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

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "Not available" : value.ToString("F1", CultureInfo.InvariantCulture) + " ms";

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record DatasetPaths(string DatasetFolder, string ManifestPath);
}
