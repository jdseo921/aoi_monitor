using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;

namespace AOI_Monitor.Views;

public partial class GuideView : UserControl
{
    private static readonly object[] Steps =
    {
        new { Num = "01", Priority = "MANDATORY", Step = "Confirm the local review console is ready before opening inspection review work." },
        new { Num = "02", Priority = "MANDATORY", Step = "Confirm active recipe, model version, lot ID, and image-vault link state." },
        new { Num = "03", Priority = "CHECK",     Step = "Review Possible Escape cases before false calls. Default policy is AI OK / GT NG first." },
        new { Num = "04", Priority = "CHECK",     Step = "Open the current sample, compare the defect overlay against ground-truth overlay, then verify RefDes and FOV." },
        new { Num = "05", Priority = "CHECK",     Step = "Check closest matching historical defect images before disposition." },
        new { Num = "06", Priority = "CHECK",     Step = "Record final disposition: Confirm NG, False Call, Possible Escape, Candidate Queue, or Hold." },
        new { Num = "07", Priority = "CHECK",     Step = "Review Log & Export database health before exporting customer validation evidence." },
        new { Num = "08", Priority = "CHECK",     Step = "Lock recipe and export the audit trail after review completion." },
    };

    public GuideView()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = Steps;
    }

    private void OnOpenSettingsClick(object sender, System.Windows.RoutedEventArgs e) => Navigate("settings");
    private void OnOpenCalibrationClick(object sender, System.Windows.RoutedEventArgs e) => Navigate("calibration");
    private void OnOpenInstallClick(object sender, System.Windows.RoutedEventArgs e) => Navigate("install");

    // "Run Setup Wizard Again" and "Export Diagnostics Report" live only in their
    // authoritative System Settings > Evidence diagnostics cluster (RunSetupWizardBtn /
    // ExportDiagnosticsBtn); the Guide keeps navigation shortcuts only.

    private void Navigate(string key)
    {
        var role = WorkflowState.Instance.CurrentRole;
        if (!RoleAuthorization.CanAccessPage(role, key))
        {
            var message = RoleAuthorization.DeniedMessage(role, $"Opening {key}");
            WorkflowState.Instance.AddEvent("ACCESS_DENIED", message);
            MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (System.Windows.Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = key;
    }
}
