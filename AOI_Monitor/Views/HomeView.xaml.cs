using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        CameraSourceFactory.ActiveSourceChanged += OnCameraSourceChanged;
        LightingSettingsService.SettingsChanged += OnIntegrationSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged += OnIntegrationSettingsChanged;
        AuthenticationSettingsService.AuthenticationChanged += OnIntegrationSettingsChanged;
        OperatingModeSettingsService.SettingsChanged += OnIntegrationSettingsChanged;
        DeploymentProfileSettingsService.SettingsChanged += OnIntegrationSettingsChanged;
        AlarmEventService.AlarmEventsChanged += OnAlarmEventsChanged;
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        CameraSourceFactory.ActiveSourceChanged -= OnCameraSourceChanged;
        LightingSettingsService.SettingsChanged -= OnIntegrationSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged -= OnIntegrationSettingsChanged;
        AuthenticationSettingsService.AuthenticationChanged -= OnIntegrationSettingsChanged;
        OperatingModeSettingsService.SettingsChanged -= OnIntegrationSettingsChanged;
        DeploymentProfileSettingsService.SettingsChanged -= OnIntegrationSettingsChanged;
        AlarmEventService.AlarmEventsChanged -= OnAlarmEventsChanged;
    }

    private void OnWorkflowStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);
    private void OnInspectionConfigurationChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);
    private void OnCameraSourceChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);
    private void OnIntegrationSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);
    private void OnAlarmEventsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);

    private void RefreshStatus()
    {
        var databaseConnected = File.Exists(AoiDatabase.DatabasePath);
        var vaultAvailable = Directory.Exists(AoiDatabase.ImageVaultPath);
        SetStatus(HomeDatabaseStatusBorder, HomeDatabaseStatusText, databaseConnected ? "Connected" : "Not Connected", databaseConnected ? StatusKind.Ok : StatusKind.Unavailable);
        SetStatus(HomeImageVaultStatusBorder, HomeImageVaultStatusText, vaultAvailable ? "Available" : "Not Available", vaultAvailable ? StatusKind.Ok : StatusKind.Unavailable);

        var engineStatus = InspectionModelConfigurationService.GetStatus();
        var engineStatusText = InspectionModelConfigurationService.GetStatusText();
        SetStatus(
            HomeEngineStatusBorder,
            HomeEngineStatusText,
            InspectionModelConfigurationService.ShortStatusLabel(engineStatusText),
            engineStatus switch
            {
                InspectionEngineStatus.MlModelReady => StatusKind.Ok,
                InspectionEngineStatus.LearnedVisualModelReady => StatusKind.Simulated,
                InspectionEngineStatus.MlRuntimeError or
                    InspectionEngineStatus.MlInvalidLabelMap or
                    InspectionEngineStatus.MlUnsupportedOutputFormat => StatusKind.Ng,
                _ => StatusKind.Warning,
            });
        HomeEngineStatusText.ToolTip = engineStatusText;

        var cameraStatus = CameraSourceFactory.ActiveSource.ConnectionStatus;
        SetStatus(
            HomeCameraStatusBorder,
            HomeCameraStatusText,
            cameraStatus switch
            {
                CameraSourceStatus.Ready => "Connected",
                CameraSourceStatus.Simulated => "Simulated",
                CameraSourceStatus.Error => "Error",
                _ => "Not Connected",
            },
            cameraStatus switch
            {
                CameraSourceStatus.Ready => StatusKind.Ok,
                CameraSourceStatus.Simulated => StatusKind.Simulated,
                CameraSourceStatus.Error => StatusKind.Ng,
                _ => StatusKind.Unavailable,
            });

        SetIntegrationStatus(HomeLightingStatusBorder, HomeLightingStatusText, IntegrationBoundaryRegistry.LightingController);
        SetIntegrationStatus(HomeRobotStatusBorder, HomeRobotStatusText, IntegrationBoundaryRegistry.RobotController);
        SetStatus(
            HomeMesStatusBorder,
            HomeMesStatusText,
            ToStatusDisplay(CombineStatuses(
                IntegrationBoundaryRegistry.MesClient.Status,
                IntegrationBoundaryRegistry.TraceabilityUploader.Status)),
            ToStatusKind(CombineStatuses(
                IntegrationBoundaryRegistry.MesClient.Status,
                IntegrationBoundaryRegistry.TraceabilityUploader.Status)));
        HomeMesStatusText.ToolTip = $"{IntegrationBoundaryRegistry.MesClient.StatusMessage} {IntegrationBoundaryRegistry.TraceabilityUploader.StatusMessage}";
        SetIntegrationStatus(HomeEStopStatusBorder, HomeEStopStatusText, IntegrationBoundaryRegistry.EmergencyStopMonitor);

        var state = WorkflowState.Instance;
        HomeSampleText.Text = string.IsNullOrWhiteSpace(state.SampleImagePath) ? "none" : Path.GetFileName(state.SampleImagePath);
        HomeSampleText.ToolTip = string.IsNullOrWhiteSpace(state.SampleImagePath) ? "No sample image selected." : state.SampleImagePath;
        HomeGoldenText.Text = string.IsNullOrWhiteSpace(state.GoldenImagePath) ? "none" : Path.GetFileName(state.GoldenImagePath);
        HomeGoldenText.ToolTip = string.IsNullOrWhiteSpace(state.GoldenImagePath) ? "No golden reference selected." : state.GoldenImagePath;
        HomeRecipeText.Text = string.IsNullOrWhiteSpace(state.LastAnalysis?.RecipeName)
            ? state.ModelVersion
            : $"{state.LastAnalysis.RecipeName} / {state.LastAnalysis.RecipeRevision}";
        HomeRecipeText.ToolTip = HomeRecipeText.Text;

        if (state.LastAnalysis is null)
        {
            HomeScoreText.Text = "--";
            HomeVerdictText.Text = "REVIEW";
            SetVerdict(StatusKind.Warning);
        }
        else
        {
            HomeScoreText.Text = $"{state.LastAnalysis.DifferenceScore:F1}%";
            HomeScoreText.ToolTip = $"Difference score: {state.LastAnalysis.DifferenceScore:F1}%.";
            HomeVerdictText.Text = state.LastAnalysis.Verdict;
            if (state.LastAnalysis.Verdict.Equals("OK", StringComparison.OrdinalIgnoreCase))
                SetVerdict(StatusKind.Ok);
            else if (state.LastAnalysis.Verdict.Equals("NG", StringComparison.OrdinalIgnoreCase))
                SetVerdict(StatusKind.Ng);
            else
                SetVerdict(StatusKind.Warning);
        }

    }

    private static void SetIntegrationStatus(Border border, TextBlock textBlock, IIntegrationEndpoint endpoint)
    {
        SetStatus(border, textBlock, ToStatusDisplay(endpoint.Status), ToStatusKind(endpoint.Status));
        textBlock.ToolTip = $"{endpoint.Name}: {endpoint.StatusMessage}";
    }

    private void SetVerdict(StatusKind kind)
    {
        // Swap the shared verdict-banner style; the shared soft brushes color the text.
        // Raw hex assignments here would escape the tokenized palette the WCAG contract
        // test covers.
        HomeVerdictBorder.Style = (Style)FindResource(kind switch
        {
            StatusKind.Ok => "HmiVerdictBannerOk",
            StatusKind.Ng => "HmiVerdictBannerNg",
            _ => "HmiVerdictBannerWarn",
        });
        HomeVerdictText.Foreground = (System.Windows.Media.Brush)FindResource(SoftBrushKey(kind));
    }

    private static void SetStatus(Border border, TextBlock textBlock, string text, StatusKind kind)
    {
        textBlock.Text = text;
        textBlock.ToolTip = text;
        textBlock.Foreground = (System.Windows.Media.Brush)border.FindResource(SoftBrushKey(kind));
        border.Style = (Style)border.FindResource(kind switch
        {
            StatusKind.Ok => "HmiAdaptiveStatusOk",
            StatusKind.Ng => "HmiAdaptiveStatusNg",
            StatusKind.Warning => "HmiAdaptiveStatusWarning",
            StatusKind.Simulated => "HmiAdaptiveStatusSimulated",
            _ => "HmiAdaptiveStatusUnavailable",
        });
    }

    private static string SoftBrushKey(StatusKind kind) => kind switch
    {
        StatusKind.Ok => "HmiOkSoftBrush",
        StatusKind.Ng => "HmiNgSoftBrush",
        StatusKind.Warning => "HmiWarnSoftBrush",
        StatusKind.Simulated => "HmiSimulatedSoftBrush",
        _ => "HmiInfoSoftBrush",
    };

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
        IntegrationConnectionStatus.Ready => "Ready",
        IntegrationConnectionStatus.Simulated => "Simulated",
        IntegrationConnectionStatus.Error => "Error",
        _ => "Not Connected",
    };

    private static StatusKind ToStatusKind(IntegrationConnectionStatus status) => status switch
    {
        IntegrationConnectionStatus.Ready => StatusKind.Ok,
        IntegrationConnectionStatus.Simulated => StatusKind.Simulated,
        IntegrationConnectionStatus.Error => StatusKind.Ng,
        _ => StatusKind.Unavailable,
    };


    private enum StatusKind
    {
        Ok,
        Ng,
        Warning,
        Simulated,
        Unavailable,
    }
}
