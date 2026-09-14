using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ProfileView : UserControl, IReleasablePageResources, IDisposable
{
    private const double SurfaceHeightScale = 0.4;
    private const int MaxSurfaceDimension = 160;

    private readonly Dictionary<(int X, int Y), double> _points = new();
    private readonly ObservableCollection<Profile3DDefectRow> _defectRows = new();
    private int _minX;
    private int _maxX;
    private int _minY;
    private int _maxY;
    private double _minHeight;
    private double _maxHeight;
    private double _meanHeight;
    private string _currentCsvPath = string.Empty;
    private (int X, int Y, double Height)? _selectedPoint;
    private Profile3DAcceptanceRun? _lastAcceptanceRun;
    private CancellationTokenSource? _acceptanceCancellation;

    // Interactive 3D view state (rotate / zoom / pan).
    private double _yaw;
    private double _pitch;
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _isRotating;
    private bool _isPanning;
    private Point _lastMouse;
    private bool _suppressListSync;

    public ProfileView()
    {
        InitializeComponent();
        DefectListGrid.ItemsSource = _defectRows;
        UpdateSourceEvidenceBadges();
    }

    private void OnLoadCsvClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load sample 3D height-map CSV",
            Filter = "CSV height map|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            LoadHeightMap(dialog.FileName);
            WorkflowState.Instance.AddEvent("PROFILE_3D", $"Loaded sample height-map CSV: {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"CSV load failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("PROFILE_3D_ERROR", $"Height-map CSV load failed: {ex.Message}");
            MessageBox.Show($"Could not load height-map CSV:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadHeightMap(string path)
    {
        var loaded = ParseHeightMap(path);
        if (loaded.Count == 0)
            throw new InvalidOperationException("No valid x,y,height rows were found.");

        _points.Clear();
        foreach (var point in loaded)
            _points[(point.X, point.Y)] = point.Height;

        _currentCsvPath = path;
        _minX = _points.Keys.Min(p => p.X);
        _maxX = _points.Keys.Max(p => p.X);
        _minY = _points.Keys.Min(p => p.Y);
        _maxY = _points.Keys.Max(p => p.Y);
        _minHeight = _points.Values.Min();
        _maxHeight = _points.Values.Max();
        _meanHeight = _points.Values.Average();
        _selectedPoint = loaded.OrderByDescending(p => p.Height).First();

        CsvNameText.Text = Path.GetFileName(path);
        HeightRangeText.Text = $"{_minHeight:F3} / {_maxHeight:F3}";
        LegendMinText.Text = _minHeight.ToString("F3", CultureInfo.InvariantCulture);
        LegendMaxText.Text = _maxHeight.ToString("F3", CultureInfo.InvariantCulture);
        MapStatusText.Text = $"{_points.Count:N0} height samples";
        EmptyMapText.Visibility = Visibility.Collapsed;
        TopDownMapInset.Visibility = Visibility.Visible;
        HeightScaleLegend.Visibility = Visibility.Visible;
        ViewHintChip.Visibility = Visibility.Visible;
        HeightMapImage.Source = BuildHeatMapBitmap();
        BuildSurfaceModel();
        BuildDefectRows();
        RefreshSelectionUi();
        DrawProfileLine();
        StatusText.Text = "Sample height-map loaded. Sample Data Mode only; 3D Camera Not Connected.";
        UpdateSourceEvidenceBadges();
    }

    private void OnProfileSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var mode = GetSelectedSourceMode();
        UpdateSourceEvidenceBadges();
        StatusText.Text = mode switch
        {
            "Sample CSV" => string.IsNullOrWhiteSpace(_currentCsvPath)
                ? "Sample CSV source selected. Load a height-map CSV before running 3D acceptance."
                : "Sample CSV source selected. This is simulation/sample evidence only.",
            "Pilot Hardware Adapter" => "Pilot hardware adapter selected. No vendor SDK is configured; this does not validate real 3D hardware.",
            _ => "No 3D profile source selected.",
        };
    }

    /// <summary>
    /// The source badge states the evidence CLASS of the selected source (the combo already
    /// names the source itself): SAMPLE DATA for the CSV path, BOUNDARY ONLY for the pilot
    /// adapter (no vendor SDK is configured, so it cannot validate real 3D hardware), and
    /// NO SOURCE when nothing is selected.
    /// </summary>
    private void UpdateSourceEvidenceBadges()
    {
        var mode = GetSelectedSourceMode();
        switch (mode)
        {
            case "Sample CSV":
                ProfileSourceBadge.Style = (Style)FindResource("HmiEvidenceSampleData");
                ProfileSourceBadgeText.Text = "SAMPLE DATA";
                ProfileConnectionBadge.Style = (Style)FindResource("HmiEvidenceNotConnected");
                ProfileConnectionBadgeText.Text = "Not Connected";
                break;
            case "Pilot Hardware Adapter":
                ProfileSourceBadge.Style = (Style)FindResource("HmiEvidenceBoundaryOnly");
                ProfileSourceBadgeText.Text = "BOUNDARY ONLY";
                ProfileConnectionBadge.Style = (Style)FindResource("HmiEvidenceNotValidated");
                ProfileConnectionBadgeText.Text = "Not Validated";
                break;
            default:
                ProfileSourceBadge.Style = (Style)FindResource("HmiEvidenceNotConnected");
                ProfileSourceBadgeText.Text = "NO SOURCE";
                ProfileConnectionBadge.Style = (Style)FindResource("HmiEvidenceNotConnected");
                ProfileConnectionBadgeText.Text = "Not Connected";
                break;
        }
    }

    private async void OnRunAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanTestModelConfiguration, "Running 3D profile acceptance", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_acceptanceCancellation is not null)
            return;

        _acceptanceCancellation = new CancellationTokenSource();
        RunAcceptanceButton.IsEnabled = false;
        CancelAcceptanceButton.IsEnabled = true;
        AcceptanceProgressBar.Value = 10;
        StatusText.Text = "3D acceptance running...";

        try
        {
            var source = CreateSelectedProfileSource();
            var token = _acceptanceCancellation.Token;
            var run = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return Profile3DAcceptanceTestService.Run(source, cancellationToken: token);
            }, token);
            AcceptanceProgressBar.Value = 100;
            _lastAcceptanceRun = run;
            AoiDatabase.RecordProfile3DAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            var issueText = string.Join(" ", run.Failures.Concat(run.Warnings).Take(3));
            StatusText.Text = $"3D acceptance {run.Status}; readiness={run.FactoryReadinessStatus}; source={run.SourceName}; simulated={run.IsSimulated}. {issueText}";
            WorkflowState.Instance.AddEvent("PROFILE_3D_ACCEPTANCE", StatusText.Text);
            MessageBox.Show(
                $"3D acceptance completed: {run.Status}\nFactory readiness: {run.FactoryReadinessStatus}\n\n{issueText}",
                "AOI Monitor",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "3D acceptance canceled.";
            WorkflowState.Instance.AddEvent("PROFILE_3D_ACCEPTANCE", "3D profile acceptance canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusText.Text = $"3D acceptance failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("PROFILE_3D_ERROR", StatusText.Text);
            MessageBox.Show(StatusText.Text, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _acceptanceCancellation?.Dispose();
            _acceptanceCancellation = null;
            RunAcceptanceButton.IsEnabled = true;
            CancelAcceptanceButton.IsEnabled = false;
            AcceptanceProgressBar.Value = 0;
        }
    }

    private void OnCancelAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _acceptanceCancellation?.Cancel();
    }

    private void OnExportAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting 3D profile acceptance report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var run = _lastAcceptanceRun ?? AoiDatabase.GetLatestProfile3DAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("Run a 3D acceptance test before exporting a report.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var output = Path.Combine(AoiDatabase.StorageRoot, "exports", "profile_3d_acceptance");
        var htmlPath = Profile3DAcceptanceTestService.ExportReport(run, output);
        StatusText.Text = $"3D acceptance report exported: {htmlPath}";
        WorkflowState.Instance.AddEvent("PROFILE_3D_EXPORT", StatusText.Text);
        MessageBox.Show($"3D acceptance report exported:\n{htmlPath}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private IProfile3DSource CreateSelectedProfileSource()
        => GetSelectedSourceMode() switch
        {
            "Sample CSV" => new CsvProfile3DSource(_currentCsvPath),
            "Pilot Hardware Adapter" => new GenericProfile3DAdapter(),
            _ => new NullProfile3DSource(),
        };

    private string GetSelectedSourceMode()
        => ComboBoxTokens.SelectedToken(ProfileSourceCombo, "Sample CSV");

    public static List<(int X, int Y, double Height)> ParseHeightMap(string path)
    {
        var rows = new List<(int X, int Y, double Height)>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            return rows;

        var start = 0;
        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var hasHeader = header.Contains("x") && header.Contains("y") && header.Contains("height");
        var xIndex = hasHeader ? Array.IndexOf(header, "x") : 0;
        var yIndex = hasHeader ? Array.IndexOf(header, "y") : 1;
        var heightIndex = hasHeader ? Array.IndexOf(header, "height") : 2;
        if (hasHeader)
            start = 1;

        foreach (var line in lines.Skip(start))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(heightIndex, Math.Max(xIndex, yIndex)))
                continue;

            if (int.TryParse(cells[xIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
                int.TryParse(cells[yIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(cells[heightIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                rows.Add((x, y, height));
            }
        }

        return rows;
    }

    private BitmapSource BuildHeatMapBitmap()
    {
        var width = _maxX - _minX + 1;
        var height = _maxY - _minY + 1;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var key = (X: _minX + x, Y: _minY + y);
                var color = _points.TryGetValue(key, out var value)
                    ? ColorForHeight(value)
                    : Color.FromRgb(18, 22, 25);
                var offset = y * stride + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private Color ColorForHeight(double height)
    {
        var range = Math.Max(0.000001, _maxHeight - _minHeight);
        var t = Math.Clamp((height - _minHeight) / range, 0, 1);
        var r = (byte)(40 + 215 * t);
        var g = (byte)(80 + 120 * (1.0 - Math.Abs(t - 0.5) * 2.0));
        var b = (byte)(220 * (1.0 - t));
        return Color.FromRgb(r, g, b);
    }

    /// <summary>
    /// Builds the interactive 3D surface mesh from the loaded height grid and resets the camera view.
    /// Large grids are decimated to keep mesh construction responsive on the UI thread.
    /// </summary>
    private void BuildSurfaceModel()
    {
        if (_points.Count == 0)
        {
            SurfaceModel.Geometry = null;
            return;
        }

        var fullWidth = _maxX - _minX + 1;
        var fullHeight = _maxY - _minY + 1;
        var stepX = Math.Max(1, (int)Math.Ceiling(fullWidth / (double)MaxSurfaceDimension));
        var stepY = Math.Max(1, (int)Math.Ceiling(fullHeight / (double)MaxSurfaceDimension));
        var width = ((fullWidth - 1) / stepX) + 1;
        var height = ((fullHeight - 1) / stepY) + 1;

        var heights = new double[width * height];
        for (var gy = 0; gy < height; gy++)
        {
            for (var gx = 0; gx < width; gx++)
            {
                var srcX = _minX + (gx * stepX);
                var srcY = _minY + (gy * stepY);
                heights[(gy * width) + gx] = _points.TryGetValue((srcX, srcY), out var value) ? value : double.NaN;
            }
        }

        MeshGeometry3D mesh = Profile3DMeshBuilder.BuildSurface(heights, width, height, _minHeight, _maxHeight, SurfaceHeightScale);
        mesh.Freeze();
        SurfaceModel.Geometry = mesh;
        ResetSurfaceView();
    }

    private void ResetSurfaceView()
    {
        _yaw = 0;
        _pitch = 0;
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        ApplySurfaceTransforms();
    }

    private void ApplySurfaceTransforms()
    {
        SurfaceYaw.Angle = _yaw;
        SurfacePitch.Angle = _pitch;
        SurfaceScale.ScaleX = _zoom;
        SurfaceScale.ScaleY = _zoom;
        SurfaceScale.ScaleZ = _zoom;
        SurfacePan.OffsetX = _panX;
        SurfacePan.OffsetY = _panY;
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e) => ResetSurfaceView();

    private void OnSurfaceMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (SurfaceModel.Geometry is null)
            return;

        _isRotating = true;
        _lastMouse = e.GetPosition(SurfaceHost);
        SurfaceHost.CaptureMouse();
    }

    private void OnSurfaceMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        _isRotating = false;
        if (!_isPanning)
            SurfaceHost.ReleaseMouseCapture();
    }

    private void OnSurfaceMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (SurfaceModel.Geometry is null)
            return;

        _isPanning = true;
        _lastMouse = e.GetPosition(SurfaceHost);
        SurfaceHost.CaptureMouse();
    }

    private void OnSurfaceMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        if (!_isRotating)
            SurfaceHost.ReleaseMouseCapture();
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotating && !_isPanning)
            return;

        var pos = e.GetPosition(SurfaceHost);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        _lastMouse = pos;

        if (_isRotating)
        {
            _yaw = (_yaw + (dx * 0.4)) % 360.0;
            _pitch = Math.Clamp(_pitch + (dy * 0.4), -85.0, 85.0);
        }
        else if (_isPanning)
        {
            _panX = Math.Clamp(_panX + (dx * 0.0028), -1.5, 1.5);
            _panY = Math.Clamp(_panY - (dy * 0.0028), -1.5, 1.5);
        }

        ApplySurfaceTransforms();
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SurfaceModel.Geometry is null)
            return;

        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        _zoom = Math.Clamp(_zoom * factor, 0.4, 4.0);
        ApplySurfaceTransforms();
    }

    private void BuildDefectRows()
    {
        _suppressListSync = true;
        _defectRows.Clear();

        // Rank sample points by deviation from the mean surface and surface the most notable ones.
        var ranked = _points
            .Select(kvp => (kvp.Key.X, kvp.Key.Y, Height: kvp.Value, Deviation: Math.Abs(kvp.Value - _meanHeight)))
            .OrderByDescending(p => p.Deviation)
            .ThenByDescending(p => p.Height)
            .Take(15)
            .ToList();

        var index = 0;
        foreach (var p in ranked)
        {
            index++;
            var point = (p.X, p.Y, p.Height);
            _defectRows.Add(new Profile3DDefectRow
            {
                Feature = $"#{index}",
                Type = GetDefectType(p.Height),
                HeightDisplay = p.Height.ToString("F3", CultureInfo.InvariantCulture),
                VolumeDisplay = ComputeRegionVolume(point).ToString("F3", CultureInfo.InvariantCulture),
                X = p.X,
                Y = p.Y,
                Height = p.Height,
            });
        }

        DefectCountText.Text = _defectRows.Count == 1 ? "1 feature" : $"{_defectRows.Count} features";
        _suppressListSync = false;
        SyncListSelectionToPoint();
    }

    private void OnDefectListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListSync)
            return;

        if (DefectListGrid.SelectedItem is Profile3DDefectRow row)
            SelectPoint((row.X, row.Y, row.Height));
    }

    private void SyncListSelectionToPoint()
    {
        if (_selectedPoint is not { } point)
            return;

        var match = _defectRows.FirstOrDefault(r => r.X == point.X && r.Y == point.Y);
        if (ReferenceEquals(DefectListGrid.SelectedItem, match))
            return;

        _suppressListSync = true;
        DefectListGrid.SelectedItem = match;
        if (match is not null)
            DefectListGrid.ScrollIntoView(match);
        _suppressListSync = false;
    }

    public void ReleasePageResources()
    {
        HeightMapImage.Source = null;
        SelectionOverlay.Children.Clear();
        ProfileCanvas.Children.Clear();
        SurfaceModel.Geometry = null;
        _defectRows.Clear();
        DefectCountText.Text = "0 features";
        MapStatusText.Text = string.Empty;
        SliceRowText.Text = "--";
        EmptyMapText.Visibility = Visibility.Visible;
        SliceEmptyText.Visibility = Visibility.Visible;
        TopDownMapInset.Visibility = Visibility.Collapsed;
        HeightScaleLegend.Visibility = Visibility.Collapsed;
        ViewHintChip.Visibility = Visibility.Collapsed;
    }

    private void OnHeightMapClick(object sender, MouseButtonEventArgs e)
    {
        if (HeightMapImage.Source is not BitmapSource bitmap || _points.Count == 0)
            return;

        if (TryGetMapPoint(e.GetPosition(HeightMapImage), bitmap, out var point))
            SelectPoint(point);
    }

    private void OnHeightMapMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (HeightMapImage.Source is not BitmapSource bitmap || _points.Count == 0)
            return;

        if (TryGetMapPoint(e.GetPosition(HeightMapImage), bitmap, out var point))
            SelectPoint(point);
    }

    private void SelectPoint((int X, int Y, double Height) point)
    {
        _selectedPoint = point;
        RefreshSelectionUi();
        DrawProfileLine();
        SyncListSelectionToPoint();
    }

    private void RefreshSelectionUi()
    {
        if (_selectedPoint is not { } point)
        {
            DefectTypeText.Text = "--";
            DefectHeightText.Text = "--";
            DefectVolumeText.Text = "--";
            DefectXText.Text = "--";
            DefectYText.Text = "--";
            SelectionOverlay.Children.Clear();
            return;
        }

        DefectTypeText.Text = GetDefectType(point.Height);
        DefectHeightText.Text = point.Height.ToString("F3", CultureInfo.InvariantCulture);
        DefectVolumeText.Text = ComputeRegionVolume(point).ToString("F3", CultureInfo.InvariantCulture);
        DefectXText.Text = point.X.ToString(CultureInfo.InvariantCulture);
        DefectYText.Text = point.Y.ToString(CultureInfo.InvariantCulture);
        DrawSelectionMarker(point);
    }

    private void OnProfileCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selectedPoint is not null)
            DrawProfileLine();
    }

    private void DrawProfileLine()
    {
        ProfileCanvas.Children.Clear();
        if (_selectedPoint is not { } point || _points.Count == 0)
        {
            SliceRowText.Text = "--";
            SliceEmptyText.Visibility = Visibility.Visible;
            return;
        }

        SliceEmptyText.Visibility = Visibility.Collapsed;

        var row = Enumerable.Range(_minX, _maxX - _minX + 1)
            .Select(x => (X: x, Height: _points.TryGetValue((x, point.Y), out var value) ? value : double.NaN))
            .Where(p => !double.IsNaN(p.Height))
            .ToArray();

        SliceRowText.Text = $"row y={point.Y}";
        if (row.Length < 2)
            return;

        var width = Math.Max(1, ProfileCanvas.ActualWidth);
        var height = Math.Max(1, ProfileCanvas.ActualHeight);
        if (width <= 1 || height <= 1)
        {
            width = 420;
            height = 240;
        }

        double MapX(int x) => (x - _minX) / Math.Max(1.0, _maxX - _minX) * width;
        double MapY(double h) => height - ((h - _minHeight) / Math.Max(0.000001, _maxHeight - _minHeight) * height);

        var polyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5CA0D3")),
            StrokeThickness = 2,
        };
        foreach (var sample in row)
            polyline.Points.Add(new Point(MapX(sample.X), MapY(sample.Height)));
        ProfileCanvas.Children.Add(polyline);

        // Mark prominent local maxima (peaks) along the selected slice.
        var sliceHeights = row.Select(r => r.Height).ToArray();
        foreach (var peakIndex in Profile3DMeshBuilder.FindPeakIndices(sliceHeights))
        {
            var sample = row[peakIndex];
            var cx = MapX(sample.X);
            var cy = MapY(sample.Height);
            var marker = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x82, 0x30)),
                Stroke = Brushes.White,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(marker, cx - 4);
            Canvas.SetTop(marker, cy - 4);
            ProfileCanvas.Children.Add(marker);

            var label = new TextBlock
            {
                Text = sample.Height.ToString("F2", CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCF, 0x9A)),
                FontSize = (double)Application.Current.FindResource("HmiFontSizeBody"),
            };
            Canvas.SetLeft(label, Math.Min(width - 36, cx + 5));
            Canvas.SetTop(label, Math.Max(0, cy - 17));
            ProfileCanvas.Children.Add(label);
        }

        // Highlight the selected column position.
        var selectedX = MapX(point.X);
        var cursor = new System.Windows.Shapes.Line
        {
            X1 = selectedX,
            X2 = selectedX,
            Y1 = 0,
            Y2 = height,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            StrokeThickness = 1,
        };
        ProfileCanvas.Children.Add(cursor);
    }

    private bool TryGetMapPoint(Point position, BitmapSource bitmap, out (int X, int Y, double Height) point)
    {
        point = default;

        var imageWidth = Math.Max(1.0, HeightMapImage.ActualWidth);
        var imageHeight = Math.Max(1.0, HeightMapImage.ActualHeight);
        var scale = Math.Min(imageWidth / bitmap.PixelWidth, imageHeight / bitmap.PixelHeight);
        var displayWidth = bitmap.PixelWidth * scale;
        var displayHeight = bitmap.PixelHeight * scale;
        var offsetX = (imageWidth - displayWidth) / 2.0;
        var offsetY = (imageHeight - displayHeight) / 2.0;

        var pixelX = (position.X - offsetX) / Math.Max(0.000001, scale);
        var pixelY = (position.Y - offsetY) / Math.Max(0.000001, scale);
        if (pixelX < 0 || pixelY < 0 || pixelX >= bitmap.PixelWidth || pixelY >= bitmap.PixelHeight)
            return false;

        var x = _minX + (int)Math.Clamp(Math.Floor(pixelX), 0, bitmap.PixelWidth - 1);
        var y = _minY + (int)Math.Clamp(Math.Floor(pixelY), 0, bitmap.PixelHeight - 1);
        if (!_points.TryGetValue((x, y), out var value))
            return false;

        point = (x, y, value);
        return true;
    }

    private void DrawSelectionMarker((int X, int Y, double Height) point)
    {
        SelectionOverlay.Children.Clear();
        if (HeightMapImage.Source is not BitmapSource bitmap)
            return;

        SelectionOverlay.Width = HeightMapImage.ActualWidth;
        SelectionOverlay.Height = HeightMapImage.ActualHeight;

        var imageWidth = Math.Max(1.0, HeightMapImage.ActualWidth);
        var imageHeight = Math.Max(1.0, HeightMapImage.ActualHeight);
        var scale = Math.Min(imageWidth / bitmap.PixelWidth, imageHeight / bitmap.PixelHeight);
        var displayWidth = bitmap.PixelWidth * scale;
        var displayHeight = bitmap.PixelHeight * scale;
        var offsetX = (imageWidth - displayWidth) / 2.0;
        var offsetY = (imageHeight - displayHeight) / 2.0;
        var x = offsetX + (point.X - _minX + 0.5) * scale;
        var y = offsetY + (point.Y - _minY + 0.5) * scale;

        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(80, 225, 163, 52)),
        };
        Canvas.SetLeft(marker, x - marker.Width / 2.0);
        Canvas.SetTop(marker, y - marker.Height / 2.0);
        SelectionOverlay.Children.Add(marker);

        var vertical = new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = y - 14, Y2 = y + 14, Stroke = Brushes.White, StrokeThickness = 1 };
        var horizontal = new System.Windows.Shapes.Line { X1 = x - 14, X2 = x + 14, Y1 = y, Y2 = y, Stroke = Brushes.White, StrokeThickness = 1 };
        SelectionOverlay.Children.Add(vertical);
        SelectionOverlay.Children.Add(horizontal);
    }

    private string GetDefectType(double height)
    {
        var highLimit = _meanHeight + ((_maxHeight - _meanHeight) * 0.60);
        var lowLimit = _meanHeight - ((_meanHeight - _minHeight) * 0.60);
        if (height >= highLimit)
            return "Height High";
        if (height <= lowLimit)
            return "Height Low";

        return "Coplanarity Review";
    }

    /// <summary>
    /// Region volume estimate in grid units cubed: flood-fills the contiguous cells that
    /// exceed the same defect threshold as the selected point (4-connectivity) and sums
    /// each cell's height deviation from the mean plane (cell area = 1 grid unit squared).
    /// A point inside tolerance contributes only its own deviation.
    /// </summary>
    private double ComputeRegionVolume((int X, int Y, double Height) point)
    {
        var highLimit = _meanHeight + ((_maxHeight - _meanHeight) * 0.60);
        var lowLimit = _meanHeight - ((_meanHeight - _minHeight) * 0.60);
        var isHigh = point.Height >= highLimit;
        var isLow = point.Height <= lowLimit;
        if (!isHigh && !isLow)
            return Math.Abs(point.Height - _meanHeight);

        bool InRegion(double height) => isHigh ? height >= highLimit : height <= lowLimit;

        var visited = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((point.X, point.Y));
        visited.Add((point.X, point.Y));
        var volume = 0.0;
        const int cellCap = 100_000;

        while (queue.Count > 0 && visited.Count <= cellCap)
        {
            var cell = queue.Dequeue();
            if (!_points.TryGetValue(cell, out var height) || !InRegion(height))
                continue;

            volume += Math.Abs(height - _meanHeight);
            foreach (var next in new[] { (cell.X + 1, cell.Y), (cell.X - 1, cell.Y), (cell.X, cell.Y + 1), (cell.X, cell.Y - 1) })
            {
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        return volume;
    }

    private void OnAcceptDefectClick(object sender, RoutedEventArgs e) => RecordDisposition("Accept Defect");
    private void OnRejectDefectClick(object sender, RoutedEventArgs e) => RecordDisposition("Reject Defect");

    private void RecordDisposition(string action)
    {
        if (_selectedPoint is not { } point)
        {
            MessageBox.Show("Load a sample height-map CSV and select a point first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = $"{action}: 3D sample-data defect type={GetDefectType(point.Height)}, x={point.X}, y={point.Y}, height={point.Height:F3}, regionVolumeEst={ComputeRegionVolume(point):F3} (grid units^3), file={Path.GetFileName(_currentCsvPath)}. 3D camera not connected; Stage 2 hardware integration required for live profile inspection.";
        WorkflowState.Instance.AddDisposition(message);
        StatusText.Text = $"{action} recorded in SQLite review events.";
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

    public void Dispose()
    {
        var source = _acceptanceCancellation;
        _acceptanceCancellation = null;
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

/// <summary>
/// Row model for the "Detected Features" list in the 3D Profile Viewer. Carries the underlying grid
/// coordinates so list selection stays synchronized with the 2D inset and the height-slice profile.
/// </summary>
public sealed class Profile3DDefectRow
{
    public string Feature { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string HeightDisplay { get; init; } = string.Empty;
    public string VolumeDisplay { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public double Height { get; init; }
}
