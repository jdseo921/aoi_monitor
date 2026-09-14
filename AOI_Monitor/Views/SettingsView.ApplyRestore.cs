using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class SettingsView
{
    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        var existingConfig = InspectionModelConfigurationService.Load();
        var newConfig = BuildConfigurationFromUi();
        var existingUiPreferences = UiPreferencesService.Load();
        var newUiPreferences = BuildUiPreferencesFromUi();
        var existingCamera = CameraSourceSettingsService.Load();
        var newCamera = BuildCameraSourceSettingsFromUi();
        var existingLighting = LightingSettingsService.Load();
        var newLighting = BuildLightingSettingsFromUi();
        var existingMes = MesIntegrationSettingsService.Load();
        var newMes = BuildMesIntegrationSettingsFromUi();
        var existingCentralSync = CentralSyncSettingsService.Load();
        var newCentralSync = BuildCentralSyncSettingsFromUi();
        var existingDeploymentProfile = DeploymentProfileSettingsService.Load();
        var newDeploymentProfile = ComboToDeploymentProfile(DeploymentProfileCombo.SelectedIndex);
        var existingOperatingMode = OperatingModeSettingsService.Load();
        var newOperatingMode = ComboToOperatingMode(OperatingModeCombo.SelectedIndex);
        var newStorageRoot = string.IsNullOrWhiteSpace(StorageRootText.Text)
            ? AoiDatabase.DefaultStorageRoot
            : StorageRootText.Text.Trim();
        var storageRootChanged = !string.Equals(
            Path.GetFullPath(AoiDatabase.StorageRoot),
            Path.GetFullPath(newStorageRoot),
            StringComparison.OrdinalIgnoreCase);
        var modelConfigChanged =
            !string.Equals(existingConfig.ModelFilePath, newConfig.ModelFilePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.ModelVersion, newConfig.ModelVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.LabelMapPath, newConfig.LabelMapPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.SelectedEngineKey, newConfig.SelectedEngineKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.InputTensorName, newConfig.InputTensorName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.OutputTensorName, newConfig.OutputTensorName, StringComparison.OrdinalIgnoreCase) ||
            existingConfig.InputImageWidth != newConfig.InputImageWidth ||
            existingConfig.InputImageHeight != newConfig.InputImageHeight;
        var cameraConfigChanged =
            !string.Equals(existingCamera.SourceKey, newCamera.SourceKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.TopFolder, newCamera.TopFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.SideFolder, newCamera.SideFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.BottomFolder, newCamera.BottomFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.TopDeviceId, newCamera.TopDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.SideDeviceId, newCamera.SideDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.BottomDeviceId, newCamera.BottomDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.AdapterFolder, newCamera.AdapterFolder, StringComparison.OrdinalIgnoreCase) ||
            existingCamera.AcquisitionMode != newCamera.AcquisitionMode ||
            Math.Abs(existingCamera.ExposureMs - newCamera.ExposureMs) > 0.0001 ||
            Math.Abs(existingCamera.Gain - newCamera.Gain) > 0.0001 ||
            existingCamera.TriggerTimeoutMs != newCamera.TriggerTimeoutMs ||
            existingCamera.FrameTimeoutMs != newCamera.FrameTimeoutMs ||
            !string.Equals(existingCamera.BoardModel, newCamera.BoardModel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.LotId, newCamera.LotId, StringComparison.OrdinalIgnoreCase);
        var mesConfigChanged =
            existingMes.Mode != newMes.Mode ||
            !string.Equals(existingMes.MockEndpointUrl, newMes.MockEndpointUrl, StringComparison.OrdinalIgnoreCase) ||
            existingMes.UploadTimeoutSeconds != newMes.UploadTimeoutSeconds ||
            existingMes.AutoUploadEnabled != newMes.AutoUploadEnabled ||
            !string.Equals(existingMes.BaseUrl, newMes.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingMes.UploadResultPath, newMes.UploadResultPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingMes.UploadImagePath, newMes.UploadImagePath, StringComparison.OrdinalIgnoreCase) ||
            existingMes.AuthMode != newMes.AuthMode ||
            !string.Equals(existingMes.ApiKeyHeaderName, newMes.ApiKeyHeaderName, StringComparison.Ordinal) ||
            !string.Equals(existingMes.ApiKey, newMes.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(existingMes.BearerToken, newMes.BearerToken, StringComparison.Ordinal) ||
            !string.Equals(existingMes.Username, newMes.Username, StringComparison.Ordinal) ||
            !string.Equals(existingMes.Password, newMes.Password, StringComparison.Ordinal) ||
            existingMes.MaxRetryCount != newMes.MaxRetryCount ||
            existingMes.RetryBackoffMs != newMes.RetryBackoffMs;
        var centralSyncConfigChanged =
            existingCentralSync.Mode != newCentralSync.Mode ||
            !string.Equals(existingCentralSync.EndpointOrFolder, newCentralSync.EndpointOrFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCentralSync.StationId, newCentralSync.StationId, StringComparison.OrdinalIgnoreCase) ||
            existingCentralSync.SyncIntervalSeconds != newCentralSync.SyncIntervalSeconds ||
            existingCentralSync.IncludeImages != newCentralSync.IncludeImages ||
            existingCentralSync.RedactOperatorId != newCentralSync.RedactOperatorId ||
            existingCentralSync.RedactImagePaths != newCentralSync.RedactImagePaths ||
            existingCentralSync.RedactEndpointInExports != newCentralSync.RedactEndpointInExports ||
            existingCentralSync.MaxRetryCount != newCentralSync.MaxRetryCount ||
            !string.Equals(existingCentralSync.SharedSecret, newCentralSync.SharedSecret, StringComparison.Ordinal);
        var lightingConfigChanged =
            !string.Equals(existingLighting.Mode, newLighting.Mode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.TcpHost, newLighting.TcpHost, StringComparison.OrdinalIgnoreCase) ||
            existingLighting.TcpPort != newLighting.TcpPort ||
            !string.Equals(existingLighting.SerialPortName, newLighting.SerialPortName, StringComparison.OrdinalIgnoreCase) ||
            existingLighting.BaudRate != newLighting.BaudRate ||
            !string.Equals(existingLighting.TopProgram, newLighting.TopProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.SideProgram, newLighting.SideProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.BottomProgram, newLighting.BottomProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.CommandTemplate, newLighting.CommandTemplate, StringComparison.Ordinal) ||
            existingLighting.ResponseTimeoutMs != newLighting.ResponseTimeoutMs;
        var deploymentProfileChanged = existingDeploymentProfile != newDeploymentProfile;
        var operatingModeChanged = existingOperatingMode != newOperatingMode;
        var uiPreferencesChanged = !UiPreferencesService.AreEquivalent(existingUiPreferences, newUiPreferences);
        var thresholdChanged =
            ComboToPriority(DetectionPriorityCombo.SelectedIndex) != state.DetectionPriority ||
            Math.Abs(existingConfig.ConfidenceThreshold - newConfig.ConfidenceThreshold) > 0.0001;

        if ((storageRootChanged || modelConfigChanged || cameraConfigChanged || lightingConfigChanged || mesConfigChanged || centralSyncConfigChanged || deploymentProfileChanged || operatingModeChanged || uiPreferencesChanged) && !Authorize(RoleAuthorization.CanManageSettings, "Changing database/vault/model paths, selected model engine, deployment target, operating mode, display assets, camera source, lighting sync, MES integration, or central sync settings"))
            return;

        if (thresholdChanged && !Authorize(RoleAuthorization.CanChangeThresholds, "Changing inspection thresholds or detection priority"))
            return;

        UiPreferencesService.Save(newUiPreferences);
        UiPreferencesService.ApplyToApplication(newUiPreferences, resizeWindowToPreset: true);
        ApplyLanguageVisuals();
        if (!ApplyStorageRoot(newStorageRoot, storageRootChanged))
            return;

        SaveInspectionConfiguration(newConfig);
        SaveCameraSourceSettings(newCamera);
        SaveLightingSettings(newLighting);
        SaveMesIntegrationSettings(newMes);
        SaveCentralSyncSettings(newCentralSync);
        if (deploymentProfileChanged)
            DeploymentProfileSettingsService.Save(newDeploymentProfile);
        if (operatingModeChanged)
            OperatingModeSettingsService.Save(newOperatingMode, WorkflowState.Instance.OperatorWithRole);

        if (!state.TrySetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex), out var message))
        {
            MessageBox.Show(BuildSettingsAppliedMessage(message), "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        RefreshWorkflowUi();
        MessageBox.Show(BuildSettingsAppliedMessage(message), "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        LoadUiPreferenceSelection();
        RefreshWorkflowUi();
        ApplyLanguageVisuals();
        ApplyFontPreset();
        MessageBox.Show(
            TextFor("Unapplied settings were discarded.", "\uC801\uC6A9\uD558\uC9C0 \uC54A\uC740 \uC124\uC815\uC744 \uCDE8\uC18C\uD588\uC2B5\uB2C8\uB2E4."),
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Resetting local settings"))
            return;

        LangCombo.SelectedIndex = 0;
        ResolutionCombo.SelectedIndex = 0;
        ConsoleTitleText.Text = UiPreferenceDefaults.ConsoleTitle;
        StationNameText.Text = UiPreferenceDefaults.StationDisplayName;
        StationSubtitleText.Text = UiPreferenceDefaults.StationSubtitle;
        AccentColorText.Text = UiPreferenceDefaults.AccentColor;
        BrandLogoPathText.Text = string.Empty;
        DetectionPriorityCombo.SelectedIndex = 0;
        DeploymentProfileCombo.SelectedIndex = 0;
        OperatingModeCombo.SelectedIndex = 0;
        InspectionEngineCombo.SelectedIndex = 0;
        ModelPathText.Text = string.Empty;
        StorageRootText.Text = AoiDatabase.DefaultStorageRoot;
        ModelVersionText.Text = "UNCONFIGURED";
        LabelMapPathText.Text = string.Empty;
        ConfidenceThresholdText.Text = "0.65";
        InputWidthText.Text = "640";
        InputHeightText.Text = "640";
        InputTensorNameText.Text = string.Empty;
        OutputTensorNameText.Text = string.Empty;
        StorageRootSettingsService.ResetStorageRoot();
        AoiDatabase.ConfigureStorageRoot(AoiDatabase.DefaultStorageRoot);
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        CameraSourceSettingsService.Save(new CameraSourceSettings());
        CameraSourceSettingsService.ApplyActiveSource();
        MesModeCombo.SelectedIndex = 0;
        CameraTopDeviceIdText.Text = string.Empty;
        CameraSideDeviceIdText.Text = string.Empty;
        CameraBottomDeviceIdText.Text = string.Empty;
        CameraAdapterFolderText.Text = string.Empty;
        CameraAcquisitionModeCombo.SelectedIndex = 0;
        CameraExposureMsText.Text = "5.0";
        CameraGainText.Text = "1.0";
        CameraTriggerTimeoutMsText.Text = "250";
        CameraFrameTimeoutMsText.Text = "1000";
        LightingModeCombo.SelectedIndex = 0;
        LightingTcpHostText.Text = string.Empty;
        LightingTcpPortText.Text = "5025";
        LightingSerialPortText.Text = string.Empty;
        LightingBaudRateText.Text = "9600";
        LightingTopProgramText.Text = "TOP";
        LightingSideProgramText.Text = "SIDE";
        LightingBottomProgramText.Text = "BOTTOM";
        LightingCommandTemplateText.Text = "SET {view} {program}\\n";
        LightingTimeoutMsText.Text = "500";
        LightingSettingsService.Save(new LightingSettings());
        LightingSettingsService.ApplyIntegrationBoundary();
        MesEndpointText.Text = string.Empty;
        MesRestBaseUrlText.Text = string.Empty;
        MesResultPathText.Text = "/api/aoi/results";
        MesImagePathText.Text = "/api/aoi/images";
        MesAuthModeCombo.SelectedIndex = 0;
        MesApiKeyHeaderText.Text = "X-API-Key";
        MesApiKeyBox.Password = string.Empty;
        MesBearerTokenBox.Password = string.Empty;
        MesUsernameText.Text = string.Empty;
        MesPasswordBox.Password = string.Empty;
        MesTimeoutText.Text = "10";
        MesMaxRetryText.Text = "2";
        MesRetryBackoffText.Text = "500";
        MesAutoUploadCheck.IsChecked = false;
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncModeCombo.SelectedIndex = 0;
        CentralSyncEndpointText.Text = string.Empty;
        CentralSyncStationIdText.Text = Environment.MachineName;
        CentralSyncIntervalText.Text = "300";
        CentralSyncMaxRetryText.Text = "5";
        CentralSyncSecretBox.Password = string.Empty;
        CentralSyncIncludeImagesCheck.IsChecked = false;
        CentralSyncRedactOperatorCheck.IsChecked = true;
        CentralSyncRedactImagePathsCheck.IsChecked = true;
        CentralSyncRedactEndpointCheck.IsChecked = true;
        CentralSyncSettingsService.Save(new CentralSyncSettings());
        DeploymentProfileSettingsService.Save(DeploymentProfile.Stage1ImageValidation);
        OperatingModeSettingsService.Save(OperatingMode.Demo, WorkflowState.Instance.OperatorWithRole);

        SaveUiPreferenceSelection();
        ApplyLanguageVisuals();

        var state = WorkflowState.Instance;
        if (!state.TrySetDetectionPriority(Models.DetectionPriority.MinimizeFalsePositives, out var message))
        {
            MessageBox.Show($"Display settings reset.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        RefreshWorkflowUi();
        MessageBox.Show("Settings reset to defaults.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRunSetupWizardClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running setup wizard"))
            return;

        var wizard = new FirstRunWizardView
        {
            Owner = Window.GetWindow(this),
        };
        wizard.ShowDialog();
        RefreshWorkflowUi();
    }

    private void OnOpenInstallNotesClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Opening installation notes"))
            return;

        _vm.CurrentPage = "install";
    }

    private void OnOpenGuideClick(object sender, RoutedEventArgs e)
        => _vm.CurrentPage = "guide";

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting diagnostics report"))
            return;

        try
        {
            var report = SystemDiagnosticService.RunDiagnostics();
            var export = SystemDiagnosticService.ExportReport(report);
            WorkflowState.Instance.AddEvent("DIAGNOSTICS", $"Diagnostics report exported: {Path.GetFileName(export.JsonPath)}");
            MessageBox.Show(
                $"Diagnostics exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}\nText: {export.TextPath}",
                "AOI Monitor Diagnostics",
                MessageBoxButton.OK,
                report.ErrorCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Diagnostics export failed:\n{ex.Message}", "AOI Monitor Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRunLayoutStressClick(object sender, RoutedEventArgs e)
    {
        // Relocated from the shell Access popup: a developer/maintenance audit harness
        // does not belong in the operator login flyout.
        if (!Authorize(RoleAuthorization.CanUseMaintenanceActions, "Running developer layout stress test"))
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

    private void OnBackupConfigurationClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Backing up workstation configuration"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select configuration backup folder",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = ConfigurationBackupService.Export(dialog.FolderName, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show(
                $"Configuration backup exported.\n\n{result.BackupPath}\n\nRaw/customer images are excluded by default.",
                "AOI Monitor Configuration Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration backup failed:\n{ex.Message}", "AOI Monitor Configuration Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRestoreConfigurationPreviewClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Previewing and restoring workstation configuration"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select AOI configuration backup",
            Filter = "AOI configuration backup (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var preview = ConfigurationBackupService.Preview(dialog.FileName);
            var details = BuildRestorePreviewMessage(preview);
            _pendingRestoreBackupPath = preview.IsCompatible ? dialog.FileName : null;
            ApplyRestoreBtn.IsEnabled = preview.IsCompatible && RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(details, "AOI Monitor Restore Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                $"{details}\n\nUse Apply Restore to apply this backup.",
                "AOI Monitor Restore Preview",
                MessageBoxButton.OK,
                preview.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            MessageBox.Show($"Configuration restore preview failed:\n{ex.Message}", "AOI Monitor Restore Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnApplyRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Applying workstation configuration restore"))
            return;
        if (string.IsNullOrWhiteSpace(_pendingRestoreBackupPath) || !File.Exists(_pendingRestoreBackupPath))
        {
            MessageBox.Show("Run Restore Configuration Preview and select a compatible backup before applying restore.", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply configuration restore from:\n{_pendingRestoreBackupPath}\n\nThis will update settings, model registry metadata, threshold profiles, recipe revisions, and deployment profile. Restart the app before production use.",
            "Apply Configuration Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var preview = ConfigurationBackupService.Import(_pendingRestoreBackupPath, WorkflowState.Instance.OperatorWithRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(BuildRestorePreviewMessage(preview), "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            RefreshWorkflowUi();
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Configuration restored. Restart the app before production use so all integration boundaries reload cleanly.", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration restore failed:\n{ex.Message}", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRollbackRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Rolling back workstation configuration restore"))
            return;

        var info = ConfigurationBackupService.GetLastRollbackInfo();
        if (info is null)
        {
            MessageBox.Show("No restore rollback package is available.", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Rollback the last configuration restore using:\n{info.RollbackBackupPath}\n\nThis re-applies the configuration backup captured immediately before the last restore.",
            "Rollback Configuration Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var preview = ConfigurationBackupService.RollbackLastRestore(WorkflowState.Instance.OperatorWithRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(BuildRestorePreviewMessage(preview), "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            RefreshWorkflowUi();
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Configuration restore rolled back. Restart the app before production use so all integration boundaries reload cleanly.", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration restore rollback failed:\n{ex.Message}", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportSupportBundleClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting support bundle"))
            return;

        if (SupportBundleIncludeModelsCheck.IsChecked == true)
        {
            var confirm = MessageBox.Show(
                "Including model files may expose customer or vendor model IP. Continue with model files included?",
                "Export Support Bundle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select support bundle export folder",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = SupportBundleService.Export(new SupportBundleOptions
            {
                OutputRoot = dialog.FolderName,
                IncludeModelFiles = SupportBundleIncludeModelsCheck.IsChecked == true,
                RedactCustomerImagePaths = SupportBundleRedactPathsCheck.IsChecked != false,
                RedactStorageRoot = SupportBundleRedactPathsCheck.IsChecked != false,
            }, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("SUPPORT_BUNDLE", $"Support bundle exported: {Path.GetFileName(result.ZipPath)}.");
            MessageBox.Show(
                $"Support bundle exported.\n\n{result.ZipPath}\n\nRaw customer images and secrets are excluded/redacted by default.",
                "Export Support Bundle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Support bundle export failed:\n{ex.Message}", "Export Support Bundle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
