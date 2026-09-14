using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ReportsView
{
    private async void OnRunDbIntegrityCheckClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Run SQLite integrity check and record the report?"))
            return;

        var cts = BeginWork("Running SQLite integrity check...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(1, 4, "Preparing SQLite integrity report..."));
                var exportsDir = EnsureExportsDir();
                var reportPath = Path.Combine(exportsDir, $"db_integrity_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(2, 4, "Running SQLite integrity check..."));
                var integrity = AoiDatabase.RunIntegrityCheck();
                var status = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) ? "OK" : "WARN";

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(3, 4, "Writing SQLite integrity report..."));
                var sb = new StringBuilder();
                sb.AppendLine($"SQLiteIntegrityCheck: {integrity}");
                sb.AppendLine($"DatabasePath: {AoiDatabase.DatabasePath}");
                sb.AppendLine($"ImageVaultPath: {AoiDatabase.ImageVaultPath}");
                sb.AppendLine($"InspectionRowsVisible: {_inspectionRows.Count}");
                sb.AppendLine($"ReviewRowsVisible: {_reviewRows.Count}");
                sb.AppendLine($"AutoArchivePolicy: {DescribeRetentionPolicy()}");
                sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                File.WriteAllText(reportPath, sb.ToString());
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(4, 4, "SQLite integrity check complete."));
                return new IntegrityOutcome(reportPath, integrity, status);
            }, cts.Token);

            var verified = ExportVerificationService.RecordVerifiedExport("DatabaseIntegrityReport", result.ReportPath, result.Status);
            WorkflowState.Instance.AddEvent("UTILITY", $"DB integrity check result: {result.Integrity}.");
            RefreshAfterExport($"DB integrity check complete: {result.Integrity}. Report: {result.ReportPath}. Verification: {verified.Verification.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "DB integrity check canceled.";
            WorkflowState.Instance.AddEvent("UTILITY", "DB integrity check canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("DB integrity check failed", ex, "UTILITY_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnOpenDatabaseHealthClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = "spc";
    }

    private async void OnRebuildImageIndexClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Rebuild the image index for the current image vault and filtered inspection paths?"))
            return;

        var cts = BeginWork("Rebuilding image index...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        var rows = _inspectionRows.ToArray();

        try
        {
            var result = await Task.Run(() => RebuildImageIndex(rows, cts.Token, progress), cts.Token);
            var verified = ExportVerificationService.RecordVerifiedExport("ImageIndex", result.Path, result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("UTILITY", $"Image index rebuilt with {result.Count} entries and {result.Errors.Count} issue(s).");
            LogErrors("UTILITY_ERROR", result.Errors);
            RefreshAfterExport($"Image index rebuilt with {result.Count} entries: {result.Path}. Verification: {verified.Verification.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Image index rebuild canceled.";
            WorkflowState.Instance.AddEvent("UTILITY", "Image index rebuild canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Image index rebuild failed", ex, "UTILITY_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnUploadMesMockClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Uploading a result to MES integration", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var row = InspectionGrid.SelectedItem as InspectionLogRow ?? _inspectionRows.FirstOrDefault();
        if (row is null)
        {
            MessageBox.Show("No inspection result is available to upload. Run or save an inspection first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = MesIntegrationSettingsService.Load();
        if (settings.Mode == MesIntegrationMode.FutureProduction)
        {
            MessageBox.Show("Future Production mode is a planned Stage 4 boundary. Use Mock Local/REST or REST mode for upload testing. OPC UA remains a Stage 4 adapter TODO.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var payload = BuildTraceabilityPayload(row, settings);
        var cts = BeginWork("Uploading result to MES boundary...");
        try
        {
            var outcome = await TraceabilityUploadService.UploadAsync(payload, cts.Token);
            var exportStatus = outcome.Result.Accepted ? "OK" : "ERROR";
            var verified = ExportVerificationService.Verify(outcome.PayloadPath, "MesTraceabilityPayload");
            WorkflowState.Instance.AddEvent(
                outcome.Result.Accepted ? "MES_UPLOAD" : "MES_UPLOAD_ERROR",
                $"MES upload {exportStatus}: {outcome.Result.Message}");
            RefreshAfterExport($"MES upload {exportStatus}. Payload: {outcome.PayloadPath}. Verification: {verified.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "MES upload canceled.";
            WorkflowState.Instance.AddEvent("MES_UPLOAD", "MES upload canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("MES upload failed", ex, "MES_UPLOAD_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRetryMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying MES spool uploads", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pending = AoiDatabase.GetPendingMesSpoolItems();
        if (pending.Count == 0)
        {
            MessageBox.Show("No pending MES spool items are ready for retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = RefreshAsync(CancellationToken.None);
            return;
        }

        var cts = BeginWork("Retrying pending MES spool uploads...");
        try
        {
            var summary = await MesSpoolService.RetryEligibleAsync(100, cts.Token);
            var message = $"MES spool retry complete: attempted={summary.Attempted}, succeeded={summary.Succeeded}, failed={summary.Failed}.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "MES spool retry canceled.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", "MES spool retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnMesQueueStatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        _ = RefreshAsync(CancellationToken.None);
    }

    private async void OnRunTraceabilityTestClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Running MES traceability signoff", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = MesIntegrationSettingsService.Load();
        string? imagePath = null;
        var chooseImage = MessageBox.Show("Select a test image payload for this traceability signoff? Choose No to send result payload only.", "Traceability Signoff", MessageBoxButton.YesNo, MessageBoxImage.Question);
        var productionConfirmed = false;
        if (chooseImage == MessageBoxResult.Yes)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select MES traceability test image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*",
            };
            if (dialog.ShowDialog() == true)
                imagePath = dialog.FileName;

            if (!string.IsNullOrWhiteSpace(imagePath) && settings.Mode == MesIntegrationMode.Rest)
            {
                productionConfirmed = MessageBox.Show(
                    "Production REST mode is selected. Send the selected image file to the configured MES image endpoint?",
                    "Confirm Production Image Upload",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
            }
        }

        var cts = BeginWork("Running MES traceability signoff...");
        try
        {
            var report = await TraceabilityAcceptanceTestService.RunAsync(imagePath, productionConfirmed, WorkflowState.Instance.OperatorWithRole, cts.Token);
            WorkflowState.Instance.AddEvent(report.Status == "PASS" ? "MES_TRACEABILITY_TEST" : "MES_TRACEABILITY_TEST_ERROR", $"Traceability signoff {report.Status}; result={report.ResultStatus}; image={report.ImageStatus}; report={Path.GetFileName(report.ReportHtmlPath)}.");
            RefreshAfterExport($"Traceability signoff {report.Status}. HTML: {report.ReportHtmlPath}. JSON: {report.ReportJsonPath}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Traceability signoff canceled.";
            WorkflowState.Instance.AddEvent("MES_TRACEABILITY_TEST", "Traceability signoff canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Traceability signoff failed", ex, "MES_TRACEABILITY_TEST_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnQueueCentralSyncClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Queueing central sync payloads", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var count = CentralSyncService.QueueLocalChangesForSync();
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_QUEUE", $"Queued {count} central sync payload(s).");
            RefreshAfterExport($"Queued {count} central sync payload(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Central sync queueing failed", ex, "CENTRAL_SYNC_QUEUE_ERROR");
        }
    }

    private async void OnRetryCentralSyncClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying central sync queue", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pending = AoiDatabase.GetPendingCentralSyncItems();
        if (pending.Count == 0)
        {
            MessageBox.Show("No pending central sync items are ready for retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = RefreshAsync(CancellationToken.None);
            return;
        }

        var cts = BeginWork("Retrying central sync queue...");
        try
        {
            var summary = await CentralSyncService.RetryEligibleAsync(100, cts.Token);
            var message = $"Central sync retry complete: attempted={summary.Attempted}, sent={summary.Sent}, failed={summary.Failed}, skipped={summary.Skipped}.";
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_RETRY", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Central sync retry canceled.";
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_RETRY", "Central sync retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRetrySelectedMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying selected MES queue items", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = MesSpoolGrid.SelectedItems.OfType<MesSpoolQueueRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more MES queue items to retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var retryable = selected
            .Where(row => row.Status is "Pending" or "Failed")
            .Select(row => row.Id)
            .ToArray();
        if (retryable.Length == 0)
        {
            MessageBox.Show("Selected MES queue items are not retryable. Only Pending or Failed items can be retried.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = BeginWork("Retrying selected MES queue items...");
        try
        {
            var summary = await MesSpoolService.RetryItemsAsync(retryable, cts.Token);
            var message = $"Selected MES retry complete: attempted={summary.Attempted}, succeeded={summary.Succeeded}, failed={summary.Failed}.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Selected MES retry canceled.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", "Selected MES retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRetrySelectedCentralSyncClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying selected central sync items", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = CentralSyncGrid.SelectedItems.OfType<CentralSyncQueueRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more central sync queue items to retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var retryable = selected
            .Where(row => row.Status == "Pending")
            .Select(row => row.Id)
            .ToArray();
        if (retryable.Length == 0)
        {
            MessageBox.Show("Selected central sync queue items are not retryable. Only Pending items can be retried.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = BeginWork("Retrying selected central sync items...");
        try
        {
            var summary = await CentralSyncService.RetryItemsAsync(retryable, cts.Token);
            var message = $"Selected central sync retry complete: attempted={summary.Attempted}, sent={summary.Sent}, failed={summary.Failed}, skipped={summary.Skipped}.";
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_RETRY", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Selected central sync retry canceled.";
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_RETRY", "Selected central sync retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnExportMesQueueReportClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting MES queue report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var report = MesSpoolService.ExportQueueReport();
            WorkflowState.Instance.AddEvent("MES_SPOOL_EXPORT", $"MES queue report exported: {Path.GetFileName(report.HtmlPath)}.");
            RefreshAfterExport($"MES queue report exported. HTML: {report.HtmlPath}. JSON: {report.JsonPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("MES queue report export failed", ex, "MES_SPOOL_EXPORT_ERROR");
        }
    }

    private void OnExportMesContractClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting MES endpoint contract", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var export = TraceabilityAcceptanceTestService.ExportEndpointContracts();
            WorkflowState.Instance.AddEvent("MES_CONTRACT_EXPORT", $"MES endpoint contract exported: {Path.GetFileName(export.PayloadContractPath)}.");
            RefreshAfterExport($"MES endpoint contract exported. Payload: {export.PayloadContractPath}. Response: {export.ResponseContractPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("MES contract export failed", ex, "MES_CONTRACT_EXPORT_ERROR");
        }
    }

    private void OnExportCentralSyncReportClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting central sync queue report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var report = CentralSyncService.ExportQueueReport();
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_EXPORT", $"Central sync queue report exported: {Path.GetFileName(report.HtmlPath)}.");
            RefreshAfterExport($"Central sync report exported. HTML: {report.HtmlPath}. JSON: {report.JsonPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Central sync report export failed", ex, "CENTRAL_SYNC_EXPORT_ERROR");
        }
    }

    private void OnAbandonMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanUseMaintenanceActions, "Abandoning MES queue items", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = MesSpoolGrid.SelectedItems.OfType<MesSpoolQueueRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more MES queue items to mark Abandoned.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = selected
            .Where(row => row.Status is "Pending" or "Failed")
            .ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show("Selected MES queue items are already terminal. Only Pending or Failed items can be abandoned.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Mark {candidates.Length} MES queue item(s) Abandoned? This is an Admin-only audit action and does not upload payloads.",
            "Confirm MES queue abandon",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            foreach (var row in candidates)
            {
                MesSpoolService.MarkAbandoned(
                    row.Id,
                    WorkflowState.Instance.CurrentRole,
                    WorkflowState.Instance.OperatorWithRole,
                    "Abandoned from MES Queue UI.");
            }

            WorkflowState.Instance.AddEvent("MES_SPOOL_ABANDON", $"Marked {candidates.Length} MES queue item(s) Abandoned.");
            RefreshAfterExport($"Marked {candidates.Length} MES queue item(s) Abandoned.");
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(ex.Message, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancelWorkClick(object sender, RoutedEventArgs e)
    {
        CancelWork();
    }

    private void OnLockActiveRecipeClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        state.IsRecipeLocked = !state.IsRecipeLocked;
        state.AddEvent("SYSTEM", state.IsRecipeLocked ? "Active recipe locked from Reports." : "Active recipe unlocked from Reports.");
        StatusText.Text = state.IsRecipeLocked ? "Active recipe locked." : "Active recipe unlocked.";
        MessageBox.Show(StatusText.Text, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

}
