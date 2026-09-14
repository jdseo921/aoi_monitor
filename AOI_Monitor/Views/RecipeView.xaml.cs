using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class RecipeView : UserControl, IReleasablePageResources, IAsyncNavigationPage
{
    private const double MinRoiPixels = 8;
    private readonly ObservableCollection<RecipeRoiRow> _rois = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string _backgroundImagePath = string.Empty;
    private BitmapSource? _backgroundBitmap;
    private RecipeRoiRow? _activeRoi;
    private Point _dragStart;
    private Rect _dragOriginal;
    private DragMode _dragMode = DragMode.None;
    private bool _isUpdatingFields;
    private bool _isRightPanning;
    private Point _panStart;
    private Point _panOrigin;
    private CancellationTokenSource? _refreshCancellation;

    public RecipeView()
    {
        InitializeComponent();
        PopulateRoiTypeCombo();
        RoiGrid.ItemsSource = _rois;
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        Unloaded += (_, _) => WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        RefreshFromState();
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _refreshCancellation.Token;
        RefreshFromState();
        await LoadLatestRecipeAsync(token);
    }

    public void RefreshFromState()
    {
        var state = WorkflowState.Instance;
        BoardProgramText.Text = state.BoardProgram;
        OperatorText.Text = state.OperatorWithRole;
        LockStatusText.Text = state.IsRecipeLocked ? "LOCKED" : "UNLOCKED";
        LockStatusText.Foreground = state.IsRecipeLocked ? Brushes.Orange : Brushes.LightGreen;
    }

    private void OnWorkflowStateChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshFromState);
    }

    private async Task LoadLatestRecipeAsync(CancellationToken cancellationToken)
    {
        var state = WorkflowState.Instance;
        try
        {
            var snapshot = await Task.Run(() => LoadLatestRecipeSnapshot(state.BoardProgram, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is null)
            {
                // Idle state is not an event: the editor's empty-state card owns the
                // "load a PCB image" instruction and RevisionText already reports that no
                // saved revision is loaded, so the status line stays empty until an
                // operator action produces an outcome worth reporting.
                RecipeStatusText.Text = string.Empty;
                return;
            }

            RecipeNameText.Text = snapshot.Document.RecipeName;
            _backgroundImagePath = snapshot.Document.BackgroundImagePath;
            PlacementToleranceText.Text = FormatDouble(snapshot.Document.PlacementToleranceMm);
            RotationToleranceText.Text = FormatDouble(snapshot.Document.RotationToleranceDeg);
            ComboBoxTokens.SelectByToken(IpcClassCombo, snapshot.Document.IpcClass);
            ComboBoxTokens.SelectByToken(LightingProfileCombo, snapshot.Document.LightingProfile);
            ComboBoxTokens.SelectByToken(FalseCallPolicyCombo, snapshot.Document.FalseCallPolicy);
            if (snapshot.BackgroundBitmap is not null)
                ApplyBackground(snapshot.Document.BackgroundImagePath, snapshot.BackgroundBitmap);

            _rois.Clear();
            foreach (var roi in snapshot.Document.Rois)
            {
                _rois.Add(RecipeRoiRow.FromDocument(roi, isSaved: true));
            }

            _activeRoi = _rois.FirstOrDefault();
            RoiGrid.SelectedItem = _activeRoi;
            UpdateFieldPanel();
            RenderRois();
            RevisionText.Text = $"Loaded revision {snapshot.Revision.Revision} saved {ToLocalDisplay(snapshot.Revision.CreatedAtUtc)} by {snapshot.Revision.OperatorId}.";
            RecipeStatusText.Text = $"Loaded {snapshot.Revision.RecipeName} with {_rois.Count} ROI(s).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecipeStatusText.Text = $"Recipe load failed: {ex.Message}";
        }
    }

    private static RecipeLoadSnapshot? LoadLatestRecipeSnapshot(string boardProgram, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = AoiDatabase.GetLatestRecipeRevision(boardProgram);
        if (revision is null || string.IsNullOrWhiteSpace(revision.RecipeJson))
            return null;

        var doc = JsonSerializer.Deserialize<RecipeDocument>(revision.RecipeJson);
        if (doc is null)
            return null;

        // Recipes saved by builds that predate ComboBoxTokens may carry Korean combo
        // tokens; heal them so token selection matches and the next save persists English.
        RecipeService.HealLocalizedTokens(doc);

        BitmapSource? bitmap = null;
        if (!string.IsNullOrWhiteSpace(doc.BackgroundImagePath) && File.Exists(doc.BackgroundImagePath))
            bitmap = ImageCacheService.LoadBitmap(doc.BackgroundImagePath, decodePixelWidth: 1600);

        return new RecipeLoadSnapshot(revision, doc, bitmap);
    }

    private void OnLoadImageClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("Load image"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Load recipe background image",
            Filter = "PCB image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };

        if (dialog.ShowDialog() != true)
            return;

        LoadBackground(dialog.FileName);
        WorkflowState.Instance.SetSampleImage(dialog.FileName);
        RecipeStatusText.Text = $"Background loaded: {Path.GetFileName(dialog.FileName)}";
    }

    private void LoadBackground(string path)
    {
        var bmp = ImageCacheService.LoadBitmap(path, decodePixelWidth: 1600);
        ApplyBackground(path, bmp);
    }

    private void ApplyBackground(string path, BitmapSource bmp)
    {
        _backgroundImagePath = path;
        _backgroundBitmap = bmp;
        RecipeImage.Source = bmp;
        RecipeImage.Width = bmp.PixelWidth;
        RecipeImage.Height = bmp.PixelHeight;
        ViewportCanvas.Width = bmp.PixelWidth;
        ViewportCanvas.Height = bmp.PixelHeight;
        RoiCanvas.Width = bmp.PixelWidth;
        RoiCanvas.Height = bmp.PixelHeight;
        RecipeEmptyStateCard.Visibility = Visibility.Collapsed;

        FitToView();
        RenderRois();
    }

    private void OnImportCentroidClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("Import centroid CSV"))
            return;

        if (_backgroundBitmap is null)
        {
            MessageBox.Show("Load a recipe background image before importing centroid data.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import pick-and-place centroid CSV",
            Filter = "Centroid CSV files|*.csv;*.txt|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var parsed = CentroidRoiImportService.ParseCentroidCsv(File.ReadAllText(dialog.FileName));
            var generated = CentroidRoiImportService.GenerateRois(
                parsed.Records,
                _backgroundBitmap.PixelWidth,
                _backgroundBitmap.PixelHeight,
                new CentroidRoiImportOptions());

            var existingIds = new HashSet<string>(_rois.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            foreach (var roiDoc in generated)
            {
                if (!existingIds.Add(roiDoc.Id))
                    continue;

                _rois.Add(RecipeRoiRow.FromDocument(roiDoc, isSaved: false));
                imported++;
            }

            _activeRoi = _rois.LastOrDefault();
            RoiGrid.SelectedItem = _activeRoi;
            UpdateFieldPanel();
            RenderRois();

            var skipped = generated.Count - imported;
            RecipeStatusText.Text = $"Centroid import: {imported} ROI(s) added from {Path.GetFileName(dialog.FileName)}.";
            var summary = $"{imported} ROIs imported from {parsed.Records.Count} rows ({parsed.Warnings.Count} warnings). Positions are approximate - review before saving.";
            if (skipped > 0)
                summary += $"\n{skipped} ROI(s) skipped because the recipe already contains their ids.";
            MessageBox.Show(summary, "AOI Monitor", MessageBoxButton.OK, imported > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            MessageBox.Show($"Centroid import failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ReleasePageResources()
    {
        _refreshCancellation?.Cancel();
        _backgroundBitmap = null;
        RecipeImage.Source = null;
        RecipeEmptyStateCard.Visibility = Visibility.Visible;
        RoiCanvas.Children.Clear();
        ImageCacheService.ClearOnPageUnload();
    }

    public void CancelWork() => _refreshCancellation?.Cancel();

    private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_backgroundBitmap is null || !EnsureUnlocked("Edit ROI"))
            return;

        ViewportCanvas.Focus();
        _dragStart = ClampToImage(e.GetPosition(RoiCanvas));
        var hit = HitTestRoi(_dragStart);
        if (hit is not null)
        {
            SelectRoi(hit);
            _dragOriginal = ToPixelRect(hit);
            _dragMode = IsResizeHandle(_dragStart, _dragOriginal) ? DragMode.Resize : DragMode.Move;
        }
        else
        {
            var roi = new RecipeRoiRow
            {
                Id = Guid.NewGuid().ToString("N"),
                RoiType = SelectedRoiType(),
                AiScoreThreshold = 0.65,
                X = _dragStart.X / _backgroundBitmap.PixelWidth,
                Y = _dragStart.Y / _backgroundBitmap.PixelHeight,
                Width = MinRoiPixels / _backgroundBitmap.PixelWidth,
                Height = MinRoiPixels / _backgroundBitmap.PixelHeight,
                IsSaved = false,
            };
            _rois.Add(roi);
            SelectRoi(roi);
            _dragOriginal = ToPixelRect(roi);
            _dragMode = DragMode.Draw;
        }

        RoiCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isRightPanning)
        {
            var panCurrent = e.GetPosition(ViewportCanvas);
            CanvasPan.X = _panOrigin.X + panCurrent.X - _panStart.X;
            CanvasPan.Y = _panOrigin.Y + panCurrent.Y - _panStart.Y;
            return;
        }

        if (_dragMode == DragMode.None || _activeRoi is null || _backgroundBitmap is null)
            return;

        var current = ClampToImage(e.GetPosition(RoiCanvas));
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        var rect = _dragOriginal;

        if (_dragMode == DragMode.Move)
        {
            rect.X = Math.Clamp(_dragOriginal.X + dx, 0, _backgroundBitmap.PixelWidth - _dragOriginal.Width);
            rect.Y = Math.Clamp(_dragOriginal.Y + dy, 0, _backgroundBitmap.PixelHeight - _dragOriginal.Height);
        }
        else
        {
            rect.Width = Math.Max(MinRoiPixels, _dragOriginal.Width + dx);
            rect.Height = Math.Max(MinRoiPixels, _dragOriginal.Height + dy);
            rect.Width = Math.Min(rect.Width, _backgroundBitmap.PixelWidth - rect.X);
            rect.Height = Math.Min(rect.Height, _backgroundBitmap.PixelHeight - rect.Y);
        }

        ApplyPixelRect(_activeRoi, rect);
        _activeRoi.IsSaved = false;
        UpdateFieldPanel();
        RoiGrid.Items.Refresh();
        RenderRois();
    }

    private void OnCanvasLeftUp(object sender, MouseButtonEventArgs e)
    {
        _dragMode = DragMode.None;
        RoiCanvas.ReleaseMouseCapture();
    }

    private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
    {
        _isRightPanning = true;
        _panStart = e.GetPosition(ViewportCanvas);
        _panOrigin = new Point(CanvasPan.X, CanvasPan.Y);
        ViewportCanvas.CaptureMouse();
    }

    private void OnCanvasRightUp(object sender, MouseButtonEventArgs e)
    {
        _isRightPanning = false;
        ViewportCanvas.ReleaseMouseCapture();
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(CanvasScale.ScaleX * (e.Delta > 0 ? 1.12 : 0.89));
    }

    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
            DeleteActiveRoi();
    }

    private void OnRoiSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoiGrid.SelectedItem is RecipeRoiRow roi)
            SelectRoi(roi);
    }

    private void SelectRoi(RecipeRoiRow roi)
    {
        _activeRoi = roi;
        RoiGrid.SelectedItem = roi;
        UpdateFieldPanel();
        RenderRois();
    }

    private void OnRoiParameterChanged(object sender, EventArgs e)
    {
        if (_isUpdatingFields || _activeRoi is null)
            return;

        if (WorkflowState.Instance.IsRecipeLocked)
            return;

        _activeRoi.RoiType = SelectedRoiType();
        _activeRoi.AiScoreThreshold = ParseDouble(AiThresholdText.Text, _activeRoi.AiScoreThreshold);
        _activeRoi.HeightMin = ParseDouble(HeightMinText.Text, _activeRoi.HeightMin);
        _activeRoi.HeightMax = ParseDouble(HeightMaxText.Text, _activeRoi.HeightMax);
        _activeRoi.VolumeMin = ParseDouble(VolumeMinText.Text, _activeRoi.VolumeMin);
        _activeRoi.VolumeMax = ParseDouble(VolumeMaxText.Text, _activeRoi.VolumeMax);
        _activeRoi.IsSaved = false;
        RoiGrid.Items.Refresh();
        RenderRois();
    }

    private void OnDeleteRoiClick(object sender, RoutedEventArgs e)
    {
        DeleteActiveRoi();
    }

    private void DeleteActiveRoi()
    {
        if (_activeRoi is null || !EnsureUnlocked("Delete ROI"))
            return;

        _rois.Remove(_activeRoi);
        _activeRoi = _rois.FirstOrDefault();
        RoiGrid.SelectedItem = _activeRoi;
        UpdateFieldPanel();
        RenderRois();
        RecipeStatusText.Text = "ROI deleted.";
    }

    private async void OnTestRunClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_backgroundImagePath) || !File.Exists(_backgroundImagePath))
        {
            MessageBox.Show("Load a recipe background image before running a test.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var state = WorkflowState.Instance;
        var testRunButton = sender as Button;
        if (testRunButton is not null)
            testRunButton.IsEnabled = false;
        try
        {
            // Test Run exercises the current in-editor recipe (including unsaved ROI/parameter
            // edits) by publishing a transient preview override the inspection engine reads,
            // instead of the last saved revision. Always cleared in finally.
            var document = BuildRecipeDocument();
            var preview = RecipeService.ParseDocument(
                document,
                "IN-EDITOR PREVIEW",
                WorkflowState.ToDisplay(state.DetectionPriority),
                state.BoardProgram,
                _backgroundImagePath,
                document.RecipeName,
                "in-editor preview");
            RecipeService.SetPreviewOverride(state.BoardProgram, preview);
            try
            {
                var sampleImagePath = _backgroundImagePath;
                state.SetSampleImage(sampleImagePath);
                RecipeStatusText.Text = "Test run in progress...";
                var analysis = await Task.Run(() =>
                    ImageAnalysisService.Analyze(sampleImagePath, state.GoldenImagePath, state.DetectionPriority));
                state.SetAnalysis(analysis);
                RecipeStatusText.Text = $"Test run (current unsaved edits): {analysis.Verdict}, score {analysis.DifferenceScore:F1}%.";
                MessageBox.Show(RecipeStatusText.Text, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                RecipeService.ClearPreviewOverride();
            }
        }
        catch (Exception ex)
        {
            RecipeStatusText.Text = "Test run failed.";
            MessageBox.Show($"Test run failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (testRunButton is not null)
                testRunButton.IsEnabled = true;
        }
    }

    private RecipeDocument BuildRecipeDocument()
    {
        var state = WorkflowState.Instance;
        return new RecipeDocument
        {
            RecipeName = string.IsNullOrWhiteSpace(RecipeNameText.Text) ? "AOI_RECIPE" : RecipeNameText.Text.Trim(),
            BoardProgram = state.BoardProgram,
            BackgroundImagePath = _backgroundImagePath,
            Rois = _rois.Select(r => r.ToDocument()).ToList(),
            PlacementToleranceMm = ParseDouble(PlacementToleranceText.Text, 0.020),
            RotationToleranceDeg = ParseDouble(RotationToleranceText.Text, 1.0),
            IpcClass = ComboBoxTokens.SelectedToken(IpcClassCombo, "IPC Class 2"),
            LightingProfile = ComboBoxTokens.SelectedToken(LightingProfileCombo, "Top bright field"),
            FalseCallPolicy = ComboBoxTokens.SelectedToken(FalseCallPolicyCombo, "Balanced: review benign visual noise"),
        };
    }

    private void OnSaveRecipeClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("Save recipe"))
            return;

        if (string.IsNullOrWhiteSpace(_backgroundImagePath) || !File.Exists(_backgroundImagePath))
        {
            MessageBox.Show("Load a recipe background image before saving.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var state = WorkflowState.Instance;
        var doc = BuildRecipeDocument();

        var json = JsonSerializer.Serialize(doc, _jsonOptions);
        var id = AoiDatabase.SaveRecipeRevision(
            doc.RecipeName,
            state.BoardProgram,
            state.OperatorWithRole,
            WorkflowState.ToDisplay(state.DetectionPriority),
            _backgroundImagePath,
            json);
        RecipeService.Invalidate(state.BoardProgram);

        foreach (var roi in _rois)
            roi.IsSaved = true;

        state.AddEvent("RECIPE", $"Recipe revision saved: {doc.RecipeName}, {doc.Rois.Count} ROI(s), rev id {id}.");
        RevisionText.Text = $"Saved revision id {id} at {DateTime.Now:yyyy-MM-dd HH:mm:ss} by {state.OperatorWithRole}.";
        RecipeStatusText.Text = $"Recipe saved with {doc.Rois.Count} ROI(s).";
        RoiGrid.Items.Refresh();
        RenderRois();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(CanvasScale.ScaleX * 1.2);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(CanvasScale.ScaleX / 1.2);
    private void OnFitClick(object sender, RoutedEventArgs e) => FitToView();

    private void RenderRois()
    {
        RoiCanvas.Children.Clear();
        if (_backgroundBitmap is null)
            return;

        foreach (var roi in _rois)
        {
            var rect = ToPixelRect(roi);
            var isActive = ReferenceEquals(roi, _activeRoi);
            var stroke = isActive ? Brushes.Yellow : roi.IsSaved ? Brushes.LimeGreen : Brushes.DeepSkyBlue;
            var shape = new System.Windows.Shapes.Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Stroke = stroke,
                StrokeThickness = isActive ? 3 : 2,
                Fill = Brushes.Transparent,
                Tag = roi,
            };

            Canvas.SetLeft(shape, rect.X);
            Canvas.SetTop(shape, rect.Y);
            RoiCanvas.Children.Add(shape);

            var label = new TextBlock
            {
                Text = roi.RoiType,
                Foreground = stroke,
                Background = Brushes.Black,
                FontSize = (double)Application.Current.FindResource("HmiFontSizeBody"),
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(3, 1, 3, 1),
            };
            Canvas.SetLeft(label, rect.X);
            Canvas.SetTop(label, Math.Max(0, rect.Y - 20));
            RoiCanvas.Children.Add(label);
        }
    }

    private void UpdateFieldPanel()
    {
        _isUpdatingFields = true;
        try
        {
            var roi = _activeRoi;
            SetComboText(roi?.RoiType ?? "Presence");
            AiThresholdText.Text = FormatDouble(roi?.AiScoreThreshold ?? 0.65);
            HeightMinText.Text = FormatDouble(roi?.HeightMin ?? 0);
            HeightMaxText.Text = FormatDouble(roi?.HeightMax ?? 0);
            VolumeMinText.Text = FormatDouble(roi?.VolumeMin ?? 0);
            VolumeMaxText.Text = FormatDouble(roi?.VolumeMax ?? 0);
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }

    private RecipeRoiRow? HitTestRoi(Point point)
    {
        for (var i = _rois.Count - 1; i >= 0; i--)
        {
            if (ToPixelRect(_rois[i]).Contains(point))
                return _rois[i];
        }

        return null;
    }

    private Rect ToPixelRect(RecipeRoiRow roi)
    {
        if (_backgroundBitmap is null)
            return Rect.Empty;

        return new Rect(
            roi.X * _backgroundBitmap.PixelWidth,
            roi.Y * _backgroundBitmap.PixelHeight,
            roi.Width * _backgroundBitmap.PixelWidth,
            roi.Height * _backgroundBitmap.PixelHeight);
    }

    private void ApplyPixelRect(RecipeRoiRow roi, Rect rect)
    {
        if (_backgroundBitmap is null)
            return;

        roi.X = Math.Round(rect.X / _backgroundBitmap.PixelWidth, 5);
        roi.Y = Math.Round(rect.Y / _backgroundBitmap.PixelHeight, 5);
        roi.Width = Math.Round(rect.Width / _backgroundBitmap.PixelWidth, 5);
        roi.Height = Math.Round(rect.Height / _backgroundBitmap.PixelHeight, 5);
    }

    private Point ClampToImage(Point point)
    {
        if (_backgroundBitmap is null)
            return point;

        return new Point(
            Math.Clamp(point.X, 0, _backgroundBitmap.PixelWidth),
            Math.Clamp(point.Y, 0, _backgroundBitmap.PixelHeight));
    }

    private bool IsResizeHandle(Point point, Rect rect)
    {
        var tolerance = Math.Max(10, 16 / Math.Max(0.1, CanvasScale.ScaleX));
        return Math.Abs(point.X - rect.Right) <= tolerance && Math.Abs(point.Y - rect.Bottom) <= tolerance;
    }

    private void FitToView()
    {
        if (_backgroundBitmap is null)
            return;

        var host = ViewportCanvas.Parent as FrameworkElement;
        var availableWidth = Math.Max(100, host?.ActualWidth - 20 ?? 900);
        var availableHeight = Math.Max(100, host?.ActualHeight - 20 ?? 520);
        var scale = Math.Min(availableWidth / _backgroundBitmap.PixelWidth, availableHeight / _backgroundBitmap.PixelHeight);
        SetZoom(Math.Clamp(scale, 0.1, 2.0));
        CanvasPan.X = 0;
        CanvasPan.Y = 0;
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.1, 6.0);
        CanvasScale.ScaleX = zoom;
        CanvasScale.ScaleY = zoom;
        ZoomText.Text = $"{zoom:P0}";
    }

    private bool EnsureUnlocked(string action)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanEditRecipes, action, out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!WorkflowState.Instance.IsRecipeLocked)
            return true;

        MessageBox.Show(
            $"{action} is blocked because the recipe is locked. Unlock the recipe before editing.",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private string SelectedRoiType()
        => ComboBoxTokens.SelectedToken(RoiTypeCombo, "Presence");

    private void PopulateRoiTypeCombo()
    {
        var selected = SelectedRoiType();
        RoiTypeCombo.Items.Clear();
        // Only classes this product can inspect for become ROI types. Classes catalogued for
        // labelling/reporting that belong to other machine types (SPI, X-ray, ICT) are excluded
        // so the editor never implies an inspection capability the software does not have.
        foreach (var roiType in new[] { "Presence", "Polarity" }.Concat(DefectTaxonomyService.InspectableCanonicalClasses()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RoiTypeCombo.Items.Add(new ComboBoxItem { Content = roiType, Tag = roiType });
        }

        SetComboText(string.IsNullOrWhiteSpace(selected) ? "Presence" : selected);
    }

    private void SetComboText(string value)
    {
        if (!ComboBoxTokens.SelectByToken(RoiTypeCombo, value))
            RoiTypeCombo.SelectedIndex = 0;
    }

    private static double ParseDouble(string text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string FormatDouble(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ToLocalDisplay(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return local == DateTime.MinValue ? "--" : local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private enum DragMode
    {
        None,
        Draw,
        Move,
        Resize,
    }

    public sealed class RecipeRoiRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string RoiType { get; set; } = "Presence";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double AiScoreThreshold { get; set; } = 0.65;
        public double HeightMin { get; set; }
        public double HeightMax { get; set; }
        public double VolumeMin { get; set; }
        public double VolumeMax { get; set; }
        public bool IsSaved { get; set; }
        public bool Enabled { get; set; } = true;

        public static RecipeRoiRow FromDocument(RecipeRoiDocument doc, bool isSaved)
        {
            return new RecipeRoiRow
            {
                Id = doc.Id,
                Name = doc.Name,
                RoiType = doc.RoiType,
                X = doc.X,
                Y = doc.Y,
                Width = doc.Width,
                Height = doc.Height,
                AiScoreThreshold = doc.AiScoreThreshold,
                HeightMin = doc.HeightMin,
                HeightMax = doc.HeightMax,
                VolumeMin = doc.VolumeMin,
                VolumeMax = doc.VolumeMax,
                Enabled = doc.Enabled,
                IsSaved = isSaved,
            };
        }

        public RecipeRoiDocument ToDocument()
        {
            return new RecipeRoiDocument
            {
                Id = Id,
                Name = string.IsNullOrWhiteSpace(Name) ? Id : Name,
                RoiType = RoiType,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                AiScoreThreshold = AiScoreThreshold,
                HeightMin = HeightMin,
                HeightMax = HeightMax,
                VolumeMin = VolumeMin,
                VolumeMax = VolumeMax,
                Enabled = Enabled,
            };
        }
    }

    private sealed record RecipeLoadSnapshot(
        RecipeRevisionRecord Revision,
        RecipeDocument Document,
        BitmapSource? BackgroundBitmap);
}
