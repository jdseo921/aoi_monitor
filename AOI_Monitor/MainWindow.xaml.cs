using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Microsoft.Win32;

namespace AOI_Monitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly Dictionary<string, UserControl> _pageCache = new();
    private readonly ObservableCollection<AlarmEvent> _alarmRows = new();
    private CancellationTokenSource? _navigationCts;
    private CancellationTokenSource? _footerCts;
    private MainViewModel _vm = null!;
    private string _activePageKey = string.Empty;
    private string _pendingNavigationKey = string.Empty;
    private int _navigationSequence;
    private bool _operatorOpenedAlarmPanel;
    private bool _updatingAlarmFlyout;
    public string CurrentPageKey => _vm?.CurrentPage ?? "home";

    private static readonly Dictionary<string, string> PageTitles = new()
    {
        ["home"] = "AOI DEFECT CONSOLE / HOME",
        ["library"] = "BOARD & IMAGE MANAGEMENT / IMAGE LIBRARY",
        ["monitor"] = "RUN INSPECTION / LOT EXECUTION",
        ["compare"] = "DEFECT REVIEW / GOLDEN TEMPLATE COMPARE",
        ["review"] = "DEFECT REVIEW / EVIDENCE & DISPOSITION",
        ["recipe"] = "RECIPE RULES / ROI, MASKS, TOLERANCES",
        ["modeltest"] = "AI / MODEL EVIDENCE AND FALSE-CALL CONTROL",
        ["spc"] = "YIELD ANALYTICS / SPC, PARETO, TRENDS",
        ["reports"] = "EXPORT & TRACE / PACKAGE, AUDIT, MES",
        ["pilot"] = "CUSTOMER PILOT WIZARD / STAGE 1-2 EVIDENCE",
        ["profile"] = "3D PROFILE VIEWER / SAMPLE DATA MODE",
        ["calibration"] = "CALIBRATION / 2D TRANSFORM AND STAGE 2 PREP",
        ["settings"] = "SYSTEM SETTINGS / DISPLAY, STORAGE, SECURITY",
        ["install"] = "SYSTEM HEALTH / INSTALLATION NOTES",
        ["guide"] = "SYSTEM HEALTH / GUIDE",
    };

    public MainWindow()
    {
        InitializeComponent();
        AlarmListBox.ItemsSource = _alarmRows;
        UiPreferencesService.ApplyToApplication();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StorageRootSettingsService.ApplySavedStorageRoot();
            AoiDatabase.Initialize();
            LogRetentionService.RunStartupRetention();
            var expiredAlarms = AlarmEventService.ExpireStaleActiveAlarms(TimeSpan.FromDays(14));
            if (expiredAlarms > 0)
                WorkflowState.Instance.AddEvent("ALARM_EXPIRE", $"{expiredAlarms} stale non-critical alarm(s) older than 14 days were auto-resolved at startup.");
            CameraSourceSettingsService.ApplyActiveSource();
            LightingSettingsService.ApplyIntegrationBoundary();
            MesIntegrationSettingsService.ApplyIntegrationBoundary();
            WorkflowState.Instance.SetAuthenticationMode(AuthenticationSettingsService.CurrentMode);
            WorkflowState.Instance.AddEvent("STORAGE", $"SQLite ready: {AoiDatabase.DatabasePath}");
            UpdateReadinessPanel(databaseConnected: File.Exists(AoiDatabase.DatabasePath), vaultAvailable: Directory.Exists(AoiDatabase.ImageVaultPath));
            _ = UpdateFooterStatusAsync();
        }
        catch (Exception ex)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Initialize local database",
                "Startup",
                ex,
                AlarmSeverity.Critical,
                "Check the configured storage path and database access, then restart the application.");
            UpdateReadinessPanel(databaseConnected: false, vaultAvailable: false);
            _ = UpdateFooterStatusAsync();
            MessageBox.Show(alarm.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _vm = (MainViewModel)DataContext;
        _vm.RefreshLanguage(UiPreferencesService.Load().Language);
        ApplyShellLanguage();
        ApplyUiPreferencesToShell();
        UiPreferencesService.ApplyLocalization(this);
        UserIdText.Text = WorkflowState.Instance.OperatorId;
        RoleCombo.SelectedIndex = WorkflowState.Instance.CurrentRole switch
        {
            UserRole.Admin => 2,
            UserRole.Engineer => 1,
            _ => 0,
        };
        AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
        _vm.RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
        RefreshRoleUi();
        RefreshOperatingModeBanner();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentPage))
                _ = SwitchPageAsync(_vm.CurrentPage);
        };

        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        CameraSourceFactory.ActiveSourceChanged += OnCameraSourceChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged += OnMesIntegrationSettingsChanged;
        AuthenticationSettingsService.AuthenticationChanged += OnAuthenticationChanged;
        OperatingModeSettingsService.SettingsChanged += OnOperatingModeSettingsChanged;
        DeploymentProfileSettingsService.SettingsChanged += OnDeploymentProfileSettingsChanged;
        UiPreferencesService.PreferencesChanged += OnUiPreferencesChanged;
        AlarmEventService.AlarmEventsChanged += OnAlarmEventsChanged;
        Closed += (_, _) => WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        Closed += (_, _) => InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        Closed += (_, _) => CameraSourceFactory.ActiveSourceChanged -= OnCameraSourceChanged;
        Closed += (_, _) => LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        Closed += (_, _) => MesIntegrationSettingsService.SettingsChanged -= OnMesIntegrationSettingsChanged;
        Closed += (_, _) => AuthenticationSettingsService.AuthenticationChanged -= OnAuthenticationChanged;
        Closed += (_, _) => OperatingModeSettingsService.SettingsChanged -= OnOperatingModeSettingsChanged;
        Closed += (_, _) => DeploymentProfileSettingsService.SettingsChanged -= OnDeploymentProfileSettingsChanged;
        Closed += (_, _) => UiPreferencesService.PreferencesChanged -= OnUiPreferencesChanged;
        Closed += (_, _) => AlarmEventService.AlarmEventsChanged -= OnAlarmEventsChanged;

        _ = SwitchPageAsync("home");
        UpdateWorkflowPanel();
        RefreshAlarmPanel();
        Dispatcher.BeginInvoke(new Action(ShowFirstRunWizardIfNeeded));
    }

    private void ShowFirstRunWizardIfNeeded()
    {
        try
        {
            if (FirstRunSettingsService.IsCompleted())
                return;

            var wizard = new FirstRunWizardView
            {
                Owner = this,
            };
            wizard.ShowDialog();
            UpdateReadinessPanel(databaseConnected: File.Exists(AoiDatabase.DatabasePath), vaultAvailable: Directory.Exists(AoiDatabase.ImageVaultPath));
            _ = UpdateFooterStatusAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Show setup wizard",
                "Startup",
                ex,
                AlarmSeverity.Warning,
                "Open Settings and review setup status before using the workstation for client evidence.");
            WorkflowState.Instance.AddEvent("FIRST_RUN", $"First-run setup wizard could not be shown. Alarm={alarm.AlarmId}.");
            MessageBox.Show(alarm.Message, "AOI Monitor Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SwitchPageAsync(string key)
    {
        key = string.IsNullOrWhiteSpace(key) ? "home" : key.Trim();
        if (string.Equals(_activePageKey, key, StringComparison.OrdinalIgnoreCase) &&
            PageContent.Content is not null)
        {
            HideLoadingOverlay();
            RefreshActiveRouteChrome(key);
            UiPerformanceMonitorService.Record("NAVIGATION_DUPLICATE_IGNORED", key, 0, $"Duplicate navigation ignored: {key}.");
            return;
        }

        if (string.Equals(_pendingNavigationKey, key, StringComparison.OrdinalIgnoreCase))
        {
            UiPerformanceMonitorService.Record("NAVIGATION_DUPLICATE_IGNORED", key, 0, $"Duplicate navigation ignored while page is loading: {key}.");
            return;
        }

        if (!EnsurePageAccess(key))
            return;

        // Header flyouts must never survive a page change: an open popup sits above the new
        // workspace, intercepts clicks, and reads as a frozen app to the operator.
        SetActiveAlarmsFlyoutOpen(false, operatorInitiated: true);
        AccessPanelPopup.IsOpen = false;

        _pendingNavigationKey = key;
        var navigationId = Interlocked.Increment(ref _navigationSequence);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var cancellationToken = _navigationCts.Token;
        var previousPage = PageContent.Content;
        var previousTitle = PageTitleText.Text;
        var previousKey = _activePageKey;

        using var navigationScope = UiPerformanceMonitorService.BeginNavigation(key);
        try
        {
            UiPerformanceMonitorService.RecordNavigationStart(key);
            var visualResponseStopwatch = Stopwatch.StartNew();
            PageErrorCard.Visibility = Visibility.Collapsed;
            if (!_pageCache.TryGetValue(key, out var page))
            {
                using var constructionScope = UiPerformanceMonitorService.BeginPageConstruction(key);
                page = CreatePage(key);
                _pageCache[key] = page;
                MemoryDiagnosticsService.SetPageCacheCount(_pageCache.Count);
            }

            if (!IsCurrentNavigation(navigationId, cancellationToken))
                return;

            PageContent.Content = page;
            _activePageKey = key;
            PageTitleText.Text = PageTitles.TryGetValue(key, out var t) ? t : key.ToUpperInvariant();
            PageTitleText.Text = LocalizedPageTitle(key);
            UiPreferencesService.ApplyLocalization(page);
            BringActiveNavItemIntoView();
            visualResponseStopwatch.Stop();
            UiPerformanceMonitorService.RecordNavigationVisualResponse(key, visualResponseStopwatch.ElapsedMilliseconds);
            UiPerformanceMonitorService.RecordCachedPageSwitch(key, navigationScope.ElapsedMilliseconds);
            _ = UpdateFooterStatusAsync();
            ReleasePreviousHeavyPageResources(previousPage, previousKey, key);
            ImageCacheService.ClearOnMemoryPressure();

            if (page is IAsyncNavigationPage asyncPage)
                await RunPageLifecycleAsync(key, asyncPage, cancellationToken);

            if (IsCurrentNavigation(navigationId, cancellationToken))
                UiPreferencesService.ApplyLocalization(page);
        }
        catch (OperationCanceledException)
        {
            HideLoadingOverlay();
            UiPerformanceMonitorService.Record("NAVIGATION", key, navigationScope.ElapsedMilliseconds, $"Navigation to {key} was cancelled.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentNavigation(navigationId, CancellationToken.None))
                return;

            HideLoadingOverlay();
            PageContent.Content = previousPage;
            _activePageKey = previousKey;
            PageTitleText.Text = previousTitle;
            ShowRecoverablePageError(key, "navigation", ex);
        }
        finally
        {
            if (string.Equals(_pendingNavigationKey, key, StringComparison.OrdinalIgnoreCase))
                _pendingNavigationKey = string.Empty;
        }
    }

    private void RefreshActiveRouteChrome(string key)
    {
        PageTitleText.Text = LocalizedPageTitle(key);
        if (PageContent.Content is FrameworkElement page)
            UiPreferencesService.ApplyLocalization(page);
        BringActiveNavItemIntoView();
    }

    private bool IsCurrentNavigation(int navigationId, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && navigationId == Volatile.Read(ref _navigationSequence);

    public async Task ExerciseUiNavigationForStabilityAsync(string pageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = pageName switch
        {
            "Main Inspection" => "monitor",
            "Run Inspection" => "monitor",
            "Home" or "Main Home" => "home",
            "Board & Images" or "Image Library" => "library",
            "Golden Compare" => "compare",
            "Review Defects" or "Defect Review" => "review",
            "Analyze Yield" or "Yield Analytics" => "spc",
            "AI / Models" or "AI Model Test" => "modeltest",
            "Recipe & Model" or "Recipe Rules" => "recipe",
            "Export / Trace" or "Export & Trace" => "reports",
            "System Health" or "System Settings" => "settings",
            "Recipe Editor" => "recipe",
            "Log & Export" => "reports",
            "Settings" => "settings",
            "Calibration" => "calibration",
            "3D Profile Viewer" => "profile",
            "Management Dashboard" => "reports",
            "Factory Readiness" => "reports",
            "Factory Acceptance Checklist" => "reports",
            "Model Registry / Acceptance" => "settings",
            "MES Queue" => "reports",
            "Central Sync" => "reports",
            "Hardware Acceptance" => "settings",
            _ => "reports",
        };

        await Dispatcher.InvokeAsync(() =>
        {
            if (_vm.CurrentPage != key)
                _vm.CurrentPage = key;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, CancellationToken.None);
        await Task.Delay(35, cancellationToken);
    }

    private static void ReleasePreviousHeavyPageResources(object? previousPage, string previousKey, string nextKey)
    {
        if (previousPage is null || string.Equals(previousKey, nextKey, StringComparison.OrdinalIgnoreCase))
            return;

        if (previousPage is IReleasablePageResources releasable)
            releasable.ReleasePageResources();

        if (previousKey is "monitor" or "recipe" or "calibration" or "profile" or "modeltest")
            ImageCacheService.ClearOnPageUnload();
    }

    private UserControl CreatePage(string key)
        => key switch
        {
            "home" => new HomeView(),
            "monitor" => new MonitorView(),
            "review" => new ReviewView(),
            "compare" => new CompareView(),
            "library" => new LibraryView(),
            "recipe" => new RecipeView(),
            "modeltest" => new AIModelTestView(),
            "profile" => new ProfileView(),
            "calibration" => new CalibrationView(),
            "spc" => new SpcView(),
            "reports" => new ReportsView(),
            "pilot" => new PilotWizardView(),
            "install" => new InstallView(),
            "settings" => new SettingsView(_vm),
            "guide" => new GuideView(),
            _ => new HomeView(),
        };

    private async Task RunPageLifecycleAsync(string key, IAsyncNavigationPage page, CancellationToken cancellationToken)
    {
        using var overlayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var overlayTask = ShowLoadingOverlayAfterDelayAsync(key, overlayCts.Token);
        using var pageScope = UiPerformanceMonitorService.BeginPageRefresh(key);
        try
        {
            var result = await UiErrorBoundaryService.RunAsync(
                $"Page refresh: {key}",
                key,
                page.OnNavigatedToAsync,
                cancellationToken);
            if (!result.Success)
                ShowRecoverablePageError(key, result.OperatorMessage);
        }
        finally
        {
            overlayCts.Cancel();
            HideLoadingOverlay();
            try
            {
                await overlayTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ShowLoadingOverlayAfterDelayAsync(string key, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            LoadingOverlayText.Text = $"Loading {LocalizedPageTitle(key)}...";
            LoadingOverlay.Visibility = Visibility.Visible;
        }, System.Windows.Threading.DispatcherPriority.Background, cancellationToken);
    }

    private void HideLoadingOverlay()
    {
        if (Dispatcher.CheckAccess())
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        Dispatcher.BeginInvoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);
    }

    private void ShowRecoverablePageError(string key, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowRecoverablePageError(key, message));
            return;
        }

        PageErrorText.Text = $"{PageTitles.GetValueOrDefault(key, key)} could not refresh.\n{message}";
        PageErrorCard.Visibility = Visibility.Visible;
        try
        {
            WorkflowState.Instance.AddEvent("PAGE_ERROR", $"{key}: {message}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Workflow page-error event could not be recorded: {ex.Message}");
        }
    }

    private void OnAlarmEventsChanged()
        => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshAlarmPanel);

    private void OnAlarmFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            RefreshAlarmPanel();
    }

    private void OnAlarmSourceFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            RefreshAlarmPanel();
    }

    private void RefreshAlarmPanel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshAlarmPanel));
            return;
        }

        var query = new AlarmEventQuery
        {
            ActiveOnly = true,
            Severity = SelectedAlarmSeverity(),
            SourceContains = AlarmSourceFilterText?.Text ?? string.Empty,
            SortOrder = SelectedAlarmSortOrder(),
        };
        var alarms = AlarmEventService.GetEvents(query);
        _alarmRows.Clear();
        foreach (var alarm in alarms)
            _alarmRows.Add(alarm);

        var allActive = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = true, SortOrder = AlarmSortOrder.SeverityDescending });
        var critical = allActive.Count(item => item.Severity == AlarmSeverity.Critical);
        var alarmLevel = allActive.Count(item => item.Severity == AlarmSeverity.Alarm);
        var warnings = allActive.Count(item => item.Severity == AlarmSeverity.Warning);
        ActiveAlarmSummaryText.Text = allActive.Count == 0
            ? "No active alarms."
            : $"{critical} Critical / {alarmLevel} Alarm / {warnings} Warning active.";
        ActiveAlarmHeaderText.Text = allActive.Count == 0
            ? "none"
            : critical > 0
                ? $"{critical} critical / {alarmLevel} alarm"
                : alarmLevel > 0
                    ? $"{alarmLevel} alarm / {warnings} warning"
                    : $"{warnings} warning";
        ActiveAlarmSummaryText.Foreground = critical > 0
            ? Brushes.LightCoral
            : alarmLevel > 0
                ? Brushes.OrangeRed
                : warnings > 0
                    ? Brushes.Gold
                    : Brushes.LightGreen;
        ActiveAlarmHeaderText.Foreground = ActiveAlarmSummaryText.Foreground;
        ActiveAlarmsButton.ToolTip = allActive.Count == 0
            ? "No active alarms."
            : $"{ActiveAlarmSummaryText.Text} Open active alarm list.";
        ApplyAlarmButtonVisual(critical, alarmLevel, warnings);

        if (allActive.Count == 0 && !_operatorOpenedAlarmPanel)
            SetActiveAlarmsFlyoutOpen(false, operatorInitiated: false);
    }

    private void OnActiveAlarmsButtonClick(object sender, RoutedEventArgs e)
        => SetActiveAlarmsFlyoutOpen(!ActiveAlarmsPopup.IsOpen, operatorInitiated: true);

    private void OnCloseActiveAlarmsClick(object sender, RoutedEventArgs e)
        => SetActiveAlarmsFlyoutOpen(false, operatorInitiated: true);

    private void OnActiveAlarmsPopupClosed(object? sender, EventArgs e)
    {
        if (!_updatingAlarmFlyout)
            _operatorOpenedAlarmPanel = false;
    }

    private void SetActiveAlarmsFlyoutOpen(bool isOpen, bool operatorInitiated)
    {
        _updatingAlarmFlyout = true;
        try
        {
            ActiveAlarmsPopup.IsOpen = isOpen;
            if (operatorInitiated)
                _operatorOpenedAlarmPanel = isOpen;
        }
        finally
        {
            _updatingAlarmFlyout = false;
        }
    }

    private void ApplyAlarmButtonVisual(int critical, int alarmLevel, int warnings)
    {
        var (background, border) = critical > 0
            ? ("#35191B", "#A13A3F")
            : alarmLevel > 0
                ? ("#3A1916", "#C64B3B")
                : warnings > 0
                    ? ("#372914", "#987538")
                    : ("#141D24", "#3A4A55");
        ActiveAlarmsButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        ActiveAlarmsButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
    }

    private void OnAccessPanelButtonClick(object sender, RoutedEventArgs e)
        => AccessPanelPopup.IsOpen = !AccessPanelPopup.IsOpen;

    private void OnAcknowledgeAlarmClick(object sender, RoutedEventArgs e)
    {
        if (AlarmListBox.SelectedItem is not AlarmEvent alarm)
        {
            MessageBox.Show("Select an alarm first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (AlarmEventService.Acknowledge(alarm.AlarmId, WorkflowState.Instance.OperatorWithRole))
            WorkflowState.Instance.AddEvent("ALARM_ACK", $"Alarm acknowledged: {alarm.AlarmId}; severity={alarm.Severity}; source={alarm.Source}.");
    }

    private void OnAcknowledgeAllAlarmsClick(object sender, RoutedEventArgs e)
    {
        var acknowledged = AlarmEventService.AcknowledgeAllActive(WorkflowState.Instance.OperatorWithRole);
        if (acknowledged == 0)
        {
            MessageBox.Show(
                UiPreferencesService.Text("No unacknowledged alarms.", "미확인 알람이 없습니다."),
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        WorkflowState.Instance.AddEvent("ALARM_ACK", $"All active alarms acknowledged in one action: {acknowledged} alarm(s).");
    }

    private void OnAlarmDetailsClick(object sender, RoutedEventArgs e)
    {
        if (AlarmListBox.SelectedItem is not AlarmEvent alarm)
        {
            MessageBox.Show("Select an alarm first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"Time UTC: {alarm.TimestampUtc:O}");
        text.AppendLine($"Severity: {alarm.Severity}");
        text.AppendLine($"Source: {alarm.Source}");
        text.AppendLine($"Message: {alarm.Message}");
        text.AppendLine($"Recommended action: {alarm.RecommendedAction}");
        text.AppendLine($"Acknowledgement: {alarm.AcknowledgementState}");
        text.AppendLine($"Simulated/mock/not validated: {alarm.IsSimulatedOrMock}");
        if (WorkflowState.Instance.CurrentRole is UserRole.Engineer or UserRole.Admin && !string.IsNullOrWhiteSpace(alarm.EngineerDetails))
        {
            text.AppendLine();
            text.AppendLine("Engineer/Admin detail:");
            text.AppendLine(alarm.EngineerDetails);
        }

        MessageBox.Show(text.ToString(), "Alarm Details", MessageBoxButton.OK, alarm.Severity == AlarmSeverity.Critical ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    private void OnExportAlarmLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = AlarmEventService.ExportLog();
            WorkflowState.Instance.AddEvent("ALARM_EXPORT", $"Alarm log exported: {result.Folder}.", relatedPath: result.Folder);
            MessageBox.Show($"Alarm log exported:\n{result.Folder}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Export alarm log",
                "AlarmPanel",
                ex,
                AlarmSeverity.Alarm,
                "Check export folder permissions and try exporting the alarm log again.");
            MessageBox.Show(alarm.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private AlarmSeverity? SelectedAlarmSeverity()
        => AlarmSeverityFilterCombo?.SelectedIndex switch
        {
            1 => AlarmSeverity.Info,
            2 => AlarmSeverity.Warning,
            3 => AlarmSeverity.Alarm,
            4 => AlarmSeverity.Critical,
            _ => null,
        };

    private AlarmSortOrder SelectedAlarmSortOrder()
        => AlarmSortCombo?.SelectedIndex switch
        {
            1 => AlarmSortOrder.NewestFirst,
            2 => AlarmSortOrder.OldestFirst,
            3 => AlarmSortOrder.SeverityAscending,
            _ => AlarmSortOrder.SeverityDescending,
        };

    private void ShowRecoverablePageError(string key, string operation, Exception ex)
    {
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = ex,
            OperationName = operation,
            CurrentPage = key,
            IsFatal = false,
            IsUiThread = true,
        });
        ShowRecoverablePageError(key, report.OperatorMessage);
    }

    private void OnDismissPageErrorClick(object sender, RoutedEventArgs e)
    {
        PageErrorCard.Visibility = Visibility.Collapsed;
    }

    private void OnCancelPageLoadClick(object sender, RoutedEventArgs e)
    {
        _navigationCts?.Cancel();
        if (PageContent.Content is IAsyncNavigationPage asyncPage)
            asyncPage.CancelWork();
        HideLoadingOverlay();
    }

    private static void BringActiveNavItemIntoView()
    {
        // Normal workflow navigation is now owned by Home so non-Home pages can use
        // the full workspace between the persistent top and bottom bars.
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _navigationCts?.Cancel();
            _navigationCts?.Dispose();
            _navigationCts = new CancellationTokenSource();
            var cancellationToken = _navigationCts.Token;

            if (PageContent.Content is IAsyncNavigationPage asyncPage)
            {
                await RunPageLifecycleAsync(_vm.CurrentPage, asyncPage, cancellationToken);
            }
            else if (PageContent.Content is CompareView compare)
            {
                compare.RefreshFromState();
            }
            else if (PageContent.Content is ReviewView review)
            {
                review.RefreshFromState();
            }
            else if (PageContent.Content is LibraryView library)
            {
                library.RefreshFromState();
            }
            else if (PageContent.Content is AIModelTestView modelTest)
            {
                modelTest.RefreshFromState();
            }
            else if (PageContent.Content is RecipeView recipe)
            {
                recipe.RefreshFromState();
            }
            else if (PageContent.Content is ReportsView reports)
            {
                reports.RefreshFromState();
            }
            else if (PageContent.Content is PilotWizardView pilot)
            {
                pilot.RefreshFromState();
            }
            else if (PageContent.Content is SpcView spc)
            {
                spc.RefreshFromState();
            }
            else if (PageContent.Content is CalibrationView calibration)
            {
                calibration.RefreshFromState();
            }

            _ = UpdateFooterStatusAsync();
            MessageBox.Show(UiPreferencesService.Text("View refreshed.", "\uD654\uBA74\uC774 \uC0C8\uB85C\uACE0\uCE68\uB418\uC5C8\uC2B5\uB2C8\uB2E4."), "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowRecoverablePageError(CurrentPageKey, "manual refresh", ex);
        }
    }

    private void OnHomeClick(object sender, RoutedEventArgs e)
    {
        PageErrorCard.Visibility = Visibility.Collapsed;
        if (_vm.CurrentPage.Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            _ = SwitchPageAsync("home");
            return;
        }

        _vm.CurrentPage = "home";
    }

    private void OnLayoutStressClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanUseMaintenanceActions, "Running developer layout stress test"))
            return;

        try
        {
            var reportPath = LayoutStressTestService.RunDeveloperStressTest();
            WorkflowState.Instance.AddEvent("LAYOUT_STRESS", $"Developer layout stress report written: {reportPath}");
            MessageBox.Show($"Layout stress report written:\n{reportPath}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Run layout stress test",
                "LayoutStress",
                ex,
                AlarmSeverity.Warning,
                "Review the layout test environment and run the audit again before client demo readiness.");
            MessageBox.Show(alarm.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReportIssueClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var issue = ShowReportIssueDialog();
            if (issue is null)
                return;

            var created = PilotIssueService.Create(issue, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("PILOT_ISSUE_CREATE", $"Manual issue reported: {created.IssueId}; {created.Category}; {created.Severity}.");
            MessageBox.Show($"Issue recorded:\n{created.IssueId}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            if (PageContent.Content is ReportsView reports)
                reports.RefreshFromState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowRecoverablePageError(CurrentPageKey, "report issue", ex);
        }
    }

    private PilotIssue? ShowReportIssueDialog()
    {
        var categoryCombo = new ComboBox { MinWidth = 240, SelectedIndex = 0 };
        foreach (var category in new[]
        {
            PilotIssueCategory.UIClipping,
            PilotIssueCategory.NavigationPerformance,
            PilotIssueCategory.Crash,
            PilotIssueCategory.Data,
            PilotIssueCategory.Model,
            PilotIssueCategory.Camera,
            PilotIssueCategory.MES,
            PilotIssueCategory.Export,
            PilotIssueCategory.Other,
        })
        {
            categoryCombo.Items.Add(new ComboBoxItem { Content = category.ToString(), Tag = category });
        }

        var severityCombo = new ComboBox { MinWidth = 160, SelectedIndex = 1 };
        foreach (var severity in new[] { "Low", "Medium", "High", "Critical" })
            severityCombo.Items.Add(new ComboBoxItem { Content = severity, Tag = severity });

        var pageText = new TextBox { Text = LocalizedPageTitle(CurrentPageKey), MinWidth = 360 };
        var stepsText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MinWidth = 520 };
        var expectedText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MinWidth = 520 };
        var actualText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MinWidth = 520 };
        var screenshotText = new TextBox { MinWidth = 420, IsReadOnly = true };
        var browse = new Button { Content = "Attach...", MinWidth = 86, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Attach screenshot or evidence file",
                Filter = "Images and reports|*.png;*.jpg;*.jpeg;*.bmp;*.txt;*.json;*.html;*.pdf|All files|*.*",
            };
            if (dialog.ShowDialog(this) == true)
                screenshotText.Text = dialog.FileName;
        };

        var ok = new Button { Content = "Record Issue", MinWidth = 112, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 86, IsCancel = true };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"Context: user={WorkflowState.Instance.OperatorWithRole}; profile={DeploymentProfileSettingsService.Load()}; page={LocalizedPageTitle(CurrentPageKey)}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(Label("Category"));
        root.Children.Add(categoryCombo);
        root.Children.Add(Label("Severity"));
        root.Children.Add(severityCombo);
        root.Children.Add(Label("Page"));
        root.Children.Add(pageText);
        root.Children.Add(Label("Reproduction steps"));
        root.Children.Add(stepsText);
        root.Children.Add(Label("Expected behavior"));
        root.Children.Add(expectedText);
        root.Children.Add(Label("Actual behavior"));
        root.Children.Add(actualText);
        root.Children.Add(Label("Screenshot / evidence"));
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { screenshotText, browse } });
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } });

        var dialogWindow = new Window
        {
            Title = "Report Manual Test Issue",
            Owner = this,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root },
        };

        ok.Click += (_, _) => dialogWindow.DialogResult = true;
        if (dialogWindow.ShowDialog() != true)
            return null;

        var selectedCategory = (categoryCombo.SelectedItem as ComboBoxItem)?.Tag is PilotIssueCategory selected
            ? selected
            : PilotIssueCategory.Other;
        var selectedSeverity = ComboBoxTokens.SelectedToken(severityCombo, "Medium");
        return new PilotIssue
        {
            Category = selectedCategory,
            Severity = selectedSeverity,
            PageName = pageText.Text,
            ReproductionSteps = stepsText.Text,
            ExpectedBehavior = expectedText.Text,
            ActualBehavior = actualText.Text,
            ScreenshotPath = screenshotText.Text,
            Owner = WorkflowState.Instance.OperatorWithRole,
            Notes = $"Manual report from {WorkflowState.Instance.OperatorWithRole}; profile={DeploymentProfileSettingsService.Load()}; auth={WorkflowState.Instance.AuthenticationMode}.",
        };
    }

    private static TextBlock Label(string text)
        => new() { Text = text, Foreground = Brushes.LightGray, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };

    private void OnExportSupportBundleClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanExportLogs, "Exporting support bundle"))
            return;

        try
        {
            var result = SupportBundleService.Export(
                new SupportBundleOptions { RedactCustomerImagePaths = true, RedactStorageRoot = true },
                WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("SUPPORT_BUNDLE", $"Support bundle exported: {result.ZipPath}");
            MessageBox.Show(
                $"Support bundle exported:\n{result.ZipPath}\n\nLatest crash report:\n{(string.IsNullOrWhiteSpace(CrashReportService.LastCrashReportPath) ? "--" : CrashReportService.LastCrashReportPath)}",
                "AOI Monitor Support Bundle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowRecoverablePageError(CurrentPageKey, "support bundle export", ex);
        }
    }

    private void OnUiPreferencesChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
        {
            UiPreferencesService.ApplyToApplication();
            _vm.RefreshLanguage(UiPreferencesService.Load().Language);
            UiPreferencesService.ApplyLocalization(this);
            // Dynamic chrome (banners, alarm chip, footer) must be rewritten after the
            // localization walk so a preferences change can never leave stale shell status.
            ApplyShellLanguage();
            ApplyUiPreferencesToShell();
            PageTitleText.Text = LocalizedPageTitle(_vm.CurrentPage);
            RefreshAlarmPanel();
            _ = UpdateFooterStatusAsync();
        });
    }

    private void ApplyShellLanguage()
    {
        FileMenuBtn.Content = UiPreferencesService.Text("File", "\uD30C\uC77C");
        AccessPanelButton.Content = UiPreferencesService.Text("Access", "\uC811\uADFC");
        ReportIssueBtn.Content = UiPreferencesService.Text("Report Issue", "\uC774\uC288 \uBCF4\uACE0");
        LayoutStressBtn.Content = UiPreferencesService.Text("Layout Stress", "\uB808\uC774\uC544\uC6C3 \uC810\uAC80");
        SupportBundleBtn.Content = UiPreferencesService.Text("Support Bundle", "\uC9C0\uC6D0 \uBC88\uB4E4");
        RefreshPageBtn.Content = UiPreferencesService.Text("Refresh", "\uC0C8\uB85C\uACE0\uCE68");
        LockRecipeBtn.Content = UiPreferencesService.Text("Lock Recipe", "\uB808\uC2DC\uD53C \uC7A0\uAE08");
        ExportBtn.Content = UiPreferencesService.Text("Export", "\uB0B4\uBCF4\uB0B4\uAE30");
        CancelPageLoadBtn.Content = UiPreferencesService.Text("Cancel", "\uCDE8\uC18C");
        RefreshOperatingModeBanner();
    }

    private void ApplyUiPreferencesToShell()
    {
        var preferences = UiPreferencesService.Load();
        BrandTitleText.Text = preferences.ConsoleTitle;
        StationNameText.Text = preferences.StationDisplayName;
        StationSubtitleText.Text = preferences.StationSubtitle;
        BrandTitleText.ToolTip = preferences.ConsoleTitle;
        StationNameText.ToolTip = preferences.StationDisplayName;
        StationSubtitleText.ToolTip = preferences.StationSubtitle;
        StationBadgeBorder.BorderBrush = UiPreferencesService.AccentBrush(preferences);
        BrandTitleText.Foreground = UiPreferencesService.AccentBrush(preferences);
        BrandAccentDot.Fill = UiPreferencesService.AccentBrush(preferences);

        if (!string.IsNullOrWhiteSpace(preferences.BrandLogoPath) && File.Exists(preferences.BrandLogoPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(preferences.BrandLogoPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                BrandLogoImage.Source = image;
                BrandLogoImage.Visibility = Visibility.Visible;
                BrandAccentDot.Visibility = Visibility.Collapsed;
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"Brand logo could not be loaded: {ex.Message}");
                BrandLogoImage.Visibility = Visibility.Collapsed;
                BrandAccentDot.Visibility = Visibility.Visible;
            }
            catch (NotSupportedException ex)
            {
                Trace.WriteLine($"Brand logo format is not supported: {ex.Message}");
                BrandLogoImage.Visibility = Visibility.Collapsed;
                BrandAccentDot.Visibility = Visibility.Visible;
            }
        }
        else
        {
            BrandLogoImage.Visibility = Visibility.Collapsed;
            BrandAccentDot.Visibility = Visibility.Visible;
        }
    }

    private static string LocalizedPageTitle(string key)
        => UiPreferencesService.Load().Language == UiLanguage.Korean
            ? key switch
            {
                "monitor" => "\uBA54\uC778 \uAC80\uC0AC / \uAC80\uD1A0 \uC6CC\uD06C\uD50C\uB85C",
                "home" => "AOI \uACB0\uD568 \uCF58\uC194 / \uD648",
                "library" => "\uBCF4\uB4DC / \uC774\uBBF8\uC9C0 \uAD00\uB9AC / \uC774\uBBF8\uC9C0 \uB77C\uC774\uBE0C\uB7EC\uB9AC",
                "compare" => "\uAE30\uC900 \uD15C\uD50C\uB9BF \uBE44\uAD50 / \uCC28\uC774 \uC810\uC218",
                "review" => "\uACB0\uD568 \uAC80\uD1A0 / \uC99D\uBE59 \uBC0F \uCC98\uBD84",
                "recipe" => "\uB808\uC2DC\uD53C \uADDC\uCE59 / ROI, \uB9C8\uC2A4\uD06C, \uACF5\uCC28",
                "modeltest" => "AI / \uBAA8\uB378 \uC99D\uBE59 \uBC0F \uD5C8\uC704 \uAC80\uCD9C \uC81C\uC5B4",
                "spc" => "\uC218\uC728 \uBD84\uC11D / SPC, \uD30C\uB808\uD1A0, \uCD94\uC138",
                "reports" => "\uB0B4\uBCF4\uB0B4\uAE30 / \uCD94\uC801 / \uD328\uD0A4\uC9C0, \uAC10\uC0AC, MES",
                "pilot" => "\uACE0\uAC1D \uD30C\uC77C\uB7FF \uB9C8\uBC95\uC0AC / 1-2\uB2E8\uACC4 \uC99D\uBE59",
                "profile" => "3D \uD504\uB85C\uD30C\uC77C \uBDF0\uC5B4 / \uC0D8\uD50C \uB370\uC774\uD130",
                "calibration" => "\uBCF4\uC815 / 2D \uBCC0\uD658 \uBC0F 2\uB2E8\uACC4 \uC900\uBE44",
                "settings" => "\uC2DC\uC2A4\uD15C \uC124\uC815 / \uD654\uBA74, \uC800\uC7A5\uC18C, \uBCF4\uC548",
                "install" => "\uC2DC\uC2A4\uD15C \uAC74\uAC15 / \uC124\uCE58 \uB178\uD2B8",
                "guide" => "\uC2DC\uC2A4\uD15C \uAC74\uAC15 / \uAC00\uC774\uB4DC",
                _ => key.ToUpperInvariant(),
            }
            : PageTitles.TryGetValue(key, out var title) ? title : key.ToUpperInvariant();

    private void OnMenuFileClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanExportLogs, "Opening the export folder"))
            return;

        var exportDir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(exportDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = exportDir,
            UseShellExecute = true,
        });
    }

    private void OnLockRecipeClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanUseMaintenanceActions, "Recipe lock maintenance"))
            return;

        var state = WorkflowState.Instance;
        state.IsRecipeLocked = !state.IsRecipeLocked;
        state.AddEvent("SYSTEM", state.IsRecipeLocked ? "Recipe locked." : "Recipe unlocked.");

        MessageBox.Show(
            state.IsRecipeLocked ? "Recipe is now locked." : "Recipe lock released.",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanExportLogs, "Export"))
            return;

        if (PageContent.Content is CompareView compare)
        {
            compare.ExportPair();
            return;
        }

        if (PageContent.Content is LibraryView library)
        {
            library.ExportSelectedRecord();
            return;
        }

        if (PageContent.Content is AIModelTestView modelTest)
        {
            modelTest.ExportResults();
            return;
        }

        MessageBox.Show("Export is available on Golden Compare, Image Library, AI Model Test, and Log & Export pages.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnWorkflowStateChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
        {
            _vm.RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
            RefreshRoleUi();
            UpdateWorkflowPanel();
            _ = UpdateFooterStatusAsync();
        });
    }

    private void OnRoleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (!OperatingModePolicyService.IsDemoRoleSelectorAllowed())
            return;
        if (WorkflowState.Instance.AuthenticationMode != AuthenticationMode.DemoLocalRoleSelector)
            return;

        var role = RoleCombo.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Engineer,
            _ => UserRole.Operator,
        };

        WorkflowState.Instance.SetCurrentUser(UserIdText.Text, role);
        _vm.RefreshRolePermissions(role);
        RefreshRoleUi();
        MessageBox.Show($"Demo local role selector set to {WorkflowState.Instance.OperatorWithRole}. This is not production authentication.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAuthModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Changing authentication mode"))
        {
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            return;
        }

        var mode = ComboToAuthenticationMode(AuthModeCombo.SelectedIndex);
        if (mode == AuthenticationMode.DemoLocalRoleSelector &&
            !OperatingModePolicyService.IsDemoRoleSelectorAllowed())
        {
            MessageBox.Show("DemoLocalRoleSelector is available only in Demo Mode. Select LocalUsers or the MES authentication boundary for Pilot/Production.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            return;
        }

        AuthenticationSettingsService.Save(new AuthenticationSettings { Mode = mode }, WorkflowState.Instance.OperatorWithRole);
        WorkflowState.Instance.SetAuthenticationMode(mode);
        RefreshRoleUi();
        MessageBox.Show($"Authentication mode set to {mode}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLocalLoginClick(object sender, RoutedEventArgs e)
    {
        var mode = WorkflowState.Instance.AuthenticationMode;
        if (mode == AuthenticationMode.DemoLocalRoleSelector &&
            !OperatingModePolicyService.IsDemoRoleSelectorAllowed())
        {
            MessageBox.Show("Demo role selection is disabled outside Demo Mode. Switch authentication to LocalUsers or MES boundary.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mode == AuthenticationMode.LocalUsers)
        {
            if (!LocalUserService.TryAuthenticate(UserIdText.Text, LoginPasswordBox.Password, out var user))
            {
                WorkflowState.Instance.AddEvent("ACCESS_DENIED", $"LocalUsers login failed for {UserIdText.Text.Trim()}.");
                MessageBox.Show("Local user login failed.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WorkflowState.Instance.SetCurrentUser(user.UserId, user.Role, AuthenticationMode.LocalUsers);
            RoleCombo.SelectedIndex = user.Role switch
            {
                UserRole.Admin => 2,
                UserRole.Engineer => 1,
                _ => 0,
            };
            _vm.RefreshRolePermissions(user.Role);
            RefreshRoleUi();
            MessageBox.Show($"Local user authenticated as {WorkflowState.Instance.OperatorWithRole}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (mode == AuthenticationMode.MesAuthenticationBoundary)
        {
            WorkflowState.Instance.SetCurrentUser(UserIdText.Text, UserRole.Operator, AuthenticationMode.MesAuthenticationBoundary);
            _vm.RefreshRolePermissions(UserRole.Operator);
            RefreshRoleUi();
            MessageBox.Show(
                "MES authentication boundary is documented for Stage 4 integration. No production MES identity provider is active in this PoC, so the session is limited to Operator.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var role = RoleCombo.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Engineer,
            _ => UserRole.Operator,
        };

        WorkflowState.Instance.SetCurrentUser(UserIdText.Text, role, AuthenticationMode.DemoLocalRoleSelector);
        _vm.RefreshRolePermissions(role);
        RefreshRoleUi();
        MessageBox.Show(
            $"Demo local role selector set to {WorkflowState.Instance.OperatorWithRole}.\nThis is not production authentication.",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnCreateLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Creating local users"))
            return;

        var password = PromptForPassword("Create Local User", $"Password for {UserIdText.Text.Trim()}");
        if (string.IsNullOrWhiteSpace(password))
            return;

        try
        {
            var role = RoleCombo.SelectedIndex switch
            {
                2 => UserRole.Admin,
                1 => UserRole.Engineer,
                _ => UserRole.Operator,
            };
            var user = LocalUserService.CreateUser(UserIdText.Text, role, password, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Local user created: {user.UserId} [{user.Role}]. Password was stored as a salted hash.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be created:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnChangeLocalPasswordClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Changing local user password"))
            return;

        var password = PromptForPassword("Set Local User Password", $"New password for {UserIdText.Text.Trim()}");
        if (string.IsNullOrWhiteSpace(password))
            return;

        try
        {
            var user = LocalUserService.ChangePassword(UserIdText.Text, password, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Password updated for local user {user.UserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user password could not be changed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDisableLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Disabling local users"))
            return;

        var targetUserId = UserIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUserId))
            return;

        try
        {
            var user = LocalUserService.DisableUser(targetUserId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, "Disabled from local user management UI.");
            MessageBox.Show($"Local user disabled: {user.UserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be disabled:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Deleting local users"))
            return;

        var targetUserId = UserIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUserId))
            return;

        if (MessageBox.Show($"Delete local user {targetUserId}? This removes the local login record.", "AOI Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            LocalUserService.DeleteUser(targetUserId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Local user deleted: {targetUserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be deleted:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshRoleUi()
    {
        var isAdmin = WorkflowState.Instance.CurrentRole == UserRole.Admin;
        FileMenuBtn.IsEnabled = isAdmin;
        LockRecipeBtn.IsEnabled = isAdmin;
        ExportBtn.IsEnabled = isAdmin;
        var canManageLocalUsers = isAdmin && WorkflowState.Instance.AuthenticationMode == AuthenticationMode.LocalUsers;
        CreateUserBtn.IsEnabled = canManageLocalUsers;
        ChangePasswordBtn.IsEnabled = canManageLocalUsers;
        DisableUserBtn.IsEnabled = canManageLocalUsers;
        DeleteUserBtn.IsEnabled = canManageLocalUsers;
        LoginPasswordBox.IsEnabled = WorkflowState.Instance.AuthenticationMode == AuthenticationMode.LocalUsers;
        RoleCombo.IsEnabled = WorkflowState.Instance.AuthenticationMode == AuthenticationMode.DemoLocalRoleSelector &&
            OperatingModePolicyService.IsDemoRoleSelectorAllowed();
        AuthModeCombo.IsEnabled = isAdmin;
        RefreshPersistentBanner();
    }

    private bool EnsurePageAccess(string key)
    {
        var role = WorkflowState.Instance.CurrentRole;
        if (RoleAuthorization.CanAccessPage(role, key))
            return true;

        var message = RoleAuthorization.DeniedMessage(role, $"Opening {PageTitles.GetValueOrDefault(key, key)}");
        WorkflowState.Instance.AddEvent("ACCESS_DENIED", message);
        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        _vm.RefreshRolePermissions(role);
        if (_vm.CurrentPage != "monitor")
            _vm.CurrentPage = "monitor";
        return false;
    }

    private bool EnsurePermission(Func<UserRole, bool> permission, string action)
    {
        if (WorkflowState.Instance.TryAuthorize(permission, action, out var message))
            return true;

        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void OnInspectionConfigurationChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, UpdateInspectionEngineStatus);
    }

    private void OnCameraSourceChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, UpdateCameraStatus);
    }

    private void OnMesIntegrationSettingsChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, UpdateIntegrationBoundaryStatus);
    }

    private void OnAuthenticationChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
        {
            WorkflowState.Instance.SetAuthenticationMode(AuthenticationSettingsService.CurrentMode);
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            RefreshRoleUi();
            RefreshPersistentBanner();
        });
    }

    private void OnOperatingModeSettingsChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
        {
            RefreshOperatingModeBanner();
            RefreshRoleUi();
            if (PageContent.Content is LibraryView library)
                library.RefreshFromState();
        });
    }

    private void OnDeploymentProfileSettingsChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshPersistentBanner);
    }

    private void RefreshOperatingModeBanner()
    {
        var mode = OperatingModeSettingsService.Load();
        OperatingModeBannerText.Text = mode switch
        {
            OperatingMode.Production => UiPreferencesService.Text("Production Mode", "\uC6B4\uC601 \uBAA8\uB4DC"),
            OperatingMode.Pilot => UiPreferencesService.Text("Pilot Mode", "\uD30C\uC77C\uB7FF \uBAA8\uB4DC"),
            _ => UiPreferencesService.Text("Demo Mode", "\uB370\uBAA8 \uBAA8\uB4DC"),
        };
        var (bg, border, fg, tooltip) = mode switch
        {
            OperatingMode.Production => ("#14311D", "#3C8D50", "#C6FFD0", "Production Mode: demo/fallback rows are blocked and factory readiness gates are enforced."),
            OperatingMode.Pilot => ("#372914", "#8C6C35", "#FFE0A7", "Pilot Mode: demo rows are hidden by default and simulated hardware must be clearly labeled."),
            _ => ("#2A1740", "#8F5FD1", "#F1D8FF", "Demo Mode: sample data, demo role selector, and simulated sources are allowed."),
        };
        OperatingModeBanner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        OperatingModeBanner.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        OperatingModeBannerText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        OperatingModeBanner.ToolTip = tooltip;
        RefreshPersistentBanner();
    }

    private void RefreshPersistentBanner()
    {
        var profile = DeploymentProfileSettingsService.Load();
        DeploymentProfileBannerText.Text = FormatDeploymentProfile(profile);
        DeploymentProfileBanner.ToolTip = $"Deployment profile: {profile}. This controls which factory readiness checks are expected.";

        HeaderUserText.Text = WorkflowState.Instance.OperatorId;
        HeaderRoleText.Text = WorkflowState.Instance.CurrentRole.ToString();
        HeaderUserText.ToolTip = $"Authenticated user: {WorkflowState.Instance.OperatorWithRole}";
        HeaderRoleText.ToolTip = $"Current role: {WorkflowState.Instance.CurrentRole}";

        var mode = OperatingModeSettingsService.Load();
        var mesMode = MesIntegrationSettingsService.Load().Mode;

        // Name the simulated boundaries instead of a generic banner: "DEMO / SIM ACTIVE"
        // duplicated the purple Demo Mode chip beside it (DESIGN.md forbids duplicated status),
        // and a named list is more useful evidence. Boundary tokens stay English for
        // traceability, like other technical codes.
        var simulatedBoundaries = new List<string>();
        if (WorkflowState.Instance.AuthenticationMode == AuthenticationMode.DemoLocalRoleSelector)
            simulatedBoundaries.Add("auth");
        if (CameraSourceFactory.ActiveSource.ConnectionStatus == CameraSourceStatus.Simulated)
            simulatedBoundaries.Add("camera");
        if (IntegrationBoundaryRegistry.LightingController.Status == IntegrationConnectionStatus.Simulated)
            simulatedBoundaries.Add("lighting");
        if (IntegrationBoundaryRegistry.RobotController.Status == IntegrationConnectionStatus.Simulated)
            simulatedBoundaries.Add("robot");
        if (IntegrationBoundaryRegistry.MesClient.Status == IntegrationConnectionStatus.Simulated || mesMode == MesIntegrationMode.MockRest)
            simulatedBoundaries.Add("MES");
        if (IntegrationBoundaryRegistry.TraceabilityUploader.Status == IntegrationConnectionStatus.Simulated)
            simulatedBoundaries.Add("trace");

        var hasSimulatedSource = mode == OperatingMode.Demo || simulatedBoundaries.Count > 0;

        SimulationWarningBanner.Visibility = hasSimulatedSource ? Visibility.Visible : Visibility.Collapsed;
        var simLabel = UiPreferencesService.Text("SIM ACTIVE", "\uC2DC\uBBAC \uD65C\uC131");
        SimulationWarningBannerText.Text = simulatedBoundaries.Count > 0
            ? $"{simLabel}: {string.Join(", ", simulatedBoundaries)}"
            : simLabel;
        SimulationWarningBanner.ToolTip = UiPreferencesService.Text(
            "Simulated or mock sources are active. Do not treat these results as production release evidence without review.",
            "\uC2DC\uBBAC\uB808\uC774\uC158 \uB610\uB294 \uBAA9 \uC18C\uC2A4\uAC00 \uD65C\uC131\uD654\uB418\uC5C8\uC2B5\uB2C8\uB2E4. \uAC80\uD1A0 \uC5C6\uC774 \uC0DD\uC0B0 \uCD9C\uC2DC \uC99D\uBE59\uC73C\uB85C \uC0AC\uC6A9\uD558\uC9C0 \uB9C8\uC2ED\uC2DC\uC624.");
        AlarmEventService.SetSimulationBoundaryWarning(
            hasSimulatedSource,
            SimulationWarningBannerText.Text,
            WorkflowState.Instance.OperatorWithRole);
    }

    private static string FormatDeploymentProfile(DeploymentProfile profile) => profile switch
    {
        DeploymentProfile.Stage1ImageValidation => UiPreferencesService.Text("Stage 1 Image", "1\uB2E8\uACC4 \uC774\uBBF8\uC9C0"),
        DeploymentProfile.Stage2CameraPilot => UiPreferencesService.Text("Stage 2 Camera", "2\uB2E8\uACC4 \uCE74\uBA54\uB77C"),
        DeploymentProfile.Stage3RobotPilot => UiPreferencesService.Text("Stage 3 Robot", "3\uB2E8\uACC4 \uB85C\uBD07"),
        DeploymentProfile.Stage4MesPilot => UiPreferencesService.Text("Stage 4 MES", "4\uB2E8\uACC4 MES"),
        DeploymentProfile.FullFactoryAutomation => UiPreferencesService.Text("Full Factory", "\uC804\uCCB4 \uACF5\uC7A5"),
        _ => profile.ToString(),
    };

    private void OnLightingSettingsChanged()
    {
        UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
        {
            LightingSettingsService.ApplyIntegrationBoundary();
            UpdateIntegrationBoundaryStatus();
        });
    }

    private void UpdateWorkflowPanel()
    {
        var state = WorkflowState.Instance;
        FooterStationText.Text = state.StationId;

        WorkflowSampleText.Text = string.IsNullOrWhiteSpace(state.SampleImagePath)
            ? "none"
            : Path.GetFileName(state.SampleImagePath);
        WorkflowSampleText.ToolTip = string.IsNullOrWhiteSpace(state.SampleImagePath)
            ? "No sample image selected."
            : state.SampleImagePath;

        WorkflowGoldenText.Text = string.IsNullOrWhiteSpace(state.GoldenImagePath)
            ? "none"
            : Path.GetFileName(state.GoldenImagePath);
        WorkflowGoldenText.ToolTip = string.IsNullOrWhiteSpace(state.GoldenImagePath)
            ? "No golden reference selected."
            : state.GoldenImagePath;

        if (state.LastAnalysis is null)
        {
            WorkflowScoreText.Text = "--";
            WorkflowScoreText.ToolTip = "No score is available yet.";
            WorkflowVerdictText.Text = "REVIEW";
            WorkflowVerdictText.ToolTip = "No current analysis is available; default workflow state is REVIEW.";
            WorkflowVerdictText.Foreground = Brushes.LightGray;
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2E31"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555E66"));
            return;
        }

        WorkflowScoreText.Text = $"{state.LastAnalysis.DifferenceScore:F1}%";
        WorkflowScoreText.ToolTip = $"Difference score: {state.LastAnalysis.DifferenceScore:F1}%.";
        WorkflowVerdictText.Text = state.LastAnalysis.Verdict;
        WorkflowVerdictText.ToolTip = $"Inspection verdict: {state.LastAnalysis.Verdict}.";

        if (state.LastAnalysis.Verdict == "NG")
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFBFC1"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35191B"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A393E"));
        }
        else if (state.LastAnalysis.Verdict == "OK")
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C6FFD0"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14311D"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#377849"));
        }
        else
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE0A7"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#372914"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C6C35"));
        }
    }

    private async Task UpdateFooterStatusAsync()
    {
        FooterStationText.Text = WorkflowState.Instance.StationId;
        _footerCts?.Cancel();
        _footerCts?.Dispose();
        _footerCts = new CancellationTokenSource();
        var cancellationToken = _footerCts.Token;

        try
        {
            var status = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspections = AoiDatabase.GetInspectionHistory(new Models.LogFilter());
                var images = AoiDatabase.GetImportedImages();
                var linkedImages = images.Count(image => File.Exists(image.VaultPath));
                var brokenLinks = images.Count - linkedImages;
                var dbExists = File.Exists(AoiDatabase.DatabasePath);
                var dbWriteTime = dbExists ? File.GetLastWriteTime(AoiDatabase.DatabasePath) : (DateTime?)null;
                return new FooterStatusSnapshot(inspections.Count, images.Count, linkedImages, brokenLinks, dbExists, dbWriteTime);
            }, cancellationToken);

            FooterRecordCountText.Text = status.InspectionCount.ToString("N0", CultureInfo.InvariantCulture);
            FooterIndexText.Text = status.BrokenImageCount == 0
                ? $"Images OK {status.LinkedImageCount:N0}/{status.ImageCount:N0}"
                : $"{status.BrokenImageCount:N0} Missing {status.LinkedImageCount:N0}/{status.ImageCount:N0}";
            FooterIndexText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status.BrokenImageCount == 0 ? "#50F56E" : "#F27777"));

            if (status.DatabaseExists && status.DatabaseWriteTime is { } dbWriteTime)
            {
                FooterDbUpdatedText.Text = dbWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                FooterDbRevText.Text = "SQLite";
            }
            else
            {
                FooterDbUpdatedText.Text = "--";
                FooterDbRevText.Text = "missing";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Footer status update failed: {ex.Message}");
            FooterRecordCountText.Text = "--";
            FooterIndexText.Text = "Unavailable";
            FooterIndexText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F27777"));
            FooterDbUpdatedText.Text = "--";
            FooterDbRevText.Text = "local";
        }
    }

    private sealed record FooterStatusSnapshot(
        int InspectionCount,
        int ImageCount,
        int LinkedImageCount,
        int BrokenImageCount,
        bool DatabaseExists,
        DateTime? DatabaseWriteTime);

    private void UpdateReadinessPanel(bool databaseConnected, bool vaultAvailable)
    {
        DatabaseStatusText.Text = databaseConnected
            ? UiPreferencesService.Text("Connected", "\uC5F0\uACB0\uB428")
            : UiPreferencesService.Text("Not Connected", "\uC5F0\uACB0 \uC548 \uB428");
        DatabaseStatusText.Foreground = StatusBrush(databaseConnected);

        ImageVaultStatusText.Text = vaultAvailable
            ? UiPreferencesService.Text("Available", "\uC0AC\uC6A9 \uAC00\uB2A5")
            : UiPreferencesService.Text("Not Available", "\uC0AC\uC6A9 \uBD88\uAC00");
        ImageVaultStatusText.Foreground = StatusBrush(vaultAvailable);

        UpdateInspectionEngineStatus();

        UpdateCameraStatus();

        UpdateIntegrationBoundaryStatus();
    }

    private static Brush StatusBrush(bool ok)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#50F56E" : "#F27777"));

    private void UpdateInspectionEngineStatus()
    {
        var status = InspectionModelConfigurationService.GetStatus();
        var statusText = InspectionModelConfigurationService.GetStatusText();
        InspectionEngineStatusText.Text = statusText;
        InspectionEngineStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            Models.InspectionEngineStatus.MlModelReady => "#50F56E",
            Models.InspectionEngineStatus.MlModelMissing => "#E1A334",
            Models.InspectionEngineStatus.MlModelNotTested => "#E1A334",
            Models.InspectionEngineStatus.MlRuntimeError => "#F27777",
            Models.InspectionEngineStatus.MlInvalidLabelMap => "#F27777",
            Models.InspectionEngineStatus.MlUnsupportedOutputFormat => "#F27777",
            Models.InspectionEngineStatus.LearnedVisualModelReady => "#C084FC",
            Models.InspectionEngineStatus.LearnedVisualModelMissing => "#E1A334",
            _ => "#E1A334",
        }));
    }

    private void UpdateCameraStatus()
    {
        var status = CameraSourceFactory.ActiveSource.ConnectionStatus;
        CameraStatusText.Text = status switch
        {
            CameraSourceStatus.Ready => UiPreferencesService.Text("Connected", "\uC5F0\uACB0\uB428"),
            CameraSourceStatus.Simulated => UiPreferencesService.Text("Simulated", "\uC2DC\uBBAC\uB808\uC774\uC158"),
            CameraSourceStatus.Error => UiPreferencesService.Text("Error", "\uC624\uB958"),
            _ => UiPreferencesService.Text("Not Connected", "\uC5F0\uACB0 \uC548 \uB428"),
        };
        CameraStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            CameraSourceStatus.Ready => "#50F56E",
            CameraSourceStatus.Simulated => "#C084FC",
            CameraSourceStatus.Error => "#F27777",
            _ => "#5CA0D3",
        }));
        RefreshPersistentBanner();
    }

    private void UpdateIntegrationBoundaryStatus()
    {
        SetIntegrationStatus(LightingStatusText, IntegrationBoundaryRegistry.LightingController);
        SetIntegrationStatus(RobotStatusText, IntegrationBoundaryRegistry.RobotController);
        SetIntegrationStatus(
            MesStatusText,
            "MES / Traceability Boundary",
            CombineStatuses(
                IntegrationBoundaryRegistry.MesClient.Status,
                IntegrationBoundaryRegistry.TraceabilityUploader.Status),
            $"{IntegrationBoundaryRegistry.MesClient.StatusMessage} {IntegrationBoundaryRegistry.TraceabilityUploader.StatusMessage}");
        SetIntegrationStatus(EmergencyStopStatusText, IntegrationBoundaryRegistry.EmergencyStopMonitor);
        RefreshPersistentBanner();
    }

    private static void SetIntegrationStatus(TextBlock target, IIntegrationEndpoint endpoint)
        => SetIntegrationStatus(target, endpoint.Name, endpoint.Status, endpoint.StatusMessage);

    private static void SetIntegrationStatus(
        TextBlock target,
        string name,
        IntegrationConnectionStatus status,
        string statusMessage)
    {
        target.Text = ToStatusDisplay(status);
        target.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#C084FC",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#5CA0D3",
        }));
        target.ToolTip = $"{name}: {statusMessage}";
    }

    private static IntegrationConnectionStatus CombineStatuses(
        IntegrationConnectionStatus first,
        IntegrationConnectionStatus second)
    {
        if (first == IntegrationConnectionStatus.Error || second == IntegrationConnectionStatus.Error)
            return IntegrationConnectionStatus.Error;
        if (first == IntegrationConnectionStatus.Ready && second == IntegrationConnectionStatus.Ready)
            return IntegrationConnectionStatus.Ready;
        if (first == IntegrationConnectionStatus.Simulated || second == IntegrationConnectionStatus.Simulated)
            return IntegrationConnectionStatus.Simulated;
        return IntegrationConnectionStatus.NotConnected;
    }

    private static string ToStatusDisplay(IntegrationConnectionStatus status) => status switch
    {
        IntegrationConnectionStatus.Ready => UiPreferencesService.Text("Ready", "\uC900\uBE44\uB428"),
        IntegrationConnectionStatus.Simulated => UiPreferencesService.Text("Simulated", "\uC2DC\uBBAC\uB808\uC774\uC158"),
        IntegrationConnectionStatus.Error => UiPreferencesService.Text("Error", "\uC624\uB958"),
        _ => UiPreferencesService.Text("Not Connected", "\uC5F0\uACB0 \uC548 \uB428"),
    };

    private static AuthenticationMode ComboToAuthenticationMode(int selectedIndex) => selectedIndex switch
    {
        1 => AuthenticationMode.LocalUsers,
        2 => AuthenticationMode.MesAuthenticationBoundary,
        _ => AuthenticationMode.DemoLocalRoleSelector,
    };

    private static int AuthenticationModeToCombo(AuthenticationMode mode) => mode switch
    {
        AuthenticationMode.LocalUsers => 1,
        AuthenticationMode.MesAuthenticationBoundary => 2,
        _ => 0,
    };

    private static string PromptForPassword(string title, string label)
    {
        var password = new PasswordBox { MinWidth = 300, MinHeight = 26, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", Width = 78, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 78, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeights.SemiBold },
                    password,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        return dialog.ShowDialog() == true ? password.Password : string.Empty;
    }

    public void Dispose()
    {
        DisposeCancellation(ref _navigationCts);
        DisposeCancellation(ref _footerCts);
        GC.SuppressFinalize(this);
    }

    private static void DisposeCancellation(ref CancellationTokenSource? cancellation)
    {
        var source = cancellation;
        cancellation = null;
        if (source is null)
            return;

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
}
