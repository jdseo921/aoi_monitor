using System.IO;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Data;
using AOI_Monitor.Services;
using static AOI_Monitor.Views.ReportsView;

namespace AOI_Monitor.Views;

public partial class ReadinessQaView
{
    private async void OnRunSoakTestClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanUseMaintenanceActions, "Running the local soak-test mode", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultEngine = InspectionModelConfigurationService.Load().SelectedEngineKey;
        var dialog = new SoakTestDialog(EnsureExportsDir(), defaultEngine)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || dialog.Options is null)
            return;

        var state = WorkflowState.Instance;
        var options = dialog.Options with
        {
            OperatorId = state.OperatorWithRole,
            BoardModel = state.BoardProgram,
            LotId = "SOAK-TEST",
        };

        var cts = BeginWork("Running soak test...");
        var progress = new Progress<SoakTestProgress>(p =>
        {
            var suffix = $" Max={p.MaxInspectionMilliseconds:F0} ms; memory peak={p.PeakWorkingSetMegabytes:F1} MB";
            if (!string.IsNullOrWhiteSpace(p.CancellationReason))
                suffix += $"; cancel={p.CancellationReason}";
            UpdateProgress(new WorkProgress(p.ElapsedSeconds, p.TotalSeconds, p.Message + suffix));
        });

        try
        {
            var result = await SoakTestService.RunAsync(options, progress, cts.Token);
            SoakTestService.Persist(result, state.OperatorWithRole);
            var reportPath = SoakTestService.WriteHtmlReport(result, options.OutputFolder);
            var jsonReportPath = SoakTestService.WriteJsonReport(result, options.OutputFolder);
            var csvReportPath = SoakTestService.WriteIterationsCsv(result, options.OutputFolder);
            var status = result.WasCanceled
                ? "CANCELED"
                : result.Errors.Count == 0 ? "OK" : "WARN";

            var verified = ExportVerificationService.RecordVerifiedExport("SoakTestReport", reportPath, status);
            ExportVerificationService.RecordVerifiedExport("SoakTestJsonReport", jsonReportPath, status);
            ExportVerificationService.RecordVerifiedExport("SoakTestIterationsCsv", csvReportPath, status);
            WorkflowState.Instance.AddEvent("SOAK_TEST", $"Soak test {status}: cycles={result.TotalCycles}, success={result.SuccessfulCycles}, failed={result.FailedCycles}, p95={result.P95InspectionMilliseconds:F0} ms, source={result.SourceKind}, report={Path.GetFileName(reportPath)}.");
            LogErrors("SOAK_TEST_ERROR", result.Errors);
            SoakReportPathText.Text = $"Latest soak-test report: {reportPath}";
            RefreshAfterExport($"Soak test {status}. Cycles={result.TotalCycles}, failed={result.FailedCycles}, p95={result.P95InspectionMilliseconds:F0} ms, cycle p95={result.P95TotalCycleMilliseconds:F0} ms, source={result.SourceKind}. HTML: {reportPath}. JSON: {jsonReportPath}. CSV: {csvReportPath}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Soak test failed", ex, "SOAK_TEST_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRunUiStabilityTestClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanUseMaintenanceActions, "Running UI stability test", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profile = SelectedUiStabilityProfile();
        var state = WorkflowState.Instance;
        var options = UiNavigationSoakTestService.CreateProfileOptions(
            profile,
            UiNavigationSoakTestService.DefaultOutputRoot,
            testMode: false,
            operatorId: state.OperatorWithRole) with
        {
            IncludeHeavyPageLoads = UiStabilityHeavyLoadsCheck.IsChecked == true ||
                profile is UiNavigationSoakProfile.FourHourClientDemoReadiness or UiNavigationSoakProfile.EightHourFactoryPoC,
        };

        var cts = BeginWork("Running UI stability test...");
        var progress = new Progress<UiNavigationSoakProgress>(p =>
        {
            UiStabilityStatsText.Text =
                $"cycles={p.CyclesCompleted}; slow={p.SlowNavigationCount}; max load={p.MaxPageLoadMilliseconds} ms; " +
                $"memory start/current/max={p.StartMemoryMegabytes:F1}/{p.CurrentMemoryMegabytes:F1}/{p.MaxMemoryMegabytes:F1} MB; " +
                $"crashes={p.CrashCount}; open High/Critical issues={p.OpenHighCriticalIssues}.";
            UpdateProgress(new WorkProgress(p.ElapsedSeconds, p.TotalSeconds, p.Message));
        });

        try
        {
            _uiStabilityEvents.Clear();
            Func<string, CancellationToken, Task>? pageAction = Window.GetWindow(this) is AOI_Monitor.MainWindow shell
                ? shell.ExerciseUiNavigationForStabilityAsync
                : null;
            var result = await UiNavigationSoakTestService.RunAsync(options, pageAction, progress, cts.Token);
            foreach (var item in result.Events.TakeLast(500))
                _uiStabilityEvents.Add(item);

            var export = UiNavigationSoakTestService.WriteReports(result);
            if (Window.GetWindow(this) is AOI_Monitor.MainWindow restoreShell)
                await restoreShell.ExerciseUiNavigationForStabilityAsync("Readiness & QA", CancellationToken.None);
            UiStabilityStatsText.Text =
                $"UI stability {result.Status}; cycles={result.CyclesCompleted}; slow={result.SlowNavigationCount}; " +
                $"max load={result.MaxPageLoadMilliseconds} ms; memory={result.StartWorkingSetMegabytes:F1}/{result.EndWorkingSetMegabytes:F1}/{result.MaxWorkingSetMegabytes:F1} MB; " +
                $"crashes={result.CrashCount}; open High/Critical issues={result.OpenHighCriticalIssues}. Report: {export.HtmlPath}";
            WorkflowState.Instance.AddEvent("UI_STABILITY_TEST", $"UI stability {result.Status}: profile={result.Profile}; cycles={result.CyclesCompleted}; crashes={result.CrashCount}; report={Path.GetFileName(export.Folder)}.", relatedPath: export.Folder);
            RefreshAfterExport($"UI stability {result.Status}. HTML: {export.HtmlPath}. PDF: {export.PdfPath}. JSON: {export.JsonPath}. CSV: {export.CsvPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("UI stability test failed", ex, "UI_STABILITY_TEST_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private UiNavigationSoakProfile SelectedUiStabilityProfile()
        => (UiStabilityProfileCombo?.SelectedItem as ComboBoxItem)?.Tag is UiNavigationSoakProfile profile
            ? profile
            : UiNavigationSoakProfile.ThirtyMinuteLocalStability;

    private async void OnRunPerformanceBenchmarkClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new BenchmarkOptionsDialog
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Options is null)
            return;

        var cts = BeginWork("Running inspection performance benchmark...");
        var progress = new Progress<string>(message => UpdateProgress(new WorkProgress(0, 0, message)));
        try
        {
            var result = await Task.Run(() => BenchmarkInspectionService.Run(dialog.Options, progress, cts.Token), cts.Token);
            WorkflowState.Instance.AddEvent("PERFORMANCE_BENCHMARK", $"Benchmark {result.Status}: count={result.CompletedCount}; p95={result.P95FrameToOverlayMs:F0} ms; realCamera={result.IsRealCameraSource}.", relatedPath: result.ReportFolder);
            RefreshAfterExport($"Performance benchmark {result.Status}. Count={result.CompletedCount}; p50={result.P50FrameToOverlayMs:F0} ms; p95={result.P95FrameToOverlayMs:F0} ms; max={result.MaxFrameToOverlayMs:F0} ms; over1s={result.OverOneSecondCount}; throughput={result.ThroughputImagesPerMinute:F1}/min. HTML: {result.HtmlPath}. JSON: {result.JsonPath}. CSV: {result.CsvPath}. PDF: {result.PdfPath}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Performance benchmark canceled.";
            WorkflowState.Instance.AddEvent("PERFORMANCE_BENCHMARK", "Performance benchmark canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DirectoryNotFoundException)
        {
            HandleWorkError("Performance benchmark failed", ex, "PERFORMANCE_BENCHMARK_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnExportLatestSoakEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting latest soak-test evidence", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var latest = AoiDatabase.GetLatestSoakTestRun();
        if (latest is null)
        {
            MessageBox.Show("No persisted soak-test run is available to export.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var output = string.IsNullOrWhiteSpace(latest.OutputFolder) ? EnsureExportsDir() : latest.OutputFolder;
            var htmlPath = SoakTestService.WriteHtmlReport(latest, output);
            var jsonPath = SoakTestService.WriteJsonReport(latest, output);
            var csvPath = SoakTestService.WriteIterationsCsv(latest, output);
            ExportVerificationService.RecordVerifiedExport("SoakTestReport", htmlPath, latest.WasCanceled ? "CANCELED" : "OK");
            ExportVerificationService.RecordVerifiedExport("SoakTestJsonReport", jsonPath, latest.WasCanceled ? "CANCELED" : "OK");
            ExportVerificationService.RecordVerifiedExport("SoakTestIterationsCsv", csvPath, latest.WasCanceled ? "CANCELED" : "OK");
            WorkflowState.Instance.AddEvent("SOAK_TEST_EXPORT", $"Latest soak evidence exported: {Path.GetFileName(htmlPath)}.");
            RefreshAfterExport($"Latest soak evidence exported. HTML: {htmlPath}. JSON: {jsonPath}. CSV: {csvPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Latest soak evidence export failed", ex, "SOAK_TEST_EXPORT_ERROR");
        }
    }
}
