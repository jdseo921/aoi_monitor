using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using static AOI_Monitor.Views.ReportsView;

namespace AOI_Monitor.Views;

/// <summary>
/// Readiness &amp; QA window: stage gates, checklists, dashboards, and acceptance evidence.
/// Split out of the Export &amp; Trace window (2026-09-14) so the operational log/queue tabs
/// and the readiness/QA evidence tabs each fit a single tab row without wrapping.
/// Shares the display-row types and pure-static evidence builders with
/// <see cref="ReportsView"/> (imported via <c>using static</c>) instead of duplicating them.
/// </summary>
public partial class ReadinessQaView : UserControl, IAsyncNavigationPage, IDisposable
{
    private readonly ObservableCollection<InspectionLogRow> _inspectionRows = new();
    private readonly ObservableCollection<ReviewLogRow> _reviewRows = new();
    private readonly ObservableCollection<AuditLogRow> _auditRows = new();
    private readonly ObservableCollection<PilotIssueRow> _pilotIssueRows = new();
    private readonly ObservableCollection<FactoryReadinessRow> _factoryReadinessRows = new();
    private readonly ObservableCollection<Stage1ReadinessRow> _stage1ReadinessRows = new();
    private readonly ObservableCollection<StandardsTraceabilityMatrix> _standardsTraceabilityRows = new();
    private readonly ObservableCollection<CompletionMatrixRow> _completionMatrixRows = new();
    private readonly ObservableCollection<FactoryAcceptanceChecklistItem> _factoryAcceptanceRows = new();
    private readonly ObservableCollection<UiNavigationSoakEvent> _uiStabilityEvents = new();
    private readonly ObservableCollection<ManagementDashboardContributor> _managementDefectRows = new();
    private readonly ObservableCollection<ManagementDashboardContributor> _managementRoiRows = new();
    private readonly ObservableCollection<ManagementDashboardTrendPoint> _managementTrendRows = new();
    private readonly ObservableCollection<ManagementDashboardBreakdown> _managementBreakdownRows = new();
    private ManagementDashboardReport? _managementDashboardReport;
    private Stage1ReadinessReport? _latestStage1ReadinessReport;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _refreshCts;

    public ReadinessQaView()
    {
        InitializeComponent();
        PilotSourceInspectionGrid.ItemsSource = _inspectionRows;
        PilotIssuesGrid.ItemsSource = _pilotIssueRows;
        FactoryReadinessGrid.ItemsSource = _factoryReadinessRows;
        Stage1ReadinessGrid.ItemsSource = _stage1ReadinessRows;
        StandardsTraceabilityGrid.ItemsSource = _standardsTraceabilityRows;
        CompletionMatrixGrid.ItemsSource = _completionMatrixRows;
        FactoryAcceptanceGrid.ItemsSource = _factoryAcceptanceRows;
        UiStabilityEventsGrid.ItemsSource = _uiStabilityEvents;
        ManagementDefectGrid.ItemsSource = _managementDefectRows;
        ManagementRoiGrid.ItemsSource = _managementRoiRows;
        ManagementModelTrendGrid.ItemsSource = _managementTrendRows;
        ManagementLotModelGrid.ItemsSource = _managementBreakdownRows;
        PopulateFactoryAcceptanceProfiles();
        PopulateManagementProfiles();
        PopulatePilotIssueFilters();
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        StatusText.Text = "Ready. Select Refresh or open this page to load readiness evidence.";
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RefreshArchivePolicyText();
        await LoadReadinessAsync(_refreshCts.Token);
    }

    public void RefreshFromState() => _ = RefreshAsync(CancellationToken.None);

    /// <summary>
    /// Keeps the on-screen policy statement equal to the exported one and to the configured
    /// retention window; the readiness footer repeats it because customer packages embed the
    /// same policy line in the database health summary.
    /// </summary>
    private void RefreshArchivePolicyText()
    {
        try
        {
            ArchivePolicyText.Text = DescribeRetentionPolicy();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Archive policy text not refreshed: {ex.Message}");
            ArchivePolicyText.Text = "Auto-archive policy unavailable — open Settings > Basics > Data Retention to confirm the configured window.";
        }
    }

    public void CancelWork()
    {
        _refreshCts?.Cancel();
        _workCts?.Cancel();
        StatusText.Text = "Cancel requested. Finishing current operation...";
    }

    private void OnCancelWorkClick(object sender, RoutedEventArgs e)
    {
        CancelWork();
    }

