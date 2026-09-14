using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;
using static AOI_Monitor.Views.ReportsView;

namespace AOI_Monitor.Views;

public partial class ReadinessQaView
{
    private sealed record ReadinessLoadSnapshot(
        InspectionLogRow[] Inspections,
        ReviewLogRow[] Reviews,
        AuditLogRow[] Audits,
        PilotIssueRow[] PilotIssues,
        FactoryReadinessRow[] Readiness,
        string ReadinessOverallStatus,
        Stage1ReadinessReport Stage1Readiness,
        StandardsTraceabilityMatrix[] StandardsTraceabilityRows,
        string StandardsTraceabilitySummary,
        CompletionMatrixRow[] CompletionRows,
        double CompletionOverallPercent,
        IReadOnlyList<FactoryAcceptanceChecklistItem> FactoryAcceptanceRows,
        PilotIssueSummary IssueSummary,
        string BuildEvidenceSummary,
        ManagementDashboardReport ManagementDashboardReport);

    private sealed record PackageOutcome(string PackageDir, string ReportPath, int OverlayCount, IReadOnlyList<string> Warnings);

    public sealed class PilotIssueRow
    {
        public string IssueId { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public string CreatedLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string Category { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string PageName { get; init; } = string.Empty;
        public string ReproductionSteps { get; init; } = string.Empty;
        public string ExpectedBehavior { get; init; } = string.Empty;
        public string ActualBehavior { get; init; } = string.Empty;
        public string ScreenshotPath { get; init; } = string.Empty;
        public string BoardModel { get; init; } = string.Empty;
        public string LotId { get; init; } = string.Empty;
        public string RelatedInspectionId { get; init; } = string.Empty;
        public string RelatedAcceptanceRunId { get; init; } = string.Empty;
        public string Owner { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;

        public static PilotIssueRow FromIssue(PilotIssue issue)
            => new()
            {
                IssueId = issue.IssueId,
                CreatedAtUtc = issue.CreatedAtUtc,
                Category = issue.Category.ToString(),
                Severity = issue.Severity,
                Status = issue.Status.ToString(),
                PageName = issue.PageName,
                ReproductionSteps = issue.ReproductionSteps,
                ExpectedBehavior = issue.ExpectedBehavior,
                ActualBehavior = issue.ActualBehavior,
                ScreenshotPath = issue.ScreenshotPath,
                BoardModel = issue.BoardModel,
                LotId = issue.LotId,
                RelatedInspectionId = issue.RelatedInspectionId,
                RelatedAcceptanceRunId = issue.RelatedAcceptanceRunId,
                Owner = issue.Owner,
                Notes = issue.Notes,
            };
    }

    public sealed class FactoryReadinessRow
    {
        public string Name { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string NextAction { get; init; } = string.Empty;

        public static FactoryReadinessRow FromCategory(FactoryReadinessCategory category)
            => new()
            {
                Name = category.Name,
                Status = category.Status,
                Evidence = category.Evidence,
                NextAction = category.NextAction,
            };
    }

    public sealed class Stage1ReadinessRow
    {
        public string Name { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string NextAction { get; init; } = string.Empty;

        public static Stage1ReadinessRow FromCheck(Stage1ReadinessCheck check)
            => new()
            {
                Name = check.Name,
                Status = check.Status,
                Evidence = check.Evidence,
                NextAction = check.NextAction,
            };
    }

    public sealed class CompletionMatrixRow
    {
        public string Stage { get; init; } = string.Empty;
        public double PercentComplete { get; init; }
        public string PercentDisplay => PercentComplete.ToString("F1", CultureInfo.InvariantCulture) + "%";
        public string EvidenceSummary { get; init; } = string.Empty;
        public string MissingEvidenceDisplay { get; init; } = string.Empty;
        public string NextActionsDisplay { get; init; } = string.Empty;

        public static CompletionMatrixRow FromCategory(CompletionAssessmentCategory category)
            => new()
            {
                Stage = category.Stage,
                PercentComplete = category.PercentComplete,
                EvidenceSummary = category.EvidenceSummary,
                MissingEvidenceDisplay = category.MissingEvidence.Count == 0
                    ? "None"
                    : string.Join(" | ", category.MissingEvidence),
                NextActionsDisplay = category.NextActions.Count == 0
                    ? "No action required."
                    : string.Join(" | ", category.NextActions),
            };
    }

    private sealed class BenchmarkOptionsDialog : Window
    {
        private readonly ComboBox _sourceCombo = new();
        private readonly TextBox _imageFolderText = new();
        private readonly TextBox _goldenFileText = new();
        private readonly TextBox _runCountText = new() { Text = "25" };
        private readonly TextBox _warmupCountText = new() { Text = "1" };
        private readonly TextBox _durationSecondsText = new();
        private readonly TextBox _outputFolderText = new();

        public BenchmarkInspectionOptions? Options { get; private set; }

        public BenchmarkOptionsDialog()
        {
            Title = "Performance Benchmark";
            Width = 640;
            Height = 470;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11161A"));
            Foreground = Brushes.White;
            _outputFolderText.Text = BenchmarkInspectionService.BenchmarkRoot;
            ConfigureSourceOptions();
            Content = BuildContent();
        }

        private Grid BuildContent()
        {
            var root = new Grid { Margin = new Thickness(16) };
            for (var i = 0; i < 10; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            AddText(root, "Runs the current inspection engine and records latency traces for every benchmarked image/frame.", 0, 0, 3, "#DCE5EB", bold: true);
            AddLabeledControl(root, "Source", _sourceCombo, 1);
            AddLabeledFolder(root, "Image folder", _imageFolderText, 2, "Select", OnSelectImageFolder);
            AddLabeledFolder(root, "Golden reference (optional)", _goldenFileText, 3, "Select", OnSelectGoldenFile);
            AddLabeledText(root, "Run count", _runCountText, 4);
            AddLabeledText(root, "Warm-up count (0-3, cold-start)", _warmupCountText, 5);
            AddLabeledText(root, "Duration seconds (optional)", _durationSecondsText, 6);
            AddLabeledFolder(root, "Output folder", _outputFolderText, 7, "Select", OnSelectOutputFolder);
            AddText(root, "Stage 2 and Full Factory readiness require Active Camera Source with real, non-simulated camera frames. Folder simulation is labeled simulation-only. Without a golden reference the default engine measures the lighter no-reference path.", 8, 0, 3, "#E1A334");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var cancel = new Button { Content = "Cancel", Width = 92, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var run = new Button { Content = "Run", Width = 92, IsDefault = true };
            run.Click += OnRunClick;
            buttons.Children.Add(cancel);
            buttons.Children.Add(run);
            Grid.SetRow(buttons, 9);
            Grid.SetColumnSpan(buttons, 3);
            root.Children.Add(buttons);

            return root;
        }

        private void ConfigureSourceOptions()
        {
            _sourceCombo.Items.Add(new ComboBoxItem { Content = "Image folder", Tag = BenchmarkInspectionSourceKind.ImageFolder });
            _sourceCombo.Items.Add(new ComboBoxItem { Content = "Folder camera simulation", Tag = BenchmarkInspectionSourceKind.FolderCameraSimulation });
            _sourceCombo.Items.Add(new ComboBoxItem { Content = "Active camera source", Tag = BenchmarkInspectionSourceKind.ActiveCameraSource });
            _sourceCombo.SelectedIndex = 0;
            _sourceCombo.SelectionChanged += (_, _) =>
            {
                var needsFolder = SelectedSource() is BenchmarkInspectionSourceKind.ImageFolder or BenchmarkInspectionSourceKind.FolderCameraSimulation;
                _imageFolderText.IsEnabled = needsFolder;
            };
        }

        private void OnSelectImageFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select benchmark image folder") is { } folder)
                _imageFolderText.Text = folder;
        }

        private void OnSelectGoldenFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select golden reference image",
                Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
                Multiselect = false,
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
                _goldenFileText.Text = dialog.FileName;
        }

        private void OnSelectOutputFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select benchmark report output folder") is { } folder)
                _outputFolderText.Text = folder;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            var source = SelectedSource();
            if (source is BenchmarkInspectionSourceKind.ImageFolder or BenchmarkInspectionSourceKind.FolderCameraSimulation &&
                !Directory.Exists(_imageFolderText.Text))
            {
                MessageBox.Show("Select a valid image folder.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_runCountText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runCount) || runCount <= 0)
            {
                MessageBox.Show("Enter a run count greater than 0.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_warmupCountText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warmupCount) || warmupCount < 0 || warmupCount > 3)
            {
                MessageBox.Show("Warm-up count must be between 0 and 3.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var goldenPath = _goldenFileText.Text.Trim();
            if (!string.IsNullOrWhiteSpace(goldenPath) && !File.Exists(goldenPath))
            {
                MessageBox.Show("The golden reference image was not found. Clear the field or select a valid image file.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TimeSpan? duration = null;
            if (!string.IsNullOrWhiteSpace(_durationSecondsText.Text))
            {
                if (!double.TryParse(_durationSecondsText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                {
                    MessageBox.Show("Duration must be blank or greater than 0 seconds.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                duration = TimeSpan.FromSeconds(seconds);
            }

            Options = new BenchmarkInspectionOptions
            {
                SourceKind = source,
                // Full paths: the inspection engines build absolute image URIs, and a
                // relative path that passes File.Exists here would fail at analysis time.
                ImageFolder = string.IsNullOrWhiteSpace(_imageFolderText.Text) ? string.Empty : Path.GetFullPath(_imageFolderText.Text.Trim()),
                GoldenImagePath = string.IsNullOrWhiteSpace(goldenPath) ? string.Empty : Path.GetFullPath(goldenPath),
                RunCount = runCount,
                WarmupCount = warmupCount,
                Duration = duration,
                OutputRoot = string.IsNullOrWhiteSpace(_outputFolderText.Text) ? BenchmarkInspectionService.BenchmarkRoot : _outputFolderText.Text.Trim(),
                AcceptanceThresholdMs = 1000,
            };
            DialogResult = true;
        }

        private BenchmarkInspectionSourceKind SelectedSource()
            => (_sourceCombo.SelectedItem as ComboBoxItem)?.Tag is BenchmarkInspectionSourceKind source
                ? source
                : BenchmarkInspectionSourceKind.ImageFolder;

        private static string? SelectFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
                ? dialog.FolderName
                : null;
        }

        private static void AddLabeledFolder(Grid root, string label, TextBox box, int row, string buttonText, RoutedEventHandler handler)
        {
            AddLabeledText(root, label, box, row);
            var button = new Button { Content = buttonText, Margin = new Thickness(6, 4, 0, 4), MinHeight = 28 };
            button.Click += handler;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            root.Children.Add(button);
        }

        private static void AddLabeledText(Grid root, string label, TextBox box, int row)
        {
            box.Margin = new Thickness(0, 4, 0, 4);
            box.MinHeight = 28;
            AddLabeledControl(root, label, box, row);
        }

        private static void AddLabeledControl(Grid root, string label, Control control, int row)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA6AF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            };
            control.Margin = new Thickness(0, 4, 0, 4);
            control.MinHeight = 28;
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            root.Children.Add(labelBlock);
            root.Children.Add(control);
        }

        private static void AddText(Grid root, string text, int row, int column, int columnSpan, string color, bool bold = false)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            Grid.SetColumnSpan(block, columnSpan);
            root.Children.Add(block);
        }
    }

    private sealed class SoakTestDialog : Window
    {
        private readonly TextBox _imageFolderText = new();
        private readonly TextBox _durationMinutesText = new() { Text = "2" };
        private readonly TextBox _delayMillisecondsText = new() { Text = "250" };
        private readonly ComboBox _profileCombo = new();
        private readonly ComboBox _engineCombo = new();
        private readonly TextBox _outputFolderText = new();

        public SoakTestOptions? Options { get; private set; }

        public SoakTestDialog(string defaultOutputFolder, string defaultEngineKey)
        {
            Title = "Run Local Soak Test";
            Width = 640;
            Height = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11161A"));
            Foreground = Brushes.White;

            _outputFolderText.Text = defaultOutputFolder;
            ConfigureEngineOptions(defaultEngineKey);
            ConfigureProfileOptions();
            Content = BuildContent();
        }

        private Grid BuildContent()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            AddText(root, "Controlled local soak test using Folder Camera Simulation frames. This does not connect to real camera hardware.", 0, 0, 3, "#DCE5EB", bold: true);
            AddLabeledFolder(root, "Image folder", _imageFolderText, 1, "Select", OnSelectImageFolder);
            AddLabeledControl(root, "Test profile", _profileCombo, 2);
            AddLabeledText(root, "Duration (minutes)", _durationMinutesText, 3);
            AddLabeledText(root, "Delay between inspections (ms)", _delayMillisecondsText, 4);
            AddLabeledControl(root, "Selected engine", _engineCombo, 5);
            AddLabeledFolder(root, "Output folder", _outputFolderText, 6, "Select", OnSelectOutputFolder);
            AddText(root, "Factory PoC profile runs for 480 minutes. Folder Camera Simulation evidence is not real camera validation.", 7, 0, 3, "#9AA6AF");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var cancel = new Button { Content = "Cancel", Width = 92, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var run = new Button { Content = "Run", Width = 92, IsDefault = true };
            run.Click += OnRunClick;
            buttons.Children.Add(cancel);
            buttons.Children.Add(run);
            Grid.SetRow(buttons, 8);
            Grid.SetColumnSpan(buttons, 3);
            root.Children.Add(buttons);

            return root;
        }

        private void ConfigureEngineOptions(string defaultEngineKey)
        {
            _engineCombo.Items.Add(new ComboBoxItem { Content = "Pixel Difference Prototype Engine", Tag = InspectionEngineFactory.DefaultEngineKey });
            _engineCombo.Items.Add(new ComboBoxItem { Content = "ONNX ML Model (configured)", Tag = InspectionEngineFactory.OnnxEngineKey });
            _engineCombo.Items.Add(new ComboBoxItem { Content = "Learned PCB Visual Model (image-only Stage 1)", Tag = InspectionEngineFactory.LearnedVisualEngineKey });
            var normalized = InspectionEngineFactory.NormalizeEngineKey(defaultEngineKey);
            _engineCombo.SelectedIndex = normalized switch
            {
                InspectionEngineFactory.OnnxEngineKey => 1,
                InspectionEngineFactory.LearnedVisualEngineKey => 2,
                _ => 0,
            };
        }

        private void ConfigureProfileOptions()
        {
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Smoke (5 min)", Tag = SoakTestProfile.Smoke });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "30Minute (30 min)", Tag = SoakTestProfile.ThirtyMinute });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "8HourFactoryPoC (8 hours)", Tag = SoakTestProfile.EightHourFactoryPoC });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Custom", Tag = SoakTestProfile.Custom });
            _profileCombo.SelectedIndex = 0;
            _durationMinutesText.Text = "5";
            _durationMinutesText.IsEnabled = false;
            _profileCombo.SelectionChanged += (_, _) =>
            {
                var profile = SelectedProfile();
                _durationMinutesText.IsEnabled = profile == SoakTestProfile.Custom;
                _durationMinutesText.Text = profile switch
                {
                    SoakTestProfile.Smoke => "5",
                    SoakTestProfile.ThirtyMinute => "30",
                    SoakTestProfile.EightHourFactoryPoC => "480",
                    _ => _durationMinutesText.Text,
                };
            };
        }

        private void OnSelectImageFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select soak-test image folder") is { } folder)
                _imageFolderText.Text = folder;
        }

        private void OnSelectOutputFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select soak-test report output folder") is { } folder)
                _outputFolderText.Text = folder;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(_imageFolderText.Text))
            {
                MessageBox.Show("Select a valid image folder.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_durationMinutesText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationMinutes) || durationMinutes <= 0)
            {
                MessageBox.Show("Enter a duration greater than 0 minutes.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_delayMillisecondsText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var delayMs) || delayMs < 0)
            {
                MessageBox.Show("Enter a delay of 0 ms or greater.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_outputFolderText.Text))
            {
                MessageBox.Show("Select an output folder.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var engineKey = ((_engineCombo.SelectedItem as ComboBoxItem)?.Tag as string)
                ?? InspectionEngineFactory.DefaultEngineKey;
            Options = new SoakTestOptions(
                _imageFolderText.Text.Trim(),
                TimeSpan.FromMinutes(durationMinutes),
                TimeSpan.FromMilliseconds(delayMs),
                engineKey,
                _outputFolderText.Text.Trim(),
                "UNKNOWN",
                "TBOX-MAIN",
                "SOAK-TEST");
            DialogResult = true;
        }

        private SoakTestProfile SelectedProfile()
            => (_profileCombo.SelectedItem as ComboBoxItem)?.Tag is SoakTestProfile profile
                ? profile
                : SoakTestProfile.Custom;

        private static string? SelectFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
                ? dialog.FolderName
                : null;
        }

        private static void AddLabeledFolder(Grid root, string label, TextBox box, int row, string buttonText, RoutedEventHandler handler)
        {
            AddLabeledText(root, label, box, row);
            var button = new Button { Content = buttonText, Margin = new Thickness(6, 4, 0, 4), MinHeight = 28 };
            button.Click += handler;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            root.Children.Add(button);
        }

        private static void AddLabeledText(Grid root, string label, TextBox box, int row)
        {
            box.Margin = new Thickness(0, 4, 0, 4);
            box.MinHeight = 28;
            AddLabeledControl(root, label, box, row);
        }

        private static void AddLabeledControl(Grid root, string label, Control control, int row)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA6AF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            };
            control.Margin = new Thickness(0, 4, 0, 4);
            control.MinHeight = 28;
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            root.Children.Add(labelBlock);
            root.Children.Add(control);
        }

        private static void AddText(Grid root, string text, int row, int column, int columnSpan, string color, bool bold = false)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            Grid.SetColumnSpan(block, columnSpan);
            root.Children.Add(block);
        }
    }
}
