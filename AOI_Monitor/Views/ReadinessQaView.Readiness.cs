using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;
using static AOI_Monitor.Views.ReportsView;

namespace AOI_Monitor.Views;

public partial class ReadinessQaView
{
    private async Task LoadReadinessAsync(CancellationToken cancellationToken)
    {
        var filter = BuildFilter();
        var pilotIssueFilter = BuildPilotIssueFilter();
        var acceptanceProfile = SelectedFactoryAcceptanceProfile();
        var managementFilter = BuildManagementDashboardFilter();
        StatusText.Text = "Loading readiness and quality-gate evidence...";

        var snapshot = await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspections = AoiDatabase.GetInspectionHistory(filter).Select(InspectionLogRow.FromRecord).ToArray();
            var reviews = AoiDatabase.GetReviewEvents(filter).Select(ReviewLogRow.FromRecord).ToArray();
            var audits = AoiDatabase.GetAuditEvents(filter).Select(AuditLogRow.FromRecord).ToArray();
            var pilotIssues = AoiDatabase.GetPilotIssues(pilotIssueFilter).Select(PilotIssueRow.FromIssue).ToArray();
            var readinessReport = await FactoryReadinessService.EvaluateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var readiness = readinessReport.Categories.Select(FactoryReadinessRow.FromCategory).ToArray();
            var stage1Readiness = Stage1ReadinessGateService.Evaluate();
            var standardsReport = StandardsTraceabilityService.Evaluate();
            var standardsRows = standardsReport.Items.ToArray();
            var completionReport = await CompletionAssessmentService.AssessAsync(cancellationToken).ConfigureAwait(false);
            var completionRows = completionReport.Categories.Select(CompletionMatrixRow.FromCategory).ToArray();
            var checklist = FactoryAcceptanceChecklistService.Generate(acceptanceProfile).Items;
            var issueSummary = PilotIssueService.Summarize();
            var buildEvidence = BuildTestEvidenceService.GetSummary();
            var managementReport = await ManagementDashboardService.BuildAsync(managementFilter, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ReadinessLoadSnapshot(
                inspections,
                reviews,
                audits,
                pilotIssues,
                readiness,
                readinessReport.OverallStatus,
                stage1Readiness,
                standardsRows,
                StandardsTraceabilitySummaryFor(standardsReport),
                completionRows,
                completionReport.OverallPercent,
                checklist,
                issueSummary,
                BuildEvidenceSummaryTextFor(buildEvidence),
                managementReport);
        }, cancellationToken);

        ReplaceRows(_inspectionRows, snapshot.Inspections);
        ReplaceRows(_reviewRows, snapshot.Reviews);
        ReplaceRows(_auditRows, snapshot.Audits);
        ReplaceRows(_pilotIssueRows, snapshot.PilotIssues);
        ReplaceRows(_factoryReadinessRows, snapshot.Readiness);
        ApplyStage1Readiness(snapshot.Stage1Readiness);
        ReplaceRows(_standardsTraceabilityRows, snapshot.StandardsTraceabilityRows);
        ReplaceRows(_completionMatrixRows, snapshot.CompletionRows);
        if (_factoryAcceptanceRows.Count == 0)
            ReplaceRows(_factoryAcceptanceRows, snapshot.FactoryAcceptanceRows);

