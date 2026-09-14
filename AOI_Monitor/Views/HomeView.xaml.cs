using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        InspectionModelConfigurationService.ConfigurationChanged += OnStatusSourceChanged;
        CameraSourceFactory.ActiveSourceChanged += OnStatusSourceChanged;
        LightingSettingsService.SettingsChanged += OnStatusSourceChanged;
        MesIntegrationSettingsService.SettingsChanged += OnStatusSourceChanged;
        AuthenticationSettingsService.AuthenticationChanged += OnStatusSourceChanged;
        OperatingModeSettingsService.SettingsChanged += OnStatusSourceChanged;
        DeploymentProfileSettingsService.SettingsChanged += OnStatusSourceChanged;
        AlarmEventService.AlarmEventsChanged += OnStatusSourceChanged;
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        InspectionModelConfigurationService.ConfigurationChanged -= OnStatusSourceChanged;
        CameraSourceFactory.ActiveSourceChanged -= OnStatusSourceChanged;
        LightingSettingsService.SettingsChanged -= OnStatusSourceChanged;
        MesIntegrationSettingsService.SettingsChanged -= OnStatusSourceChanged;
        AuthenticationSettingsService.AuthenticationChanged -= OnStatusSourceChanged;
        OperatingModeSettingsService.SettingsChanged -= OnStatusSourceChanged;
        DeploymentProfileSettingsService.SettingsChanged -= OnStatusSourceChanged;
        AlarmEventService.AlarmEventsChanged -= OnStatusSourceChanged;
    }

    private void OnStatusSourceChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshStatus);

    private void RefreshStatus()
    {
        // Exception-based status: the engine truth chip is always visible; every other
        // chip appears only when its state differs from the Stage-1-expected baseline
        // (DB/Vault present, no hardware connected). Collapsed states are rolled up
        // into HomeStatusSummaryText so nothing is silently omitted.
        var summaryHealthy = new List<string>();
        var summaryAbsent = new List<string>();

        var databaseConnected = File.Exists(AoiDatabase.DatabasePath);
        SetStatus(HomeDatabaseStatusBorder, HomeDatabaseStatusText,
            databaseConnected ? "Connected" : "Not Connected",
            databaseConnected ? StatusKind.Ok : StatusKind.Ng,
            expected: StatusKind.Ok);
        if (databaseConnected)
            summaryHealthy.Add("DB connected");

        var vaultAvailable = Directory.Exists(AoiDatabase.ImageVaultPath);
        SetStatus(HomeImageVaultStatusBorder, HomeImageVaultStatusText,
            vaultAvailable ? "Available" : "Not Available",
            vaultAvailable ? StatusKind.Ok : StatusKind.Ng,
            expected: StatusKind.Ok);
        if (vaultAvailable)
            summaryHealthy.Add("vault available");

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
            },
            expected: null);
        HomeEngineStatusText.ToolTip = engineStatusText;

        var cameraStatus = CameraSourceFactory.ActiveSource.ConnectionStatus;
        var cameraKind = cameraStatus switch
        {
            CameraSourceStatus.Ready => StatusKind.Ok,
            CameraSourceStatus.Simulated => StatusKind.Simulated,
            CameraSourceStatus.Error => StatusKind.Ng,
            _ => StatusKind.Unavailable,
        };
        SetStatus(HomeCameraStatusBorder, HomeCameraStatusText,
            cameraStatus switch
            {
                CameraSourceStatus.Ready => "Connected",
                CameraSourceStatus.Simulated => "Simulated",
                CameraSourceStatus.Error => "Error",
                _ => "Not Connected",
            },
            cameraKind,
            expected: StatusKind.Unavailable);
        if (cameraKind == StatusKind.Unavailable)
            summaryAbsent.Add("camera");

        if (SetIntegrationStatus(HomeLightingStatusBorder, HomeLightingStatusText, IntegrationBoundaryRegistry.LightingController))
            summaryAbsent.Add("lighting");
        if (SetIntegrationStatus(HomeRobotStatusBorder, HomeRobotStatusText, IntegrationBoundaryRegistry.RobotController))
            summaryAbsent.Add("robot");

        var mesCombined = CombineStatuses(
            IntegrationBoundaryRegistry.MesClient.Status,
            IntegrationBoundaryRegistry.TraceabilityUploader.Status);
        SetStatus(HomeMesStatusBorder, HomeMesStatusText,
            ToStatusDisplay(mesCombined), ToStatusKind(mesCombined),
            expected: StatusKind.Unavailable);
        HomeMesStatusText.ToolTip = $"{IntegrationBoundaryRegistry.MesClient.StatusMessage} {IntegrationBoundaryRegistry.TraceabilityUploader.StatusMessage}";
        if (ToStatusKind(mesCombined) == StatusKind.Unavailable)
            summaryAbsent.Add("MES");

        if (SetIntegrationStatus(HomeEStopStatusBorder, HomeEStopStatusText, IntegrationBoundaryRegistry.EmergencyStopMonitor))
            summaryAbsent.Add("E-Stop");

        var parts = new List<string>();
        if (summaryHealthy.Count > 0)
            parts.Add(string.Join(", ", summaryHealthy));
        if (summaryAbsent.Count > 0)
            parts.Add($"{string.Join(", ", summaryAbsent)} not connected (Stage 1 image-only)");
        HomeStatusSummaryText.Text = parts.Count > 0
            ? $"{string.Join(" · ", parts)} — details: Hardware Readiness."
            : "All monitored states are shown as chips above.";
    }

    /// <summary>Returns true when the endpoint sits in the expected not-connected baseline (collapsed into the summary).</summary>
    private static bool SetIntegrationStatus(Border border, TextBlock textBlock, IIntegrationEndpoint endpoint)
    {
        var kind = ToStatusKind(endpoint.Status);
        SetStatus(border, textBlock, ToStatusDisplay(endpoint.Status), kind, expected: StatusKind.Unavailable);
        textBlock.ToolTip = $"{endpoint.Name}: {endpoint.StatusMessage}";
        return kind == StatusKind.Unavailable;
    }

    private static void SetStatus(Border border, TextBlock textBlock, string text, StatusKind kind, StatusKind? expected)
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
        border.Visibility = expected is null || kind != expected ? Visibility.Visible : Visibility.Collapsed;
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
