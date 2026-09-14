using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class HmiLayoutAuditTests : IDisposable
{
    private readonly string _root;

    public HmiLayoutAuditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_HmiLayoutAudit_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("HmiAuditAdmin", UserRole.Admin);
        UiPreferencesService.ResetForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(HmiLayoutAuditTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void MajorOperatorViewsPassHmiLayoutAudit()
    {
        var report = RunOnSta(() =>
        {
            var audit = HmiLayoutAuditService.RunAudit(new HmiLayoutAuditOptions
            {
                Width = 1920,
                Height = 1080,
                DpiScales = new[] { 1.0, 1.25, 1.5 },
            });
            AssertSharedHmiStylesAreRegistered();
            HmiLayoutAuditService.WriteAuditArtifacts(audit);
            return audit;
        });

        var failures = report.Issues
            .Where(issue => issue.Severity == HmiLayoutIssueSeverity.Fail && !issue.Approved)
            .Select(issue => $"{issue.ViewName} {issue.DpiScale:P0} {issue.IssueType} {issue.Target}: {issue.Message}")
            .ToArray();

        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));

        var clippingWarnings = report.Issues
            .Where(issue => issue.Severity == HmiLayoutIssueSeverity.Warn && !issue.Approved && IsClippingPolicyIssue(issue.IssueType))
            .Select(issue => $"{issue.ViewName} {issue.DpiScale:P0} {issue.IssueType} {issue.Target}: {issue.Message}")
            .ToArray();

        Assert.True(clippingWarnings.Length == 0, string.Join(Environment.NewLine, clippingWarnings));

        var shellRoutes = report.Views
            .Where(view => view.Key.StartsWith("shell-", StringComparison.OrdinalIgnoreCase))
            .Select(view => view.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(14, shellRoutes.Length);

        var requiredRules = new[]
        {
            "FixedHeaderRow",
            "CrowdedTabsNoScroll",
            "InputTextClippingRisk",
            "DataGridHeaderTooNarrow",
            "DataGridHeaderMayWrapInsideWord",
            "DataGridHeaderSingleCharacterLines",
            "LightBackgroundInDarkHmi",
            "LongTextNoWrapOrTooltip",
            "SmallPrimaryButton",
            "ClippedText",
            "ClippedButton",
        };
        foreach (var rule in requiredRules)
            Assert.Contains(rule, HmiLayoutAuditService.EnforcedRuleTypes);
    }

    private static bool IsClippingPolicyIssue(string issueType)
        => issueType is "ClippedText" or "ClippedButton" or "InputTextClippingRisk" or "LongTextNoWrapOrTooltip"
            || issueType.StartsWith("DataGridHeader", StringComparison.OrdinalIgnoreCase);

    private static void AssertSharedHmiStylesAreRegistered()
    {
        var requiredStyles = new[]
        {
            "HmiAdaptiveStatusChip",
            "HmiAdaptiveStatusOk",
            "HmiAdaptiveStatusNg",
            "HmiAdaptiveStatusWarning",
            "HmiAdaptiveStatusInfo",
            "HmiAdaptiveStatusSimulated",
            "HmiAdaptiveStatusUnavailable",
            "HmiAdaptiveSectionHeader",
            "HmiAdaptiveFieldRow",
            "HmiFieldTextBox",
            "HmiFieldComboBox",
            "HmiFieldDatePicker",
            "HmiFieldPasswordBox",
            "HmiDataGridColumnHeader",
            "HmiDataGridCell",
            "HmiDenseTable",
            "HmiHeaderRefDesTemplate",
            "HmiHeaderGroundTruthTemplate",
            "HmiHeaderInspectionResultTemplate",
            "HmiHeaderFieldOfViewTemplate",
            "HmiCompactActionBand",
            "HmiStateCard",
            "HmiScrollBar",
            "HmiScrollBarButton",
            "HmiScrollBarThumb",
            "HmiBoundaryEvidenceBadge",
            "HmiEvidenceRealHardware",
            "HmiEvidencePilot",
            "HmiEvidenceBoundaryOnly",
            "HmiEvidenceSimulated",
            "HmiEvidenceMock",
            "HmiEvidenceDemo",
            "HmiEvidenceSampleData",
            "HmiEvidenceNotConnected",
            "HmiEvidenceNotValidated",
        };

        foreach (var style in requiredStyles)
            Assert.NotNull(Application.Current.TryFindResource(style));
    }

    [Fact]
    public void HmiAuditCoversOperatorFacingProblemTables()
    {
        var definitions = HmiLayoutAuditService.CreateViewDefinitions();

        AssertDefinitionRequires(definitions, "defect-review", "QueueGrid");
        AssertDefinitionRequires(definitions, "board-image-library", "RecordsGrid");
        AssertDefinitionRequires(definitions, "export-trace", "ExportGrid");
        AssertDefinitionRequires(definitions, "readiness-qa", "ReadinessSummaryText");
        AssertDefinitionRequires(definitions, "reports-inspection-history", "InspectionGrid");
        AssertDefinitionRequires(definitions, "reports-review-events", "ReviewGrid");
        AssertDefinitionRequires(definitions, "reports-audit-trail", "AuditGrid");
        AssertDefinitionRequires(definitions, "readiness-pilot-issues", "PilotIssuesGrid");
        AssertDefinitionRequires(definitions, "yield-analytics", "DbHealthGrid");
        AssertDefinitionRequires(definitions, "main-inspection", "DefectGrid");
        AssertDefinitionRequires(definitions, "main-inspection", "CalibrationProfileCombo");
        AssertDefinitionRequires(definitions, "main-inspection", "OpenInspectionImageViewerButton");
        AssertDefinitionRequires(definitions, "golden-compare", "FindingsGrid");
        AssertDefinitionRequires(definitions, "golden-compare", "OpenDefectImageViewerButton");
        AssertDefinitionRequires(definitions, "golden-compare", "OpenGoldenImageViewerButton");
        AssertDefinitionRequires(definitions, "image-viewer-window", "ViewerImage");
        AssertDefinitionRequires(definitions, "image-viewer-window", "SaveImageButton");
        AssertDefinitionRequires(definitions, "recipe-editor", "RecipeEmptyStateCard");
        AssertDefinitionRequires(definitions, "profile-3d", "ProfileSourceBadgeText");
        AssertDefinitionRequires(definitions, "hardware-readiness", "StepsGrid");
    }

    [Fact]
    public void HmiAuditFailsDataGridHeadersThatCollapseIntoCharacters()
    {
        var issues = RunOnSta(() =>
        {
            EnsureApplicationResourcesForTest();
            var header = CreateBadHeaderStressElement();
            return HmiLayoutAuditService.RunElementAuditForTests(header, width: 240, height: 160);
        });

        Assert.Contains(issues, issue => issue.IssueType == "DataGridHeaderMayWrapInsideWord");
        Assert.Contains(issues, issue => issue.IssueType == "DataGridHeaderSingleCharacterLines");
    }

    [Fact]
    public void HmiDenseTableHandlesLongHeadersAndValuesWithScrollPolicy()
    {
        var issues = RunOnSta(() =>
        {
            EnsureApplicationResourcesForTest();
            var grid = CreateReadableLongValueStressGrid();
            return HmiLayoutAuditService.RunElementAuditForTests(grid, width: 900, height: 220);
        });

        var headerFailures = issues
            .Where(issue => issue.IssueType.StartsWith("DataGridHeader", StringComparison.OrdinalIgnoreCase))
            .Select(issue => issue.Message)
            .ToArray();
        Assert.True(headerFailures.Length == 0, string.Join(Environment.NewLine, headerFailures));
    }

    private static void AssertDefinitionRequires(IReadOnlyList<HmiViewAuditDefinition> definitions, string key, string controlName)
    {
        var definition = Assert.Single(definitions, item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(controlName, definition.RequiredControlNames);
    }

    private static FrameworkElement CreateBadHeaderStressElement()
    {
        var headerText = new TextBlock
        {
            Text = "Severity",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 18,
            Width = 12,
            Height = 90,
        };

        var header = new DataGridColumnHeader
        {
            Content = headerText,
            Width = 24,
            Height = 96,
            MinWidth = 20,
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var root = new Grid();
        root.Children.Add(header);
        return root;
    }

    private static DataGrid CreateReadableLongValueStressGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            Style = (Style)Application.Current.FindResource("HmiDenseTable"),
            MinWidth = 900,
            Width = 900,
            Height = 220,
            ItemsSource = new[] { new TableStressRow("SAMPLE_CUSTOMER_BOARD_REVISION_A_PANEL_000123_TOP_SIDE_LONG_FILENAME", "REVIEW_REQUIRED_BUT_OPERATOR_READABLE", "C:\\AOI\\Evidence\\CustomerValidation\\LongPath\\annotated_overlay_customer_board_revision_A_panel_000123_top.png") },
            ToolTip = "Long table values trim with cell tooltips through the shared HMI table style.",
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Sample", Binding = new Binding(nameof(TableStressRow.Sample)), Width = 220, MinWidth = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Board", Binding = new Binding(nameof(TableStressRow.Sample)), Width = 140, MinWidth = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "RefDes", HeaderTemplate = (DataTemplate)Application.Current.FindResource("HmiHeaderRefDesTemplate"), Binding = new Binding(nameof(TableStressRow.Sample)), Width = 96, MinWidth = 96 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Result", Binding = new Binding(nameof(TableStressRow.Status)), Width = 120, MinWidth = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Verdict", Binding = new Binding(nameof(TableStressRow.Status)), Width = 120, MinWidth = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Risk", Binding = new Binding(nameof(TableStressRow.Status)), Width = 100, MinWidth = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding(nameof(TableStressRow.Status)), Width = 100, MinWidth = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(TableStressRow.Status)), Width = 140, MinWidth = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new Binding(nameof(TableStressRow.Path)), Width = 300, MinWidth = 300 });
        return grid;
    }

    private sealed record TableStressRow(string Sample, string Status, string Path);

    private static void EnsureApplicationResourcesForTest()
    {
        if (Application.Current is not null)
            return;

        var app = new AOI_Monitor.App();
        app.InitializeComponent();
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                ShutdownApplicationIfOwnedByCurrentThread();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;

        return result!;
    }

    private static void ShutdownApplicationIfOwnedByCurrentThread()
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }
}
