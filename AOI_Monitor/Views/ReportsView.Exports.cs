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
    private void OnExportInspectionHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_inspectionRows.Count == 0)
        {
            MessageBox.Show("No inspection history rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered inspection history to CSV?"))
            return;

        var dialog = SaveCsvDialog("inspection_history");
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildInspectionCsv(_inspectionRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("InspectionHistoryCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Inspection history CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Inspection history CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Inspection history CSV export failed", ex, "EXPORT_ERROR");
        }
    }

    private void OnExportReviewLogClick(object sender, RoutedEventArgs e)
    {
        if (_reviewRows.Count == 0)
        {
            MessageBox.Show("No review log rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered review/disposition log to CSV?"))
            return;

        var dialog = SaveCsvDialog("review_log");
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildReviewCsv(_reviewRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("ReviewLogCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Review log CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Review log CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Review log CSV export failed", ex, "EXPORT_ERROR");
        }
    }

    private void OnExportAuditTrailClick(object sender, RoutedEventArgs e)
    {
        if (_auditRows.Count == 0)
        {
            MessageBox.Show("No audit trail rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered audit trail to CSV for QC documentation?"))
            return;

        var dialog = SaveCsvDialog("audit_trail");
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildAuditCsv(_auditRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("AuditTrailCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Audit trail CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Audit trail CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Audit trail CSV export failed", ex, "EXPORT_ERROR");
        }
    }

    private async void OnExportAnnotatedOverlaysClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = _inspectionRows.Where(r => File.Exists(r.SampleImagePath)).ToArray();
        if (rows.Length == 0)
        {
            MessageBox.Show("No inspection rows with accessible sample images match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport($"Export annotated overlays for {rows.Length} filtered inspection image(s)?"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select annotated overlay export folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginWork("Exporting annotated overlays...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var result = await Task.Run(() => ExportAnnotatedOverlays(rows, dialog.FolderName, cts.Token, progress), cts.Token);
            var verified = ExportVerificationService.RecordVerifiedExport(
                "AnnotatedImageOverlays",
                dialog.FolderName,
                result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s).");
            LogErrors("EXPORT_ERROR", result.Errors);
            RefreshAfterExport($"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s) to {dialog.FolderName}. Verification: {verified.Verification.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Annotated overlay export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Annotated overlay export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Annotated overlay export failed", ex, "EXPORT_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnVerifyImagePathsClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmExport("Run image path verification and record the utility report?"))
            return;

        var exportsDir = EnsureExportsDir();
        var reportPath = Path.Combine(exportsDir, $"image_path_verification_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();
        var inaccessible = 0;

        foreach (var row in _inspectionRows)
        {
            CheckPath("Sample", row.SampleImagePath, row.Id, sb, ref inaccessible);
            if (!string.IsNullOrWhiteSpace(row.GoldenImagePath))
                CheckPath("Golden", row.GoldenImagePath, row.Id, sb, ref inaccessible);
        }

        if (sb.Length == 0)
            sb.AppendLine("No inaccessible image paths detected for the current filtered inspection rows.");

        sb.AppendLine();
        sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"RowsChecked: {_inspectionRows.Count}");
        sb.AppendLine($"Issues: {inaccessible}");
        File.WriteAllText(reportPath, sb.ToString());

        var verified = ExportVerificationService.RecordVerifiedExport("ImagePathVerification", reportPath, inaccessible == 0 ? "OK" : "WARN");
        WorkflowState.Instance.AddEvent("UTILITY", $"Image path verification completed. Issues={inaccessible}.");
        RefreshAfterExport($"Image path verification complete. Issues={inaccessible}. Report: {reportPath}. Verification: {verified.Verification.Status}.");
    }

    private void OnVerifySelectedExportClick(object sender, RoutedEventArgs e)
    {
        if (ExportGrid.SelectedItem is not ExportHistoryRow row)
        {
            MessageBox.Show("Select an export history row first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = ExportVerificationService.Verify(row.FilePath, row.ExportType, row.Id, persist: true);
            var reportFolder = Path.Combine(EnsureExportsDir(), "export_verification");
            var report = ExportVerificationService.ExportReport(result, reportFolder);
            ExportVerificationService.RecordVerifiedExport("ExportVerificationJsonReport", report.JsonPath);
            ExportVerificationService.RecordVerifiedExport("ExportVerificationTextReport", report.TextPath);
            WorkflowState.Instance.AddEvent(
                result.Status == ExportVerificationStatus.OK ? "EXPORT_VERIFY" : "EXPORT_VERIFY_WARN",
                $"Export verification {result.Status}: {row.ExportType}; sha256={result.Sha256}; path={row.FilePath}");
            RefreshAfterExport($"Export verification {result.Status}. SHA-256: {ShortHash(result.Sha256)}. Report: {report.JsonPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            HandleWorkError("Export verification failed", ex, "EXPORT_VERIFY_ERROR");
        }
    }

}
