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
    private InspectionModelConfiguration BuildConfigurationFromUi()
    {
        var modelPath = ModelPathText.Text.Trim();
        var version = string.IsNullOrWhiteSpace(ModelVersionText.Text)
            ? string.Empty
            : ModelVersionText.Text.Trim();

        if (string.IsNullOrWhiteSpace(version))
        {
            version = string.IsNullOrWhiteSpace(modelPath)
            ? "UNCONFIGURED"
            : Path.GetFileNameWithoutExtension(modelPath);
        }

        return new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineCombo.SelectedIndex switch
            {
                1 => InspectionEngineFactory.OnnxEngineKey,
                2 => InspectionEngineFactory.LearnedVisualEngineKey,
                _ => InspectionEngineFactory.DefaultEngineKey,
            },
            ModelFilePath = modelPath,
            ModelVersion = version,
            LabelMapPath = LabelMapPathText.Text.Trim(),
            InputImageWidth = int.TryParse(
                InputWidthText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var inputWidth)
                ? Math.Clamp(inputWidth, 32, 8192)
                : 640,
            InputImageHeight = int.TryParse(
                InputHeightText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var inputHeight)
                ? Math.Clamp(inputHeight, 32, 8192)
                : 640,
            InputTensorName = InputTensorNameText.Text.Trim(),
            OutputTensorName = OutputTensorNameText.Text.Trim(),
            ConfidenceThreshold = double.TryParse(
                ConfidenceThresholdText.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var threshold)
                ? Math.Clamp(threshold, 0.0, 1.0)
                : 0.65,
        };
    }

    private ModelRegistrationRequest BuildModelRegistrationRequestFromUi()
    {
        var configuration = BuildConfigurationFromUi();
        return new ModelRegistrationRequest
        {
            ModelFilePath = configuration.ModelFilePath,
            LabelMapPath = configuration.LabelMapPath,
            DisplayName = string.IsNullOrWhiteSpace(configuration.ModelFilePath)
                ? configuration.ModelVersion
                : Path.GetFileNameWithoutExtension(configuration.ModelFilePath),
            Version = configuration.ModelVersion,
            InputTensorName = configuration.InputTensorName,
            OutputTensorName = configuration.OutputTensorName,
            InputWidth = configuration.InputImageWidth,
            InputHeight = configuration.InputImageHeight,
            ConfidenceThreshold = configuration.ConfidenceThreshold,
            Notes = "Registered from Settings. No training is performed by AOI Monitor.",
        };
    }

    private static Brush StatusBrush(InspectionEngineStatus status)
    {
        var color = status switch
        {
            InspectionEngineStatus.MlModelReady => "#50F56E",
            InspectionEngineStatus.MlModelMissing => "#E1A334",
            InspectionEngineStatus.MlModelNotTested => "#E1A334",
            InspectionEngineStatus.MlInvalidLabelMap => "#F27777",
            InspectionEngineStatus.MlRuntimeError => "#F27777",
            InspectionEngineStatus.MlUnsupportedOutputFormat => "#F27777",
            InspectionEngineStatus.LearnedVisualModelReady => "#C084FC",
            InspectionEngineStatus.LearnedVisualModelMissing => "#E1A334",
            _ => "#E1A334",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static Brush StatusBrush(ModelConfigurationTestStatus status)
    {
        var color = status switch
        {
            ModelConfigurationTestStatus.Ready => "#50F56E",
            ModelConfigurationTestStatus.MissingModel => "#E1A334",
            ModelConfigurationTestStatus.InvalidLabelMap => "#F27777",
            ModelConfigurationTestStatus.RuntimeError => "#F27777",
            ModelConfigurationTestStatus.UnsupportedOutputFormat => "#F27777",
            _ => "#E1A334",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static Brush StatusBrush(CameraSourceStatus status)
    {
        var color = status switch
        {
            CameraSourceStatus.Ready => "#50F56E",
            CameraSourceStatus.Simulated => "#E1A334",
            CameraSourceStatus.Error => "#F27777",
            _ => "#F27777",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string CameraStatusDisplay(CameraSourceStatus status)
        => status switch
        {
            CameraSourceStatus.Ready => "Connected",
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };

    private static Brush IntegrationStatusBrush(IntegrationConnectionStatus status)
    {
        var color = status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string IntegrationStatusDisplay(IntegrationConnectionStatus status)
        => status switch
        {
            IntegrationConnectionStatus.Ready => "Ready",
            IntegrationConnectionStatus.Simulated => "Simulated",
            IntegrationConnectionStatus.Error => "Error",
            _ => "Not Connected",
        };

    private string? ValidateRawModelCheckFields()
    {
        if (!int.TryParse(InputWidthText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(InputHeightText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width < 32 ||
            width > 8192 ||
            height < 32 ||
            height > 8192)
        {
            return "Input width and height must be whole numbers between 32 and 8192.";
        }

        if (!double.TryParse(ConfidenceThresholdText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
            threshold < 0 ||
            threshold > 1)
        {
            return "Confidence threshold must be a number between 0 and 1.";
        }

        return null;
    }

    private static bool HasAdminOnlyModelConfigurationChange(
        InspectionModelConfiguration existing,
        InspectionModelConfiguration candidate)
        => !string.Equals(existing.SelectedEngineKey, candidate.SelectedEngineKey, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.ModelFilePath, candidate.ModelFilePath, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.ModelVersion, candidate.ModelVersion, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.LabelMapPath, candidate.LabelMapPath, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.InputTensorName, candidate.InputTensorName, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.OutputTensorName, candidate.OutputTensorName, StringComparison.OrdinalIgnoreCase) ||
           existing.InputImageWidth != candidate.InputImageWidth ||
           existing.InputImageHeight != candidate.InputImageHeight;

    private void ApplyLanguageVisuals()
    {
        _isKorean = LangCombo.SelectedIndex == 1;
        var culture = _isKorean ? new CultureInfo("ko-KR") : new CultureInfo("en-US");

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current.MainWindow is Window mainWindow)
        {
            mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            mainWindow.FontFamily = _isKorean ? new FontFamily("Malgun Gothic, Segoe UI") : new FontFamily("Segoe UI");
        }

        var languageFont = _isKorean ? new FontFamily("Malgun Gothic, Segoe UI") : new FontFamily("Segoe UI");
        LangCombo.FontFamily = languageFont;
        FontCombo.FontFamily = languageFont;
        ResolutionCombo.FontFamily = languageFont;
        ThemeCombo.FontFamily = languageFont;
        DetectionPriorityCombo.FontFamily = languageFont;
        InspectionEngineCombo.FontFamily = languageFont;
        MesModeCombo.FontFamily = languageFont;

        DisplayLanguageHeaderText.Text = TextFor("Display / Language", "\uD654\uBA74 / \uC5B8\uC5B4");
        LanguageLabelText.Text = TextFor("Language", "\uC5B8\uC5B4");
        ResolutionLabelText.Text = TextFor("Resolution", "\uD574\uC0C1\uB3C4");
        ThemeLabelText.Text = TextFor("Theme", "\uD14C\uB9C8");
        FontSizeLabelText.Text = TextFor("Font Size", "\uAE00\uC790 \uD06C\uAE30");
        ProgramAssetsHeaderText.Text = TextFor("Program Assets", "\uD504\uB85C\uADF8\uB7A8 \uC790\uC0B0");
        ConsoleTitleLabelText.Text = TextFor("Console Title", "\uCF58\uC194 \uC81C\uBAA9");
        StationLabelText.Text = TextFor("Station Label", "\uC2A4\uD14C\uC774\uC158 \uB77C\uBCA8");
        StationSubtitleLabelText.Text = TextFor("Station Subtitle", "\uC2A4\uD14C\uC774\uC158 \uC124\uBA85");
        AccentColorLabelText.Text = TextFor("Accent Color", "\uAC15\uC870 \uC0C9\uC0C1");
        LogoImageLabelText.Text = TextFor("Logo Image", "\uB85C\uACE0 \uC774\uBBF8\uC9C0");
        BrowseBrandLogoBtn.Content = TextFor("Browse", "\uCC3E\uC544\uBCF4\uAE30");
        ProgramAssetsPolicyText.Text = TextFor(
            "Changes are staged until Apply. Cancel discards edits and restores the last saved asset settings.",
            "\uBCC0\uACBD\uC0AC\uD56D\uC740 \uC801\uC6A9\uC744 \uB204\uB974\uAE30 \uC804\uAE4C\uC9C0 \uB300\uAE30\uB429\uB2C8\uB2E4. \uCDE8\uC18C\uB294 \uD3B8\uC9D1\uC744 \uBC84\uB9AC\uACE0 \uB9C8\uC9C0\uB9C9\uC73C\uB85C \uC800\uC7A5\uB41C \uC790\uC0B0 \uC124\uC815\uC744 \uBCF5\uC6D0\uD569\uB2C8\uB2E4.");
        StoragePathLabelText.Text = TextFor("Storage Path", "\uC800\uC7A5 \uACBD\uB85C");
        ReviewDefaultLabelText.Text = TextFor("Review Default", "\uAC80\uD1A0 \uAE30\uBCF8\uAC12");
        DetectionPriorityLabelText.Text = TextFor("Detection Priority", "\uAC80\uCD9C \uC6B0\uC120\uC21C\uC704");
        RunSetupWizardBtn.Content = TextFor("Run Setup Wizard Again", "\uC124\uCE58 \uB9C8\uBC95\uC0AC \uB2E4\uC2DC \uC2E4\uD589");
        OpenInstallNotesBtn.Content = TextFor("Installation Notes", "\uC124\uCE58 \uB178\uD2B8");
        OpenGuideBtn.Content = TextFor("Open Guide", "\uAC00\uC774\uB4DC \uC5F4\uAE30");
        ExportDiagnosticsBtn.Content = TextFor("Export Diagnostics Report", "\uC9C4\uB2E8 \uBCF4\uACE0\uC11C \uB0B4\uBCF4\uB0B4\uAE30");
        BackupConfigurationBtn.Content = TextFor("Backup Configuration", "\uAD6C\uC131 \uBC31\uC5C5");
        RestoreConfigurationPreviewBtn.Content = TextFor("Restore Configuration Preview", "\uAD6C\uC131 \uBCF5\uC6D0 \uBBF8\uB9AC\uBCF4\uAE30");
        ApplyRestoreBtn.Content = TextFor("Apply Restore", "\uBCF5\uC6D0 \uC801\uC6A9");
        RollbackRestoreBtn.Content = TextFor("Rollback Last Restore", "\uB9C8\uC9C0\uB9C9 \uBCF5\uC6D0 \uB864\uBC31");
        ExportSupportBundleBtn.Content = TextFor("Export Support Bundle", "\uC9C0\uC6D0 \uBC88\uB4E4 \uB0B4\uBCF4\uB0B4\uAE30");
        ApplyBtn.Content = TextFor("Apply", "\uC801\uC6A9");
        CancelBtn.Content = TextFor("Cancel", "\uCDE8\uC18C");
        ResetBtn.Content = TextFor("Reset", "\uCD08\uAE30\uD654");
        var localizationScope = TextFor(
            "Language/font changes are applied after pressing Apply. Supported shell, page, table, and Settings labels update immediately; technical codes and evidence IDs remain English for traceability.",
            "\uC5B8\uC5B4/\uAE00\uAF34 \uC124\uC815\uC740 \uC801\uC6A9\uC744 \uB204\uB978 \uB4A4 \uBC18\uC601\uB429\uB2C8\uB2E4. \uC9C0\uC6D0\uB418\uB294 \uC178, \uD398\uC774\uC9C0, \uD45C, \uC124\uC815 \uB77C\uBCA8\uC740 \uC989\uC2DC \uBC14\uB00C\uBA70, \uAE30\uC220 \uCF54\uB4DC\uC640 \uC99D\uBE59 ID\uB294 \uCD94\uC801\uC131\uC744 \uC704\uD574 \uC601\uC5B4\uB85C \uC720\uC9C0\uB429\uB2C8\uB2E4.");
        LocalizationStatusText.Text = localizationScope;

        SetComboItemText(LangCombo, 0, "English");
        SetComboItemText(LangCombo, 1, TextFor("Korean", "\uD55C\uAD6D\uC5B4"));

        if (_isKorean)
        {
            SetComboItemText(FontCombo, 0, "\uD45C\uC900 - 14pt \uC6B4\uC601\uC790 \uCD5C\uC18C \uD06C\uAE30");
            SetComboItemText(ResolutionCombo, 0, "1920 x 1080 \uCD5C\uC18C HMI");
            SetComboItemText(ResolutionCombo, 1, "2560 x 1440 \uC5D4\uC9C0\uB2C8\uC5B4\uB9C1 \uBAA8\uB2C8\uD130");
            SetComboItemText(ResolutionCombo, 2, "3840 x 2160 \uBCBD\uBA74 \uD45C\uC2DC");
            SetComboItemText(ThemeCombo, 0, "\uC0B0\uC5C5\uC6A9 \uB2E4\uD06C");

            SetComboItemText(DetectionPriorityCombo, 0, DetectionPriorityDisplay(Models.DetectionPriority.MinimizeFalsePositives, true));
            SetComboItemText(DetectionPriorityCombo, 1, DetectionPriorityDisplay(Models.DetectionPriority.Balanced, true));
            SetComboItemText(DetectionPriorityCombo, 2, DetectionPriorityDisplay(Models.DetectionPriority.MaximizeDefectRecall, true));
        }
        else
        {
            SetComboItemText(FontCombo, 0, "Standard - 14 pt operator floor");
            SetComboItemText(ResolutionCombo, 0, "1920 x 1080 minimum HMI");
            SetComboItemText(ResolutionCombo, 1, "2560 x 1440 engineering monitor");
            SetComboItemText(ResolutionCombo, 2, "3840 x 2160 wall display");
            SetComboItemText(ThemeCombo, 0, "Industrial Dark");

            SetComboItemText(DetectionPriorityCombo, 0, "Minimize False Positives");
            SetComboItemText(DetectionPriorityCombo, 1, "Balanced");
            SetComboItemText(DetectionPriorityCombo, 2, "Maximize Defect Recall");
        }

        ReviewDefaultText.Text = DetectionPriorityDisplay(ComboToPriority(DetectionPriorityCombo.SelectedIndex), _isKorean);
    }

    private string BuildSettingsAppliedMessage(string workflowMessage)
    {
        var languageMessage = TextFor(
            "Language/font settings applied. Settings and supported runtime text update immediately; some workstation labels and technical codes remain English for traceability.",
            "\uC5B8\uC5B4/\uAE00\uAF34 \uC124\uC815\uC774 \uC801\uC6A9\uB418\uC5C8\uC2B5\uB2C8\uB2E4. \uC124\uC815 \uD654\uBA74\uACFC \uC9C0\uC6D0\uB418\uB294 \uC2E4\uD589 \uD14D\uC2A4\uD2B8\uB294 \uC989\uC2DC \uBC18\uC601\uB418\uBA70, \uC77C\uBD80 \uC791\uC5C5\uC790 \uD654\uBA74 \uB77C\uBCA8\uACFC \uAE30\uC220 \uCF54\uB4DC\uB294 \uCD94\uC801\uC131\uC744 \uC704\uD574 \uC601\uC5B4\uB85C \uC720\uC9C0\uB429\uB2C8\uB2E4.");
        var title = TextFor("Display settings applied.", "\uD654\uBA74 \uC124\uC815\uC774 \uC801\uC6A9\uB418\uC5C8\uC2B5\uB2C8\uB2E4.");
        return $"{title}\n{workflowMessage}\n\n{languageMessage}";
    }

    private void ApplyFontPreset()
    {
        UiPreferencesService.ApplyToApplication(BuildUiPreferencesFromUi());
    }

    private void LoadUiPreferenceSelection()
    {
        var preferences = UiPreferencesService.Load();
        LangCombo.SelectedIndex = preferences.Language == UiLanguage.Korean ? 1 : 0;
        _ = preferences.FontPreset;
        FontCombo.SelectedIndex = 0;
        ResolutionCombo.SelectedIndex = preferences.ResolutionPreset switch
        {
            UiResolutionPreset.Qhd2560x1440 => 1,
            UiResolutionPreset.Uhd3840x2160 => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = 0;
        ConsoleTitleText.Text = preferences.ConsoleTitle;
        StationNameText.Text = preferences.StationDisplayName;
        StationSubtitleText.Text = preferences.StationSubtitle;
        AccentColorText.Text = preferences.AccentColor;
        BrandLogoPathText.Text = preferences.BrandLogoPath;
    }

    private void SaveUiPreferenceSelection()
        => UiPreferencesService.Save(BuildUiPreferencesFromUi());

    private UiPreferences BuildUiPreferencesFromUi()
        => new()
        {
            Language = LangCombo.SelectedIndex == 1 ? UiLanguage.Korean : UiLanguage.English,
            FontPreset = UiFontPreset.Standard,
            ResolutionPreset = ResolutionCombo.SelectedIndex switch
            {
                1 => UiResolutionPreset.Qhd2560x1440,
                2 => UiResolutionPreset.Uhd3840x2160,
                _ => UiResolutionPreset.FullHd1920x1080,
            },
            Theme = UiTheme.IndustrialDark,
            ConsoleTitle = ConsoleTitleText.Text,
            StationDisplayName = StationNameText.Text,
            StationSubtitle = StationSubtitleText.Text,
            AccentColor = AccentColorText.Text,
            BrandLogoPath = BrandLogoPathText.Text,
        };

    private static void SetComboItemText(ComboBox comboBox, int index, string text)
    {
        if (index < 0 || index >= comboBox.Items.Count)
            return;

        if (comboBox.Items[index] is ComboBoxItem item)
            item.Content = text;
    }

    private string TextFor(string english, string korean) => _isKorean ? korean : english;

    private void RefreshRetentionUi()
    {
        var settings = LogRetentionSettingsService.LoadSettings();
        RetentionEnabledCheck.IsChecked = settings.Enabled;
        RetentionDaysText.Text = settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
        RetentionWarningCheck.IsChecked = settings.WarningEnabled;
        RetentionStatusText.Text = settings.Enabled
            ? $"Retention policy: archive and purge logs older than {settings.RetentionDays} day(s); warning {(settings.WarningEnabled ? $"on ({settings.WarningLeadDays}-day lead)" : "off")}."
            : "Retention policy: automatic purge is disabled. Logs are kept indefinitely in the live tables.";
    }

    private void OnApplyRetentionClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing the data retention policy"))
            return;

        if (!int.TryParse(RetentionDaysText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1)
        {
            MessageBox.Show("Enter a whole number of retention days (1 or more).", "Data Retention", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var current = LogRetentionSettingsService.LoadSettings();
        var settings = new LogRetentionSettings
        {
            Enabled = RetentionEnabledCheck.IsChecked == true,
            RetentionDays = days,
            WarningEnabled = RetentionWarningCheck.IsChecked == true,
            WarningLeadDays = current.WarningLeadDays,
        };
        LogRetentionSettingsService.Save(settings, WorkflowState.Instance.OperatorWithRole);
        RefreshRetentionUi();
        MessageBox.Show(
            "Data retention policy saved. Archive and purge run at application startup; the recoverable archive keeps the full record of purged logs.",
            "Data Retention",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool Authorize(Func<UserRole, bool> permission, string action)
    {
        if (WorkflowState.Instance.TryAuthorize(permission, action, out var message))
            return true;

        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static string DetectionPriorityDisplay(Models.DetectionPriority priority, bool isKorean)
    {
        if (!isKorean)
            return WorkflowState.ToDisplay(priority);

        return priority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => "\uC624\uAC80\uCD9C \uCD5C\uC18C\uD654",
            Models.DetectionPriority.Balanced => "\uADE0\uD615",
            Models.DetectionPriority.MaximizeDefectRecall => "\uACB0\uD568 \uAC80\uCD9C \uCD5C\uB300\uD654",
            _ => "\uADE0\uD615",
        };
    }

    private static Models.DetectionPriority ComboToPriority(int selectedIndex) => selectedIndex switch
    {
        0 => Models.DetectionPriority.MinimizeFalsePositives,
        1 => Models.DetectionPriority.Balanced,
        2 => Models.DetectionPriority.MaximizeDefectRecall,
        _ => Models.DetectionPriority.MinimizeFalsePositives,
    };

    private static DeploymentProfile ComboToDeploymentProfile(int selectedIndex) => selectedIndex switch
    {
        0 => DeploymentProfile.Stage1ImageValidation,
        1 => DeploymentProfile.Stage2CameraPilot,
        2 => DeploymentProfile.Stage3RobotPilot,
        3 => DeploymentProfile.Stage4MesPilot,
        4 => DeploymentProfile.FullFactoryAutomation,
        _ => DeploymentProfile.Stage1ImageValidation,
    };

    private static int DeploymentProfileToCombo(DeploymentProfile profile) => profile switch
    {
        DeploymentProfile.Stage1ImageValidation => 0,
        DeploymentProfile.Stage2CameraPilot => 1,
        DeploymentProfile.Stage3RobotPilot => 2,
        DeploymentProfile.Stage4MesPilot => 3,
        DeploymentProfile.FullFactoryAutomation => 4,
        _ => 0,
    };

    private static OperatingMode ComboToOperatingMode(int selectedIndex) => selectedIndex switch
    {
        1 => OperatingMode.Pilot,
        2 => OperatingMode.Production,
        _ => OperatingMode.Demo,
    };

    private static int OperatingModeToCombo(OperatingMode mode) => mode switch
    {
        OperatingMode.Pilot => 1,
        OperatingMode.Production => 2,
        _ => 0,
    };

    private static string PromptForText(string title, string label)
    {
        var input = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 10),
            MinWidth = 360,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 3,
            MaxLines = 5,
        };
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
                    input,
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
        return dialog.ShowDialog() == true ? input.Text.Trim() : string.Empty;
    }

    private static string BuildRestorePreviewMessage(ConfigurationRestorePreview preview)
    {
        var lines = new List<string>
        {
            preview.Summary,
            $"Schema: {preview.SchemaVersion}",
            $"Database schema: {preview.DatabaseSchemaVersion}",
            $"Target storage path: {preview.TargetStoragePath}",
            $"Settings: {string.Join(", ", preview.SettingsKeys.DefaultIfEmpty("--"))}",
        };

        AddPreviewSection(lines, "Settings that will change:", preview.SettingsChanges);
        AddPreviewSection(lines, "Conflicts:", preview.Conflicts);
        AddPreviewSection(lines, "Missing model files:", preview.MissingModelFiles);
        AddPreviewSection(lines, "Missing plugin folders:", preview.MissingPluginFolders);

        if (preview.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(preview.Warnings.Select(warning => $"- {warning}"));
        }

        if (preview.SecretProtectionStatus.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Secret protection:");
            lines.AddRange(preview.SecretProtectionStatus.Select(status => $"- {status}"));
        }

        if (preview.BlockingIssues.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Blocking issues:");
            lines.AddRange(preview.BlockingIssues.Select(issue => $"- {issue}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddPreviewSection(List<string> lines, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;

        lines.Add(string.Empty);
        lines.Add(title);
        lines.AddRange(items.Select(item => $"- {item}"));
    }

    private sealed class ModelRegistryRow
    {
        public ModelRegistryRow(ModelRegistryEntry entry)
        {
            ModelId = entry.ModelId;
            DisplayName = entry.DisplayName;
            Version = entry.Version;
            ValidationStatus = ModelConfigurationValidator.ToDisplay(entry.ValidationStatus);
            LifecycleState = entry.LifecycleState.ToString();
            LatestAcceptanceStatus = string.IsNullOrWhiteSpace(entry.LatestAcceptanceStatus) ? "--" : entry.LatestAcceptanceStatus;
            LatestAcceptanceRunDisplay = entry.LatestAcceptanceRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
            ReleasePackageDisplay = string.IsNullOrWhiteSpace(entry.LatestReleasePackagePath)
                ? "--"
                : $"{entry.LatestReleasePackagePath} (#{entry.LatestReleasePackageId?.ToString(CultureInfo.InvariantCulture) ?? "--"})";
            WaiverDisplay = string.IsNullOrWhiteSpace(entry.DeploymentWaiverReason)
                ? "--"
                : $"{entry.DeploymentWaiverRiskClassification}; expires {entry.WaiverExpiresAtUtc?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "--"}";
            ThresholdDisplay = entry.ConfidenceThreshold.ToString("P0", CultureInfo.InvariantCulture);
            LastValidatedDisplay = entry.LastValidatedAtUtc is { } timestamp
                ? timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "--";
            ActiveDisplay = entry.IsActive ? "Yes" : string.Empty;
        }

        public string ModelId { get; }
        public string DisplayName { get; }
        public string Version { get; }
        public string ValidationStatus { get; }
        public string LifecycleState { get; }
        public string LatestAcceptanceStatus { get; }
        public string LatestAcceptanceRunDisplay { get; }
        public string ReleasePackageDisplay { get; }
        public string WaiverDisplay { get; }
        public string ThresholdDisplay { get; }
        public string LastValidatedDisplay { get; }
        public string ActiveDisplay { get; }
    }

    private sealed class LearnedVisualModelRow
    {
        public LearnedVisualModelRow(LearnedVisualModelRegistryEntry entry)
        {
            ModelId = entry.ModelId;
            ModelVersion = entry.ModelVersion;
            ProjectName = string.IsNullOrWhiteSpace(entry.ProjectName) ? entry.ProjectId : entry.ProjectName;
            BoardModel = string.IsNullOrWhiteSpace(entry.BoardModel) ? "--" : entry.BoardModel;
            ImagesLearnedFrom = entry.ImagesLearnedFrom.ToString(CultureInfo.InvariantCulture);
            OkValidationCount = entry.OkValidationCount.ToString(CultureInfo.InvariantCulture);
            FalseCallTargetDisplay = entry.FalseCallTargetDisplay;
            RecommendedThresholdDisplay = entry.RecommendedThresholdDisplay;
            EvidenceMode = entry.EvidenceMode;
            ArtifactStatus = entry.ArtifactStatus;
            ActiveDisplay = entry.ActiveDisplay;
        }

        public string ModelId { get; }
        public string ModelVersion { get; }
        public string ProjectName { get; }
        public string BoardModel { get; }
        public string ImagesLearnedFrom { get; }
        public string OkValidationCount { get; }
        public string FalseCallTargetDisplay { get; }
        public string RecommendedThresholdDisplay { get; }
        public string EvidenceMode { get; }
        public string ArtifactStatus { get; }
        public string ActiveDisplay { get; }
    }

    private sealed class ModelAcceptanceRunRow
    {
        public ModelAcceptanceRunRow(ModelAcceptanceRun run)
        {
            Id = run.Id;
            ModelId = string.IsNullOrWhiteSpace(run.ModelId) ? "--" : run.ModelId;
            Status = run.Status;
            DatasetName = run.DatasetName;
            CandidateDisplay = run.IsProductionCandidate ? "Yes" : "No";
        }

        public long Id { get; }
        public string ModelId { get; }
        public string Status { get; }
        public string DatasetName { get; }
        public string CandidateDisplay { get; }
    }

    private sealed class ThresholdProfileRow
    {
        public ThresholdProfileRow(ThresholdProfile profile)
        {
            ProfileId = profile.ProfileId;
            Revision = profile.Revision;
            BoardModel = profile.BoardModel;
            BoardProgram = profile.BoardProgram;
            RecipeName = profile.RecipeName;
            Status = profile.Status;
            RuleCount = profile.Rules.Count;
            CreatedBy = profile.CreatedBy;
            ApprovedBy = string.IsNullOrWhiteSpace(profile.ApprovedBy) ? "--" : profile.ApprovedBy;
            Deployed = string.Equals(profile.Status, "Deployed", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
            SourceFalseCallReductionRunDisplay = profile.SourceFalseCallReductionRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
            SourceValidationRunDisplay = profile.SourceValidationRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
        }

        public string ProfileId { get; }
        public string Revision { get; }
        public string BoardModel { get; }
        public string BoardProgram { get; }
        public string RecipeName { get; }
        public string Status { get; }
        public int RuleCount { get; }
        public string CreatedBy { get; }
        public string ApprovedBy { get; }
        public string Deployed { get; }
        public string SourceFalseCallReductionRunDisplay { get; }
        public string SourceValidationRunDisplay { get; }
    }

    public void Dispose()
    {
        DisposeCancellation(ref _modelAcceptanceCancellation);
        DisposeCancellation(ref _cameraAcceptanceCancellation);
        DisposeCancellation(ref _lightingAcceptanceCancellation);
        DisposeCancellation(ref _robotAcceptanceCancellation);
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
