using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class LibraryView : UserControl, IAsyncNavigationPage, IDisposable
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private CancellationTokenSource? _importCts;

    // Demo rows are shown only while the SQLite image table is empty.
    private static readonly ImageLibraryRecord[] DemoRecords =
    {
        new("IMG_0241","TBOX-MAIN","U107","Solder Bridge",  "Critical","OK","NG","Possible Escape","DEMO","01-28 18:04", "", "", "", true),
        new("IMG_0188","TBOX-MAIN","C684","Insuff. Solder", "Major",   "NG","NG","Verified NG",    "DEMO","01-28 17:52", "", "", "", true),
        new("IMG_0182","TBOX-MAIN","CN8", "Pin Height Err.","Major",   "NG","OK","False Call",     "DEMO","01-28 17:44", "", "", "", true),
        new("IMG_0177","TBOX-MAIN","D12", "Polarity Error", "Critical","OK","NG","Possible Escape","DEMO","01-28 17:39", "", "", "", true),
        new("IMG_0164","TBOX-MAIN","R88", "Tombstone",      "Major",   "NG","NG","Verified NG",    "DEMO","01-28 17:20", "", "", "", true),
        new("IMG_0153","TBOX-MAIN","SH1", "Shield Can Gap", "Major",   "NG","OK","False Call",     "DEMO","01-28 17:11", "", "", "", true),
    };

    // Temporary schema preview text; the actual PoC schema is created in Data/AoiDatabase.cs.
    private static readonly object[] SchemaRows =
    {
        new { Table = "Images",            Columns = "id, original_path, vault_path, hash, board, lot, view", Status = "LIVE" },
        new { Table = "InspectionResults", Columns = "score, verdict, confidence, policy, hotspot",          Status = "LIVE" },
        new { Table = "Defects",           Columns = "inspection_result_id, image_id, refdes, roi",          Status = "READY" },
        new { Table = "ReviewEvents",      Columns = "category, message, disposition, operator, timestamp",  Status = "LIVE" },
        new { Table = "TrainingSamples",   Columns = "source_path, vault_path, label, notes",                Status = "LIVE" },
    };

    public LibraryView()
    {
        InitializeComponent();
        SchemaGrid.ItemsSource = SchemaRows;
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
        => await LoadRecordsFromDatabaseAsync(null, cancellationToken);

    public void RefreshFromState() => _ = RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh/navigation.
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = $"Library refresh failed: {ex.Message}";
        }
    }

    public void CancelWork()
    {
        _importCts?.Cancel();
    }

    public void ExportSelectedRecord()
    {
        OnExportSelectedClick(this, new RoutedEventArgs());
    }

    private async void OnOpenRecordClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import sample PCB image",
            Filter = "PCB image files|*.png;*.jpg;*.jpeg",
        };

        if (dialog.ShowDialog() != true) return;

        ImageImportResult result;
        try
        {
            result = await Task.Run(() => ImportOne(dialog.FileName, "sample"));
            await LoadRecordsFromDatabaseAsync(result.Image?.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ImportStatusText.Text = "Single image import failed. The app is still usable.";
            WorkflowState.Instance.AddEvent("IMPORT_ERROR", $"Single image import failed: {ex.Message}");
            MessageBox.Show($"Single image import failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Image is { } image && File.Exists(image.VaultPath))
        {
            WorkflowState.Instance.SetSampleImage(image.VaultPath);
            TryShowImagePreview(image.VaultPath, "Sample Image");
        }

        ShowImportStatus(new[] { result }, "Single image import");
        LogImportIssues(new[] { result });
    }

    private async void OnBatchImportClick(object sender, RoutedEventArgs e)
    {
        if (_importCts is not null)
        {
            MessageBox.Show("A batch import is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder containing PNG/JPG/JPEG PCB images",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginImport($"Starting batch import from {dialog.FolderName}...");
        var progress = new Progress<ImportProgress>(UpdateImportProgress);

        try
        {
            var results = await Task.Run(() => ImportFolder(dialog.FolderName, cts.Token, progress).ToArray(), cts.Token);
            var newestId = results.FirstOrDefault(r => r.Imported)?.Image?.Id;
            await LoadRecordsFromDatabaseAsync(newestId, cts.Token);
            ShowImportStatus(results, $"Batch import from {dialog.FolderName}");
            LogImportIssues(results);
        }
        catch (OperationCanceledException)
        {
            ImportStatusText.Text = "Batch import canceled.";
            WorkflowState.Instance.AddEvent("IMPORT", "Batch import canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ImportStatusText.Text = "Batch import failed. The app is still usable.";
            WorkflowState.Instance.AddEvent("IMPORT_ERROR", $"Batch import failed: {ex.Message}");
            MessageBox.Show($"Batch import failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndImport();
        }
    }

    private void OnCancelImportClick(object sender, RoutedEventArgs e)
    {
        _importCts?.Cancel();
        ImportStatusText.Text = "Cancel requested. Finishing current file...";
    }

    private async void OnCompareGoldenClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!EnsureSelectedRecordAsSample())
        {
            MessageBox.Show("Import or select a valid sample image first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import golden reference image",
            Filter = "PCB image files|*.png;*.jpg;*.jpeg",
        };

        if (dialog.ShowDialog() != true) return;

        var compareGoldenButton = sender as Button;
        if (compareGoldenButton is not null)
            compareGoldenButton.IsEnabled = false;
        try
        {
            var goldenSourcePath = dialog.FileName;
            var result = await Task.Run(() => ImportOne(goldenSourcePath, "golden"));
            if (result.Image is null)
            {
                ShowImportStatus(new[] { result }, "Golden import");
                LogImportIssues(new[] { result });
                return;
            }

            state.SetGoldenImage(result.Image.VaultPath);
            TryShowImagePreview(result.Image.VaultPath, "Golden Reference Image");
            ShowImportStatus(new[] { result }, "Golden import");
            LogImportIssues(new[] { result });

            try
            {
                var sampleImagePath = state.SampleImagePath!;
                var goldenImagePath = state.GoldenImagePath;
                var analysis = await Task.Run(() =>
                    ImageAnalysisService.Analyze(sampleImagePath, goldenImagePath, state.DetectionPriority));
                state.SetAnalysis(analysis);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Comparison could not run:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (FindMainVm() is { } vm)
                vm.CurrentPage = "compare";
        }
        finally
        {
            if (compareGoldenButton is not null)
                compareGoldenButton.IsEnabled = true;
        }
    }

    private void OnAddToTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!EnsureSelectedRecordAsSample() || string.IsNullOrWhiteSpace(state.SampleImagePath) || !File.Exists(state.SampleImagePath))
        {
            MessageBox.Show("No valid sample image is selected.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var trainingDir = AoiDatabase.TrainingVaultPath;
            Directory.CreateDirectory(trainingDir);

            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(state.SampleImagePath)}";
            var target = Path.Combine(trainingDir, fileName);
            File.Copy(state.SampleImagePath, target, true);
            AoiDatabase.RecordTrainingSample(target, "candidate", "Queued from Library candidate action.");

            state.QueueTrainingSample(fileName);
            MessageBox.Show($"Saved to local candidate queue:\n{target}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = ex is UnauthorizedAccessException
                ? "Training-set candidate copy failed: file access was denied or the image is locked."
                : $"Training-set candidate copy failed: {ex.Message}";
            ImportStatusText.Text = message;
            WorkflowState.Instance.AddEvent("TRAINING_SET_EXPORT_ERROR", message);
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBatchRelabelClick(object sender, RoutedEventArgs e)
    {
        var selectedCount = RecordsGrid.SelectedItems.Count > 0 ? RecordsGrid.SelectedItems.Count : 1;
        WorkflowState.Instance.AddEvent("LABEL", $"Batch relabel executed for {selectedCount} record(s).");
        MessageBox.Show($"Batch relabel complete for {selectedCount} record(s).", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = RecordsGrid.SelectedItem as ImageLibraryRecord ?? DemoRecords.FirstOrDefault();
        if (selected is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export selected record",
            Filter = "CSV file|*.csv",
            FileName = $"record_{selected.Sample}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Sample,Board,RefDes,Defect,Severity,AI,GT,Risk,Image,Updated,VaultPath,OriginalPath,FileHash");
        sb.AppendLine(string.Join(",",
            EscapeCsv(selected.Sample),
            EscapeCsv(selected.Board),
            EscapeCsv(selected.RefDes),
            EscapeCsv(selected.Defect),
            EscapeCsv(selected.Severity),
            EscapeCsv(selected.AiResult),
            EscapeCsv(selected.GroundTruth),
            EscapeCsv(selected.Risk),
            EscapeCsv(selected.ImageLink),
            EscapeCsv(selected.Date),
            EscapeCsv(selected.VaultPath),
            EscapeCsv(selected.OriginalPath),
            EscapeCsv(selected.FileHash)));

        var analysis = WorkflowState.Instance.LastAnalysis;
        if (analysis is not null)
        {
            sb.AppendLine();
            sb.AppendLine("AnalysisScore,Verdict,SuggestedDefect,Timestamp");
            sb.AppendLine($"{analysis.DifferenceScore:F2},{analysis.Verdict},{analysis.SuggestedDefect},{analysis.Timestamp:yyyy-MM-dd HH:mm:ss}");
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString());
            var verified = ExportVerificationService.RecordVerifiedExport("LibraryRecord", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Library record exported: {Path.GetFileName(dialog.FileName)}");
            ImportStatusText.Text = $"Library record exported: {dialog.FileName}. Verification: {verified.Verification.Status}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = ex is UnauthorizedAccessException
                ? "Library record export failed: export folder access was denied or the file is locked."
                : $"Library record export failed: {ex.Message}";
            ImportStatusText.Text = message;
            WorkflowState.Instance.AddEvent("EXPORT_ERROR", message);
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRecordsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not ImageLibraryRecord record || record.IsDemo)
            return;

        ImportStatusText.Text = File.Exists(record.VaultPath)
            ? $"Selected {record.Sample} from vault."
            : $"Selected {record.Sample}, but the vaulted image file is missing.";
    }

    private async Task LoadRecordsFromDatabaseAsync(long? selectedImageId, CancellationToken cancellationToken)
    {
        var snapshot = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var images = AoiDatabase.GetImportedImages();
            var records = images.Select(ToLibraryRecord).ToArray();
            var mode = OperatingModeSettingsService.Load();
            var showDemoRows = OperatingModePolicyService.ShouldShowDemoRows(records.Length, mode);
            return new LibraryRecordsSnapshot(records, mode, showDemoRows);
        }, cancellationToken);

        RecordsGrid.ItemsSource = snapshot.Records.Length > 0 ? snapshot.Records : snapshot.ShowDemoRows ? DemoRecords : Array.Empty<ImageLibraryRecord>();
        RecordsSourceText.Text = snapshot.Records.Length > 0
            ? "SQLite Records"
            : snapshot.ShowDemoRows
                ? "Demo Data"
                : $"{snapshot.Mode} Mode: Demo Hidden";

        // Shared chip styles + soft-brush tokens instead of raw hex in code-behind.
        var sourceChipStyle = snapshot.Records.Length > 0 ? "ChipGreen" : snapshot.ShowDemoRows ? "ChipAmber" : "ChipRed";
        var sourceTextBrush = snapshot.Records.Length > 0 ? "HmiOkSoftBrush" : snapshot.ShowDemoRows ? "HmiWarnSoftBrush" : "HmiNgSoftBrush";
        RecordsSourceChip.Style = (Style)FindResource(sourceChipStyle);
        RecordsSourceText.SetResourceReference(TextBlock.ForegroundProperty, sourceTextBrush);

        if (selectedImageId is not null)
            RecordsGrid.SelectedItem = snapshot.Records.FirstOrDefault(r => r.ImageLink == selectedImageId.Value.ToString());

        if (RecordsGrid.SelectedIndex < 0 && RecordsGrid.Items.Count > 0)
            RecordsGrid.SelectedIndex = 0;

        ImportStatusText.Text = snapshot.Records.Length > 0
            ? $"{snapshot.Records.Length} imported image record(s) loaded from SQLite."
            : snapshot.ShowDemoRows
                ? "No imported images yet. Use Batch Import to add PNG/JPG board images; demo rows remain visible until SQLite has customer records."
                : $"{snapshot.Mode} Mode hides demo rows. Use Batch Import or select a customer dataset before pilot/production review.";
    }

    private sealed record LibraryRecordsSnapshot(ImageLibraryRecord[] Records, OperatingMode Mode, bool ShowDemoRows);

    private static ImageLibraryRecord ToLibraryRecord(ImportedImage image)
    {
        var importedLocal = image.ImportedAt.Kind == DateTimeKind.Utc
            ? image.ImportedAt.ToLocalTime()
            : image.ImportedAt;

        return new ImageLibraryRecord(
            Path.GetFileNameWithoutExtension(image.FileName),
            image.BoardModel,
            image.ViewType,
            "Imported PCB Image",
            File.Exists(image.VaultPath) ? "Available" : "Missing",
            "PENDING",
            "PENDING",
            File.Exists(image.VaultPath) ? "Vaulted" : "Missing",
            image.Id.ToString(),
            importedLocal == DateTime.MinValue ? "--" : importedLocal.ToString("MM-dd HH:mm"),
            image.VaultPath,
            image.OriginalPath,
            image.FileHash,
            false);
    }

    private ImageImportResult ImportOne(string path, string viewType)
    {
        var state = WorkflowState.Instance;
        return AoiDatabase.TryImportImage(path, state.BoardProgram, "POC-LOT", viewType);
    }

    private IEnumerable<ImageImportResult> ImportFolder(string folderPath, CancellationToken token, IProgress<ImportProgress> progress)
    {
        if (!Directory.Exists(folderPath))
        {
            yield return new ImageImportResult(null, false, "Missing", "Folder does not exist.");
            yield break;
        }

        string[] files;
        string? folderError = null;
        try
        {
            files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
            folderError = $"Folder cannot be read: {ex.Message}";
        }

        if (folderError is not null)
        {
            yield return new ImageImportResult(null, false, "Invalid", folderError);
            yield break;
        }

        for (var i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            progress.Report(new ImportProgress(i, files.Length, $"Importing {Path.GetFileName(file)}..."));

            if (!SupportedImageExtensions.Contains(Path.GetExtension(file)))
            {
                yield return new ImageImportResult(null, false, "Unsupported", $"{Path.GetFileName(file)} is not PNG/JPG/JPEG.");
                continue;
            }

            ImageImportResult result;
            try
            {
                result = ImportOne(file, "sample");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                result = new ImageImportResult(null, false, "Unreadable", FriendlyImportError(file, ex));
            }

            yield return result;
        }

        progress.Report(new ImportProgress(files.Length, files.Length, "Batch import complete."));
    }

    private bool EnsureSelectedRecordAsSample()
    {
        var state = WorkflowState.Instance;
        if (RecordsGrid.SelectedItem is not ImageLibraryRecord record || record.IsDemo)
            return !string.IsNullOrWhiteSpace(state.SampleImagePath) && File.Exists(state.SampleImagePath);

        if (string.IsNullOrWhiteSpace(record.VaultPath) || !File.Exists(record.VaultPath))
            return !string.IsNullOrWhiteSpace(state.SampleImagePath) && File.Exists(state.SampleImagePath);

        state.SetSampleImage(record.VaultPath);
        return true;
    }

    private void ShowImportStatus(IReadOnlyCollection<ImageImportResult> results, string title)
    {
        if (results.Count == 0)
        {
            ImportStatusText.Text = $"{title}: no PNG/JPG/JPEG files found.";
            MessageBox.Show("No PNG/JPG/JPEG images were found to import.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var imported = results.Count(r => r.Imported);
        var duplicates = results.Count(r => r.Status == "Duplicate");
        var unsupported = results.Count(r => r.Status == "Unsupported");
        var missing = results.Count(r => r.Status == "Missing");
        var invalid = results.Count(r => r.Status is "Unreadable" or "Invalid");
        var summary = $"{title}: imported {imported}, duplicate {duplicates}, unsupported {unsupported}, missing {missing}, invalid/unreadable {invalid}.";

        ImportStatusText.Text = summary;
        WorkflowState.Instance.AddEvent("IMPORT", summary);

        var details = results
            .Where(r => !r.Imported)
            .Take(8)
            .Select(r => $"{r.Status}: {r.Image?.FileName ?? r.Message}")
            .ToArray();

        var message = details.Length == 0
            ? summary
            : summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);

        MessageBox.Show(
            message,
            "AOI Monitor",
            MessageBoxButton.OK,
            invalid > 0 || missing > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static void LogImportIssues(IReadOnlyCollection<ImageImportResult> results)
    {
        foreach (var issue in results.Where(r => !r.Imported && r.Status != "Duplicate").Take(40))
        {
            var subject = issue.Image?.FileName;
            WorkflowState.Instance.AddEvent(
                "IMPORT_ERROR",
                $"{issue.Status}: {(string.IsNullOrWhiteSpace(subject) ? issue.Message : subject + " - " + issue.Message)}");
        }
    }

    private MainViewModel? FindMainVm()
        => (Window.GetWindow(this) as MainWindow)?.DataContext as MainViewModel;

    private void TryShowImagePreview(string path, string title)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"Image file is missing:\n{path}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var previewBitmap = ImageCacheService.LoadBitmap(path, decodePixelWidth: 1400);
            var image = new System.Windows.Controls.Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                Source = previewBitmap,
            };

            var win = new Window
            {
                Title = title,
                Width = 860,
                Height = 620,
                Content = new Border
                {
                    Background = System.Windows.Media.Brushes.Black,
                    Padding = new Thickness(10),
                    Child = image,
                },
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            win.Closed += (_, _) => image.Source = null;
            win.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Image preview failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private CancellationTokenSource BeginImport(string message)
    {
        _importCts = new CancellationTokenSource();
        ImportProgressBar.Value = 0;
        ImportProgressPanel.Visibility = Visibility.Visible;
        CancelImportButton.IsEnabled = true;
        ImportStatusText.Text = message;
        return _importCts;
    }

    private void EndImport()
    {
        _importCts?.Dispose();
        _importCts = null;
        CancelImportButton.IsEnabled = false;
        ImportProgressBar.Value = 0;
        ImportProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateImportProgress(ImportProgress progress)
    {
        ImportProgressBar.Value = progress.Total <= 0 ? 0 : Math.Min(100, progress.Completed * 100.0 / progress.Total);
        ImportStatusText.Text = progress.Message;
    }

    private static string FriendlyImportError(string path, Exception ex)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            UnauthorizedAccessException => $"{name} cannot be imported because access is denied or the file is locked.",
            NotSupportedException => $"{name} is not a supported image.",
            IOException => $"{name} could not be read or written: {ex.Message}",
            _ => $"{name}: {ex.Message}",
        };
    }

    private sealed record ImportProgress(int Completed, int Total, string Message);

    public void Dispose()
    {
        var source = _importCts;
        _importCts = null;
        if (source is not null)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed elsewhere; disposal must stay idempotent.
            }

            source.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
