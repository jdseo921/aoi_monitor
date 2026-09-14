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
            databaseConnected
                ? UiPreferencesService.Text("Connected", "연결됨")
                : UiPreferencesService.Text("Not Connected", "연결 안 됨"),
            databaseConnected ? StatusKind.Ok : StatusKind.Ng,
            expected: StatusKind.Ok);
        if (databaseConnected)
            summaryHealthy.Add(UiPreferencesService.Text("DB connected", "DB 연결됨"));

        var vaultAvailable = Directory.Exists(AoiDatabase.ImageVaultPath);
        SetStatus(HomeImageVaultStatusBorder, HomeImageVaultStatusText,
            vaultAvailable
                ? UiPreferencesService.Text("Available", "사용 가능")
                : UiPreferencesService.Text("Not Available", "사용 불가"),
            vaultAvailable ? StatusKind.Ok : StatusKind.Ng,
            expected: StatusKind.Ok);
        if (vaultAvailable)
            summaryHealthy.Add(UiPreferencesService.Text("vault available", "보관소 사용 가능"));

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
                CameraSourceStatus.Ready => UiPreferencesService.Text("Connected", "연결됨"),
                CameraSourceStatus.Simulated => UiPreferencesService.Text("Simulated", "시뮬레이션"),
                CameraSourceStatus.Error => UiPreferencesService.Text("Error", "오류"),
                _ => UiPreferencesService.Text("Not Connected", "연결 안 됨"),
            },
            cameraKind,
            expected: StatusKind.Unavailable);
        if (cameraKind == StatusKind.Unavailable)
            summaryAbsent.Add(UiPreferencesService.Text("camera", "카메라"));

        if (SetIntegrationStatus(HomeLightingStatusBorder, HomeLightingStatusText, IntegrationBoundaryRegistry.LightingController))
            summaryAbsent.Add(UiPreferencesService.Text("lighting", "조명"));
        if (SetIntegrationStatus(HomeRobotStatusBorder, HomeRobotStatusText, IntegrationBoundaryRegistry.RobotController))
            summaryAbsent.Add(UiPreferencesService.Text("robot", "로봇"));

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
            summaryAbsent.Add(UiPreferencesService.Text("E-Stop", "비상정지"));

        var parts = new List<string>();
        if (summaryHealthy.Count > 0)
            parts.Add(string.Join(", ", summaryHealthy));
        if (summaryAbsent.Count > 0)
            parts.Add(string.Join(", ", summaryAbsent) + UiPreferencesService.Text(
                " not connected (Stage 1 image-only)", " 미연결 (1단계 이미지 전용)"));
        HomeStatusSummaryText.Text = parts.Count > 0
            ? string.Join(" · ", parts) + UiPreferencesService.Text(
                " — details: Hardware Readiness.", " — 상세: 하드웨어 준비성.")
            : UiPreferencesService.Text(
                "All monitored states are shown as chips above.",
                "모니터링되는 모든 상태가 위 칩으로 표시됩니다.");
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
        IntegrationConnectionStatus.Ready => UiPreferencesService.Text("Ready", "준비됨"),
        IntegrationConnectionStatus.Simulated => UiPreferencesService.Text("Simulated", "시뮬레이션"),
        IntegrationConnectionStatus.Error => UiPreferencesService.Text("Error", "오류"),
        _ => UiPreferencesService.Text("Not Connected", "연결 안 됨"),
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