        PilotIssueSummaryText.Text = $"Issues total={snapshot.IssueSummary.Total}; open={snapshot.IssueSummary.Open}; critical open={snapshot.IssueSummary.CriticalOpen}.";
        ReadinessSummaryText.Text = $"{snapshot.PilotIssues.Length} pilot issues / factory readiness {snapshot.ReadinessOverallStatus} / Stage 1 {snapshot.Stage1Readiness.OverallStatus} / evidence completion {snapshot.CompletionOverallPercent:F1}%";
        StandardsTraceabilitySummaryText.Text = snapshot.StandardsTraceabilitySummary;
        CompletionMatrixSummaryText.Text = $"Overall evidence completion {snapshot.CompletionOverallPercent:F1}% across {snapshot.CompletionRows.Length} readiness areas.";
        BuildEvidenceSummaryText.Text = snapshot.BuildEvidenceSummary;
        UpdateClientDemoGateText(ClientDemoReadinessGateService.Evaluate());
        StatusText.Text = "Loaded readiness and quality-gate evidence from local records.";
        ApplyManagementDashboard(snapshot.ManagementDashboardReport);
    }

    private void OnGenerateFactoryAcceptanceChecklistClick(object sender, RoutedEventArgs e)
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(SelectedFactoryAcceptanceProfile());
        ReplaceRows(_factoryAcceptanceRows, checklist.Items);
        StatusText.Text = $"Generated factory acceptance checklist for {checklist.ProfileDisplayName}.";
    }

    private void OnClientDemoReadinessClick(object sender, RoutedEventArgs e)
    {
        var report = ClientDemoReadinessGateService.Evaluate();
        UpdateClientDemoGateText(report);
        MessageBox.Show(
            $"Client demo readiness: {report.OverallStatus}\n\nBlocking: {report.BlockingIssues.Count}\nWarnings: {report.Warnings.Count}\n\n{string.Join("\n", report.Checks.Select(check => $"{check.Name}: {check.Status}").Take(10))}",
            "Client Demo Readiness",
            MessageBoxButton.OK,
            report.OverallStatus == ClientDemoGateStatus.Blocked ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnExportClientDemoReadinessClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting client demo readiness gate", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = ClientDemoReadinessGateService.ExportReport();
            var report = ClientDemoReadinessGateService.Evaluate();
            UpdateClientDemoGateText(report);
            WorkflowState.Instance.AddEvent("CLIENT_DEMO_GATE_EXPORT", $"Client demo readiness gate exported: {Path.GetFileName(result.Folder)}.");
            RefreshAfterExport($"Client demo readiness gate exported: {result.Folder}. Status: {report.OverallStatus}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Client demo readiness export failed", ex, "CLIENT_DEMO_GATE_EXPORT_ERROR");
        }
    }

    private void UpdateClientDemoGateText(ClientDemoReadinessGateReport report)
    {
        ClientDemoGateText.Text = $"Client demo readiness: {report.OverallStatus}; blocking={report.BlockingIssues.Count}; warnings={report.Warnings.Count}";
        ClientDemoGateText.Foreground = report.OverallStatus switch
        {
            ClientDemoGateStatus.Pass => Brushes.LightGreen,
            ClientDemoGateStatus.Blocked => Brushes.LightCoral,
            _ => Brushes.Orange,
        };
        ClientDemoGateText.ToolTip = string.Join(Environment.NewLine, report.Checks.Select(check => $"{check.Name}: {check.Status} - {check.Evidence}"));
    }

    private void ApplyStage1Readiness(Stage1ReadinessReport report)
    {
        _latestStage1ReadinessReport = report;
        ReplaceRows(_stage1ReadinessRows, report.Checks.Select(Stage1ReadinessRow.FromCheck));
        Stage1ReadinessStatusText.Text = report.OverallStatus;
        Stage1ReadinessStatusText.Foreground = report.OverallStatus switch
        {
            Stage1ReadinessGateService.Pass => Brushes.LightGreen,
            Stage1ReadinessGateService.Fail => Brushes.LightCoral,
            _ => Brushes.Orange,
        };
        Stage1ReadinessSummaryText.Text =
            $"Stage 1 readiness {report.OverallStatus}; checks={report.Checks.Count}; missing or conditional={report.MissingEvidence.Count}; " +
            $"images={report.TotalImages}; false calls={report.FalseCallCount}; possible escapes={report.PossibleEscapeCount}; over1s={report.OverOneSecondCount}.";
        Stage1PreflightText.Text = report.LatestPreflightStatus;
        Stage1BatchText.Text = report.LatestBatchRunSummary;
        Stage1BenchmarkText.Text = report.LatestBenchmarkSummary;
        Stage1PackageText.Text = string.IsNullOrWhiteSpace(report.LatestValidationPackagePath)
            ? "No Stage 1 validation package exported."
            : report.LatestValidationPackagePath;
        Stage1PackageText.ToolTip = Stage1PackageText.Text;
        Stage1MissingEvidenceList.Items.Clear();
        foreach (var item in report.MissingEvidence.Take(40))
            Stage1MissingEvidenceList.Items.Add(item);
        if (Stage1MissingEvidenceList.Items.Count == 0)
            Stage1MissingEvidenceList.Items.Add("None.");
        Stage1NextActionText.Text = report.NextRecommendedAction;
    }

    private async void OnRefreshStage1ReadinessClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Refresh Stage 1 readiness",
            "Readiness & QA",
            async token =>
            {
                var report = await Task.Run(() => Stage1ReadinessGateService.Evaluate(), token);
                token.ThrowIfCancellationRequested();
                ApplyStage1Readiness(report);
                StatusText.Text = $"Stage 1 readiness refreshed: {report.OverallStatus}. Next: {report.NextRecommendedAction}";
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message =>
            {
                Stage1ReadinessSummaryText.Text = "Stage 1 readiness refresh failed safely. Review diagnostics.";
                StatusText.Text = message;
            });
    }

    private void OnExportStage1ReadinessReportClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting Stage 1 readiness report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var export = Stage1ReadinessGateService.ExportReport();
            ApplyStage1Readiness(export.Report);
            WorkflowState.Instance.AddEvent("STAGE1_READINESS_EXPORT", $"Stage 1 readiness report exported: {export.Report.OverallStatus}; folder={Path.GetFileName(export.Folder)}.", relatedPath: export.Folder);
            RefreshAfterExport($"Stage 1 readiness report exported: {export.Folder}. HTML: {export.HtmlPath}; PDF: {export.PdfPath}; JSON: {export.JsonPath}. Status: {export.Report.OverallStatus}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Stage 1 readiness report export failed", ex, "STAGE1_READINESS_EXPORT_ERROR");
        }
    }

    private void OnOpenSampleDatasetGuideClick(object sender, RoutedEventArgs e)
    {
        var guide = Path.Combine(FindRepositoryRootForDocs(), "Docs", "VALIDATION.md");
        OpenPathOrWarn(guide, "Sample Dataset Guide");
    }

    private void OnOpenLatestValidationPackageClick(object sender, RoutedEventArgs e)
    {
        var path = _latestStage1ReadinessReport?.LatestValidationPackagePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            path = AoiDatabase.GetValidationPackages(1).FirstOrDefault()?.PackagePath ?? string.Empty;
        OpenPathOrWarn(path, "Latest Stage 1 Validation Package");
    }

    private void OnOpenLatestBenchmarkReportClick(object sender, RoutedEventArgs e)
    {
        var latest = BenchmarkInspectionService.GetLatestBenchmark();
        var path = latest is null
            ? string.Empty
            : File.Exists(latest.HtmlPath) ? latest.HtmlPath : latest.ReportFolder;
        OpenPathOrWarn(path, "Latest Benchmark Report");
    }

    private async void OnRefreshStandardsTraceabilityClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Refresh standards traceability",
            "Standards & Quality Checklist",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                var report = await Task.Run(() => StandardsTraceabilityService.Evaluate(), token);
                ReplaceRows(_standardsTraceabilityRows, report.Items);
                StandardsTraceabilitySummaryText.Text = StandardsTraceabilitySummaryFor(report);
                StatusText.Text = "Standards traceability matrix refreshed.";
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message =>
            {
                StandardsTraceabilitySummaryText.Text = "Standards traceability refresh failed safely. Review diagnostics.";
                StatusText.Text = message;
            });
    }

    private void OnExportStandardsTraceabilityClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting standards traceability matrix", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var export = StandardsTraceabilityService.ExportReport();
            var report = StandardsTraceabilityService.Evaluate();
            ReplaceRows(_standardsTraceabilityRows, report.Items);
            StandardsTraceabilitySummaryText.Text = StandardsTraceabilitySummaryFor(report);
            WorkflowState.Instance.AddEvent("STANDARDS_TRACEABILITY_EXPORT", $"Standards traceability matrix exported: {Path.GetFileName(export.Folder)}.", relatedPath: export.Folder);
            RefreshAfterExport($"Standards traceability matrix exported. HTML: {export.HtmlPath}; PDF: {export.PdfPath}; JSON: {export.JsonPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Standards traceability export failed", ex, "STANDARDS_TRACEABILITY_EXPORT_ERROR");
        }
    }

    private async void OnRefreshCompletionMatrixClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Refresh completion matrix",
            "Completion Matrix",
            async token =>
            {
                var completionReport = await CompletionAssessmentService.AssessAsync(token);
                ReplaceRows(_completionMatrixRows, completionReport.Categories.Select(CompletionMatrixRow.FromCategory));
                CompletionMatrixSummaryText.Text = $"Overall evidence completion {completionReport.OverallPercent:F1}% across {completionReport.Categories.Count} readiness areas.";
                StatusText.Text = "Completion matrix refreshed from evidence records.";
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message =>
            {
                CompletionMatrixSummaryText.Text = "Completion matrix refresh failed safely. Review the diagnostic report.";
                StatusText.Text = message;
            });
    }

    private void OnRefreshManagementDashboardClick(object sender, RoutedEventArgs e)
    {
        _ = LoadManagementDashboardAsync();
        StatusText.Text = "Management dashboard refreshed from local SQLite.";
    }

    private async void OnExportManagementDashboardClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting management dashboard", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var report = _managementDashboardReport ?? await ManagementDashboardService.BuildAsync(BuildManagementDashboardFilter());
        var dialog = new OpenFolderDialog
        {
            Title = "Select management dashboard export folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        try
        {
            var result = ManagementDashboardService.Export(report, dialog.FolderName, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("MANAGEMENT_DASHBOARD_EXPORT", $"Management dashboard exported: {Path.GetFileName(result.Folder)}.", relatedPath: result.Folder);
            RefreshAfterExport($"Management dashboard exported. HTML: {result.HtmlPath}; CSV: {result.CsvPath}; PDF: {result.PdfPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Management dashboard export failed", ex, "EXPORT_ERROR");
        }
    }

    private void OnPilotIssueFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _ = RefreshAsync(CancellationToken.None);
    }

    private void OnRefreshPilotIssuesClick(object sender, RoutedEventArgs e)
    {
        _ = RefreshAsync(CancellationToken.None);
        StatusText.Text = "Pilot issues refreshed.";
    }

    private void OnCreateFalseCallIssueClick(object sender, RoutedEventArgs e)
        => CreateIssueFromSelectedInspection(PilotIssueCategory.FalseCall, "High", "False call captured from selected inspection row.");

    private void OnCreatePossibleEscapeIssueClick(object sender, RoutedEventArgs e)
        => CreateIssueFromSelectedInspection(PilotIssueCategory.PossibleEscape, "Critical", "Possible escape captured from selected inspection row.");

    private void CreateIssueFromSelectedInspection(PilotIssueCategory category, string severity, string notes)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Creating pilot issue", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (PilotSourceInspectionGrid.SelectedItem is not InspectionLogRow row)
        {
            MessageBox.Show("Select a row in the filtered inspection list first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var issue = PilotIssueService.Create(new PilotIssue
        {
            Category = category,
            Severity = severity,
            BoardModel = row.BoardProgram,
            LotId = string.Empty,
            ImagePath = row.SampleImagePath,
            RelatedInspectionId = row.Id.ToString(CultureInfo.InvariantCulture),
            Owner = WorkflowState.Instance.OperatorWithRole,
            Notes = $"{notes} Verdict={row.Verdict}; defect={row.SuggestedDefect}; score={row.ScoreDisplay}.",
        }, WorkflowState.Instance.OperatorWithRole);
        _ = RefreshAsync(CancellationToken.None);
        StatusText.Text = $"Pilot issue created: {issue.IssueId}.";
    }

    private void OnClosePilotIssueClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Closing pilot issue", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (PilotIssuesGrid.SelectedItem is not PilotIssueRow row)
        {
            MessageBox.Show("Select a pilot issue first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PilotIssueService.Close(row.IssueId, "Closed from Readiness & QA after engineering review.", WorkflowState.Instance.OperatorWithRole);
        _ = RefreshAsync(CancellationToken.None);
        StatusText.Text = $"Pilot issue closed: {row.IssueId}.";
    }

    private void OnExportPilotIssueReportClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting pilot issue report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select pilot issue report export folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        try
        {
            var result = PilotIssueService.Export(new PilotIssueExportOptions
            {
                OutputRoot = dialog.FolderName,
                RedactImagePaths = true,
                Filter = BuildPilotIssueFilter(),
            }, WorkflowState.Instance.OperatorWithRole);
            RefreshAfterExport($"Pilot issue report exported. HTML: {result.HtmlPath}; JSON: {result.JsonPath}; CSV: {result.CsvPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Pilot issue report export failed", ex, "PILOT_ISSUE_EXPORT_ERROR");
        }
    }

    private async Task LoadManagementDashboardAsync()
    {
        try
        {
            ApplyManagementDashboard(await ManagementDashboardService.BuildAsync(BuildManagementDashboardFilter()));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ManagementDashboardSummaryText.Text = $"Management dashboard unavailable: {ex.Message}";
        }
    }

    private void ApplyManagementDashboard(ManagementDashboardReport report)
    {
        _managementDashboardReport = report;
        ReplaceRows(_managementDefectRows, report.TopDefectClasses);
        ReplaceRows(_managementRoiRows, report.TopRoiRefdesContributors);
        ReplaceRows(_managementTrendRows, report.ModelVersionTrend);
        ReplaceRows(_managementBreakdownRows, report.LotModelBreakdown);
        ManagementDashboardSummaryText.Text =
            $"Boards={report.TotalBoardsInspected:N0}; OK/NG/REVIEW={report.OkCount:N0}/{report.NgCount:N0}/{report.ReviewCount:N0}; " +
            $"false-call={report.FalseCallRate:P1}; escapes={report.PossibleEscapeCount:N0}; review burden={report.ManualReviewBurdenMinutes:F1} min; " +
            $"avg/p95={report.AverageInspectionTimeMs:F0}/{report.P95InspectionTimeMs:F0} ms; readiness={report.AcceptanceReadinessStatus}; " +
            $"MES={report.MesSyncStatus}; central={report.CentralSyncStatus}";
    }
}
