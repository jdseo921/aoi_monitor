using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ReportsView
{
    // Shared with ReadinessQaView (Stage 1 customer package): pure static rendering with
    // no instance state, so the readiness window reuses it instead of duplicating it.
    internal static ExportOutcome ExportAnnotatedOverlays(
        IReadOnlyCollection<InspectionLogRow> rows,
        string folder,
        CancellationToken token,
        IProgress<WorkProgress> progress,
        int completedOffset = 0,
        int totalOffset = 0)
    {
        Directory.CreateDirectory(folder);
        var errors = new List<string>();
        var exported = 0;
        var index = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new WorkProgress(
                completedOffset + index,
                Math.Max(1, totalOffset + rows.Count),
                $"Exporting overlay {index + 1} of {rows.Count}..."));

            try
            {
                if (!File.Exists(row.SampleImagePath))
                {
                    errors.Add($"Missing sample image for inspection {row.Id}: {row.SampleImagePath}");
                    continue;
                }

                var bitmap = CreateAnnotatedBitmap(row);
                var path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(row.ImageName)}_{row.Verdict}_{row.Id}_overlay.png");
                SavePng(bitmap, path);
                exported++;
                if (exported % 10 == 0)
                {
                    MemoryDiagnosticsService.RecordExportCheckpoint("Annotated overlay export", exported);
                    ImageCacheService.ClearOnMemoryPressure();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                errors.Add(FriendlyFileError(row.SampleImagePath, ex));
            }
            finally
            {
                index++;
            }
        }

        progress.Report(new WorkProgress(completedOffset + rows.Count, Math.Max(1, totalOffset + rows.Count), "Annotated overlay export complete."));
        return new ExportOutcome(exported, errors);
    }

    private static RenderTargetBitmap CreateAnnotatedBitmap(InspectionLogRow row)
    {
        var source = LoadBitmap(row.SampleImagePath);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            var roi = new Rect(
                row.HotspotX * source.PixelWidth,
                row.HotspotY * source.PixelHeight,
                Math.Max(2, row.HotspotWidth * source.PixelWidth),
                Math.Max(2, row.HotspotHeight * source.PixelHeight));

            var color = row.Verdict == "NG" ? Colors.Red : row.Verdict == "OK" ? Colors.LimeGreen : Colors.Orange;
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, Math.Max(3, source.PixelWidth / 300.0));
            dc.DrawRectangle(null, pen, roi);

            var text = new FormattedText(
                $"{row.Verdict} / {row.ScoreDisplay}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Max(16, source.PixelWidth / 34.0),
                brush,
                1.0);

            var origin = new Point(Math.Max(0, roi.X), Math.Max(0, roi.Y - text.Height - 8));
            dc.DrawRectangle(Brushes.Black, null, new Rect(origin.X, origin.Y, text.Width + 10, text.Height + 6));
            dc.DrawText(text, new Point(origin.X + 5, origin.Y + 3));
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource LoadBitmap(string path)
        => ImageCacheService.LoadBitmap(path, cache: false);

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void RefreshAfterExport(string message)
    {
        _ = RefreshAsync(CancellationToken.None);
        StatusText.Text = message;
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal static string BuildInspectionCsv(IEnumerable<InspectionLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,BoardProgram,Operator,Result,Score,Confidence,Engine,ModelVersion,ConfidenceThreshold,ModelPath,Defect,SampleImage,GoldenImage,DecisionReason,ImageLoadMs,PreprocessingMs,InferenceMs,OverlayRenderingMs,TotalInspectionMs");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.BoardProgram),
                EscapeCsv(row.OperatorId),
                EscapeCsv(row.Verdict),
                row.DifferenceScore.ToString("F4", CultureInfo.InvariantCulture),
                row.Confidence.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.InspectionEngine),
                EscapeCsv(row.ModelVersion),
                row.ConfidenceThreshold.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.ModelFilePath),
                EscapeCsv(row.SuggestedDefect),
                EscapeCsv(row.SampleImagePath),
                EscapeCsv(row.GoldenImagePath),
                EscapeCsv(row.DecisionReason),
                row.ImageLoadMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.PreprocessingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.InferenceMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.OverlayRenderingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.TotalInspectionMilliseconds.ToString("F1", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    internal static string BuildReviewCsv(IEnumerable<ReviewLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,Category,Operator,Disposition,Message");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.EventTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.Category),
                EscapeCsv(row.OperatorId),
                EscapeCsv(row.Disposition),
                EscapeCsv(row.Message)));
        }

        return sb.ToString();
    }

    internal static string BuildAuditCsv(IEnumerable<AuditLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,LocalTimestamp,UserId,UserRole,StationId,ActionCategory,ActionDetail,RelatedEntityType,RelatedEntityId,RelatedPath");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.LocalTimestamp.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.UserId),
                EscapeCsv(row.UserRole),
                EscapeCsv(row.StationId),
                EscapeCsv(row.ActionCategory),
                EscapeCsv(row.ActionDetail),
                EscapeCsv(row.RelatedEntityType),
                EscapeCsv(row.RelatedEntityId),
                EscapeCsv(row.RelatedPath)));
        }

        return sb.ToString();
    }

    private static TraceabilityPayload BuildTraceabilityPayload(InspectionLogRow row, MesIntegrationSettings settings)
    {
        var cameraSettings = CameraSourceSettingsService.Load();
        var lotId = string.IsNullOrWhiteSpace(cameraSettings.LotId) ? "UNKNOWN" : cameraSettings.LotId;
        var timestamp = row.CreatedAtUtc == DateTime.MinValue ? DateTime.UtcNow : row.CreatedAtUtc.ToUniversalTime();
        return new TraceabilityPayload
        {
            IntegrationMode = TraceabilityUploadService.ToDisplay(settings.Mode),
            LotId = lotId,
            BoardModel = string.IsNullOrWhiteSpace(row.BoardProgram) ? cameraSettings.BoardModel : row.BoardProgram,
            SerialNumber = null,
            StationId = WorkflowState.Instance.StationId,
            OperatorId = row.OperatorId,
            Result = row.Verdict,
            TimestampUtc = timestamp,
            DefectSummary = $"{row.SuggestedDefect}; score={row.ScoreDisplay}; confidence={row.ConfidenceDisplay}",
            ImagePath = row.SampleImagePath,
            OverlayPath = string.Empty,
            InspectionEngine = row.InspectionEngine,
            ModelVersion = row.ModelVersion,
            Confidence = row.Confidence,
            Score = row.DifferenceScore,
        };
    }

    private static SaveFileDialog SaveCsvDialog(string stem)
    {
        return new SaveFileDialog
        {
            Title = $"Export {stem.Replace('_', ' ')}",
            Filter = "CSV file|*.csv",
            FileName = $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
    }

    internal static bool ConfirmExport(string message)
    {
        return MessageBox.Show(
            message,
            "Confirm Export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    internal static string EnsureExportsDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    internal static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string ShortHash(string hash)
        => string.IsNullOrWhiteSpace(hash) || hash.Length <= 12 ? hash : hash[..12];

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private CancellationTokenSource BeginWork(string message)
    {
        UiPerformanceMonitorService.RecordSlowOperation(message, 0, $"Long operation started: {message}");
        _workCts = new CancellationTokenSource();
        WorkProgressBar.Value = 0;
        CancelWorkButton.IsEnabled = true;
        StatusText.Text = message;
        return _workCts;
    }

    private void EndWork()
    {
        _workCts?.Dispose();
        _workCts = null;
        CancelWorkButton.IsEnabled = false;
        WorkProgressBar.Value = 0;
    }

    private void UpdateProgress(WorkProgress progress)
    {
        WorkProgressBar.Value = progress.Total <= 0 ? 0 : Math.Min(100, progress.Completed * 100.0 / progress.Total);
        StatusText.Text = progress.Message;
    }

    private void HandleWorkError(string title, Exception ex, string category)
    {
        var message = ex is UnauthorizedAccessException
            ? $"{title}: export folder or database access was denied."
            : $"{title}: {ex.Message}";
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = ex,
            OperationName = title,
            CurrentPage = "Log & Export",
            IsFatal = false,
            IsUiThread = true,
        });
        StatusText.Text = message;
        WorkflowState.Instance.AddEvent(category, message, relatedEntityType: "CrashReport", relatedPath: report.ReportPath);
        MessageBox.Show($"{message}\n\n{report.OperatorMessage}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void LogErrors(string category, IReadOnlyList<string> errors)
    {
        foreach (var error in errors.Take(40))
            WorkflowState.Instance.AddEvent(category, error);
    }

    private static string FriendlyFileError(string path, Exception ex)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "(unknown file)" : Path.GetFileName(path);
        return ex switch
        {
            UnauthorizedAccessException => $"Permission denied or locked file: {name}",
            NotSupportedException => $"Unsupported image format: {name}",
            IOException => $"File could not be read or written: {name} ({ex.Message})",
            _ => $"{name}: {ex.Message}",
        };
    }

    private static IndexOutcome RebuildImageIndex(IReadOnlyCollection<InspectionLogRow> rows, CancellationToken token, IProgress<WorkProgress> progress)
    {
        var exportsDir = EnsureExportsDir();
        var file = Path.Combine(exportsDir, $"image_index_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        if (Directory.Exists(AoiDatabase.ImageVaultPath))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(AoiDatabase.ImageVaultPath, "*.*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    if (ImageExtensions.Contains(Path.GetExtension(path)))
                        paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Image vault cannot be fully indexed: {ex.Message}");
            }
        }
        else
        {
            errors.Add($"Image vault folder is not available: {AoiDatabase.ImageVaultPath}");
        }

        var checkedRows = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new WorkProgress(checkedRows, Math.Max(1, rows.Count), $"Indexing inspection image paths {checkedRows + 1} of {rows.Count}..."));
            AddExistingPath(row.SampleImagePath, paths, errors, row.Id, "sample");
            AddExistingPath(row.GoldenImagePath, paths, errors, row.Id, "golden");
            checkedRows++;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Path,Bytes,LastWriteUtc");
        var written = 0;
        foreach (var path in paths.OrderBy(p => p))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                sb.AppendLine(string.Join(",",
                    EscapeCsv(path),
                    info.Length.ToString(CultureInfo.InvariantCulture),
                    EscapeCsv(info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture))));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(FriendlyFileError(path, ex));
            }
        }

        File.WriteAllText(file, sb.ToString(), CsvEncoding);
        progress.Report(new WorkProgress(rows.Count, Math.Max(1, rows.Count), "Image index rebuild complete."));
        return new IndexOutcome(file, written, errors);
    }

    private static void AddExistingPath(string path, ISet<string> paths, ICollection<string> errors, long inspectionId, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path))
            paths.Add(path);
        else
            errors.Add($"Missing {label} image for inspection {inspectionId}: {path}");
    }

    private static void CheckPath(string label, string path, long id, StringBuilder sb, ref int inaccessible)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            inaccessible++;
            sb.AppendLine($"[MISSING] Inspection {id} {label}: {path}");
        }
    }
}
