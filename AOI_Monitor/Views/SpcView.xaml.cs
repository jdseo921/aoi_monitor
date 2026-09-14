using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class SpcView : UserControl, IAsyncNavigationPage
{
    public SpcView()
    {
        InitializeComponent();
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var healthRows = AoiDatabase.GetDatabaseHealthRows();
            var inspections = AoiDatabase.GetInspectionHistory(new LogFilter());
            var images = AoiDatabase.GetImportedImages();
            var ok = inspections.Count(row => string.Equals(row.Verdict, "OK", StringComparison.OrdinalIgnoreCase));
            var ng = inspections.Count(row => string.Equals(row.Verdict, "NG", StringComparison.OrdinalIgnoreCase));
            var review = inspections.Count(row => string.Equals(row.Verdict, "REVIEW", StringComparison.OrdinalIgnoreCase));
            var brokenLinks = images.Count(image => string.IsNullOrWhiteSpace(image.VaultPath) || !File.Exists(image.VaultPath));
            var yield = inspections.Count == 0
                ? "--"
                : (ok / (double)inspections.Count).ToString("P1", System.Globalization.CultureInfo.InvariantCulture);
            return new SpcSnapshot(healthRows, inspections.Count, ok, ng, review, brokenLinks, yield);
        }, cancellationToken);

        DbHealthGrid.ItemsSource = snapshot.HealthRows;

        VerdictBreakdownText.Text = $"{snapshot.Ok:N0} / {snapshot.Ng:N0} / {snapshot.Review:N0}";
        YieldText.Text = snapshot.Yield;
        var hasWarning = snapshot.BrokenLinks > 0;
        BrokenImageLinksText.Text = snapshot.BrokenLinks.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        BrokenImageLinksText.Foreground = (Brush)FindResource(hasWarning ? "HmiNgSoftBrush" : "HmiTextBodyBrush");

        DatabaseWarningText.Text = hasWarning
            ? $"SQLite warning: {snapshot.BrokenLinks:N0} imported image vault link(s) are missing. Run Verify Paths in Log & Export."
            : "SQLite summary uses local database counts. Trend chart is prototype data, not live factory SPC.";
        // Evidence banner is state-driven: neutral info surface by default, NG surface
        // tokens only on a real warning (style swap, never raw hex brushes).
        DatabaseWarningBorder.Style = (Style)FindResource(hasWarning ? "HmiVerdictBannerNg" : "HmiVerdictBanner");
        DatabaseWarningText.Foreground = (Brush)FindResource(hasWarning ? "HmiNgSoftBrush" : "HmiInfoSoftBrush");
    }

    public void RefreshFromState() => _ = RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            DatabaseWarningText.Text = $"SQLite summary could not be loaded: {ex.Message}";
            DatabaseWarningBorder.Style = (Style)FindResource("HmiVerdictBannerNg");
            DatabaseWarningText.Foreground = (Brush)FindResource("HmiNgSoftBrush");
        }
    }

    public void CancelWork()
    {
    }

    private sealed record SpcSnapshot(
        IReadOnlyList<DbHealthRow> HealthRows,
        int InspectionCount,
        int Ok,
        int Ng,
        int Review,
        int BrokenLinks,
        string Yield);
}