    private async void OnApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Apply readiness filters",
            "Readiness & QA",
            RefreshAsync,
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message => StatusText.Text = message);
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        BoardFilterText.Text = string.Empty;
        OperatorFilterText.Text = string.Empty;
        _ = RefreshAsync(CancellationToken.None);
    }

    private LogFilter BuildFilter()
    {
        return new LogFilter
        {
            FromDate = FromDatePicker.SelectedDate,
            ToDate = ToDatePicker.SelectedDate,
            BoardProgram = NullIfBlank(BoardFilterText.Text),
            OperatorId = NullIfBlank(OperatorFilterText.Text),
        };
    }

    private void PopulateFactoryAcceptanceProfiles()
    {
        FactoryAcceptanceProfileCombo.Items.Clear();
        foreach (DeploymentProfile profile in Enum.GetValues<DeploymentProfile>())
        {
            FactoryAcceptanceProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        FactoryAcceptanceProfileCombo.SelectedIndex = Math.Max(0, (int)DeploymentProfileSettingsService.Load());
    }

    private DeploymentProfile SelectedFactoryAcceptanceProfile()
        => (FactoryAcceptanceProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : DeploymentProfile.Stage1ImageValidation;

    private void PopulateManagementProfiles()
    {
        ManagementDeploymentProfileCombo.Items.Clear();
        ManagementDeploymentProfileCombo.Items.Add(new ComboBoxItem { Content = "Active deployment profile", Tag = null });
        foreach (DeploymentProfile profile in Enum.GetValues<DeploymentProfile>())
        {
            ManagementDeploymentProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        ManagementDeploymentProfileCombo.SelectedIndex = 0;
    }

    private DeploymentProfile? SelectedManagementProfile()
        => (ManagementDeploymentProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : null;

    private void PopulatePilotIssueFilters()
    {
        PilotIssueCategoryFilterCombo.Items.Clear();
        PilotIssueCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (PilotIssueCategory category in Enum.GetValues<PilotIssueCategory>())
            PilotIssueCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = category.ToString(), Tag = category });
        PilotIssueCategoryFilterCombo.SelectedIndex = 0;

        PilotIssueStatusFilterCombo.Items.Clear();
        PilotIssueStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (PilotIssueStatus status in Enum.GetValues<PilotIssueStatus>())
            PilotIssueStatusFilterCombo.Items.Add(new ComboBoxItem { Content = status.ToString(), Tag = status });
        PilotIssueStatusFilterCombo.SelectedIndex = 0;
    }

    private PilotIssueFilter BuildPilotIssueFilter()
        => new()
        {
            Category = (PilotIssueCategoryFilterCombo?.SelectedItem as ComboBoxItem)?.Tag is PilotIssueCategory category ? category : null,
            Status = (PilotIssueStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag is PilotIssueStatus status ? status : null,
            Severity = ComboBoxTokens.SelectedToken(PilotIssueSeverityFilterCombo, "All") == "All"
                ? string.Empty
                : ComboBoxTokens.SelectedToken(PilotIssueSeverityFilterCombo, string.Empty),
            BoardModel = BoardFilterText.Text.Trim(),
            LotId = ManagementLotFilterText?.Text.Trim() ?? string.Empty,
        };

    private ManagementDashboardFilter BuildManagementDashboardFilter()
        => new()
        {
            FromDate = FromDatePicker.SelectedDate,
            ToDate = ToDatePicker.SelectedDate,
            BoardModel = BoardFilterText.Text.Trim(),
            LotId = ManagementLotFilterText.Text.Trim(),
            OperatorId = OperatorFilterText.Text.Trim(),
            ModelVersion = ManagementModelFilterText.Text.Trim(),
            DeploymentProfile = SelectedManagementProfile(),
        };

    private CancellationTokenSource BeginWork(string message)
    {
        UiPerformanceMonitorService.RecordSlowOperation(message, 0, $"Long operation started: {message}");
        _workCts = new CancellationTokenSource();
        WorkProgressBar.Value = 0;
        CancelWorkButton.IsEnabled = true;
        StatusText.Text = message;
        return _workCts;
    }

    private void EndWork()
    {
        _workCts?.Dispose();
        _workCts = null;
        CancelWorkButton.IsEnabled = false;
        WorkProgressBar.Value = 0;
    }

    private void UpdateProgress(WorkProgress progress)
    {
        WorkProgressBar.Value = progress.Total <= 0 ? 0 : Math.Min(100, progress.Completed * 100.0 / progress.Total);
        StatusText.Text = progress.Message;
    }

    private void HandleWorkError(string title, Exception ex, string category)
    {
        var message = ex is UnauthorizedAccessException
            ? $"{title}: export folder or database access was denied."
            : $"{title}: {ex.Message}";
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = ex,
            OperationName = title,
            CurrentPage = "Readiness & QA",
            IsFatal = false,
            IsUiThread = true,
        });
        StatusText.Text = message;
        WorkflowState.Instance.AddEvent(category, message, relatedEntityType: "CrashReport", relatedPath: report.ReportPath);
        MessageBox.Show($"{message}\n\n{report.OperatorMessage}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RefreshAfterExport(string message)
    {
        _ = RefreshAsync(CancellationToken.None);
        StatusText.Text = message;
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Dispose()
    {
        DisposeCancellation(ref _workCts);
        DisposeCancellation(ref _refreshCts);
        GC.SuppressFinalize(this);
    }
}
