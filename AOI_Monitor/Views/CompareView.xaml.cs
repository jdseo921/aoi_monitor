using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class CompareView : UserControl
{
    private bool _defectOverlayVisible = true;
    private bool _goldenOverlayVisible = true;
    private bool _zoomed;

    /// <summary>Typed presentation row for the comparison findings table.</summary>
    private sealed record CompareFindingRow(string Region, string Defect, string Golden, string Judgement);

    private static readonly CompareFindingRow[] Findings =
    {
        new("U107 pin row B",   "Bridge-like solder mass", "Separated joints", "NG"),
        new("U107 lower-right", "Excess highlight",        "Normal pad edge",  "Review"),
        new("Board fiducial",   "Aligned",                 "Aligned",          "OK"),
        new("Connector CN8",    "No difference",           "No difference",    "OK"),
    };

    public CompareView()
    {
        InitializeComponent();
        FindingsGrid.ItemsSource = Findings;
        WorkflowState.Instance.StateChanged += OnStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        Unloaded += OnUnloaded;
        RefreshFromState();
    }

    public void RefreshFromState()
    {
        var state = WorkflowState.Instance;
        if (!string.IsNullOrWhiteSpace(state.SampleImagePath))
            DefectSubtitleText.Text = $"{Path.GetFileName(state.SampleImagePath)} / loaded sample";

        if (!string.IsNullOrWhiteSpace(state.GoldenImagePath))
            GoldenSubtitleText.Text = $"{Path.GetFileName(state.GoldenImagePath)} / loaded golden";

        if (state.LastAnalysis is { } a)
        {
            DiffScoreText.Text = $"{a.DifferenceScore:F0}%";
            DiffSummaryText.Text = $"{a.Verdict} - {a.SuggestedDefect} | confidence {a.Confidence:P0}";

            var judgement = ToChipVerdict(a.Verdict);
            var topEvidence = a.Evidence.Take(3).ToArray();

            var rows = new List<CompareFindingRow>();
            rows.AddRange(a.Defects.Select(defect => new CompareFindingRow(
                string.IsNullOrWhiteSpace(defect.RoiId) ? "Defect ROI" : defect.RoiId,
                $"{defect.DefectType} {defect.Confidence:P0}",
                string.IsNullOrWhiteSpace(defect.RoiType) ? $"Box {defect.BoundingBox.X:P0},{defect.BoundingBox.Y:P0}" : defect.RoiType,
                ToChipVerdict(defect.JudgmentStatus))));
            rows.Add(new CompareFindingRow("Decision", a.DecisionReason, $"Policy: {a.PolicyName}", judgement));
            rows.Add(new CompareFindingRow("Score vs Threshold", $"{a.DifferenceScore:F1}%", $"R {a.ReviewThreshold:F1}% / NG {a.NgThreshold:F1}%", judgement));
            rows.Add(new CompareFindingRow("Hotspot ROI", $"x={a.Hotspot.X:P0}, y={a.Hotspot.Y:P0}", $"w={a.Hotspot.Width:P0}, h={a.Hotspot.Height:P0}", judgement));
            rows.Add(new CompareFindingRow("Evidence 1", topEvidence.Length > 0 ? topEvidence[0] : "-", "", judgement));
            rows.Add(new CompareFindingRow("Evidence 2", topEvidence.Length > 1 ? topEvidence[1] : "-", "", judgement));
            rows.Add(new CompareFindingRow("Evidence 3", topEvidence.Length > 2 ? topEvidence[2] : "-", "", judgement));
            FindingsGrid.ItemsSource = rows;

            SetFindingsSource("Analysis Result", "HmiAdaptiveStatusOk", "HmiOkSoftBrush");
        }
        else
        {
            SetFindingsSource("Demo Data", "HmiAdaptiveStatusSimulated", "HmiSimulatedSoftBrush");
        }

        ApplyLearnedVisualComparison(state.LastAnalysis);
    }

    private static string ToChipVerdict(string verdict)
    {
        return verdict.ToUpperInvariant() switch
        {
            "NG" => "NG",
            "OK" => "OK",
            _ => "Review",
        };
    }

    // The findings source chip and the per-row result chips reuse the shared
    // adaptive-status styles instead of page-local colors. Pure presentation mapping:
    // state -> shared style key and shared soft foreground brush, swapped via
    // FindResource (never raw hexes).
    private void SetFindingsSource(string label, string chipStyleKey, string textBrushKey)
    {
        FindingsSourceText.Text = label;
        FindingsSourceText.SetResourceReference(TextBlock.ForegroundProperty, textBrushKey);
        FindingsSourceChip.Style = (Style)FindResource(chipStyleKey);
    }

    private void OnResultChipLoaded(object sender, RoutedEventArgs e)
        => ApplyResultChipStyle(sender as Border);

    private void OnResultChipDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyResultChipStyle(sender as Border);

    private void ApplyResultChipStyle(Border? chip)
    {
        if (chip is null) return;

        var judgement = (chip.DataContext as CompareFindingRow)?.Judgement;
        var (chipStyleKey, textBrushKey) = judgement switch
        {
            "NG" => ("HmiAdaptiveStatusNg", "HmiNgSoftBrush"),
            "OK" => ("HmiAdaptiveStatusOk", "HmiOkSoftBrush"),
            "Review" => ("HmiAdaptiveStatusWarning", "HmiWarnSoftBrush"),
            _ => ("HmiAdaptiveStatusUnavailable", "HmiTextBodyBrush"),
        };

        chip.Style = (Style)FindResource(chipStyleKey);
        if (chip.Child is TextBlock text)
            text.SetResourceReference(TextBlock.ForegroundProperty, textBrushKey);
    }

    public void ExportPair()
    {
        OnExportPairClick(this, new RoutedEventArgs());
    }

    private void OnStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshFromState);

    private void OnInspectionConfigurationChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshFromState);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
    }

    private void OnOpenDefectImageViewerClick(object sender, RoutedEventArgs e)
        => AOI_Monitor.ImageViewerWindow.ShowFromVisual(this, "Defect Sample Image / Overlay", DefectImageViewport);

    private void OnOpenGoldenImageViewerClick(object sender, RoutedEventArgs e)
        => AOI_Monitor.ImageViewerWindow.ShowFromVisual(this, "Golden Reference Image / Overlay", GoldenImageViewport);

    private void OnSyncZoomClick(object sender, RoutedEventArgs e)
    {
        _zoomed = !_zoomed;
        var scale = _zoomed ? 1.2 : 1.0;
        DefectZoomTransform.ScaleX = scale;
        DefectZoomTransform.ScaleY = scale;
        GoldenZoomTransform.ScaleX = scale;
        GoldenZoomTransform.ScaleY = scale;
    }

    private void OnSyncPanClick(object sender, RoutedEventArgs e)
    {
        DefectZoomTransform.ScaleX = 1;
        DefectZoomTransform.ScaleY = 1;
        GoldenZoomTransform.ScaleX = 1;
        GoldenZoomTransform.ScaleY = 1;
        _zoomed = false;
    }

    private async void OnShowDifferenceClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        var configuration = InspectionModelConfigurationService.Load();
        if (string.IsNullOrWhiteSpace(state.SampleImagePath) ||
            (!configuration.IsLearnedVisualModelSelected && string.IsNullOrWhiteSpace(state.GoldenImagePath)))
        {
            MessageBox.Show(
                configuration.IsLearnedVisualModelSelected
                    ? "Load a sample image first. The selected Learned PCB Visual Model supplies the learned reference artifact."
                    : "Load sample and golden images from Library > Open Record / Compare Golden first.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var showDifferenceButton = sender as Button;
        if (showDifferenceButton is not null)
            showDifferenceButton.IsEnabled = false;
        try
        {
            var sampleImagePath = state.SampleImagePath!;
            var goldenImagePath = state.GoldenImagePath;
            var result = await Task.Run(() =>
                InspectionEngineFactory.Create().Analyze(sampleImagePath, goldenImagePath, state.DetectionPriority));
            state.SetAnalysis(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(
                $"Could not analyze the images. One of them may be unreadable or corrupt.\n\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            if (showDifferenceButton is not null)
                showDifferenceButton.IsEnabled = true;
        }

        RefreshFromState();
    }

    private void OnAiOverlayClick(object sender, RoutedEventArgs e)
    {
        _defectOverlayVisible = !_defectOverlayVisible;
        DefectCanvasViewbox.Opacity = _defectOverlayVisible ? 1.0 : 0.72;
    }

    private void OnGtOverlayClick(object sender, RoutedEventArgs e)
    {
        _goldenOverlayVisible = !_goldenOverlayVisible;
        GoldenCanvasViewbox.Opacity = _goldenOverlayVisible ? 0.88 : 0.62;
    }

    private void OnExportPairClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export comparison snapshot",
            Filter = "PNG image|*.png",
            FileName = $"compare_pair_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var rtb = new RenderTargetBitmap((int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(dialog.FileName))
            {
                encoder.Save(fs);
            }

            var verified = ExportVerificationService.RecordVerifiedExport("ComparisonSnapshot", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Comparison pair exported: {Path.GetFileName(dialog.FileName)}; verification={verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(
                $"Could not export the comparison snapshot. The destination may be locked or not writable.\n\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApplyLearnedVisualComparison(AnalysisResult? analysis)
    {
        var configuration = InspectionModelConfigurationService.Load();
        if (!configuration.IsLearnedVisualModelSelected ||
            LearnedVisualModelRegistryService.GetActiveLearnedVisualModel() is not { } active)
        {
            ShowDemoComparisonCanvases();
            return;
        }

        var state = WorkflowState.Instance;
        SetImageSource(DefectActualImage, state.SampleImagePath);
        SetImageSource(GoldenLearnedReferenceImage, LearnedVisualModelRegistryService.ArtifactPath(active.Model, "learned_reference.png"));
        SetImageSource(LearnedToleranceImage, LearnedVisualModelRegistryService.ArtifactPath(active.Model, "tolerance_map.png"));
        SetImageSource(LearnedAnomalyMapImage, LearnedVisualModelRegistryService.ArtifactPath(active.Model, "anomaly_threshold_map.png"));

        DefectCanvasViewbox.Visibility = DefectActualImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        GoldenCanvasViewbox.Visibility = GoldenLearnedReferenceImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        DefectActualImage.Visibility = DefectActualImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        GoldenLearnedReferenceImage.Visibility = GoldenLearnedReferenceImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        LearnedArtifactsPanel.Visibility = Visibility.Visible;

        DefectSubtitleText.Text = string.IsNullOrWhiteSpace(state.SampleImagePath)
            ? "No inspected image loaded"
            : $"{Path.GetFileName(state.SampleImagePath)} / inspected image";
        GoldenSubtitleText.Text = "learned_reference.png / image-only Stage 1";
        LearnedCompareEvidenceText.Text = analysis is not null &&
            string.Equals(analysis.InspectionEngine, ImageOnlyPcbLearningService.EngineName, StringComparison.OrdinalIgnoreCase)
                ? string.Join(" ", analysis.Evidence.Take(4))
                : string.Join(" ", LearnedVisualModelRegistryService.BuildEvidenceLines(active.Model));

        SetFindingsSource("Learned Visual Model", "HmiAdaptiveStatusSimulated", "HmiSimulatedSoftBrush");
    }

    private void ShowDemoComparisonCanvases()
    {
        DefectCanvasViewbox.Visibility = Visibility.Visible;
        GoldenCanvasViewbox.Visibility = Visibility.Visible;
        DefectActualImage.Visibility = Visibility.Collapsed;
        GoldenLearnedReferenceImage.Visibility = Visibility.Collapsed;
        DefectActualImage.Source = null;
        GoldenLearnedReferenceImage.Source = null;
        LearnedToleranceImage.Source = null;
        LearnedAnomalyMapImage.Source = null;
        LearnedArtifactsPanel.Visibility = Visibility.Collapsed;
    }

    private static void SetImageSource(Image image, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            image.Source = null;
            return;
        }

        try
        {
            image.Source = ImageCacheService.LoadBitmap(path, decodePixelWidth: 900, cache: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            image.Source = null;
        }
    }
}
