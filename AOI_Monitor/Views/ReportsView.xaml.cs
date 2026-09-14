using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

/// <summary>
/// Export &amp; Trace window: operational log grids, verified exports, and the MES /
/// central-sync queues. The readiness/QA evidence tabs (pilot issues, stage gates,
/// dashboards, checklists, acceptance) live in <see cref="ReadinessQaView"/> since the
/// 2026-09-14 split; that view reuses this class's internal row types and pure-static
/// evidence builders instead of duplicating them.
/// </summary>
public partial class ReportsView : UserControl, IAsyncNavigationPage, IDisposable
{
    internal static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly ObservableCollection<InspectionLogRow> _inspectionRows = new();
    private readonly ObservableCollection<ReviewLogRow> _reviewRows = new();
    private readonly ObservableCollection<ExportHistoryRow> _exportRows = new();
    private readonly ObservableCollection<AuditLogRow> _auditRows = new();
    private readonly ObservableCollection<MesSpoolQueueRow> _mesSpoolRows = new();
    private readonly ObservableCollection<CentralSyncQueueRow> _centralSyncRows = new();
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _refreshCts;

    public ReportsView()
    {
        InitializeComponent();
        InspectionGrid.ItemsSource = _inspectionRows;
        ReviewGrid.ItemsSource = _reviewRows;
        ExportGrid.ItemsSource = _exportRows;
        AuditGrid.ItemsSource = _auditRows;
        MesSpoolGrid.ItemsSource = _mesSpoolRows;
        CentralSyncGrid.ItemsSource = _centralSyncRows;
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        StatusText.Text = "Ready. Select Refresh or open this page to load logs.";
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await LoadLogsAsync(_refreshCts.Token);
        await RefreshRetentionWarningAsync(_refreshCts.Token);
    }

    public void RefreshFromState() => _ = RefreshAsync(CancellationToken.None);

    internal static string DescribeRetentionPolicy()
    {
        var settings = LogRetentionSettingsService.LoadSettings();
        return settings.Enabled
            ? $"Archive-then-purge: logs older than {settings.RetentionDays} day(s) are copied to the recoverable LogArchive and removed from the live tables at startup."
            : "Automatic purge disabled: logs are retained indefinitely in the live tables.";
    }

    private async Task RefreshRetentionWarningAsync(CancellationToken cancellationToken)
    {
        LogRetentionSettings settings;
        try
        {
            settings = LogRetentionSettingsService.LoadSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Retention warning skipped (settings load): {ex.Message}");
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!settings.Enabled || !settings.WarningEnabled)
        {
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        int nearing;
        try
        {
            nearing = await Task.Run(
                () => AoiDatabase.CountRowsNearingPurge(settings.RetentionDays, settings.WarningLeadDays),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Retention warning skipped (count): {ex.Message}");
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        if (nearing > 0)
        {
            RetentionWarningText.Text =
                $"Retention notice: {nearing} log row(s) will be archived and purged within {settings.WarningLeadDays} day(s) (retention {settings.RetentionDays}d). Configure in System Settings.";
            RetentionWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            RetentionWarningText.Visibility = Visibility.Collapsed;
        }
    }

    public void CancelWork()
    {
        _refreshCts?.Cancel();
        _workCts?.Cancel();
        StatusText.Text = "Cancel requested. Finishing current operation...";
    }

    private async void OnApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Apply report filters",
            "Log & Export",
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
        ResultFilterCombo.SelectedIndex = 0;
        RoleFilterCombo.SelectedIndex = 0;
        ActionTypeFilterText.Text = string.Empty;
        _ = RefreshAsync(CancellationToken.None);
    }

    private async Task LoadLogsAsync(CancellationToken cancellationToken)
    {
        var filter = BuildFilter();
        var mesQueueStatus = ComboBoxTokens.SelectedToken(MesQueueStatusFilter, "All");
        StatusText.Text = "Loading logs and queue records...";

        var snapshot = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspections = AoiDatabase.GetInspectionHistory(filter).Select(InspectionLogRow.FromRecord).ToArray();
            var reviews = AoiDatabase.GetReviewEvents(filter).Select(ReviewLogRow.FromRecord).ToArray();
            var exports = AoiDatabase.GetExportHistory(filter)
                .Select(record => ExportHistoryRow.FromRecord(record, AoiDatabase.GetLatestExportVerification(record.Id)))
                .ToArray();
            var audits = AoiDatabase.GetAuditEvents(filter).Select(AuditLogRow.FromRecord).ToArray();
            var mesSpool = ApplyMesQueueFilter(AoiDatabase.GetMesSpoolQueue().Select(MesSpoolQueueRow.FromRecord), mesQueueStatus).ToArray();
            var centralSync = AoiDatabase.GetCentralSyncQueue().Select(CentralSyncQueueRow.FromRecord).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new LogLoadSnapshot(inspections, reviews, exports, audits, mesSpool, centralSync);
        }, cancellationToken);

        ReplaceRows(_inspectionRows, snapshot.Inspections);
        ReplaceRows(_reviewRows, snapshot.Reviews);
        ReplaceRows(_exportRows, snapshot.Exports);
        ReplaceRows(_auditRows, snapshot.Audits);
        ReplaceRows(_mesSpoolRows, snapshot.MesSpool);
        ReplaceRows(_centralSyncRows, snapshot.CentralSync);

        LogSummaryText.Text = $"{snapshot.Inspections.Length} inspections / {snapshot.Reviews.Length} review events / {snapshot.Exports.Length} exports / {snapshot.Audits.Length} audit rows / {snapshot.MesSpool.Length} MES spool / {snapshot.CentralSync.Length} central sync";
        StatusText.Text = "Loaded real SQLite log records.";
    }

    private static IEnumerable<MesSpoolQueueRow> ApplyMesQueueFilter(IEnumerable<MesSpoolQueueRow> rows, string selected)
    {
        return string.Equals(selected, "All", StringComparison.OrdinalIgnoreCase)
            ? rows
            : rows.Where(row => row.Status.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private LogFilter BuildFilter()
    {
        return new LogFilter
        {
            FromDate = FromDatePicker.SelectedDate,
            ToDate = ToDatePicker.SelectedDate,
            BoardProgram = NullIfBlank(BoardFilterText.Text),
            OperatorId = NullIfBlank(OperatorFilterText.Text),
            Result = ComboBoxTokens.Token(ResultFilterCombo.SelectedItem as ComboBoxItem),
            UserRole = ComboBoxTokens.Token(RoleFilterCombo.SelectedItem as ComboBoxItem),
            ActionCategory = NullIfBlank(ActionTypeFilterText.Text),
        };
    }
}
