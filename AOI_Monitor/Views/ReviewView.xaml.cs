using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ReviewView : UserControl
{
    private bool _overlayVisible = true;
    private bool _zoomed;

    public sealed record ReviewQueueItem(
        string Priority, string Sample, string AiResult, string GroundTruth, string Defect, string RefDes, string Risk);

    private static readonly ReviewQueueItem[] QueueItems =
    {
        new("1", "IMG_0241", "OK", "NG", "Solder Bridge",  "U107", "Escape"),
        new("2", "IMG_0177", "OK", "NG", "Polarity Error", "D12",  "Escape"),
        new("3", "IMG_0188", "NG", "NG", "Insuff Solder",  "C684", "Verified NG"),
        new("4", "IMG_0164", "NG", "NG", "Tombstone",      "R88",  "Verified NG"),
        new("5", "IMG_0182", "NG", "OK", "Pin Height Err", "CN8",  "False Call"),
    };

    public ReviewView()
    {
        InitializeComponent();
        QueueGrid.ItemsSource = QueueItems;
        QueueGrid.SelectedIndex = 0;
        WorkflowState.Instance.StateChanged += OnStateChanged;
        Unloaded += (_, _) => WorkflowState.Instance.StateChanged -= OnStateChanged;
    }

    public void RefreshFromState()
    {
        QueueGrid.Items.Refresh();
    }

    private void OnStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshFromState);

    // Risk chips in the queue reuse the shared adaptive-status chip styles instead of
    // page-local color styles. Pure presentation mapping: risk state -> shared style key
    // and shared soft foreground brush, swapped via FindResource (never raw hexes).
    private void OnRiskChipLoaded(object sender, RoutedEventArgs e)
        => ApplyRiskChipStyle(sender as Border);

    private void OnRiskChipDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyRiskChipStyle(sender as Border);

    private void ApplyRiskChipStyle(Border? chip)
    {
        if (chip is null) return;

        var risk = (chip.DataContext as ReviewQueueItem)?.Risk;
        var (chipStyleKey, textBrushKey) = risk switch
        {
            "Escape" => ("HmiAdaptiveStatusNg", "HmiNgSoftBrush"),
            "False Call" => ("HmiAdaptiveStatusWarning", "HmiWarnSoftBrush"),
            "Verified NG" => ("HmiAdaptiveStatusInfo", "HmiInfoSoftBrush"),
            _ => ("HmiAdaptiveStatusUnavailable", "HmiTextBodyBrush"),
        };

        chip.Style = (Style)FindResource(chipStyleKey);
        if (chip.Child is TextBlock text)
            text.SetResourceReference(TextBlock.ForegroundProperty, textBrushKey);
    }

    private void OnOverlayClick(object sender, RoutedEventArgs e)
    {
        _overlayVisible = !_overlayVisible;
        ReviewCanvasViewbox.Opacity = _overlayVisible ? 1.0 : 0.7;
    }

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        _zoomed = !_zoomed;
        var scale = _zoomed ? 1.25 : 1.0;
        ReviewZoomTransform.ScaleX = scale;
        ReviewZoomTransform.ScaleY = scale;
    }

    private void OnGoldenCompareClick(object sender, RoutedEventArgs e)
    {
        if ((Window.GetWindow(this) as MainWindow)?.DataContext is MainViewModel vm)
            vm.CurrentPage = "compare";
    }

    private void OnRoiCropClick(object sender, RoutedEventArgs e)
    {
        var sample = WorkflowState.Instance.SampleImagePath;
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
        {
            MessageBox.Show("Load a sample image first from the Library page.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var bmp = LoadBitmapOnLoad(sample!);
            int x = (int)(bmp.PixelWidth * 0.35);
            int y = (int)(bmp.PixelHeight * 0.35);
            int w = (int)(bmp.PixelWidth * 0.3);
            int h = (int)(bmp.PixelHeight * 0.3);
            var roi = new CroppedBitmap(bmp, new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h)));

            var dialog = new SaveFileDialog
            {
                Filter = "PNG image|*.png",
                FileName = $"roi_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Title = "Save ROI crop",
            };

            if (dialog.ShowDialog() != true) return;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(roi));
            using var fs = File.Create(dialog.FileName);
            encoder.Save(fs);

            WorkflowState.Instance.AddEvent("ROI", $"ROI crop saved: {Path.GetFileName(dialog.FileName)}");
            MessageBox.Show("ROI crop saved.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(
                $"Could not create the ROI crop. The image may be unreadable or the destination is not writable.\n\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static BitmapImage LoadBitmapOnLoad(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var history = WorkflowState.Instance.History;
        if (history.Count == 0)
        {
            MessageBox.Show("No history entries yet.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = history.TakeLast(20).Select(h => $"[{h.Timestamp:HH:mm:ss}] {h.Category}: {h.Message}");
        MessageBox.Show(string.Join(Environment.NewLine, lines), "Workflow History", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnConfirmNgClick(object sender, RoutedEventArgs e) => LogDisposition("Confirm NG");
    private void OnFalseCallClick(object sender, RoutedEventArgs e) => LogDisposition("Mark False Call");
    private void OnPossibleEscapeClick(object sender, RoutedEventArgs e) => LogDisposition("Mark Possible Escape");
    private void OnHoldClick(object sender, RoutedEventArgs e) => LogDisposition("Hold for 2nd Review");

    // Operator hotkeys (verification-station ergonomics): 1=Confirm NG, 2=False Call,
    // 3=Possible Escape, 4=Hold. Suppressed while a text/editable field has focus so typing
    // is never hijacked. Calls the same handlers as the buttons — no separate logic.
    private void OnReviewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditableElementFocused()) return;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: OnConfirmNgClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: OnFalseCallClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D3 or Key.NumPad3: OnPossibleEscapeClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D4 or Key.NumPad4: OnHoldClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private static bool IsEditableElementFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.Primitives.TextBoxBase
            || focused is PasswordBox
            || (focused is ComboBox { IsEditable: true });
    }

    private void OnSendTrainingClick(object sender, RoutedEventArgs e)
    {
        var sample = WorkflowState.Instance.SampleImagePath;
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
        {
            MessageBox.Show("No sample image loaded.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var targetDir = AoiDatabase.TrainingVaultPath;
            Directory.CreateDirectory(targetDir);

            var target = Path.Combine(targetDir, $"review_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(sample)}");
            File.Copy(sample, target, true);
            AoiDatabase.RecordTrainingSample(target, "review_candidate", "Queued from Review candidate action.");
            WorkflowState.Instance.QueueTrainingSample(Path.GetFileName(target));
            MessageBox.Show("Sample copied to the local candidate queue.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Could not copy the sample to the training queue. The file may be locked or the destination is not writable.\n\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LogDisposition(string action)
    {
        var state = WorkflowState.Instance;
        var analysis = state.LastAnalysis;
        if (analysis is null)
        {
            MessageBox.Show("Run image comparison before recording disposition.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (action == "Confirm NG" && analysis.Confidence < 0.70)
        {
            MessageBox.Show(
                "NG confirmation blocked because confidence is below 70%. Use Hold for 2nd Review or gather more evidence.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (action == "Mark False Call" && analysis.Confidence >= 0.85 && analysis.Verdict == "NG")
        {
            MessageBox.Show(
                "False-call override blocked for high-confidence NG. Escalate as Hold for 2nd Review.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        state.AddDisposition(action);

        var logDir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(logDir);
        var file = Path.Combine(logDir, "review_disposition_log.csv");

        try
        {
            if (!File.Exists(file))
            {
                File.WriteAllText(
                    file,
                    "TimestampUtc,StationId,OperatorId,Sample,Golden,Verdict,Confidence,Policy,ModelVersion,DecisionReason,Action\n");
            }

            File.AppendAllText(
                file,
                string.Join(",",
                    DateTime.UtcNow.ToString("O"),
                    EscapeCsv(state.StationId),
                    EscapeCsv(state.OperatorWithRole),
                    EscapeCsv(Path.GetFileName(analysis.SamplePath)),
                    EscapeCsv(analysis.GoldenPath is null ? "none" : Path.GetFileName(analysis.GoldenPath)),
                    EscapeCsv(analysis.Verdict),
                    analysis.Confidence.ToString("F4"),
                    EscapeCsv(analysis.PolicyName),
                    EscapeCsv(analysis.ModelVersion),
                    EscapeCsv(analysis.DecisionReason),
                    EscapeCsv(action)) + Environment.NewLine);
            ExportVerificationService.RecordVerifiedExport("ReviewDispositionLog", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddEvent("EXPORT", $"Disposition log write failed: {ex.Message}");
            MessageBox.Show(
                $"The disposition was recorded in this session, but the CSV log could not be written. It may be open in Excel or read-only.\n\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            MachineInterfaceExportService.ExportDispositionEvent(action, analysis);
        }
        catch (Exception ex)
        {
            state.AddEvent("INTEGRATION", $"Disposition export failed: {ex.Message}");
        }

        MessageBox.Show($"Disposition recorded: {action}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}
