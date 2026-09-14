using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class PilotWizardView : UserControl, IAsyncNavigationPage
{
    private readonly ObservableCollection<PilotStepRow> _steps = new();
    private CustomerPilotSnapshot? _snapshot;
    private CancellationTokenSource? _refreshCancellation;

    public PilotWizardView()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = _steps;
        PopulateProfiles();
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _refreshCancellation.Token;
        if (_snapshot is not null)
        {
            var snapshot = await Task.Run(() => CustomerPilotWizardService.Load(_snapshot.Session.Id), token);
            token.ThrowIfCancellationRequested();
            LoadSnapshot(snapshot);
        }
        else
        {
            await ResumeLatestAsync(showMessage: false, token);
        }
    }

    public void RefreshFromState() => _ = RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh/navigation.
        }
        catch (Exception ex)
        {
            SetStatus($"Pilot wizard refresh failed: {ex.Message}", StatusSeverity.Error);
        }
    }

    public void CancelWork() => _refreshCancellation?.Cancel();

    private void PopulateProfiles()
    {
        ProfileCombo.Items.Clear();
        foreach (var profile in new[] { DeploymentProfile.Stage1ImageValidation, DeploymentProfile.Stage2CameraPilot })
        {
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        ProfileCombo.SelectedIndex = 0;
    }

    private DeploymentProfile SelectedProfile()
        => (ProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : DeploymentProfile.Stage1ImageValidation;

    private void OnStartNewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSnapshot(CustomerPilotWizardService.StartNew(SelectedProfile(), WorkflowState.Instance.OperatorWithRole));
            SetStatus("Started a new customer pilot session.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Could not start pilot", ex);
        }
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
        => await ResumeLatestAsync(showMessage: true, CancellationToken.None);

    private async Task ResumeLatestAsync(bool showMessage, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latest = AoiDatabase.GetLatestIncompleteCustomerPilotSession();
                return latest is null
                    ? null
                    : new ResumePilotResult(latest.SessionId, CustomerPilotWizardService.Load(latest.Id));
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null)
            {
                if (showMessage)
                    SetStatus("No incomplete customer pilot session exists.", StatusSeverity.Warning);
                return;
            }

            LoadSnapshot(result.Snapshot);
            SetStatus($"Resumed pilot session {result.SessionId}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Could not resume pilot", ex);
        }
    }

    private void OnRunStepClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PilotStepRow row)
            return;

        switch (row.StepKey)
        {
            case CustomerPilotStepKind.ConfirmDeploymentProfile:
                OnConfirmProfileClick(sender, e);
                break;
            case CustomerPilotStepKind.RunSystemDiagnostics:
                OnDiagnosticsClick(sender, e);
                break;
            case CustomerPilotStepKind.SelectCustomerDataset:
                OnSelectDatasetClick(sender, e);
                break;
            case CustomerPilotStepKind.RunDatasetPreflight:
                OnPreflightClick(sender, e);
                break;
            case CustomerPilotStepKind.RunBatchValidation:
                OnBatchClick(sender, e);
                break;
            case CustomerPilotStepKind.RunFalseCallReduction:
                OnFalseCallClick(sender, e);
                break;
            case CustomerPilotStepKind.RunModelAcceptance:
                OnModelClick(sender, e);
                break;
            case CustomerPilotStepKind.ExportCustomerValidationPackage:
                OnCustomerPackageClick(sender, e);
                break;
            case CustomerPilotStepKind.RunStage2Acceptance:
                OnStage2Click(sender, e);
                break;
            case CustomerPilotStepKind.ExportReadinessAndChecklist:
                OnReadinessClick(sender, e);
                break;
        }
    }

    private void OnConfirmProfileClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.ConfirmDeploymentProfile(RequireSession().Session.Id, SelectedProfile(), WorkflowState.Instance.OperatorWithRole), "Deployment profile confirmed.");

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.RunSystemDiagnostics(RequireSession().Session.Id), "Diagnostics completed.");

    private void OnSelectDatasetClick(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = "Select customer dataset folder",
            Multiselect = false,
        };
        if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
            return;

        var fileDialog = new OpenFileDialog
        {
            Title = "Select customer validation manifest CSV",
            Filter = "CSV manifest (*.csv)|*.csv|All files (*.*)|*.*",
            InitialDirectory = folderDialog.FolderName,
        };
        if (fileDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(fileDialog.FileName))
            return;

        RunStep(() => CustomerPilotWizardService.RecordDatasetSelection(RequireSession().Session.Id, folderDialog.FolderName, fileDialog.FileName), "Dataset selection recorded.");
    }

    private void OnPreflightClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.RunDatasetPreflight(RequireSession().Session.Id), "Dataset preflight completed.");

    private void OnBatchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var latest = AoiDatabase.GetLatestBatchTestRun();
            var status = latest is null ? CustomerPilotStepStatus.Failed : CustomerPilotStepStatus.Passed;
            var evidence = latest is null ? string.Empty : $"SQLite:BatchTestRuns/{latest.Id}";
            var message = latest is null ? "No batch validation run has been recorded." : $"Latest batch validation run {latest.Id}; rows={latest.TotalImages}; falseCall={latest.FalseCallRate:P1}.";
            LoadSnapshot(CustomerPilotWizardService.RecordBatchValidationEvidence(RequireSession().Session.Id, evidence, status, message));
            SetStatus("Batch validation evidence evaluated.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Batch validation blocked", ex);
        }
    }

    private void OnFalseCallClick(object sender, RoutedEventArgs e)
    {
        var latest = AoiDatabase.GetLatestFalseCallReductionRun();
        var status = latest?.Recommendation.Point is null ? CustomerPilotStepStatus.Failed : CustomerPilotStepStatus.Passed;
        var evidence = latest is null ? string.Empty : $"SQLite:FalseCallReductionRuns/{latest.Id}";
        var message = latest is null ? "No false-call reduction run has been recorded." : $"False-call reduction run {latest.Id}; recommendation={latest.Recommendation.Status}.";
        RunStep(() => CustomerPilotWizardService.RecordFalseCallReductionEvidence(RequireSession().Session.Id, evidence, status, message), "False-call evidence evaluated.");
    }

    private void OnModelClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateModelAcceptance(RequireSession().Session.Id), "Model acceptance evidence evaluated.");

    private void OnCustomerPackageClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateCustomerValidationPackage(RequireSession().Session.Id), "Customer package evidence evaluated.");

    private void OnStage2Click(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateStage2Acceptance(RequireSession().Session.Id), "Stage 2 acceptance evidence evaluated.");

    private void OnReadinessClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.ExportReadinessAndChecklist(RequireSession().Session.Id), "Readiness package and checklist exported.");

    private void OnWaiveSelectedClick(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItem is not PilotStepRow row)
        {
            SetStatus("Select a step to waive.", StatusSeverity.Warning);
            return;
        }

        if (WorkflowState.Instance.CurrentRole < UserRole.Engineer)
        {
            MessageBox.Show(RoleAuthorization.DeniedMessage(WorkflowState.Instance.CurrentRole, "Recording a customer pilot waiver"), "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = Microsoft.VisualBasic.Interaction.InputBox("Enter waiver reason. This will be audited.", "Customer Pilot Waiver", "Engineer-reviewed exception for customer pilot evidence.");
        if (string.IsNullOrWhiteSpace(reason))
            return;

        RunStep(
            () => CustomerPilotWizardService.RecordWaiver(RequireSession().Session.Id, row.StepKey, reason, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole),
            "Waiver recorded.");
    }

    private void OnExportBundleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select pilot evidence bundle output folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        try
        {
            var result = CustomerPilotWizardService.ExportPilotEvidenceBundle(RequireSession().Session.Id, dialog.FolderName);
            RefreshFromState();
            SetStatus($"Pilot evidence bundle exported. JSON: {result.JsonPath}; HTML: {result.HtmlPath}; PDF: {result.PdfPath}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Pilot bundle export failed", ex);
        }
    }

    private void RunStep(Func<CustomerPilotSnapshot> action, string successMessage)
    {
        try
        {
            LoadSnapshot(action());
            SetStatus(successMessage);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Pilot step failed", ex);
        }
    }

    private CustomerPilotSnapshot RequireSession()
    {
        if (_snapshot is not null)
            return _snapshot;

        _snapshot = CustomerPilotWizardService.StartNew(SelectedProfile(), WorkflowState.Instance.OperatorWithRole);
        LoadSnapshot(_snapshot);
        return _snapshot;
    }

    private void LoadSnapshot(CustomerPilotSnapshot snapshot)
    {
        _snapshot = snapshot;
        var profileIndex = snapshot.Session.DeploymentProfile == DeploymentProfile.Stage2CameraPilot ? 1 : 0;
        if (ProfileCombo.SelectedIndex != profileIndex)
            ProfileCombo.SelectedIndex = profileIndex;
        SessionSummaryText.Text = $"Session {snapshot.Session.SessionId} / {snapshot.Session.DeploymentProfile} / {snapshot.Session.Status} / Dataset: {ShortPath(snapshot.Session.DatasetFolder)}";
        var orderedSteps = snapshot.Steps.OrderBy(item => item.StepOrder).ToArray();
        var currentStep = orderedSteps.FirstOrDefault(step => !step.Waived && step.Status != CustomerPilotStepStatus.Passed);
        var blockers = orderedSteps.Count(step => !step.Waived && step.Status == CustomerPilotStepStatus.Failed);
        var conditional = orderedSteps.Count(step => !step.Waived && step.Status == CustomerPilotStepStatus.Conditional);
        var waived = orderedSteps.Count(step => step.Waived);
        CurrentStepText.Text = currentStep is null ? "All defined steps complete" : $"{currentStep.StepOrder}. {CustomerPilotWizardService.DisplayName(currentStep.StepKey)}";
        BlockersText.Text = blockers > 0
            ? $"{blockers} failed gate(s) require action"
            : conditional > 0
                ? $"{conditional} conditional gate(s) need review"
                : "No failed gates";
        EvidenceRequiredText.Text = currentStep is null
            ? "Export readiness bundle and keep evidence with client package."
            : string.IsNullOrWhiteSpace(currentStep.EvidencePath)
                ? "Evidence not attached for the current step."
                : ShortPath(currentStep.EvidencePath);
        NextActionText.Text = blockers > 0
            ? "Resolve failed gates before client demo packaging."
            : currentStep is null
                ? "Export Bundle or Readiness Export."
                : $"Run step {currentStep.StepOrder} or attach required evidence.";
        WaivedStatusText.Text = waived == 0 ? "0 waived steps" : $"{waived} waived step(s) - audited exceptions";
        _steps.Clear();
        foreach (var step in orderedSteps)
            _steps.Add(PilotStepRow.FromRecord(step));
        SummaryTilesPanel.Visibility = Visibility.Visible;
        StepsEmptyStateText.Visibility = Visibility.Collapsed;
    }

    private static string ShortPath(string path)
        => string.IsNullOrWhiteSpace(path) ? "not selected" : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private void ShowError(string title, Exception ex)
    {
        SetStatus($"{title}: {ex.Message}", StatusSeverity.Error);
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetStatus(string message, StatusSeverity severity = StatusSeverity.Info)
    {
        StatusText.Text = message;
        var brushKey = severity switch
        {
            StatusSeverity.Error => "HmiNgSoftBrush",
            StatusSeverity.Warning => "HmiWarnSoftBrush",
            _ => "HmiTextLabelBrush",
        };
        if (TryFindResource(brushKey) is Brush brush)
            StatusText.Foreground = brush;
    }

    private enum StatusSeverity
    {
        Info,
        Warning,
        Error,
    }

    public sealed class PilotStepRow
    {
        public CustomerPilotStepKind StepKey { get; init; }
        public int StepOrder { get; init; }
        public string StepName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string EvidencePath { get; init; } = string.Empty;
        public string MessagesDisplay { get; init; } = string.Empty;
        public bool Waived { get; init; }
        public Brush StatusBrush { get; init; } = Brushes.Transparent;

        public string RunActionName => $"Run step {StepOrder}: {StepName}";

        public static PilotStepRow FromRecord(CustomerPilotStepRecord step)
            => new()
            {
                StepKey = step.StepKey,
                StepOrder = step.StepOrder,
                StepName = CustomerPilotWizardService.DisplayName(step.StepKey),
                Status = step.Status.ToString(),
                EvidencePath = step.EvidencePath,
                MessagesDisplay = string.Join(" | ", step.Messages),
                Waived = step.Waived,
                StatusBrush = step.Status switch
                {
                    CustomerPilotStepStatus.Passed => new SolidColorBrush(Color.FromRgb(20, 78, 45)),
                    CustomerPilotStepStatus.Conditional => new SolidColorBrush(Color.FromRgb(98, 72, 22)),
                    CustomerPilotStepStatus.Failed => new SolidColorBrush(Color.FromRgb(96, 36, 36)),
                    CustomerPilotStepStatus.Running => new SolidColorBrush(Color.FromRgb(36, 65, 96)),
                    CustomerPilotStepStatus.Skipped => new SolidColorBrush(Color.FromRgb(56, 58, 62)),
                    _ => new SolidColorBrush(Color.FromRgb(24, 29, 33)),
                },
            };
    }

    private sealed record ResumePilotResult(string SessionId, CustomerPilotSnapshot Snapshot);
}
