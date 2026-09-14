using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;
using static AOI_Monitor.Views.ReportsView;

namespace AOI_Monitor.Views;

public partial class ReadinessQaView
{
    private async void OnExportCustomerPackageClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var gateReport = ClientDemoReadinessGateService.Evaluate();
        if (!EnsureClientDemoGateAllowsExport(gateReport, allowMissingStage1PackageForStage1Export: true))
            return;
        var gateWarnings = gateReport.Checks
            .Where(check => check.Status != ClientDemoGateStatus.Pass)
            .Select(check => $"Client demo readiness gate {check.Status}: {check.Name}: {check.Evidence}")
            .ToArray();

        if (!ConfirmExport("Create a Stage 1 customer-demo evidence package from the current filtered logs and latest validation run?"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select Stage 1 customer package output folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginWork("Creating customer package...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        var inspectionRows = _inspectionRows.ToArray();
        var reviewRows = _reviewRows.ToArray();
        var auditRows = _auditRows.ToArray();
        var filter = BuildFilter();

        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var warnings = new List<string>();
                warnings.AddRange(gateWarnings);
                var packageDir = Path.Combine(dialog.FolderName, $"stage1_customer_package_{DateTime.Now:yyyyMMdd_HHmmss}");
                var validationDir = Path.Combine(packageDir, "validation");
                var logsDir = Path.Combine(packageDir, "logs");
                var overlayDir = Path.Combine(packageDir, "annotated_overlays");
                var summariesDir = Path.Combine(packageDir, "summaries");

                Directory.CreateDirectory(packageDir);
                Directory.CreateDirectory(validationDir);
                Directory.CreateDirectory(logsDir);
                Directory.CreateDirectory(overlayDir);
                Directory.CreateDirectory(summariesDir);

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(1, 6, "Loading latest validation results..."));
                var latestRun = AoiDatabase.GetLatestBatchTestRun();
                var validationRows = latestRun is null
                    ? Array.Empty<BatchTestRow>()
                    : AoiDatabase.GetBatchTestResults(latestRun.Id).Select(BatchTestRow.FromRecord).ToArray();

                if (latestRun is null)
                    warnings.Add("No Stage 1 validation batch run was found. validation/customer_validation_report.html and validation/validation_results.csv were generated with no validation rows.");
                else if (validationRows.Length == 0)
                    warnings.Add($"Latest Stage 1 validation run {latestRun.Id} has no persisted result rows.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(2, 6, "Writing validation artifacts..."));
                File.WriteAllText(Path.Combine(validationDir, "validation_results.csv"), BatchValidationService.BuildResultsCsv(validationRows), CsvEncoding);

                if (inspectionRows.Length == 0)
                    warnings.Add("No inspection history rows matched the current filters. logs/inspection_history.csv contains only headers.");
                if (reviewRows.Length == 0)
                    warnings.Add("No review/disposition rows matched the current filters. logs/review_disposition_log.csv contains only headers.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(3, 6, "Writing log CSV files..."));
                File.WriteAllText(Path.Combine(logsDir, "inspection_history.csv"), BuildInspectionCsv(inspectionRows), CsvEncoding);
                File.WriteAllText(Path.Combine(logsDir, "review_disposition_log.csv"), BuildReviewCsv(reviewRows), CsvEncoding);
                File.WriteAllText(Path.Combine(logsDir, "audit_trail.csv"), BuildAuditCsv(auditRows), CsvEncoding);
                if (auditRows.Length == 0)
                    warnings.Add("No audit trail rows matched the current filters. logs/audit_trail.csv contains only headers.");

                var overlays = ExportAnnotatedOverlays(
                    inspectionRows.Where(r => File.Exists(r.SampleImagePath)).ToArray(),
                    overlayDir,
                    cts.Token,
                    progress,
                    completedOffset: 3,
                    totalOffset: 6);
                warnings.AddRange(overlays.Errors);
                if (overlays.Count == 0)
                    warnings.Add("No annotated overlay images were generated. This usually means no filtered inspection rows had accessible sample image paths.");

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(4, 6, "Preparing validation report sample images..."));
                var sampleImageDir = Path.Combine(validationDir, "sample_annotated_images");
                var sampleImages = ValidationReportAssetService.ExportSampleAnnotatedImages(
                    validationRows,
                    sampleImageDir,
                    "sample_annotated_images",
                    maxCount: 8,
                    cts.Token);
                warnings.AddRange(sampleImages.Warnings);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(5, 6, "Writing configuration and database summaries..."));
                var configuration = InspectionModelConfigurationService.Load();
                File.WriteAllText(Path.Combine(summariesDir, "model_engine_configuration.txt"), BuildModelConfigurationSummary(configuration, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "database_health_summary.txt"), BuildDatabaseHealthSummary(inspectionRows.Length, reviewRows.Length, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "recipe_revision_summary.txt"), BuildRecipeRevisionSummary(warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "calibration_profile_summary.txt"), BuildCalibrationProfileSummary(warnings), CsvEncoding);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(6, 6, "Writing package README..."));
                var reportPath = Path.Combine(validationDir, "customer_validation_report.html");
                var reportPdfPath = Path.Combine(validationDir, "customer_validation_report.pdf");
                var reportContext = BuildCustomerValidationReportContext(latestRun, validationRows, sampleImages.Images, warnings);
                File.WriteAllText(reportPath, CustomerValidationReportService.BuildHtml(reportContext), CsvEncoding);
                PdfExportService.ExportHtmlFileToPdf(reportPath, reportPdfPath, "Customer Validation Report");
                File.WriteAllText(Path.Combine(validationDir, "customer_validation_report.md"), CustomerValidationReportService.BuildMarkdown(reportContext), CsvEncoding);
                File.WriteAllText(
                    Path.Combine(validationDir, "customer_validation_report_print_to_pdf.txt"),
                    CustomerValidationReportService.BuildPrintToPdfInstructions(reportPath),
                    CsvEncoding);
                File.WriteAllText(
                    Path.Combine(packageDir, "README.md"),
                    BuildStage1PackageReadme(packageDir, latestRun, validationRows.Length, overlays.Count, inspectionRows.Length, reviewRows.Length, auditRows.Length, warnings, filter),
                    CsvEncoding);
                File.WriteAllText(Path.Combine(packageDir, "warnings.txt"), BuildWarningsText(warnings), CsvEncoding);
                WritePackageManifest(packageDir, warnings);

                return new PackageOutcome(packageDir, reportPath, overlays.Count, warnings);
            }, cts.Token);

            var packageVerification = ExportVerificationService.RecordVerifiedExport(
                "Stage1CustomerPackage",
                result.PackageDir,
                result.Warnings.Count == 0 ? "OK" : "WARN");
            var reportVerification = ExportVerificationService.RecordVerifiedExport(
                "CustomerValidationHtmlReport",
                result.ReportPath,
                result.Warnings.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 customer package exported: {Path.GetFileName(result.PackageDir)}, overlays={result.OverlayCount}, warnings={result.Warnings.Count}.");
            LogErrors("EXPORT_WARNING", result.Warnings);
            PackagePathText.Text = $"Latest customer package: {result.PackageDir}";
            RefreshAfterExport($"Stage 1 customer package exported: {result.PackageDir}. Warnings: {result.Warnings.Count}. Package verification: {packageVerification.Verification.Status}; report verification: {reportVerification.Verification.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stage 1 customer package export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Stage 1 customer package export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Stage 1 customer package export failed", ex, "EXPORT_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnExportFactoryReadinessPackageClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting factory readiness Go/No-Go package", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmExport("Export a management/customer Factory Readiness Go/No-Go package?"))
            return;

        try
        {
            var gateReport = ClientDemoReadinessGateService.Evaluate();
            if (!EnsureClientDemoGateAllowsExport(gateReport, allowMissingStage1PackageForStage1Export: false))
                return;
            var result = FactoryReadinessService.ExportGoNoGoPackage();
            WorkflowState.Instance.AddEvent("FACTORY_READINESS_EXPORT", $"Factory readiness package exported: {Path.GetFileName(result.PackageFolder)}.");
            RefreshAfterExport($"Factory readiness package exported: {result.PackageFolder}. Summary: {result.SummaryHtmlPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Factory readiness package export failed", ex, "FACTORY_READINESS_EXPORT_ERROR");
        }
    }

    private bool EnsureClientDemoGateAllowsExport(ClientDemoReadinessGateReport report, bool allowMissingStage1PackageForStage1Export)
    {
        UpdateClientDemoGateText(report);
        var blocking = report.Checks
            .Where(check => check.Status == ClientDemoGateStatus.Blocked)
            .Where(check => !(allowMissingStage1PackageForStage1Export && check.Name == "Stage 1 package"))
            .ToArray();
        if (blocking.Length > 0)
        {
            MessageBox.Show(
                $"Client demo readiness is BLOCKED.\n\n{string.Join("\n", blocking.Select(check => $"- {check.Name}: {check.Evidence}"))}",
                "Client Demo Readiness Gate",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            WorkflowState.Instance.AddEvent("CLIENT_DEMO_GATE_BLOCKED", $"Client-facing export blocked: {string.Join("; ", blocking.Select(check => check.Name))}.");
            return false;
        }

        if (report.OverallStatus == ClientDemoGateStatus.Pass)
            return true;

        var warningText = string.Join("\n", report.Checks
            .Where(check => check.Status != ClientDemoGateStatus.Pass)
            .Select(check => $"- {check.Name}: {check.Status}; {check.Evidence}"));
        var proceed = MessageBox.Show(
            $"Client demo readiness has warnings.\n\n{warningText}\n\nProceed and include these warnings in the client-facing package?",
            "Client Demo Readiness Gate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.Yes)
            return false;

        WorkflowState.Instance.AddEvent("CLIENT_DEMO_GATE_WARNING", $"Client-facing export proceeded with gate status {report.OverallStatus}.");
        return true;
    }

    private void OnExportFactoryAcceptanceChecklistClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting factory acceptance checklist", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmExport("Export a client-facing Factory Acceptance Checklist package?"))
            return;

        try
        {
            var profile = SelectedFactoryAcceptanceProfile();
            var result = FactoryAcceptanceChecklistService.Export(profile, EnsureExportsDir());
            var checklist = FactoryAcceptanceChecklistService.Generate(profile);
            ReplaceRows(_factoryAcceptanceRows, checklist.Items);
            WorkflowState.Instance.AddEvent("FACTORY_ACCEPTANCE_EXPORT", $"Factory acceptance checklist exported: {Path.GetFileName(result.Folder)}.");
            RefreshAfterExport($"Factory acceptance checklist exported. HTML: {result.HtmlPath}. JSON: {result.JsonPath}. CSV: {result.CsvPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Factory acceptance checklist export failed", ex, "FACTORY_ACCEPTANCE_EXPORT_ERROR");
        }
    }

    private void OnImportBuildTestEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Importing build/test evidence", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select build/test evidence JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var evidence = BuildTestEvidenceService.ImportEvidence(dialog.FileName, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("BUILD_TEST_EVIDENCE", $"Build/test evidence imported: {Path.GetFileName(evidence.EvidencePath)}.");
            _ = RefreshAsync(CancellationToken.None);
            StatusText.Text = $"Build/test evidence imported: {evidence.EvidencePath}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            HandleWorkError("Build/test evidence import failed", ex, "BUILD_TEST_EVIDENCE_ERROR");
        }
    }

    private void OnOpenBuildEvidenceFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BuildTestEvidenceService.EvidenceFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildTestEvidenceService.EvidenceFolder,
                UseShellExecute = true,
            });
            StatusText.Text = $"Opened build/test evidence folder: {BuildTestEvidenceService.EvidenceFolder}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Open build/test evidence folder failed", ex, "BUILD_TEST_EVIDENCE_ERROR");
        }
    }

    private static void OpenPathOrWarn(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show(
                $"{title} is not available yet.\n\nExpected path: {path}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(
                $"{title} could not be opened:\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string FindRepositoryRootForDocs()
    {
        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "Docs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? "(not configured)" : value;

    private static string BuildEvidenceSummaryTextFor(BuildTestEvidenceSummary summary)
    {
        if (summary.Latest is null)
            return "Build/test evidence: not imported";

        var latest = summary.Latest;
        var commit = string.IsNullOrWhiteSpace(latest.GitCommit) ? "unknown" : ShortHash(latest.GitCommit);
        return $"Build/test evidence: {summary.Status} | {latest.Configuration} | commit {commit} | hygiene {latest.HygieneStatus}, restore {latest.RestoreStatus}, build {latest.BuildStatus}, test {latest.TestStatus}, publish {latest.PublishValidationStatus}";
    }

    private static string StandardsTraceabilitySummaryFor(StandardsTraceabilityReport report)
        => $"Standards traceability: {report.OverallStatus}; satisfied={report.SatisfiedCount}; partial={report.PartialCount}; missing={report.MissingCount}; not applicable={report.NotApplicableCount}; missing release blockers={report.ReleaseBlockerMissingCount}. Standards-aligned evidence only; not certification.";

    private static CustomerValidationReportContext BuildCustomerValidationReportContext(
        BatchTestRunRecord? run,
        IReadOnlyCollection<BatchTestRow> rows,
        IReadOnlyList<ReportImageReference> sampleImages,
        IReadOnlyList<string> warnings)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var state = WorkflowState.Instance;
        var boardModel = CustomerValidationReportService.SummarizeDistinct(rows.Select(row => row.BoardModel));
        if (string.Equals(boardModel, "Not provided", StringComparison.OrdinalIgnoreCase))
            boardModel = state.BoardProgram;

        var timestamp = run?.CreatedAtUtc ?? DateTime.Now;
        if (timestamp.Kind == DateTimeKind.Utc)
            timestamp = timestamp.ToLocalTime();

        return new CustomerValidationReportContext
        {
            StationId = state.StationId,
            UserId = state.CurrentUser.UserId,
            UserRole = state.CurrentRole.ToString(),
            RunId = run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture),
            TestTimestamp = timestamp,
            BoardModel = boardModel,
            LotId = CustomerValidationReportService.SummarizeDistinct(rows.Select(row => row.LotId)),
            EngineName = run?.EngineName ?? (configuration.IsOnnxSelected ? "ONNX ML Model" : "Pixel Difference Prototype Engine"),
            ModelVersion = run?.ModelVersion ?? configuration.EffectiveModelVersion,
            ModelFileName = string.IsNullOrWhiteSpace(configuration.ModelFilePath)
                ? "Not configured"
                : Path.GetFileName(configuration.ModelFilePath),
            ConfidenceThreshold = configuration.ConfidenceThreshold,
            DatasetFolder = string.IsNullOrWhiteSpace(run?.ImageFolder) ? "Not available" : run.ImageFolder,
            GroundTruthFile = string.IsNullOrWhiteSpace(run?.GroundTruthCsvPath) ? "Not selected" : run.GroundTruthCsvPath,
            Metrics = BatchValidationService.CalculateMetrics(rows),
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(rows),
            Rows = rows.ToArray(),
            SampleAnnotatedImages = sampleImages,
            Warnings = warnings.ToArray(),
            DatasetQualitySummary = DatasetQualityService.Analyze(rows),
            CameraAcceptanceSummary = CameraAcceptanceTestService.ToSummary(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true)),
            RobotAcceptanceSummary = RobotAcceptanceTestService.ToSummary(AoiDatabase.GetLatestRobotAcceptanceRun()),
            MesReadinessSummary = MesSpoolService.EvaluateReadiness(),
        };
    }

    private static string BuildModelConfigurationSummary(InspectionModelConfiguration configuration, ICollection<string> warnings)
    {
        var status = InspectionModelConfigurationService.GetStatusText();
        if (configuration.IsLearnedVisualModelSelected)
            warnings.Add("Inspection engine is the Learned PCB Visual Model. Evidence is image-only Stage 1 learning, not live camera validation or production acceptance.");
        else if (!configuration.IsOnnxSelected)
            warnings.Add("Inspection engine is the deterministic Pixel Difference Prototype Engine, not a trained production ML model.");
        if (configuration.IsOnnxSelected && !configuration.HasModelFile)
            warnings.Add($"ONNX engine is selected, but the configured model file is missing: {configuration.ModelFilePath}");
        if (!string.IsNullOrWhiteSpace(configuration.LabelMapPath) && !File.Exists(configuration.LabelMapPath))
            warnings.Add($"Configured label-map file is missing: {configuration.LabelMapPath}");

        var sb = new StringBuilder();
        sb.AppendLine("Model / Engine Configuration Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"EngineKey: {configuration.SelectedEngineKey}");
        sb.AppendLine($"EngineStatus: {status}");
        sb.AppendLine($"ModelVersion: {configuration.EffectiveModelVersion}");
        sb.AppendLine($"ModelFilePath: {NullIfEmpty(configuration.ModelFilePath)}");
        sb.AppendLine($"ModelFileExists: {configuration.HasModelFile}");
        sb.AppendLine($"InputImageWidth: {configuration.InputImageWidth}");
        sb.AppendLine($"InputImageHeight: {configuration.InputImageHeight}");
        sb.AppendLine($"InputTensorName: {NullIfEmpty(configuration.InputTensorName)}");
        sb.AppendLine($"OutputTensorName: {NullIfEmpty(configuration.OutputTensorName)}");
        sb.AppendLine($"ConfidenceThreshold: {configuration.ConfidenceThreshold.ToString("F3", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"LabelMapPath: {NullIfEmpty(configuration.LabelMapPath)}");
        sb.AppendLine($"LabelMapFileExists: {!string.IsNullOrWhiteSpace(configuration.LabelMapPath) && File.Exists(configuration.LabelMapPath)}");
        sb.AppendLine("OutputParser: Generic Detection [class,confidence,x,y,width,height]");
        sb.AppendLine("BuiltInLabelMap:");
        foreach (var label in configuration.BuiltInLabelMap.OrderBy(kvp => kvp.Key))
            sb.AppendLine($"  {label.Key}: {label.Value}");
        sb.AppendLine();
        sb.AppendLine("PrototypeNotice: Stage 1 is a local PoC. The default Pixel Difference Prototype Engine is deterministic evidence generation. Learned PCB Visual Model evidence is image-only learning. ONNX ML Model inference is claimed only when a configured local model loads and inference succeeds.");
        return sb.ToString();
    }

    private static string BuildDatabaseHealthSummary(int visibleInspectionRows, int visibleReviewRows, ICollection<string> warnings)
    {
        var integrity = AoiDatabase.RunIntegrityCheck();
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"SQLite integrity check returned '{integrity}'.");

        var sb = new StringBuilder();
        sb.AppendLine("Database Health Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"DatabasePath: {AoiDatabase.DatabasePath}");
        sb.AppendLine($"ImageVaultPath: {AoiDatabase.ImageVaultPath}");
        sb.AppendLine($"SQLiteIntegrityCheck: {integrity}");
        sb.AppendLine($"VisibleInspectionRowsInPackage: {visibleInspectionRows}");
        sb.AppendLine($"VisibleReviewRowsInPackage: {visibleReviewRows}");
        sb.AppendLine($"AutoArchivePolicy: {DescribeRetentionPolicy()}");
        sb.AppendLine();
        sb.AppendLine("Table Counts:");
        foreach (var row in AoiDatabase.GetDatabaseHealthRows())
            sb.AppendLine($"- {row.Table}: {row.Count} ({row.Status})");
        return sb.ToString();
    }

    private static string BuildRecipeRevisionSummary(ICollection<string> warnings)
    {
        var boardProgram = WorkflowState.Instance.BoardProgram;
        var revision = AoiDatabase.GetLatestRecipeRevision(boardProgram);
        if (revision is null)
            warnings.Add($"No recipe revision was found for board program '{boardProgram}'. summaries/recipe_revision_summary.txt was generated with no revision details.");

        var sb = new StringBuilder();
        sb.AppendLine("Recipe Revision Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"BoardProgram: {boardProgram}");
        sb.AppendLine($"RecipeRevisionAvailable: {revision is not null}");

        if (revision is null)
        {
            sb.AppendLine("Status: No persisted recipe revision is available for the active board program.");
            sb.AppendLine("PrototypeNotice: Stage 1 can run the operator workflow without a saved production recipe; customer packages record that condition as a warning.");
            return sb.ToString();
        }

        sb.AppendLine($"RecipeName: {revision.RecipeName}");
        sb.AppendLine($"Revision: {revision.Revision}");
        sb.AppendLine($"OperatorId: {revision.OperatorId}");
        sb.AppendLine($"DetectionPriority: {revision.DetectionPriority}");
        sb.AppendLine($"BackgroundImagePath: {NullIfEmpty(revision.BackgroundImagePath)}");
        sb.AppendLine($"CreatedUtc: {revision.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");

        try
        {
            var document = System.Text.Json.JsonSerializer.Deserialize<RecipeDocument>(revision.RecipeJson);
            sb.AppendLine($"RoiCount: {document?.Rois.Count ?? 0}");
            if (document is not null)
            {
                foreach (var roi in document.Rois.Take(50))
                {
                    sb.AppendLine(string.Join(" | ",
                        $"ROI={roi.Id}",
                        $"Type={roi.RoiType}",
                        $"X={roi.X:F4}",
                        $"Y={roi.Y:F4}",
                        $"W={roi.Width:F4}",
                        $"H={roi.Height:F4}",
                        $"Threshold={roi.AiScoreThreshold:F3}",
                        $"HeightMin={roi.HeightMin:F3}",
                        $"HeightMax={roi.HeightMax:F3}",
                        $"VolumeMin={roi.VolumeMin:F3}",
                        $"VolumeMax={roi.VolumeMax:F3}"));
                }

                if (document.Rois.Count > 50)
                    sb.AppendLine($"RoiListTruncated: true; shown=50; total={document.Rois.Count}");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            warnings.Add($"Latest recipe revision {revision.Id} could not be parsed for ROI details: {ex.Message}");
            sb.AppendLine($"RoiParseStatus: Failed - {ex.Message}");
        }

        sb.AppendLine("PrototypeNotice: Recipe data reflects the local Stage 1 recipe editor and SQLite revision history.");
        return sb.ToString();
    }

    private static string BuildCalibrationProfileSummary(ICollection<string> warnings)
    {
        var profiles = AoiDatabase.GetCalibrationProfiles();
        if (profiles.Count == 0)
            warnings.Add("No 2D calibration profile was found. summaries/calibration_profile_summary.txt was generated with no profile details.");

        var sb = new StringBuilder();
        sb.AppendLine("2D Calibration Profile Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"ProfileCount: {profiles.Count}");
        sb.AppendLine("PrototypeNotice: Calibration profiles are approximate 2D image-to-board mapping data for Stage 2 planning. They are not live camera calibration, robot calibration, or production coordinate validation.");
        sb.AppendLine();

        if (profiles.Count == 0)
        {
            sb.AppendLine("Status: No saved calibration profiles are available.");
            return sb.ToString();
        }

        foreach (var profile in profiles.Take(20))
        {
            sb.AppendLine($"ProfileId: {profile.Id}");
            sb.AppendLine($"ProfileName: {profile.ProfileName}");
            sb.AppendLine($"BoardModel: {profile.BoardModel}");
            sb.AppendLine($"ViewType: {profile.ViewType}");
            sb.AppendLine($"OperatorId: {profile.OperatorId}");
            sb.AppendLine($"CreatedUtc: {profile.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"SampleImagePath: {NullIfEmpty(profile.SampleImagePath)}");
            sb.AppendLine($"PointCount: {profile.PointCount}");
            sb.AppendLine($"HasApproximateTransform: {profile.HasTransform}");
            sb.AppendLine($"Transform: {profile.TransformSummary}");

            foreach (var point in profile.Points.Take(25))
            {
                sb.AppendLine(string.Join(" | ",
                    $"PointId={point.Id}",
                    $"ImageX={point.ImageX:F3}",
                    $"ImageY={point.ImageY:F3}",
                    $"BoardXmm={point.BoardXMillimeters:F3}",
                    $"BoardYmm={point.BoardYMillimeters:F3}"));
            }

            if (profile.Points.Count > 25)
                sb.AppendLine($"PointListTruncated: true; shown=25; total={profile.Points.Count}");

            sb.AppendLine();
        }

        if (profiles.Count > 20)
            sb.AppendLine($"ProfileListTruncated: true; shown=20; total={profiles.Count}");

        return sb.ToString();
    }

    private static string BuildStage1PackageReadme(
        string packageDir,
        BatchTestRunRecord? run,
        int validationRows,
        int overlayCount,
        int inspectionRows,
        int reviewRows,
        int auditRows,
        IReadOnlyList<string> warnings,
        LogFilter filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stage 1 Customer Demo Evidence Package");
        sb.AppendLine();
        sb.AppendLine("This folder contains the current Stage 1 PoC evidence exported from AOI Monitor for customer review. It is safe to open outside the application.");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine("- `validation/customer_validation_report.html` - Customer-facing Stage 1 validation report with summary metrics, failed samples, sample annotated images, prototype limitations, and signature/approval section.");
        sb.AppendLine("- `validation/customer_validation_report.pdf` - Native PDF rendering of the customer validation report.");
        sb.AppendLine("- `validation/customer_validation_report_print_to_pdf.txt` - Fallback instructions for creating a PDF from the HTML report with a browser print workflow.");
        sb.AppendLine("- `validation/customer_validation_report.md` - Markdown companion copy of the validation report.");
        sb.AppendLine("- `validation/sample_annotated_images/` - Sample annotated images referenced by the validation report.");
        sb.AppendLine("- `validation/validation_results.csv` - Row-level validation results from the latest persisted Stage 1 validation run.");
        sb.AppendLine("- `annotated_overlays/` - Generated PNG overlays for filtered inspection rows with accessible sample images.");
        sb.AppendLine("- `logs/inspection_history.csv` - Filtered SQLite inspection history.");
        sb.AppendLine("- `logs/review_disposition_log.csv` - Filtered review and disposition event log.");
        sb.AppendLine("- `logs/audit_trail.csv` - Filtered QC audit trail with UTC/local timestamps, user, role, station, action type, detail, and related IDs/paths.");
        sb.AppendLine("- `summaries/model_engine_configuration.txt` - Active model/engine configuration and prototype status.");
        sb.AppendLine("- `summaries/database_health_summary.txt` - SQLite health, table counts, and archive policy summary.");
        sb.AppendLine("- `summaries/recipe_revision_summary.txt` - Latest local recipe revision for the active board program, when available.");
        sb.AppendLine("- `summaries/calibration_profile_summary.txt` - Saved 2D calibration profiles and approximate image-to-board transform details for Stage 2 preparation.");
        sb.AppendLine("- `warnings.txt` - Missing optional items or non-blocking export issues.");
        sb.AppendLine();
        sb.AppendLine("## Package Summary");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Generated by: {WorkflowState.Instance.OperatorWithRole}");
        sb.AppendLine($"- Package path: `{packageDir}`");
        sb.AppendLine($"- Validation run: {(run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"- Validation rows: {validationRows}");
        sb.AppendLine($"- Annotated overlays: {overlayCount}");
        sb.AppendLine($"- Inspection history rows: {inspectionRows}");
        sb.AppendLine($"- Review/disposition rows: {reviewRows}");
        sb.AppendLine($"- Audit trail rows: {auditRows}");
        sb.AppendLine($"- Warnings: {warnings.Count}");
        sb.AppendLine();
        sb.AppendLine("## Applied Log Filters");
        sb.AppendLine();
        sb.AppendLine($"- From date: {filter.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- To date: {filter.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- Board/model: {filter.BoardProgram ?? "ALL"}");
        sb.AppendLine($"- Operator: {filter.OperatorId ?? "ALL"}");
        sb.AppendLine($"- Result: {filter.Result ?? "ALL"}");
        sb.AppendLine($"- User role: {filter.UserRole ?? "ALL"}");
        sb.AppendLine($"- Audit action type: {filter.ActionCategory ?? "ALL"}");
        sb.AppendLine();
        sb.AppendLine("## Prototype / Planned Scope");
        sb.AppendLine();
        sb.AppendLine("Implemented in Stage 1: local operator/review workflow, deterministic prototype inspection engine, SQLite persistence, image import/library support, approximate 2D calibration profile planning data, batch validation evidence, annotated overlay export, role-based local access controls, and customer package generation.");
        sb.AppendLine();
        sb.AppendLine("Planned for later stages: Stage 2 Planned Hardware Integration for live AOI camera, real 3D camera acquisition, and lighting control; Stage 3 Planned Robot Integration for PLC/robot/handler control; Stage 4 Planned MES/ERP Integration for authentication and traceability; production database integration; and trained ML inference unless separately configured and verified.");
        sb.AppendLine();
        sb.AppendLine("Missing optional inputs, such as absent validation runs or inaccessible image paths, are recorded in `warnings.txt` and do not prevent package creation.");
        return sb.ToString();
    }

    private static void WritePackageManifest(string packageDir, IReadOnlyList<string> warnings)
    {
        var manifestPath = Path.Combine(packageDir, "package_manifest.json");
        // Written twice on purpose: the first write creates the manifest file so the second
        // write's directory enumeration can list the manifest itself among the package files.
        WritePackageManifestFile(packageDir, manifestPath, warnings);
        WritePackageManifestFile(packageDir, manifestPath, warnings);
    }

    private static void WritePackageManifestFile(string packageDir, string manifestPath, IReadOnlyList<string> warnings)
    {
        var manifest = new
        {
            schemaVersion = "stage1-customer-package/v1",
            packageId = Path.GetFileName(packageDir),
            generatedAtUtc = DateTime.UtcNow,
            generatedBy = WorkflowState.Instance.OperatorWithRole,
            includedFiles = Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(packageDir, path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new ValidationIncludedFile
                {
                    RelativePath = Path.GetRelativePath(packageDir, path).Replace('\\', '/'),
                    FileType = ClassifyPackageFile(packageDir, path),
                    Bytes = new FileInfo(path).Length,
                })
                .ToList(),
            warnings = warnings.ToArray(),
        };
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
            CsvEncoding);
    }

    private static string ClassifyPackageFile(string packageDir, string path)
    {
        var relative = Path.GetRelativePath(packageDir, path).Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return "CSV";
        if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "Annotated image";
        if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return "HTML report";
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return "PDF report";
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return "Markdown";
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "Manifest";
        if (relative.StartsWith("summaries/", StringComparison.OrdinalIgnoreCase))
            return "Summary";
        return "Package evidence";
    }

    private static string BuildWarningsText(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return "No warnings were recorded while creating this Stage 1 customer package." + Environment.NewLine;

        var sb = new StringBuilder();
        sb.AppendLine("Stage 1 Customer Package Warnings");
        sb.AppendLine();
        foreach (var warning in warnings)
            sb.AppendLine($"- {warning}");
        return sb.ToString();
    }
}
