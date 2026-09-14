using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class MonitorView : UserControl, IReleasablePageResources, IAsyncNavigationPage, IDisposable
{
    private readonly ObservableCollection<DefectRow> _defects = new();
    private readonly ObservableCollection<AlarmRow> _alarms = new();
    private readonly Dictionary<CameraViewType, string> _viewFolders = new();
    private readonly List<ImportedImage> _importedQueue = new();
    private readonly SimulatedRobotController _robotController = new();
    private readonly SimulatedEmergencyStopMonitor _emergencyStopMonitor;
    private readonly RobotCycleService _robotCycleService;
    private RobotAcceptanceRun? _lastRobotAcceptanceRun;

    private ICameraSource _cameraSource = CameraSourceFactory.ActiveSource;
    private bool _isRunning;
    private bool _currentResultSaved;
    private bool _loadNextBoardInProgress;
    private bool _updatingCalibrationProfiles;
    private CancellationTokenSource? _robotCycleCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private int _importedQueueIndex = -1;
    private string? _currentImagePath;
    private string _currentBoardModel = "TBOX-MAIN";
    private string _currentLotId = "POC-LOT";
    private string _currentSourceFrameId = string.Empty;
    private string _currentSourceKind = "File";
    private string _currentFrameMetadata = "No frame";
    // The Board/Status row label already reads "Lighting"; keep the value prefix-free.
    private string _lastLightingResult = "Disabled / Not Connected";
    private bool _currentIsSimulatedSource;
    private BitmapSource? _currentBitmap;
    private AnalysisResult? _currentAnalysis;
    private InspectionLatencyTraceBuilder? _currentLatencyTrace;
    private InspectionLatencyTrace? _lastLatencyTrace;
    private CalibrationProfileRecord? _selectedCalibrationProfile;
    private readonly SimulatedPlcSafetyController _plcSafetyController = new();
    private IRobotController? _previousRobotController;
    private IEmergencyStopMonitor? _previousEmergencyStopMonitor;
    private IPlcSafetyController? _previousPlcSafetyController;

    public MonitorView()
    {
        InitializeComponent();
        _emergencyStopMonitor = new SimulatedEmergencyStopMonitor(_robotController);
        RegisterSimulationBoundaries();
        Loaded += OnLoaded;
        _robotCycleService = new RobotCycleService();
        _robotCycleService.StateChanged += OnRobotCycleStateChanged;
        LightingSettingsService.ApplyIntegrationBoundary();
        DefectGrid.ItemsSource = _defects;
        AlarmGrid.ItemsSource = _alarms;
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnEngineConfigurationChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        Unloaded += OnUnloaded;
        UpdateRobotSimulationStatus();
        LogEvent("ROBOT SIM", "Robot/handler simulation is available. No real robot hardware is connected.");
        LogEvent("READY", "Main Inspection ready. Use folder simulation or Image Library imported images.");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => RegisterSimulationBoundaries();

    /// <summary>
    /// Installs this page's clearly-labeled simulators only over the fail-safe Null
    /// defaults. A commissioned real robot/PLC controller registered by a reviewed
    /// bootstrap must survive operators opening Main Inspection; the simulators never
    /// replace a non-null registration.
    /// </summary>
    private void RegisterSimulationBoundaries()
    {
        _previousRobotController = IntegrationBoundaryRegistry.RobotController;
        if (_previousRobotController is NullRobotController)
            IntegrationBoundaryRegistry.RobotController = _robotController;
        _previousEmergencyStopMonitor = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        if (_previousEmergencyStopMonitor is NullEmergencyStopMonitor)
            IntegrationBoundaryRegistry.EmergencyStopMonitor = _emergencyStopMonitor;
        // The simulated PLC accompanies the simulated robot so the simulation cycle can
        // pass the fail-safe interlock check honestly; without it the Null PLC (which
        // reports an active fault by design) blocks every simulated command.
        _previousPlcSafetyController = IntegrationBoundaryRegistry.PlcSafetyController;
        if (_previousPlcSafetyController is NullPlcSafetyController)
            IntegrationBoundaryRegistry.PlcSafetyController = _plcSafetyController;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnEngineConfigurationChanged;
        LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        _robotCycleService.StateChanged -= OnRobotCycleStateChanged;
        CancelAndDispose(ref _robotCycleCancellation);
        CancelAndDispose(ref _refreshCancellation);
        if (ReferenceEquals(IntegrationBoundaryRegistry.RobotController, _robotController))
            IntegrationBoundaryRegistry.RobotController = _previousRobotController ?? new NullRobotController();
        if (ReferenceEquals(IntegrationBoundaryRegistry.EmergencyStopMonitor, _emergencyStopMonitor))
            IntegrationBoundaryRegistry.EmergencyStopMonitor = _previousEmergencyStopMonitor ?? new NullEmergencyStopMonitor();
        if (ReferenceEquals(IntegrationBoundaryRegistry.PlcSafetyController, _plcSafetyController))
            IntegrationBoundaryRegistry.PlcSafetyController = _previousPlcSafetyController ?? new NullPlcSafetyController();
    }

    private void OnWorkflowStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, () => _ = RefreshSafeAsync());

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Navigation raced the refresh; the next navigation refreshes again.
        }
        catch (Exception ex)
        {
            // Fire-and-forget refresh: without this the exception is discarded and the
            // imported-image queue / calibration profiles silently go stale.
            LogEvent("ERROR", $"Page refresh failed: {ex.Message}");
        }
    }
    private void OnEngineConfigurationChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshHeader);
    private void OnLightingSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshHeader);
    private void OnRobotCycleStateChanged(RobotCycleState state) => UiDispatcher.InvokeIfAvailable(Dispatcher, UpdateRobotSimulationStatus);

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        CancelAndDispose(ref _refreshCancellation);
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _refreshCancellation.Token;
        var selectedProfileId = _selectedCalibrationProfile?.Id;
        var samplePath = WorkflowState.Instance.SampleImagePath;

        var snapshot = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var imported = AoiDatabase.GetImportedImages()
                .Where(image => File.Exists(image.VaultPath))
                .ToArray();
            var profileItems = AoiDatabase.GetCalibrationProfiles()
                .Select(profile => new CalibrationProfileListItem(profile.DisplayName, profile))
                .ToList();
            profileItems.Insert(0, new CalibrationProfileListItem("No 2D profile (Stage 2 prep)", null));
            var latestAcceptance = AoiDatabase.GetLatestRobotAcceptanceRun();
            var sampleExists = !string.IsNullOrWhiteSpace(samplePath) && File.Exists(samplePath);
            return new MonitorRefreshSnapshot(imported, profileItems, latestAcceptance, samplePath ?? string.Empty, sampleExists);
        }, token);

        token.ThrowIfCancellationRequested();
        _importedQueue.Clear();
        _importedQueue.AddRange(snapshot.ImportedImages);
        ApplyCalibrationProfiles(snapshot.CalibrationProfiles, selectedProfileId);
        _lastRobotAcceptanceRun = snapshot.LatestRobotAcceptance;
        RefreshHeader();

        if (_currentImagePath is null && snapshot.SampleExists && !string.IsNullOrWhiteSpace(snapshot.SamplePath))
        {
            _currentImagePath = snapshot.SamplePath;
            LoadImage(snapshot.SamplePath);
        }

        var state = WorkflowState.Instance;
        if (state.LastAnalysis is not null && !ReferenceEquals(state.LastAnalysis, _currentAnalysis))
        {
            _currentAnalysis = state.LastAnalysis;
            _currentResultSaved = true;
            RefreshDefectRows();
            RenderOverlay();
            UpdateTimingDisplay(state.LastAnalysis.Timing);
            SetResultStatus(state.LastAnalysis.Verdict);
        }
    }

    public void CancelWork()
    {
        TryCancel(_refreshCancellation);
        TryCancel(_robotCycleCancellation);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Navigation and unload can race through cached WPF pages; cleanup must stay idempotent.
        }
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        var source = cancellation;
        cancellation = null;
        if (source is null)
            return;

        TryCancel(source);
        source.Dispose();
    }

    private void OnOpenDispositionClick(object sender, RoutedEventArgs e) => Navigate("review");
    private void OnOpenCompareClick(object sender, RoutedEventArgs e) => Navigate("compare");
    private void OnOpenLibraryClick(object sender, RoutedEventArgs e) => Navigate("library");
    private void OnOpenImageViewerClick(object sender, RoutedEventArgs e)
        => AOI_Monitor.ImageViewerWindow.ShowFromVisual(this, "Run Inspection Image / Overlay", InspectionImageViewport);

    private void Navigate(string key)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = key;
    }

    private void OnSelectTopFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Top);
    private void OnSelectSideFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Side);
    private void OnSelectBottomFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Bottom);

    private void SelectFolderForView(CameraViewType viewType)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Select simulated {viewType} camera image folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _viewFolders[viewType] = dialog.FolderName;
        var source = CameraSourceFactory.CreateFolder(_viewFolders);
        source.SelectedView = SelectedCameraView();
        _cameraSource = source;
        CameraSourceFactory.SetActiveSource(source);

        LogEvent("CAMERA SOURCE", $"Configured simulated {viewType} folder: {dialog.FolderName}.");
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _isRunning = true;
            _cameraSource.SelectedView = SelectedCameraView();
            await SynchronizeLightingForSelectedViewAsync(CancellationToken.None);
            _cameraSource.StartAcquisition();
            ModeText.Text = "RUNNING";
            ModeText.Foreground = Brushes.LightGreen;
            LogEvent("START", $"Simulated inspection mode started. Camera status: {CameraStatusText()}.");

            if (_currentImagePath is null)
                await LoadNextBoardAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogEvent("ERROR", $"Start inspection failed safely: {ex.Message}");
            MessageBox.Show($"Start inspection failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _isRunning = false;
        _cameraSource.StopAcquisition();
        ModeText.Text = "STOPPED";
        ModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"));
        LogEvent("STOP", "Simulated inspection mode paused.");
    }

    // Operator hotkeys (verification-station ergonomics): F5=Start, F6=Stop, F7=Next Board,
    // Ctrl+S=Save Result. Suppressed while a text/editable field has focus. Calls the same
    // handlers as the buttons — no separate logic.
    private void OnMonitorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditableElementFocused()) return;

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnSaveResultClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F5: OnStartClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.F6: OnStopClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.F7: OnNextBoardClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private static bool IsEditableElementFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.Primitives.TextBoxBase
            || focused is PasswordBox
            || (focused is ComboBox { IsEditable: true });
    }

    private async void OnNextBoardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isRunning)
                LogEvent("NEXT BOARD", "Manual next-board inspection requested while simulated mode is paused.");

            await LoadNextBoardAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogEvent("ERROR", $"Next board failed safely: {ex.Message}");
            MessageBox.Show($"Next board failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveResultClick(object sender, RoutedEventArgs e)
    {
        if (_currentAnalysis is null)
        {
            LogEvent("ERROR", "No inspection result is available to save.");
            MessageBox.Show("Run inspection before saving a result.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentResultSaved)
        {
            LogEvent("SAVE", "Current result was already saved.");
            MessageBox.Show("Current inspection result is already saved.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _currentLatencyTrace?.StartSpan("resultPersist");
            WorkflowState.Instance.SetAnalysis(_currentAnalysis, persist: true);
            _currentLatencyTrace?.StopSpan("resultPersist");
            if (_currentLatencyTrace is not null)
                _lastLatencyTrace = InspectionLatencyService.Persist(_currentLatencyTrace, saved: true);
            _currentLatencyTrace = null;
            _currentResultSaved = true;
            UpdateTimingDisplay(_currentAnalysis.Timing);
            LogEvent("SAVE", $"Inspection result saved to SQLite: {_currentAnalysis.Verdict}.");
        }
        catch (Exception ex)
        {
            LogEvent("ERROR", $"Save failed: {ex.Message}");
            MessageBox.Show($"Save failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRobotLoadClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT LOAD", out var token))
            return;

        try
        {
            if (!_robotController.IsBoardLoaded)
                await LoadNextBoardAsync(runInspection: false, token);

            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT LOAD", "Manual load stopped because no board image is available.");
                return;
            }

            var result = await _robotCycleService.LoadAsync(BuildLoadCommand(), token);
            LogRobotCommandResult("ROBOT LOAD", result);
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT LOAD", "Robot load canceled.");
        }
        catch (Exception ex)
        {
            HandleRobotOperationException("ROBOT LOAD", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotInspectClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT INSPECT", out var token))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT INSPECT", "Manual inspect stopped because no board image is available.");
                return;
            }

            var move = await _robotCycleService.MoveToInspectAsync(BuildInspectCommand(), token);
            LogRobotCommandResult("ROBOT INSPECT", move);
            if (!move.Accepted)
                return;

            var analysis = await _robotCycleService.RunInspectionStepAsync(
                _ => RunInspectionAsync(_currentImagePath),
                token);
            if (analysis is not null)
                LogEvent("ROBOT INSPECT", $"Inspection step completed through cycle service: {analysis.Verdict}.");
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT INSPECT", "Robot inspect canceled.");
        }
        catch (Exception ex)
        {
            HandleRobotOperationException("ROBOT INSPECT", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotUnloadClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT UNLOAD", out var token))
            return;

        try
        {
            var result = await _robotCycleService.UnloadAsync(BuildUnloadCommand(), token);
            LogRobotCommandResult("ROBOT UNLOAD", result);
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT UNLOAD", "Robot unload canceled.");
        }
        catch (Exception ex)
        {
            HandleRobotOperationException("ROBOT UNLOAD", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnRobotCancelClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleCancellation is null)
        {
            LogEvent("ROBOT CANCEL", "No robot cycle action is currently running.");
            return;
        }

        _robotCycleCancellation.Cancel();
        LogEvent("ROBOT CANCEL", "Robot cycle cancellation requested.");
        UpdateRobotSimulationStatus();
    }

    private async void OnRobotResetClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT RESET", out var token))
            return;

        try
        {
            var result = await _robotCycleService.ResetAsync(token);
            LogRobotCommandResult("ROBOT RESET", result);
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT RESET", "Robot reset canceled.");
        }
        catch (Exception ex)
        {
            HandleRobotOperationException("ROBOT RESET", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnRobotEmergencyStopClick(object sender, RoutedEventArgs e)
    {
        if (IntegrationBoundaryRegistry.RobotController is SimulatedRobotController simulatedRobot)
        {
            simulatedRobot.TriggerEmergencyStop();
            _robotCycleService.RefreshEmergencyStopStatus("Emergency stop simulation triggered. No real safety hardware is connected.");
            LogEvent("ROBOT E-STOP", "Emergency stop simulation triggered. No real safety hardware is connected.");
        }
        else
        {
            LogEvent("ROBOT E-STOP", "Emergency stop trigger is available only for the local simulated robot controller.");
        }

        UpdateRobotSimulationStatus();
    }

    private async void OnRobotCycleClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT CYCLE", out var token))
            return;

        try
        {
            await RunRobotCycleAsync(token);
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT CYCLE", "Robot cycle canceled.");
        }
        catch (Exception ex)
        {
            HandleRobotOperationException("ROBOT CYCLE", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT ACCEPTANCE", out var token))
            return;

        try
        {
            RobotAcceptanceStatusText.Text = "Robot cell acceptance: running...";
            var run = await RobotCellAcceptanceTestService.RunAsync(progress: new Progress<string>(message => RobotAcceptanceStatusText.Text = message), cancellationToken: token);
            AoiDatabase.RecordRobotAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastRobotAcceptanceRun = run;
            RobotAcceptanceStatusText.Text = BuildRobotAcceptanceStatus(run);
            LogEvent("ROBOT ACCEPTANCE", RobotAcceptanceStatusText.Text);
            MessageBox.Show(
                RobotAcceptanceStatusText.Text,
                "Robot Acceptance Test",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RobotAcceptanceStatusText.Text = "Robot acceptance: canceled";
            LogEvent("ROBOT ACCEPTANCE", "Robot acceptance canceled.");
        }
        catch (Exception ex)
        {
            RobotAcceptanceStatusText.Text = "Robot acceptance: failed safely";
            HandleRobotOperationException("ROBOT ACCEPTANCE", ex);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnExportRobotAcceptanceClick(object sender, RoutedEventArgs e)
    {
        var run = _lastRobotAcceptanceRun ?? AoiDatabase.GetLatestRobotAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No robot acceptance run is available to export.", "Robot Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = RobotCellAcceptanceTestService.ExportReport(run);
            LogEvent("ROBOT ACCEPTANCE", $"Robot acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Robot acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Robot Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Robot acceptance export failed:\n{ex.Message}", "Robot Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoardModelText is null)
            return;

        _cameraSource.SelectedView = SelectedCameraView();
        CameraSourceFactory.ActiveSource.SelectedView = SelectedCameraView();
        RefreshHeader();
        RefreshDefectRows();
        RenderOverlay();
    }

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (DefectOverlayCanvas is null || OverlayCheck is null)
            return;

        DefectOverlayCanvas.Visibility = OverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCalibrationProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationProfiles)
            return;

        _selectedCalibrationProfile = (CalibrationProfileCombo.SelectedItem as CalibrationProfileListItem)?.Profile;
        RefreshDefectRows();

        if (_selectedCalibrationProfile is null)
            LogEvent("CALIBRATION", "No 2D calibration profile selected. Defect coordinates remain image-space only.");
        else
            LogEvent("CALIBRATION", $"Selected 2D calibration profile '{_selectedCalibrationProfile.ProfileName}' for approximate board-mm display. Stage 2 preparation only.");
    }

    private async Task LoadNextBoardAsync(bool runInspection = true, CancellationToken cancellationToken = default)
    {
        if (_loadNextBoardInProgress)
        {
            LogEvent("NEXT BOARD", "Next-board request ignored: a board load/inspection is already in progress.");
            return;
        }

        _loadNextBoardInProgress = true;
        try
        {
            var nextBoard = await GetNextBoardAsync(cancellationToken);
            if (nextBoard is null || string.IsNullOrWhiteSpace(nextBoard.ImagePath) || !File.Exists(nextBoard.ImagePath))
            {
                LogEvent("ERROR", "No simulated board image is available. Select a folder or import an image first.");
                MessageBox.Show("Select a simulated image folder or load an image from Image Library first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentImagePath = nextBoard.ImagePath;
            _currentBoardModel = nextBoard.BoardModel;
            _currentLotId = nextBoard.LotId;
            _currentSourceFrameId = nextBoard.SourceFrameId;
            _currentSourceKind = nextBoard.SourceKind;
            _currentFrameMetadata = nextBoard.FrameMetadata;
            _currentIsSimulatedSource = nextBoard.IsSimulatedSource;
            _currentResultSaved = false;
            WorkflowState.Instance.SetSampleImage(nextBoard.ImagePath);
            LoadImage(nextBoard.ImagePath);
            RefreshHeader();
            if (runInspection)
            {
                await RunInspectionAsync(nextBoard.ImagePath);
            }
            else
            {
                _currentAnalysis = null;
                _defects.Clear();
                DefectOverlayCanvas.Children.Clear();
                TimingText.Text = "--";
                SetResultStatus("REVIEW");
                LogEvent("BOARD READY", "Simulated board image loaded for Simulated Robot cycle. Inspection has not run yet.");
            }
        }
        catch (Exception ex)
        {
            // The board identity/image may already point at the NEW board; never leave the
            // PREVIOUS board's verdict, defects, or overlay on screen (and never let Save
            // re-persist the stale analysis as if it belonged to this board).
            _currentAnalysis = null;
            _currentResultSaved = true;
            _defects.Clear();
            DefectOverlayCanvas.Children.Clear();
            TimingText.Text = "--";
            SetResultStatus("REVIEW");
            LogEvent("ERROR", $"Next board failed: {ex.Message}");
            MessageBox.Show($"Next board failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loadNextBoardInProgress = false;
        }
    }

    private async Task<BoardImageContext?> GetNextBoardAsync(CancellationToken cancellationToken)
    {
        _cameraSource.SelectedView = SelectedCameraView();
        await SynchronizeLightingForSelectedViewAsync(cancellationToken);
        if (!_cameraSource.IsAcquiring)
            _cameraSource.StartAcquisition();

        var frame = _cameraSource.GetNextFrame();
        if (frame is not null)
        {
            var frameMetadata = FrameMetadataDisplay(frame);
            LogEvent("FRAME", $"{frame.SourceName} supplied {frame.ViewType} frame {frame.FrameId}: {Path.GetFileName(frame.SourcePath)}. {frameMetadata}");
            return new BoardImageContext(frame.SourcePath, null, frame.BoardModel, frame.LotId, frame.FrameId, frame.ViewType.ToString(), frame.SourceKind, frame.IsSimulated, frameMetadata);
        }

        if (_cameraSource is GenericVisionCameraSource && _cameraSource.ConnectionStatus != CameraSourceStatus.Ready)
            LogEvent("CAMERA WARNING", $"{_cameraSource.Name}: {_cameraSource.StatusMessage} Main Inspection will continue with imported/file fallback if available.");

        var imported = GetNextImportedImage();
        if (imported is not null)
        {
            LogEvent("QUEUE", $"Loaded imported image queue item: {imported.FileName}.");
            var importedView = string.Equals(imported.ViewType, "sample", StringComparison.OrdinalIgnoreCase)
                ? SelectedView()
                : imported.ViewType;
            return new BoardImageContext(imported.VaultPath, imported, imported.BoardModel, imported.LotId, $"IMG-{imported.Id}", importedView, "ImageVault", false, $"Image vault {imported.FileName}");
        }

        return string.IsNullOrWhiteSpace(WorkflowState.Instance.SampleImagePath)
            ? null
            : new BoardImageContext(WorkflowState.Instance.SampleImagePath, null, WorkflowState.Instance.BoardProgram, "POC-LOT", Path.GetFileName(WorkflowState.Instance.SampleImagePath), SelectedView(), "File", false, "Workflow file source");
    }

    private ImportedImage? GetNextImportedImage()
    {
        ReloadImportedQueue();
        if (_importedQueue.Count == 0)
            return null;

        var selectedView = SelectedView();
        var candidates = _importedQueue
            .Where(image =>
                !string.Equals(image.ViewType, "golden", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(image.ViewType, "sample", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(image.ViewType, selectedView, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (candidates.Length == 0)
            candidates = _importedQueue.Where(image => !string.Equals(image.ViewType, "golden", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (candidates.Length == 0)
            return null;

        _importedQueueIndex = (_importedQueueIndex + 1) % candidates.Length;
        return candidates[_importedQueueIndex];
    }

    private void ReloadImportedQueue()
    {
        _importedQueue.Clear();
        _importedQueue.AddRange(AoiDatabase.GetImportedImages().Where(image => File.Exists(image.VaultPath)));
    }

    private void LoadImage(string imagePath)
    {
        var bitmap = ImageCacheService.LoadBitmap(imagePath, decodePixelWidth: 1400);

        _currentBitmap = bitmap;
        InspectionImage.Source = bitmap;
        EmptyImageText.Visibility = Visibility.Collapsed;
        ImageStatusText.Text = Path.GetFileName(imagePath);
        LogEvent("NEXT BOARD", $"Loaded simulated board image: {Path.GetFileName(imagePath)}.");
    }

    public void ReleasePageResources()
    {
        _currentBitmap = null;
        InspectionImage.Source = null;
        DefectOverlayCanvas.Children.Clear();
        EmptyImageText.Visibility = Visibility.Visible;
        ImageCacheService.ClearOnPageUnload();
    }

    private async Task<AnalysisResult?> RunInspectionAsync(string imagePath)
    {
        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        RefreshHeader();

        var frameCapturedAtUtc = DateTime.UtcNow;
        var analysisStartUtc = DateTime.UtcNow;
        // The pixel/model analysis is CPU-bound and can take seconds on large boards; it must
        // not freeze the operator UI. Everything before and after stays on the UI thread.
        var goldenImagePath = state.GoldenImagePath;
        var detectionPriority = state.DetectionPriority;
        var analysis = await Task.Run(() => engine.Analyze(imagePath, goldenImagePath, detectionPriority));
        var analysisEndUtc = DateTime.UtcNow;
        analysis.BoardProgram = BoardModelText.Text;
        analysis.BoardId = CurrentBoardId();
        analysis.LotId = LotText.Text;
        analysis.StationId = StationText.Text;
        analysis.SourceFrameId = string.IsNullOrWhiteSpace(_currentSourceFrameId) ? Path.GetFileName(imagePath) : _currentSourceFrameId;
        analysis.SourceKind = _currentSourceKind;
        analysis.IsSimulatedSource = _currentIsSimulatedSource;
        analysis.OperatorId = state.OperatorWithRole;

        var selectedView = SelectedView();
        analysis.ViewType = selectedView;
        foreach (var defect in analysis.Defects)
        {
            defect.SideOrViewType = selectedView;
            if (string.IsNullOrWhiteSpace(defect.SourceRoiId))
                defect.SourceRoiId = defect.RoiId;
        }

        _currentAnalysis = analysis;
        _currentLatencyTrace = InspectionLatencyService.StartTrace(
            _currentSourceKind,
            engine.Name,
            InspectionModelConfigurationService.Load().ActiveModelId,
            _currentBitmap?.PixelWidth ?? 0,
            _currentBitmap?.PixelHeight ?? 0,
            frameCapturedAtUtc);
        PopulateAnalysisSpans(_currentLatencyTrace, analysis.Timing, analysisStartUtc, analysisEndUtc);
        var autoSave = AutoSaveCheck.IsChecked == true;
        RefreshDefectRows();
        _currentLatencyTrace.StartSpan("overlay");
        var overlayMs = RenderOverlay();
        _currentLatencyTrace.StopSpan("overlay");
        _currentLatencyTrace.Trace.Verdict = analysis.Verdict;
        InspectionLatencyService.CompleteTrace(_currentLatencyTrace, saved: false);
        analysis.Timing.OverlayRenderingMilliseconds = overlayMs;
        analysis.Timing.RecalculateTotal();
        UpdateTimingDisplay(analysis.Timing);
        SetResultStatus(analysis.Verdict);
        if (analysis.Timing.IsOverOneSecond)
            LogEvent("PERFORMANCE WARNING", $"Visualization target exceeded: {analysis.Timing.TotalInspectionMilliseconds:F0} ms (limit 1000 ms).");

        // A corrupt/empty recipe silently downgrades to whole-image diff; the engine records
        // it only in the evidence lines, which this page does not display. Make it visible.
        foreach (var recipeWarning in analysis.Evidence.Where(line =>
                     line.Contains("recipe", StringComparison.OrdinalIgnoreCase) &&
                     (line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("no valid ROI", StringComparison.OrdinalIgnoreCase))))
        {
            LogEvent("RECIPE WARNING", recipeWarning);
        }

        if (autoSave)
            _currentLatencyTrace.StartSpan("resultPersist");
        state.SetAnalysis(analysis, persist: autoSave);
        if (autoSave)
        {
            _currentLatencyTrace.StopSpan("resultPersist");
            _lastLatencyTrace = InspectionLatencyService.Persist(_currentLatencyTrace, saved: true);
            _currentLatencyTrace = null;
            UpdateTimingDisplay(analysis.Timing);
        }
        _currentResultSaved = autoSave;

        LogEvent("INSPECTION COMPLETE", $"{analysis.Verdict}: {analysis.SuggestedDefect}, score {analysis.DifferenceScore:F1}%, confidence {analysis.Confidence:P0}, total {analysis.Timing.TotalInspectionMilliseconds:F0} ms.");

        if (autoSave)
            LogEvent("SAVE", "Auto-save wrote inspection result to SQLite.");

        return analysis;
    }

    public void RefreshFromState() => _ = RefreshSafeAsync();

    private void RefreshHeader()
    {
        if (StationText is null ||
            BoardModelText is null ||
            LotText is null ||
            OperatorText is null ||
            EngineText is null ||
            ModelVersionText is null ||
            LearnedModelEvidenceText is null ||
            CameraSourceText is null ||
            CameraFrameMetadataText is null ||
            LightingSyncText is null ||
            TimingText is null)
        {
            return;
        }

        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        _cameraSource = CameraSourceFactory.ActiveSource;
        StationText.Text = state.StationId;
        BoardModelText.Text = string.IsNullOrWhiteSpace(_currentBoardModel) ? state.BoardProgram : _currentBoardModel;
        LotText.Text = string.IsNullOrWhiteSpace(_currentLotId) ? "POC-LOT" : _currentLotId;
        OperatorText.Text = state.OperatorWithRole;
        // The Engine row shows only the engine; camera connectivity is the Camera row's fact.
        EngineText.Text = engine.Name;
        ModelVersionText.Text = engine.Version;
        LearnedModelEvidenceText.Text = BuildLearnedModelEvidenceText(_currentAnalysis);
        // With the null source, "No Camera Connected: Not Connected" said the same thing twice.
        CameraSourceText.Text = _cameraSource is NullCameraSource
            ? CameraStatusText()
            : $"{_cameraSource.Name}: {CameraStatusText()}";
        CameraFrameMetadataText.Text = _currentFrameMetadata;
        LightingSyncText.Text = _lastLightingResult;
        if (_currentAnalysis is null)
            TimingText.Text = "--";
        UpdateRobotSimulationStatus();
    }

    private static string BuildLearnedModelEvidenceText(AnalysisResult? analysis)
    {
        var configuration = InspectionModelConfigurationService.Load();
        if (!configuration.IsLearnedVisualModelSelected)
            return "Learned visual model: not active.";

        if (analysis is not null &&
            string.Equals(analysis.InspectionEngine, ImageOnlyPcbLearningService.EngineName, StringComparison.OrdinalIgnoreCase))
        {
            var evidenceLines = analysis.Evidence
                .Where(line =>
                    line.StartsWith("Learned from", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("False-call target", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Recommended threshold", StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToArray();
            if (evidenceLines.Length > 0)
                return string.Join(" ", evidenceLines);
        }

        return LearnedVisualModelRegistryService.GetActiveLearnedVisualModel() is { } active
            ? string.Join(" ", LearnedVisualModelRegistryService.BuildEvidenceLines(active.Model).Take(3))
            : "Learned PCB Visual Model selected, but no active model metadata is available. Review required.";
    }

    private void ApplyCalibrationProfiles(IReadOnlyList<CalibrationProfileListItem> profileItems, long? selectedId)
    {
        _updatingCalibrationProfiles = true;
        try
        {
            var items = profileItems.Count == 0
                ? new List<CalibrationProfileListItem> { new("No 2D profile (Stage 2 prep)", null) }
                : profileItems.ToList();
            CalibrationProfileCombo.ItemsSource = items;
            CalibrationProfileCombo.SelectedItem = items.FirstOrDefault(item => item.Profile?.Id == selectedId) ?? items[0];
            _selectedCalibrationProfile = (CalibrationProfileCombo.SelectedItem as CalibrationProfileListItem)?.Profile;
        }
        finally
        {
            _updatingCalibrationProfiles = false;
        }
    }

    private void RefreshDefectRows()
    {
        _defects.Clear();
        if (_currentAnalysis is null)
            return;

        var side = SelectedView();
        var index = 1;
        foreach (var defect in _currentAnalysis.Defects)
        {
            double? boardXMillimeters = null;
            double? boardYMillimeters = null;
            if (TryMapDefectToBoard(defect, out var mappedBoardX, out var mappedBoardY))
            {
                boardXMillimeters = mappedBoardX;
                boardYMillimeters = mappedBoardY;
            }

            _defects.Add(new DefectRow(
                index++,
                defect.DefectType,
                defect.RoiId,
                defect.RoiType,
                defect.Confidence,
                // Classification-table severity of the defect *class* (Critical/Major/Minor),
                // resolved through the active taxonomy. Falls back to the engine's per-occurrence
                // severity when the class is not in the taxonomy, so the column is never blank.
                ResolveDefectSeverity(defect),
                string.IsNullOrWhiteSpace(defect.SideOrViewType) || defect.SideOrViewType == "sample" ? side : defect.SideOrViewType,
                defect.XPosition,
                defect.YPosition,
                boardXMillimeters,
                boardYMillimeters));
        }
    }

    /// <summary>
    /// Severity shown in the defect list. The customer classification table defines severity per
    /// defect class, so the active taxonomy is the source of truth; the engine's per-occurrence
    /// severity is the fallback for classes the taxonomy does not carry.
    /// </summary>
    private static string ResolveDefectSeverity(DefectResult defect)
    {
        var classSeverity = DefectTaxonomyService.SeverityFor(defect.DefectType);
        return string.IsNullOrWhiteSpace(classSeverity)
            ? (string.IsNullOrWhiteSpace(defect.Severity) ? "--" : defect.Severity)
            : classSeverity;
    }

    private bool TryMapDefectToBoard(DefectResult defect, out double boardXMillimeters, out double boardYMillimeters)
    {
        boardXMillimeters = 0;
        boardYMillimeters = 0;
        if (_currentBitmap is null)
            return false;

        var imageX = ToImagePixelCoordinate(defect.XPosition, _currentBitmap.PixelWidth);
        var imageY = ToImagePixelCoordinate(defect.YPosition, _currentBitmap.PixelHeight);
        return CalibrationTransformService.TryConvertImageToBoard(_selectedCalibrationProfile, imageX, imageY, out boardXMillimeters, out boardYMillimeters);
    }

    private static double ToImagePixelCoordinate(double coordinate, int pixelSize)
        => coordinate is >= 0 and <= 1 ? coordinate * pixelSize : coordinate;

    private double RenderOverlay()
    {
        var watch = Stopwatch.StartNew();
        DefectOverlayCanvas.Children.Clear();
        if (_currentAnalysis is null || _currentBitmap is null)
            return StopElapsed(watch);

        var imageArea = CalculateImageArea(_currentBitmap.PixelWidth, _currentBitmap.PixelHeight, 1000, 700);
        foreach (var defect in _currentAnalysis.Defects)
        {
            var box = defect.BoundingBox;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(4, box.Width * imageArea.Width),
                Height = Math.Max(4, box.Height * imageArea.Height),
                Stroke = new SolidColorBrush(ToVerdictColor(_currentAnalysis.Verdict)),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(32, ToVerdictColor(_currentAnalysis.Verdict).R, ToVerdictColor(_currentAnalysis.Verdict).G, ToVerdictColor(_currentAnalysis.Verdict).B)),
            };
            if (!string.IsNullOrWhiteSpace(defect.SourceRoiId) && defect.SourceRoiId != "UNASSIGNED")
                rect.StrokeDashArray = new DoubleCollection { 5, 3 };

            Canvas.SetLeft(rect, imageArea.X + box.X * imageArea.Width);
            Canvas.SetTop(rect, imageArea.Y + box.Y * imageArea.Height);
            DefectOverlayCanvas.Children.Add(rect);

            var roiLabel = string.IsNullOrWhiteSpace(defect.RoiId) ? string.Empty : $" [{defect.RoiId}]";
            var label = $"{defect.DefectType}{roiLabel} {defect.Confidence:P0}";
            var text = new TextBlock
            {
                Text = label,
                Background = new SolidColorBrush(Color.FromArgb(210, 5, 6, 7)),
                Foreground = new SolidColorBrush(ToVerdictColor(_currentAnalysis.Verdict)),
                FontWeight = FontWeights.Bold,
                FontSize = (double)Application.Current.FindResource("HmiFontSizeSection"),
                Padding = new Thickness(5, 2, 5, 2),
            };

            Canvas.SetLeft(text, imageArea.X + box.X * imageArea.Width);
            Canvas.SetTop(text, Math.Max(0, imageArea.Y + box.Y * imageArea.Height - 30));
            DefectOverlayCanvas.Children.Add(text);
        }

        return StopElapsed(watch);
    }

    private void UpdateTimingDisplay(InspectionTiming timing)
    {
        var session = InspectionLatencyService.GetCurrentSessionSummary();
        var activeTrace = _currentLatencyTrace?.Trace ?? _lastLatencyTrace;
        var saveMs = _currentLatencyTrace?.Trace.ResultPersistStartUtc is { } saveStart && _currentLatencyTrace.Trace.ResultPersistEndUtc is { } saveEnd
            ? Math.Max(0, (saveEnd - saveStart).TotalMilliseconds)
            : activeTrace?.TotalFrameToSavedResultMs > 0 && activeTrace.ResultPersistStartUtc is not null
                ? activeTrace.TotalFrameToSavedResultMs
                : session.P95SaveMs;
        var frameToOverlay = activeTrace?.TotalFrameToOverlayMs > 0
            ? activeTrace.TotalFrameToOverlayMs
            : timing.TotalInspectionMilliseconds;
        var frameToSaved = activeTrace?.TotalFrameToSavedResultMs > 0
            ? activeTrace.TotalFrameToSavedResultMs
            : saveMs;
        var warningSuffix = session.OverOneSecondCount > 0
            ? $" | over1s {session.OverOneSecondCount}"
            : string.Empty;
        TimingText.Text = $"Frame-overlay {frameToOverlay:F0} ms | frame-saved {frameToSaved:F0} | infer {timing.InferenceMilliseconds:F0} | overlay {timing.OverlayRenderingMilliseconds:F0} | save {saveMs:F0} | session p95 {session.P95FrameToOverlayMs:F0}{warningSuffix}";
        TimingText.Foreground = timing.IsOverOneSecond || session.OverOneSecondCount > 0
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCE5EB"));
    }

    private static void PopulateAnalysisSpans(InspectionLatencyTraceBuilder trace, InspectionTiming timing, DateTime analysisStartUtc, DateTime analysisEndUtc)
    {
        var cursor = analysisStartUtc;
        var loadMs = Math.Max(0, timing.ImageLoadMilliseconds);
        cursor = cursor.AddMilliseconds(loadMs);
        trace.StartSpan("preprocessing", cursor);
        cursor = cursor.AddMilliseconds(Math.Max(0, timing.PreprocessingMilliseconds));
        trace.StopSpan("preprocessing", cursor);
        trace.StartSpan("inference", cursor);
        cursor = cursor.AddMilliseconds(Math.Max(0, timing.InferenceMilliseconds));
        if (cursor > analysisEndUtc)
            cursor = analysisEndUtc;
        trace.StopSpan("inference", cursor);
        trace.StartSpan("postprocess", cursor);
        trace.StopSpan("postprocess", analysisEndUtc);
    }

    private void SetResultStatus(string verdict)
    {
        // Swap the shared verdict banner style instead of assigning raw hex brushes so the
        // Result surface stays on the HmiVerdictBanner Ok/Ng/Warn token family.
        var normalized = verdict.ToUpperInvariant();
        string bannerStyleKey;
        if (normalized == "OK")
        {
            ResultStatusText.Text = "OK";
            bannerStyleKey = "HmiVerdictBannerOk";
        }
        else if (normalized == "NG")
        {
            ResultStatusText.Text = "NG";
            bannerStyleKey = "HmiVerdictBannerNg";
        }
        else
        {
            ResultStatusText.Text = normalized == "WARNING" ? "WARNING" : "REVIEW";
            bannerStyleKey = "HmiVerdictBannerWarn";
        }

        ResultStatusBorder.Style = (Style)FindResource(bannerStyleKey);
    }

    private void LogEvent(string eventName, string message)
    {
        _alarms.Insert(0, new AlarmRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), eventName, message));
        if (_alarms.Count > 80)
            _alarms.RemoveAt(_alarms.Count - 1);

        WorkflowState.Instance.AddEvent("MAIN_INSPECTION", $"{eventName}: {message}");
    }

    private bool TryBeginRobotOperation(string eventName, out CancellationToken cancellationToken)
    {
        if (_robotCycleCancellation is not null)
        {
            cancellationToken = CancellationToken.None;
            LogEvent(eventName, "Robot cycle action ignored because another robot action is running.");
            return false;
        }

        _robotCycleCancellation = new CancellationTokenSource();
        cancellationToken = _robotCycleCancellation.Token;
        UpdateRobotSimulationStatus();
        return true;
    }

    private void EndRobotOperation()
    {
        _robotCycleCancellation?.Dispose();
        _robotCycleCancellation = null;
        UpdateRobotSimulationStatus();
    }

    private void LogRobotCommandResult(string eventName, IntegrationCommandResult result)
    {
        var elapsedMs = _robotCycleService.Elapsed.TotalMilliseconds;
        LogEvent(
            result.Accepted ? eventName : $"{eventName} WARNING",
            $"{result.Message} Status={result.Status}; state={_robotCycleService.CurrentState}; elapsed={elapsedMs:F0} ms.");
    }

    private void HandleRobotOperationException(string operationName, Exception ex)
    {
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = ex,
            OperationName = operationName,
            CurrentPage = "Main Inspection",
            IsFatal = false,
            IsUiThread = true,
        });

        LogEvent("ROBOT ERROR", $"{operationName} failed safely. Diagnostic: {Path.GetFileName(report.ReportPath)}");
        MessageBox.Show(report.OperatorMessage, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private LoadCommand BuildLoadCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text);

    private InspectCommand BuildInspectCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, SelectedView());

    private UnloadCommand BuildUnloadCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, "Simulated output tray");

    private async Task RunRobotCycleAsync(CancellationToken cancellationToken)
    {
        UpdateRobotSimulationStatus();

        try
        {
            if (IntegrationBoundaryRegistry.EmergencyStopMonitor.IsEmergencyStopActive)
            {
                _robotCycleService.RefreshEmergencyStopStatus("Cycle blocked because emergency stop simulation is active. Press Reset to clear it.");
                LogEvent("ROBOT CYCLE", "Cycle blocked because emergency stop simulation is active. Press Reset to clear it.");
                return;
            }

            await LoadNextBoardAsync(runInspection: false, cancellationToken);
            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT CYCLE", "Cycle stopped because no simulated board image is available.");
                return;
            }

            var run = await _robotCycleService.RunFullCycleAsync(
                BuildLoadCommand(),
                BuildInspectCommand(),
                async _ =>
                {
                    var analysis = await RunInspectionAsync(_currentImagePath);
                    if (analysis is not null && !SaveCurrentResultFromRobotCycle())
                        return null;
                    return analysis;
                },
                BuildUnloadCommand(),
                cancellationToken);

            LogEvent(
                run.Accepted ? "ROBOT CYCLE" : "ROBOT CYCLE WARNING",
                $"{run.Message} State={run.State}; elapsed={run.Elapsed.TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT CYCLE", "Robot cycle canceled.");
        }
        catch (Exception ex)
        {
            LogEvent("ROBOT ERROR", $"Robot cycle failed: {ex.Message}");
        }
        finally
        {
            UpdateRobotSimulationStatus();
        }
    }

    private bool SaveCurrentResultFromRobotCycle()
    {
        if (_currentAnalysis is null)
        {
            LogEvent("ROBOT CYCLE", "Cycle stopped because inspection did not produce a result to save.");
            return false;
        }

        if (_currentResultSaved)
            return true;

        try
        {
            WorkflowState.Instance.SetAnalysis(_currentAnalysis, persist: true);
            _currentResultSaved = true;
            LogEvent("ROBOT SAVE", $"Simulated cycle saved inspection result to SQLite: {_currentAnalysis.Verdict}.");
            return true;
        }
        catch (Exception ex)
        {
            LogEvent("ROBOT ERROR", $"Simulated cycle save failed: {ex.Message}");
            return false;
        }
    }

    private void UpdateRobotSimulationStatus()
    {
        if (RobotSimStatusText is null)
            return;

        var controller = IntegrationBoundaryRegistry.RobotController;
        var emergencyStop = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        var emergencyActive = emergencyStop.IsEmergencyStopActive;
        var state = _robotCycleService.CurrentState;
        var busy = _robotCycleCancellation is not null;

        RobotSimStatusText.Text = emergencyActive
            ? "E-STOP ACTIVE (simulation)"
            : controller.Status switch
            {
                IntegrationConnectionStatus.Ready => "Robot controller ready",
                IntegrationConnectionStatus.Simulated => "Ready: simulation only",
                IntegrationConnectionStatus.Error => "Robot boundary error",
                _ => "No robot connected",
            };
        RobotSimStatusText.Foreground = IntegrationStatusBrush(emergencyActive ? IntegrationConnectionStatus.Error : controller.Status);

        RobotBoardStateText.Text = !string.IsNullOrWhiteSpace(_robotCycleService.BoardId)
            ? $"Board: {_robotCycleService.BoardId}"
            : _robotController.IsBoardLoaded
                ? $"Simulated board loaded: {_robotController.LoadedBoardId}"
                : "No simulated board loaded";

        RobotCycleModeText.Text = busy
            ? $"State: {state} / action running"
            : $"State: {state}";

        var elapsed = _robotCycleService.Elapsed;
        RobotCycleTimeText.Text = elapsed > TimeSpan.Zero
            ? $"Cycle {elapsed.TotalMilliseconds:F0} ms"
            : "Last cycle --";

        RobotFaultText.Text = string.IsNullOrWhiteSpace(_robotCycleService.LastError)
            ? "Fault: none"
            : $"Fault: {_robotCycleService.LastError}";
        RobotFaultText.Foreground = string.IsNullOrWhiteSpace(_robotCycleService.LastError)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA6AF"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F27777"));

        var latestAcceptance = _lastRobotAcceptanceRun;
        RobotAcceptanceStatusText.Text = latestAcceptance is null
            ? "Robot acceptance: not validated"
            : BuildRobotAcceptanceStatus(latestAcceptance);
        RobotAcceptanceStatusText.Foreground = latestAcceptance?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };

        RobotLoadButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        RobotInspectButton.IsEnabled = !busy && (state is RobotCycleState.Loaded or RobotCycleState.ReadyToInspect);
        RobotUnloadButton.IsEnabled = !busy && (state is RobotCycleState.Loaded or RobotCycleState.ReadyToInspect or RobotCycleState.Inspecting);
        RobotCancelButton.IsEnabled = busy;
        RobotResetButton.IsEnabled = !busy;
        RobotEmergencyStopButton.IsEnabled = controller is SimulatedRobotController;
        RunRobotCycleButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        RunRobotAcceptanceButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        ExportRobotAcceptanceButton.IsEnabled = !busy;
    }

    private static string BuildRobotAcceptanceStatus(RobotAcceptanceRun run)
    {
        var boundary = run.SourceKind == "Simulated"
            ? " Simulation evidence only; no production robot movement was validated."
            : string.Empty;
        return $"Robot acceptance: {run.Status}; {run.SourceKind}; full cycle {run.FullCycleMs:F0} ms; e-stop {(run.EmergencyStopBlocked ? "blocked" : "not verified")}; invalid transition {(run.InvalidTransitionRejected ? "rejected" : "not verified")}.{boundary}";
    }

    private string CurrentBoardId()
    {
        var fileName = string.IsNullOrWhiteSpace(_currentImagePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(_currentImagePath);

        return string.IsNullOrWhiteSpace(fileName)
            ? $"SIM-{DateTime.Now:yyyyMMddHHmmss}"
            : fileName;
    }

    private string SelectedView()
        => ComboBoxTokens.SelectedToken(ViewSelector, "Top");

    private CameraViewType SelectedCameraView()
        => SelectedView() switch
        {
            "Side" => CameraViewType.Side,
            "Bottom" => CameraViewType.Bottom,
            _ => CameraViewType.Top,
        };

    private string CameraStatusText()
        => _cameraSource.ConnectionStatus switch
        {
            CameraSourceStatus.Ready => "Connected",
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };

    private async Task<IntegrationCommandResult> SynchronizeLightingForSelectedViewAsync(CancellationToken cancellationToken)
    {
        var settings = LightingSettingsService.Load();
        var controller = LightingControllerFactory.Create(settings);
        IntegrationBoundaryRegistry.LightingController = controller;

        var view = SelectedView();
        var program = LightingCommandFormatter.ProgramForView(settings, view);
        var result = await LightingSynchronizationService.SynchronizeAsync(controller, settings, view, cancellationToken);

        _lastLightingResult = FormatLightingResult(controller, view, program, result);
        LightingSyncText.Text = _lastLightingResult;
        LightingSyncText.Foreground = IntegrationStatusBrush(result.Status);

        if (!string.Equals(settings.Mode, LightingModes.None, StringComparison.OrdinalIgnoreCase))
            LogEvent(result.Accepted ? "LIGHTING" : "LIGHTING WARNING", result.Message);

        return result;
    }

    private static string FormatLightingResult(
        ILightingController controller,
        string view,
        string program,
        IntegrationCommandResult result)
    {
        var status = IntegrationStatusDisplay(result.Status);
        return $"{controller.Name}: {status} | {view} -> {program}";
    }

    private static Brush IntegrationStatusBrush(IntegrationConnectionStatus status)
    {
        var color = status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#9AA6AF",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string IntegrationStatusDisplay(IntegrationConnectionStatus status)
        => status switch
        {
            IntegrationConnectionStatus.Ready => "Ready",
            IntegrationConnectionStatus.Simulated => "Simulated",
            IntegrationConnectionStatus.Error => "Error",
            _ => "Not Connected",
        };

    private static string FrameMetadataDisplay(CameraFrame frame)
    {
        var size = frame.Width > 0 && frame.Height > 0
            ? $"{frame.Width}x{frame.Height}"
            : "size unknown";
        var pixelFormat = string.IsNullOrWhiteSpace(frame.PixelFormat) ? "format unknown" : frame.PixelFormat;
        var cameraId = string.IsNullOrWhiteSpace(frame.CameraId) ? "camera unknown" : frame.CameraId;
        return $"{frame.FrameId} | {cameraId} | {frame.ViewType} | {size} | {pixelFormat} | {frame.SourceKind}";
    }

    private static Rect CalculateImageArea(double imageWidth, double imageHeight, double hostWidth, double hostHeight)
    {
        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new Rect((hostWidth - width) / 2.0, (hostHeight - height) / 2.0, width, height);
    }

    private static double StopElapsed(Stopwatch watch)
    {
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static Color ToVerdictColor(string verdict) => verdict.ToUpperInvariant() switch
    {
        "OK" => Colors.LimeGreen,
        "NG" => Colors.Red,
        _ => Colors.Orange,
    };

    public sealed record DefectRow(int No, string Type, string RoiId, string RoiType, double Score, string Severity, string Side, double X, double Y, double? BoardX, double? BoardY)
    {
        public string ScoreDisplay => Score.ToString("P0", CultureInfo.InvariantCulture);
        public string XDisplay => X.ToString("P0", CultureInfo.InvariantCulture);
        public string YDisplay => Y.ToString("P0", CultureInfo.InvariantCulture);
        public string BoardXDisplay => BoardX is null ? "--" : BoardX.Value.ToString("F2", CultureInfo.InvariantCulture);
        public string BoardYDisplay => BoardY is null ? "--" : BoardY.Value.ToString("F2", CultureInfo.InvariantCulture);
    }

    public sealed record AlarmRow(string Time, string Event, string Message);

    private sealed record BoardImageContext(
        string ImagePath,
        ImportedImage? ImportedImage,
        string BoardModel,
        string LotId,
        string SourceFrameId,
        string ViewType,
        string SourceKind,
        bool IsSimulatedSource,
        string FrameMetadata);

    private sealed record CalibrationProfileListItem(string DisplayName, CalibrationProfileRecord? Profile)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record MonitorRefreshSnapshot(
        ImportedImage[] ImportedImages,
        IReadOnlyList<CalibrationProfileListItem> CalibrationProfiles,
        RobotAcceptanceRun? LatestRobotAcceptance,
        string SamplePath,
        bool SampleExists);

    public void Dispose()
    {
        CancelAndDispose(ref _robotCycleCancellation);
        CancelAndDispose(ref _refreshCancellation);
        GC.SuppressFinalize(this);
    }
}
