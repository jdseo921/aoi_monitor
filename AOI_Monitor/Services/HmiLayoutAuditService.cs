using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;

namespace AOI_Monitor.Services;

public sealed class HmiLayoutAuditReport
{
    public string SchemaVersion { get; init; } = "hmi-layout-audit/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string AppVersion { get; init; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public double MinimumWindowWidth { get; init; } = 1920;
    public double MinimumWindowHeight { get; init; } = 1080;
    public double MinimumReadableTextPt { get; init; } = 14;
    public double MinimumPrimaryButtonWidth { get; init; } = 120;
    public double MinimumPrimaryButtonHeight { get; init; } = 40;
    public List<HmiLayoutViewResult> Views { get; init; } = new();
    public List<HmiLayoutIssue> Issues { get; init; } = new();
    public List<string> ApprovedExceptions { get; init; } = new();
    public bool Passed => Issues.All(issue => issue.Approved || issue.Severity != HmiLayoutIssueSeverity.Fail);
}

public sealed class HmiLayoutViewResult
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool DensePage { get; init; }
    public bool HasScrollViewer { get; init; }
    public int VisualElementCount { get; init; }
    public int IssueCount { get; init; }
    public int FailureCount { get; init; }
    public int WarningCount { get; init; }

    /// <summary>Bottom edge (DIP) of the lowest visible content inside the viewport.</summary>
    public double ContentBottom { get; init; }

    /// <summary>
    /// Share of the viewport height below the lowest visible content — the page's trailing
    /// dead space at this resolution. Scrollable pages whose content exceeds the viewport
    /// report 0.
    /// </summary>
    public double TrailingVoidPercent { get; init; }
}

public sealed class HmiLayoutIssue
{
    public string Id { get; init; } = string.Empty;
    public string ViewKey { get; init; } = string.Empty;
    public string ViewName { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public string IssueType { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public HmiLayoutIssueSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Approved { get; set; }
}

public enum HmiLayoutIssueSeverity
{
    Info,
    Warn,
    Fail,
}

public sealed class HmiLayoutAuditOptions
{
    public double Width { get; init; } = 1920;
    public double Height { get; init; } = 1080;
    public IReadOnlyList<double> DpiScales { get; init; } = new[] { 1.0, 1.25, 1.5 };
    public string? OutputRoot { get; init; }
    public string? ApprovedExceptionsPath { get; init; }
}

public static class HmiLayoutAuditService
{
    private const double ClipTolerance = 3.0;
    private const double MinimumReadableFontDip = 18.6667;
    private const double LightBackgroundLuminanceThreshold = 0.82;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static IReadOnlyList<string> EnforcedRuleTypes { get; } = new[]
    {
        "MissingRequiredControl",
        "UnreachableRequiredControl",
        "ZeroSizeControl",
        "ClippedText",
        "SmallPrimaryButton",
        "ClippedButton",
        "LongTextNoWrapOrTooltip",
        "FixedHeaderRow",
        "CrowdedTabsNoScroll",
        "InputTextClippingRisk",
        "DataGridHeaderTooNarrow",
        "DataGridHeaderMayWrapInsideWord",
        "DataGridHeaderSingleCharacterLines",
        "LightBackgroundInDarkHmi",
        "ShellChromeTooTall",
        "ExpandedEmptyAlarmPanel",
    };

    public static HmiLayoutAuditReport RunAudit(HmiLayoutAuditOptions? options = null)
    {
        options ??= new HmiLayoutAuditOptions();
        EnsureApplicationResources();
        var exceptions = LoadApprovedExceptions(options.ApprovedExceptionsPath);
        var report = new HmiLayoutAuditReport
        {
            MinimumWindowWidth = options.Width,
            MinimumWindowHeight = options.Height,
        };

        foreach (var definition in CreateViewDefinitions())
        {
            foreach (var dpiScale in options.DpiScales)
            {
                var issues = new List<HmiLayoutIssue>();
                FrameworkElement? element = null;
                var hasScrollViewer = false;
                var elementCount = 0;
                var contentBottom = 0.0;

                try
                {
                    element = definition.Factory();
                    MeasureElement(element, options.Width, options.Height);
                    var descendants = Descendants(element).ToArray();
                    elementCount = descendants.Length + 1;
                    hasScrollViewer = descendants.OfType<ScrollViewer>().Any();
                    contentBottom = MeasureContentBottom(element, options.Height);

                    // Trailing dead space: exposed page background below the lowest panel or
                    // control. The five worst offenders measured 19-59% before the 2026-08-23
                    // layout pass; this rule keeps pages from regressing. The image viewer is
                    // exempt because the audit harness opens it without an image, so its whole
                    // viewport reads as empty here while at runtime it always hosts one.
                    var voidPercent = options.Height <= 0 ? 0 : Math.Max(0, options.Height - contentBottom) / options.Height * 100.0;
                    if (definition.Key != "image-viewer-window" && voidPercent > 20.0)
                    {
                        issues.Add(CreateIssue(definition, dpiScale, "TrailingDeadSpace", definition.Key,
                            voidPercent > 35.0 ? HmiLayoutIssueSeverity.Fail : HmiLayoutIssueSeverity.Warn,
                            $"Bottom {voidPercent:N1}% of the viewport is empty page background (content ends at {contentBottom:N0} of {options.Height:N0} DIP). Let a table, chart, or status band absorb the height."));
                    }

                    if (definition.RequiresScrollViewer && !hasScrollViewer)
                    {
                        issues.Add(CreateIssue(definition, dpiScale, "MissingScrollViewer", definition.Key, HmiLayoutIssueSeverity.Fail,
                            "Dense operator page does not contain a ScrollViewer or approved exception."));
                    }

                    AddRequiredControlIssues(definition, element, dpiScale, issues);
                    AddZeroSizeIssues(definition, element, dpiScale, issues);
                    AddTextIssues(definition, element, dpiScale, issues);
                    AddButtonIssues(definition, element, dpiScale, issues);
                    AddLongTextIssues(definition, element, dpiScale, issues);
                    AddFixedHeaderRowIssues(definition, element, dpiScale, issues);
                    AddTabControlIssues(definition, element, dpiScale, issues);
                    AddInputControlTextIssues(definition, element, dpiScale, issues);
                    AddDataGridHeaderIssues(definition, element, dpiScale, issues);
                    AddLightBackgroundIssues(definition, element, dpiScale, issues);
                    AddShellIssues(definition, element, dpiScale, issues);
                }
                catch (Exception ex)
                {
                    issues.Add(CreateIssue(definition, dpiScale, "ViewConstruction", definition.Key, HmiLayoutIssueSeverity.Fail,
                        $"{ex.GetType().Name}: {ex.Message}"));
                }
                finally
                {
                    if (element is Window window)
                        window.Close();
                }

                foreach (var issue in issues)
                {
                    issue.Approved = exceptions.IsApproved(issue);
                    if (issue.Approved)
                        report.ApprovedExceptions.Add(issue.Id);
                }

                report.Issues.AddRange(issues);
                report.Views.Add(new HmiLayoutViewResult
                {
                    Key = definition.Key,
                    Name = definition.Name,
                    DpiScale = dpiScale,
                    Width = options.Width,
                    Height = options.Height,
                    DensePage = definition.RequiresScrollViewer,
                    HasScrollViewer = hasScrollViewer,
                    VisualElementCount = elementCount,
                    IssueCount = issues.Count,
                    FailureCount = issues.Count(issue => issue.Severity == HmiLayoutIssueSeverity.Fail && !issue.Approved),
                    WarningCount = issues.Count(issue => issue.Severity == HmiLayoutIssueSeverity.Warn && !issue.Approved),
                    ContentBottom = contentBottom,
                    TrailingVoidPercent = options.Height <= 0 ? 0 : Math.Max(0, options.Height - contentBottom) / options.Height * 100.0,
                });
            }
        }

        return report;
    }

    public static HmiLayoutAuditArtifacts WriteAuditArtifacts(HmiLayoutAuditReport report, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(GetRepositoryRoot(), "TestResults")
            : outputRoot.Trim();
        Directory.CreateDirectory(root);

        var jsonPath = Path.Combine(root, "hmi_layout_audit.json");
        var htmlPath = Path.Combine(root, "hmi_layout_audit.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        return new HmiLayoutAuditArtifacts(jsonPath, htmlPath);
    }

    public static IReadOnlyList<HmiViewAuditDefinition> CreateViewDefinitions()
    {
        return new HmiViewAuditDefinition[]
        {
            new("shell-home", "Shell - Home", () => CreateShellRoute("home"), false, ShellRequiredControls),
            new("shell-board-images", "Shell - Board & Images", () => CreateShellRoute("library"), false, ShellRequiredControls),
            new("shell-run-inspection", "Shell - Run Inspection", () => CreateShellRoute("monitor"), false, ShellRequiredControls),
            new("shell-golden-compare", "Shell - Golden Compare", () => CreateShellRoute("compare"), false, ShellRequiredControls),
            new("shell-defect-review", "Shell - Defect Review", () => CreateShellRoute("review"), false, ShellRequiredControls),
            new("shell-recipe-rules", "Shell - Recipe Rules", () => CreateShellRoute("recipe"), false, ShellRequiredControls),
            new("shell-ai-models", "Shell - AI / Models", () => CreateShellRoute("modeltest"), false, ShellRequiredControls),
            new("shell-yield-analytics", "Shell - Yield Analytics", () => CreateShellRoute("spc"), false, ShellRequiredControls),
            new("shell-export-trace", "Shell - Export & Trace", () => CreateShellRoute("reports"), false, ShellRequiredControls),
            new("shell-calibration", "Shell - Calibration", () => CreateShellRoute("calibration"), false, ShellRequiredControls),
            new("shell-3d-profile", "Shell - 3D Profile", () => CreateShellRoute("profile"), false, ShellRequiredControls),
            new("shell-hardware-readiness", "Shell - Hardware Readiness", () => CreateShellRoute("pilot"), false, ShellRequiredControls),
            new("shell-system-settings", "Shell - System Settings", () => CreateShellRoute("settings"), false, ShellRequiredControls),
            new("image-viewer-window", "Image Viewer Window", () => CreateImageViewerRoute(), false, new[] { "ViewerTitleText", "ViewerCriticalText", "ViewerScrollHost", "ImageViewportHost", "ViewerImage", "FitImageButton", "ActualSizeButton", "ZoomOutButton", "ZoomInButton", "SaveImageButton", "CloseViewerButton" }),
            new("home", "AOI Defect Console Home", () => new HomeView { DataContext = new MainViewModel() }, true, new[] { "HomeModuleItems", "HomeAnalyzeItems", "HomeSystemItems", "HomeEngineStatusText", "HomeStatusSummaryText" }),
            new("board-image-library", "Board & Image Library", () => new LibraryView(), false, new[] { "RecordsGrid", "SchemaGrid", "ImportStatusText", "CancelImportButton" }),
            new("main-inspection", "Main Inspection", () => new MonitorView(), false, new[] { "StartInspectionButton", "StopInspectionButton", "NextBoardButton", "SaveResultButton", "OpenInspectionImageViewerButton", "InspectionImageViewport", "DefectGrid", "CalibrationProfileCombo" }),
            new("golden-compare", "Golden Template Compare", () => new CompareView(), false, new[] { "FindingsGrid", "DiffScoreText", "OpenDefectImageViewerButton", "OpenGoldenImageViewerButton", "DefectImageViewport", "GoldenImageViewport", "DefectCanvasViewbox", "GoldenCanvasViewbox" }),
            new("defect-review", "Defect Review", () => new ReviewView(), false, new[] { "QueueGrid", "ReviewCanvasViewbox" }),
            new("recipe-editor", "Recipe Editor", () => new RecipeView(), false, new[] { "RecipeNameText", "RoiGrid", "ViewportCanvas", "RecipeEmptyStateCard" }),
            new("ai-model-test", "AI Model Test", () => new AIModelTestView(), true, new[] { "CancelWorkButton", "ResultsGrid", "FolderPathText", "GroundTruthPathText" }),
            new("yield-analytics", "Yield Analytics", () => new SpcView(), false, new[] { "VerdictBreakdownText", "YieldText", "DbHealthGrid" }),
            new("export-trace", "Export & Trace", () => SelectReportsTab(new ReportsView(), "Export History"), true, new[] { "ExportGrid", "CancelWorkButton" }),
            new("reports-inspection-history", "Reports - Inspection History", () => SelectReportsTab(new ReportsView(), "Inspection History"), true, new[] { "InspectionGrid" }),
            new("reports-review-events", "Reports - Review Events", () => SelectReportsTab(new ReportsView(), "Review / Disposition Events"), true, new[] { "ReviewGrid" }),
            new("reports-audit-trail", "Reports - Audit Trail", () => SelectReportsTab(new ReportsView(), "Audit Trail"), true, new[] { "AuditGrid" }),
            new("reports-ui-stability", "Reports - UI Stability Test", () => SelectReportsTab(new ReportsView(), "UI Stability Test"), true, new[] { "UiStabilityEventsGrid" }),
            new("reports-pilot-issues", "Reports - Pilot Issues", () => SelectReportsTab(new ReportsView(), "Pilot Issues"), true, new[] { "PilotIssuesGrid" }),
            new("settings-basics", "Settings - Basics", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Basics"), true, new[] { "ApplyBtn", "CancelBtn", "ResolutionCombo", "ThemeCombo", "LangCombo", "StorageRootText" }),
            new("settings-qol", "Settings - QOL", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "QOL"), true, new[] { "ApplyBtn", "CancelBtn", "ReviewDefaultText", "DetectionPriorityCombo" }),
            new("settings-ai", "Settings - AI", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "AI"), true, new[] { "InspectionEngineCombo", "ModelRegistryGrid", "ThresholdProfilesGrid", "TestModelBtn" }),
            new("settings-hardware", "Settings - Hardware", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Hardware"), true, new[] { "CameraSourceCombo", "LightingModeCombo", "RunCameraAcceptanceBtn", "RunLightingAcceptanceBtn", "RunRobotCellAcceptanceBtn" }),
            new("settings-traceability", "Settings - Traceability", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Traceability"), true, new[] { "MesModeCombo", "CentralSyncModeCombo", "TestMesRestBtn" }),
            new("settings-evidence", "Settings - Evidence", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Evidence"), true, new[] { "OpenInstallNotesBtn", "OpenGuideBtn", "ExportDiagnosticsBtn", "BackupConfigurationBtn", "ExportSupportBundleBtn", "TrainingStatusText" }),
            new("calibration", "Calibration", () => new CalibrationView(), false, new[] { "PointsGrid", "ProfileCombo", "SampleImagePathText" }),
            new("profile-3d", "3D Profile Viewer", () => new ProfileView(), false, new[] { "RunAcceptanceButton", "CancelAcceptanceButton", "ProfileSourceCombo", "ProfileSourceBadgeText", "ProfileConnectionBadgeText" }),
            new("hardware-readiness", "Hardware Readiness", () => new PilotWizardView(), false, new[] { "ProfileCombo", "StepsGrid", "StatusText", "CurrentStepText", "BlockersText", "NextActionText" }),
            new("factory-readiness", "Factory Readiness", () => SelectReportsTab(new ReportsView(), "Factory Readiness"), true, new[] { "FactoryReadinessGrid" }),
            new("stage1-readiness", "Stage 1 Readiness", () => SelectReportsTab(new ReportsView(), "Stage 1 Readiness"), true, new[] { "Stage1ReadinessGrid", "Stage1ReadinessStatusText", "Stage1MissingEvidenceList", "Stage1NextActionText" }),
            new("standards-quality-checklist", "Standards & Quality Checklist", () => SelectReportsTab(new ReportsView(), "Standards & Quality Checklist"), true, new[] { "StandardsTraceabilityGrid", "StandardsTraceabilitySummaryText" }),
            new("management-dashboard", "Management Dashboard", () => SelectReportsTab(new ReportsView(), "Management Dashboard"), true, new[] { "ManagementDefectGrid", "ManagementRoiGrid", "ManagementModelTrendGrid" }),
            new("model-registry-acceptance", "Model Registry / Model Acceptance", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "AI"), true, new[] { "ModelRegistryGrid", "ModelAcceptanceRunsGrid" }),
            new("mes-queue", "MES Queue", () => SelectReportsTab(new ReportsView(), "MES Upload Queue"), true, new[] { "MesSpoolGrid", "MesQueueStatusFilter" }),
            new("central-sync", "Central Sync", () => SelectReportsTab(new ReportsView(), "Central Evidence Sync"), true, new[] { "CentralSyncGrid" }),
            new("factory-acceptance", "Factory Acceptance Checklist", () => SelectReportsTab(new ReportsView(), "Factory Acceptance"), true, new[] { "FactoryAcceptanceGrid", "FactoryAcceptanceProfileCombo" }),
            new("completion-matrix", "Completion Matrix", () => SelectReportsTab(new ReportsView(), "Evidence Completion"), true, new[] { "CompletionMatrixGrid", "CompletionMatrixSummaryText" }),
            new("installation-notes", "Installation Notes", () => new InstallView(), true, new[] { "RuntimeGrid", "BoundaryGrid" }),
            new("guide", "Guide", () => new GuideView(), true, new[] { "StepsGrid" }),
        };
    }

    public static IReadOnlyList<HmiLayoutIssue> RunElementAuditForTests(FrameworkElement element, double width = 900, double height = 360, double dpiScale = 1.0)
    {
        EnsureApplicationResources();
        MeasureElement(element, width, height);
        var definition = new HmiViewAuditDefinition("test-element", "Test Element", () => element, false, Array.Empty<string>());
        var issues = new List<HmiLayoutIssue>();
        AddTextIssues(definition, element, dpiScale, issues);
        AddButtonIssues(definition, element, dpiScale, issues);
        AddLongTextIssues(definition, element, dpiScale, issues);
        AddInputControlTextIssues(definition, element, dpiScale, issues);
        AddDataGridHeaderIssues(definition, element, dpiScale, issues);
        AddLightBackgroundIssues(definition, element, dpiScale, issues);
        return issues;
    }

    private static readonly string[] ShellRequiredControls =
    {
        "BrandTitleText",
        "StationNameText",
        "HeaderUserText",
        "HeaderRoleText",
        "HomeNavBtn",
        "OperatingModeBannerText",
        "DeploymentProfileBannerText",
        "ActiveAlarmsButton",
        "AccessPanelButton",
        "ActiveAlarmHeaderText",
        "PageContent",
        "FooterRecordCountText",
                "FooterDbRevText",
        "FooterStationText",
    };

    private static FrameworkElement CreateShellRoute(string route)
    {
        // MainWindow is IDisposable (owns cancellation sources). Dispose it on the path where
        // only its extracted content root is returned; transfer ownership (null the local)
        // when the window itself is the audited element.
        MainWindow? window = new MainWindow();
        try
        {
            if (window.DataContext is MainViewModel viewModel)
                viewModel.CurrentPage = route;

            SetText(window, "BrandTitleText", "PCBA AOI REVIEW CONSOLE - CUSTOMER VALIDATION LINE WITH LONG PROGRAM TITLE");
            SetText(window, "StationNameText", "AOI-LIB-LINE-02-STATION-WEST-REVIEW-CELL-WITH-LONG-NAME");
            SetText(window, "StationSubtitleText", "local review console / prototype / customer validation workstation / long subtitle");
            SetText(window, "HeaderUserText", "Engineer01.CustomerValidationShiftLeadWithLongName");
            SetText(window, "HeaderRoleText", "Admin / Shift Lead");
            SetText(window, "ActiveAlarmHeaderText", "none");
            SetText(window, "ActiveAlarmSummaryText", "No active alarms.");
            SetText(window, "FooterRecordCountText", "123456");
            SetText(window, "FooterIndexText", "Images OK 123,456/123,456");
            SetText(window, "FooterDbRevText", "SQLite local revision");
            SetText(window, "FooterStationText", "AOI-LIB-LINE-02-STATION-WEST");
            SetText(window, "FooterIndexText", "Images OK / Long Index Status");
            SetText(window, "FooterDbUpdatedText", "06-23 12:30");
            if (FindByName(window, "ActiveAlarmsPopup") is Popup alarmPopup)
                alarmPopup.IsOpen = false;
            if (FindByName(window, "AccessPanelPopup") is Popup accessPopup)
                accessPopup.IsOpen = false;

            if (window.Content is FrameworkElement root)
            {
                if (root.DataContext is null)
                    root.DataContext = window.DataContext;
                window.Content = null;
                window.Close();
                return root;
            }

            var transferred = window;
            window = null;
            return transferred;
        }
        finally
        {
            window?.Dispose();
        }
    }

    private static FrameworkElement CreateImageViewerRoute()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E1214")), null, new Rect(0, 0, 900, 560));
            context.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#164321")), new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5A8C59")), 3), new Rect(90, 70, 680, 390));
            context.DrawRectangle(Brushes.Transparent, new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334")), 4), new Rect(320, 220, 190, 120));
            context.DrawText(
                new FormattedText(
                    "AOI image viewer audit snapshot",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    28,
                    new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8EEF2")),
                    1.0),
                new Point(120, 480));
        }

        var bitmap = new RenderTargetBitmap(900, 560, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (bitmap.CanFreeze)
            bitmap.Freeze();
        return new AOI_Monitor.ImageViewerWindow("AOI Image Viewer / Audit Snapshot", bitmap);
    }

    private static void SetText(FrameworkElement root, string name, string value)
    {
        if (FindByName(root, name) is TextBlock textBlock)
        {
            textBlock.Text = value;
            textBlock.ToolTip = value;
        }
    }

    private static FrameworkElement SelectSettingsTab(SettingsView view, string header)
    {
        view.Measure(new Size(1920, 1080));
        view.Arrange(new Rect(0, 0, 1920, 1080));
        view.UpdateLayout();
        foreach (var tabControl in Descendants(view).OfType<TabControl>())
        {
            foreach (var item in tabControl.Items.OfType<TabItem>())
            {
                if ((item.Header?.ToString() ?? string.Empty).Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    tabControl.SelectedItem = item;
                    tabControl.UpdateLayout();
                    view.UpdateLayout();
                    return view;
                }
            }
        }

        return view;
    }

    private static FrameworkElement SelectReportsTab(ReportsView view, string header)
    {
        view.Measure(new Size(1920, 1080));
        view.Arrange(new Rect(0, 0, 1920, 1080));
        view.UpdateLayout();
        foreach (var tabControl in Descendants(view).OfType<TabControl>())
        {
            foreach (var item in tabControl.Items.OfType<TabItem>())
            {
                if ((item.Header?.ToString() ?? string.Empty).Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    tabControl.SelectedItem = item;
                    tabControl.UpdateLayout();
                    return view;
                }
            }
        }

        return view;
    }

    private static void AddRequiredControlIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var name in definition.RequiredControlNames)
        {
            if (FindByName(root, name) is not FrameworkElement control)
            {
                issues.Add(CreateIssue(definition, dpiScale, "MissingRequiredControl", name, HmiLayoutIssueSeverity.Fail,
                    $"Important named control '{name}' was not found."));
                continue;
            }

            if (!IsEffectivelyVisible(control) || control.ActualWidth <= 0 || control.ActualHeight <= 0)
            {
                issues.Add(CreateIssue(definition, dpiScale, "UnreachableRequiredControl", name, HmiLayoutIssueSeverity.Fail,
                    $"Important control '{name}' is not visible/reachable. Actual={control.ActualWidth:N0}x{control.ActualHeight:N0}."));
            }
        }
    }

    private static void AddZeroSizeIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (IsTemplateInternal(element))
                continue;
            if (!IsEffectivelyVisible(element) || element is not (TextBlock or Button or Label or TextBox or ComboBox or DataGrid))
                continue;
            if (element is TextBlock textBlock && string.IsNullOrWhiteSpace(textBlock.Text))
                continue;
            if (element.ActualWidth > 0 && element.ActualHeight > 0)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "ZeroSizeControl", Describe(element), HmiLayoutIssueSeverity.Fail,
                $"Visible control has non-positive rendered size. Actual={element.ActualWidth:N0}x{element.ActualHeight:N0}."));
        }
    }

    private static void AddTextIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var textBlock in Descendants(root).OfType<TextBlock>())
        {
            if (IsTemplateInternal(textBlock))
                continue;
            if (!IsEffectivelyVisible(textBlock) || string.IsNullOrWhiteSpace(textBlock.Text) || textBlock.ActualWidth <= 0 || textBlock.ActualHeight <= 0)
                continue;

            if (IsCriticalText(textBlock) && textBlock.FontSize < MinimumReadableFontDip)
            {
                // Fail severity since 2026-08-23: the typography floor was raised to true 14 pt
                // (18.67 DIP) across every shared style and literal, taking this rule from 948
                // warnings to zero. It now blocks regressions instead of merely reporting them.
                issues.Add(CreateIssue(definition, dpiScale, "SmallCriticalText", Describe(textBlock), HmiLayoutIssueSeverity.Fail,
                    $"Critical text is below the 14 pt (18.67 DIP) operator floor. FontSize={textBlock.FontSize:N1} DIP."));
            }

            var desired = MeasureTextBlock(textBlock, textBlock.ActualWidth);
            var tooNarrow = textBlock.TextWrapping == TextWrapping.NoWrap &&
                textBlock.TextTrimming == TextTrimming.None &&
                desired.Width > textBlock.ActualWidth + ClipTolerance;
            var tooShort = desired.Height > textBlock.ActualHeight + ClipTolerance;
            if (!tooNarrow && !tooShort)
                continue;

            var severity = IsCriticalText(textBlock) ? HmiLayoutIssueSeverity.Fail : HmiLayoutIssueSeverity.Warn;
            issues.Add(CreateIssue(definition, dpiScale, "ClippedText", Describe(textBlock), severity,
                $"Text may be clipped. Desired={desired.Width:N0}x{desired.Height:N0}; actual={textBlock.ActualWidth:N0}x{textBlock.ActualHeight:N0}."));
        }
    }

    private static void AddButtonIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var button in Descendants(root).OfType<Button>())
        {
            if (IsTemplateInternal(button))
                continue;
            if (!IsEffectivelyVisible(button))
                continue;

            var target = Describe(button);
            if (IsPrimaryOperatorButton(button) && (button.ActualWidth < 120 || button.ActualHeight < 40))
            {
                issues.Add(CreateIssue(definition, dpiScale, "SmallPrimaryButton", target, HmiLayoutIssueSeverity.Fail,
                    $"Primary operator button is below 120x40 px. Actual={button.ActualWidth:N0}x{button.ActualHeight:N0}."));
            }

            var desired = MeasureButton(button);
            if (desired.Width > button.ActualWidth + ClipTolerance || desired.Height > button.ActualHeight + ClipTolerance)
            {
                issues.Add(CreateIssue(definition, dpiScale, "ClippedButton", target, HmiLayoutIssueSeverity.Fail,
                    $"Button content may be clipped. Desired={desired.Width:N0}x{desired.Height:N0}; actual={button.ActualWidth:N0}x{button.ActualHeight:N0}."));
            }

            // A button whose content is a panel exposes no UIA name unless one is set
            // explicitly, so assistive tech and UI automation announce it as an anonymous
            // button. Mirrors WPF's name derivation: an explicit AutomationProperties.Name,
            // plain string content, or a directly hosted TextBlock all count; anything else
            // needs the explicit property.
            if (string.IsNullOrWhiteSpace(GetEffectiveAutomationName(button)))
            {
                issues.Add(CreateIssue(definition, dpiScale, "UnnamedInteractiveControl", target, HmiLayoutIssueSeverity.Fail,
                    "Button exposes no accessible name (AutomationProperties.Name is empty and the content is not plain text)."));
            }
        }
    }

    private static string GetEffectiveAutomationName(Button button)
    {
        var explicitName = System.Windows.Automation.AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName;

        return button.Content switch
        {
            string text => text,
            TextBlock textBlock => textBlock.Text ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static void AddLongTextIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            var text = element switch
            {
                TextBlock textBlock => textBlock.Text,
                TextBox textBox => textBox.Text,
                ComboBox comboBox => comboBox.SelectionBoxItem?.ToString() ?? comboBox.Text,
                DatePicker datePicker => datePicker.Text,
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(text) || text.Length < 32)
                continue;
            if (!IsEffectivelyVisible(element))
                continue;
            if (!LooksLikePathModelOrMessage(element, text))
                continue;

            var hasTooltip = element.ToolTip is not null;
            var wraps = element is TextBlock { TextWrapping: not TextWrapping.NoWrap } or TextBox { TextWrapping: not TextWrapping.NoWrap };
            var trims = element is TextBlock { TextTrimming: not TextTrimming.None };
            if (hasTooltip || wraps || trims)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "LongTextNoWrapOrTooltip", Describe(element), HmiLayoutIssueSeverity.Warn,
                "Long path/model/message text should wrap, trim with tooltip, or expose a tooltip."));
        }
    }

    private static void AddFixedHeaderRowIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var grid in DescendantsAndSelf(root).OfType<Grid>())
        {
            for (var index = 0; index < grid.RowDefinitions.Count; index++)
            {
                var row = grid.RowDefinitions[index];
                if (!row.Height.IsAbsolute || row.Height.Value > 25 || row.MinHeight >= 32)
                    continue;

                var headerText = grid.Children
                    .OfType<FrameworkElement>()
                    .Where(child => Grid.GetRow(child) == index && IsEffectivelyVisible(child))
                    .SelectMany(DescendantsAndSelf)
                    .OfType<TextBlock>()
                    .FirstOrDefault(textBlock =>
                        IsEffectivelyVisible(textBlock) &&
                        !string.IsNullOrWhiteSpace(textBlock.Text) &&
                        textBlock.FontSize >= 14);
                if (headerText is null)
                    continue;

                issues.Add(CreateIssue(definition, dpiScale, "FixedHeaderRow", Describe(headerText), HmiLayoutIssueSeverity.Fail,
                    $"Panel header row is fixed at {row.Height.Value:N0}px with readable text. Use Auto + MinHeight so DPI/localized text cannot clip."));
            }
        }
    }

    private static void AddTabControlIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var tabControl in DescendantsAndSelf(root).OfType<TabControl>())
        {
            if (!IsEffectivelyVisible(tabControl))
                continue;
            if (tabControl.Items.Count <= 6)
                continue;

            var hasHorizontalScroll = Descendants(tabControl)
                .OfType<ScrollViewer>()
                .Any(scrollViewer => scrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled);
            var hasWrapping = Descendants(tabControl).OfType<WrapPanel>().Any();
            if (hasHorizontalScroll || hasWrapping)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "CrowdedTabsNoScroll", Describe(tabControl), HmiLayoutIssueSeverity.Fail,
                $"TabControl has {tabControl.Items.Count} tabs but no visible wrapping or horizontal scrolling strategy."));
        }
    }

    private static void AddInputControlTextIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (!IsEffectivelyVisible(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            var text = InputText(element);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var availableWidth = Math.Max(0, element.ActualWidth - HorizontalTextChrome(element));
            var desiredWidth = MeasurePlainText(text, element).Width;
            if (desiredWidth <= availableWidth + ClipTolerance)
                continue;

            var hasPolicy = element.ToolTip is not null ||
                element is TextBox { TextWrapping: not TextWrapping.NoWrap };
            if (hasPolicy)
                continue;

            var severity = IsCriticalTextValue(element, text) ? HmiLayoutIssueSeverity.Fail : HmiLayoutIssueSeverity.Warn;
            issues.Add(CreateIssue(definition, dpiScale, "InputTextClippingRisk", Describe(element), severity,
                $"Input text can exceed visible width without a wrap/trim/tooltip policy. Desired={desiredWidth:N0}; available={availableWidth:N0}."));
        }
    }

    private static void AddDataGridHeaderIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var header in Descendants(root).OfType<DataGridColumnHeader>())
        {
            if (!IsEffectivelyVisible(header) || header.ActualWidth <= 0)
                continue;

            var text = HeaderText(header);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (text.Length > 3 && header.ActualWidth < 54)
            {
                issues.Add(CreateIssue(definition, dpiScale, "DataGridHeaderTooNarrow", Describe(header), HmiLayoutIssueSeverity.Fail,
                    $"DataGrid header '{text}' is narrower than the shared minimum and may wrap into unreadable fragments. Actual width={header.ActualWidth:N0}."));
            }

            foreach (var textBlock in HeaderTextBlocks(header))
            {
                if (!IsEffectivelyVisible(textBlock) || textBlock.TextWrapping == TextWrapping.NoWrap)
                    continue;
                var longestWordWidth = LongestWordWidth(textBlock.Text, textBlock);
                if (longestWordWidth <= textBlock.ActualWidth + ClipTolerance)
                    continue;

                issues.Add(CreateIssue(definition, dpiScale, "DataGridHeaderMayWrapInsideWord", Describe(textBlock), HmiLayoutIssueSeverity.Fail,
                    "DataGrid header can wrap inside a word in a column too narrow for its longest token. Use a wider column, an abbreviated header with tooltip, or the shared HMI header style."));

                var singleCharacterWidth = MeasurePlainText("M", textBlock).Width;
                if (textBlock.ActualWidth <= singleCharacterWidth * 2.35)
                {
                    issues.Add(CreateIssue(definition, dpiScale, "DataGridHeaderSingleCharacterLines", Describe(textBlock), HmiLayoutIssueSeverity.Fail,
                        "DataGrid header appears to render as one-character-per-line. Use a wider column, an abbreviated header with tooltip, or the shared HMI header style."));
                }
            }
        }
    }

    private static void AddLightBackgroundIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in DescendantsAndSelf(root).OfType<FrameworkElement>())
        {
            if (!IsEffectivelyVisible(element))
                continue;
            if (element is TextBlock or System.Windows.Shapes.Path or ScrollBar)
                continue;

            var brush = BackgroundBrush(element);
            if (brush is not SolidColorBrush solid || solid.Opacity < 0.25 || solid.Color.A < 64)
                continue;
            if (RelativeLuminance(solid.Color) < LightBackgroundLuminanceThreshold)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "LightBackgroundInDarkHmi", DescribeWithAncestry(element), HmiLayoutIssueSeverity.Fail,
                $"Visible control uses a light/default background ({solid.Color}). Dark HMI pages must use shared high-contrast dark styles."));
        }
    }

    private static void AddShellIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        if (!definition.Key.StartsWith("shell-", StringComparison.OrdinalIgnoreCase))
            return;

        if (FindByName(root, "PageContent") is FrameworkElement pageContent)
        {
            var pageContentTop = pageContent.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
            if (pageContentTop > 260)
            {
                issues.Add(CreateIssue(definition, dpiScale, "ShellChromeTooTall", "PageContent", HmiLayoutIssueSeverity.Fail,
                    $"Shell chrome consumes too much vertical space before workflow content. Page content starts at Y={pageContentTop:N0}; limit=260."));
            }
        }

        if (FindByName(root, "ActiveAlarmsPopup") is Popup { IsOpen: true } &&
            FindByName(root, "ActiveAlarmHeaderText") is TextBlock header &&
            header.Text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(CreateIssue(definition, dpiScale, "ExpandedEmptyAlarmPanel", "ActiveAlarmsPopup", HmiLayoutIssueSeverity.Fail,
                "Active Alarms detail must stay in the flyout and remain closed when no actionable alarm is present."));
        }

    }

    private static Size MeasureTextBlock(TextBlock source, double width)
    {
        var clone = new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            TextWrapping = source.TextWrapping,
            TextTrimming = source.TextTrimming,
            LineHeight = source.LineHeight,
            MaxWidth = source.MaxWidth,
            MinWidth = source.MinWidth,
            Padding = source.Padding,
        };
        clone.Measure(new Size(width, double.PositiveInfinity));
        return clone.DesiredSize;
    }

    private static Size MeasurePlainText(string text, FrameworkElement source)
    {
        var clone = new TextBlock
        {
            Text = text,
            FontFamily = source is Control control ? control.FontFamily : new FontFamily("Segoe UI"),
            FontSize = source is Control controlWithFont ? controlWithFont.FontSize : 14,
            FontWeight = source is Control controlWithWeight ? controlWithWeight.FontWeight : FontWeights.Normal,
            FontStyle = source is Control controlWithStyle ? controlWithStyle.FontStyle : FontStyles.Normal,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
        };
        clone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return clone.DesiredSize;
    }

    private static double LongestWordWidth(string text, TextBlock source)
    {
        var longestWord = text
            .Split(new[] { ' ', '/', '\\', '-', '_', '.', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(word => word.Length)
            .FirstOrDefault() ?? text;
        var clone = new TextBlock
        {
            Text = longestWord,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            TextWrapping = TextWrapping.NoWrap,
        };
        clone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return clone.DesiredSize.Width;
    }

    private static Size MeasureButton(Button source)
    {
        var clone = new Button
        {
            Content = source.Content,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            Padding = source.Padding,
            MinWidth = source.MinWidth,
            MinHeight = source.MinHeight,
        };
        clone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return clone.DesiredSize;
    }

    private static bool IsPrimaryOperatorButton(Button button)
    {
        var actionSized = button.ActualHeight >= 36 || button.MinHeight >= 36;
        if (!actionSized)
            return false;

        if (!string.IsNullOrWhiteSpace(button.Name) &&
            (button.Name.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Export", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var content = button.Content?.ToString() ?? string.Empty;
        return content.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Generate", StringComparison.OrdinalIgnoreCase);
    }

    private static string InputText(FrameworkElement element)
        => element switch
        {
            TextBox textBox when !IsTemplateInternal(textBox) => textBox.Text,
            ComboBox comboBox => comboBox.SelectionBoxItem?.ToString() ?? comboBox.Text,
            DatePicker datePicker => datePicker.Text,
            _ => string.Empty,
        };

    private static double HorizontalTextChrome(FrameworkElement element)
        => element switch
        {
            TextBox textBox => textBox.Padding.Left + textBox.Padding.Right + 10,
            ComboBox => 48,
            DatePicker => 48,
            _ => 16,
        };

    private static string HeaderText(DataGridColumnHeader header)
        => header.Column?.Header switch
        {
            TextBlock textBlock => textBlock.Text,
            { } value => value.ToString() ?? string.Empty,
            _ => header.Content switch
            {
                TextBlock textBlock => textBlock.Text,
                { } value => value.ToString() ?? string.Empty,
                _ => string.Empty,
            },
        };

    private static IEnumerable<TextBlock> HeaderTextBlocks(DataGridColumnHeader header)
    {
        foreach (var textBlock in Descendants(header).OfType<TextBlock>())
            yield return textBlock;

        if (header.Content is TextBlock directTextBlock)
            yield return directTextBlock;
    }

    private static Brush? BackgroundBrush(FrameworkElement element)
        => element switch
        {
            Control control => control.Background,
            Panel panel => panel.Background,
            Border border => border.Background,
            _ => null,
        };

    private static double RelativeLuminance(Color color)
    {
        static double Convert(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.03928
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Convert(color.R) + 0.7152 * Convert(color.G) + 0.0722 * Convert(color.B);
    }

    private static bool IsCriticalTextValue(FrameworkElement element, string text)
    {
        var name = element.Name ?? string.Empty;
        return name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Verdict", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ALARM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
            ContainsNgToken(text) ||
            text.Contains("FAIL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the text carries the NG verdict as a standalone token. The previous substring
    /// match flagged every word containing the "ng" bigram ("Settings", "Lighting", "Missing"),
    /// which classified roughly a third of all page text as verdict-critical and made the
    /// criticality-driven severities meaningless.
    /// </summary>
    private static bool ContainsNgToken(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text ?? string.Empty, @"\bNG\b");

    private static bool IsTemplateInternal(FrameworkElement element)
        => element.TemplatedParent is not null ||
           (!string.IsNullOrWhiteSpace(element.Name) && element.Name.StartsWith("PART_", StringComparison.Ordinal));

    private static bool IsEffectivelyVisible(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = ParentOf(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
            if (current is Expander { IsExpanded: false } expander &&
                !ReferenceEquals(element, expander) &&
                !IsInsideExpanderHeader(element, expander))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideExpanderHeader(DependencyObject element, Expander expander)
    {
        if (expander.Header is not DependencyObject headerRoot)
            return false;
        for (DependencyObject? current = element; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, headerRoot))
                return true;
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
    {
        DependencyObject? visualParent = null;
        if (element is Visual or Visual3D)
            visualParent = VisualTreeHelper.GetParent(element);

        return visualParent ?? LogicalTreeHelper.GetParent(element);
    }

    private static bool IsCriticalText(TextBlock textBlock)
    {
        var name = textBlock.Name ?? string.Empty;
        var text = textBlock.Text ?? string.Empty;
        return name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Summary", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ALARM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
            ContainsNgToken(text) ||
            text.Contains("FAIL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePathModelOrMessage(FrameworkElement element, string text)
    {
        var name = element.Name ?? string.Empty;
        return name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\\', StringComparison.Ordinal) ||
            text.Contains('/', StringComparison.Ordinal) ||
            text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("message", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bottom edge of the lowest visible, non-trivial content in the viewport. Backgrounds and
    /// decorative containers stretch to the full page by design, so only content-bearing
    /// elements count (text, buttons, inputs, tables, images, shapes). Content inside a
    /// ScrollViewer is clamped to the viewport, and pages that fill or overflow the viewport
    /// report a bottom equal to the viewport height (zero trailing void).
    /// </summary>
    private static double MeasureContentBottom(FrameworkElement root, double viewportHeight)
    {
        var bottom = 0.0;
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (IsTemplateInternal(element) || !IsEffectivelyVisible(element))
                continue;
            // Panels and image wells are content too: a card or viewport that stretches to the
            // bottom edge is a designed surface, not dead space. Dead space is exposed *page*
            // background below the lowest panel or control.
            var isContentContainer = element is Border { Background: SolidColorBrush { Color.A: > 0 } } border &&
                border.Background != Brushes.Transparent;
            if (!isContentContainer &&
                element is not (TextBlock or Button or Label or TextBox or ComboBox or DataGrid or CheckBox or Image or ProgressBar or ListBox or TabControl or DatePicker or System.Windows.Shapes.Shape))
                continue;
            if (element is TextBlock textBlock && string.IsNullOrWhiteSpace(textBlock.Text))
                continue;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            double elementBottom;
            try
            {
                elementBottom = element.TransformToAncestor(root).Transform(new Point(0, element.ActualHeight)).Y;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            bottom = Math.Max(bottom, Math.Min(elementBottom, viewportHeight));
            if (bottom >= viewportHeight)
                return viewportHeight;
        }

        return bottom;
    }

    private static void MeasureElement(FrameworkElement element, double width, double height)
    {
        if (element is Window { Content: FrameworkElement content })
        {
            element.Width = width;
            element.Height = height;
            element.Visibility = Visibility.Visible;
            content.Width = width;
            content.Height = height;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            element.UpdateLayout();
            return;
        }

        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static DependencyObject? FindByName(FrameworkElement root, string name)
    {
        if (root.Name == name)
            return root;

        if (Descendants(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name) is { } visualMatch)
            return visualMatch;

        return root.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(root) as DependencyObject;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        => Descendants(root, new HashSet<DependencyObject>());

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root, ISet<DependencyObject> visited)
    {
        var count = root is Visual or Visual3D
            ? VisualTreeHelper.GetChildrenCount(root)
            : 0;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (!visited.Add(child))
                continue;

            yield return child;
            foreach (var descendant in Descendants(child, visited))
                yield return descendant;
        }

        foreach (var child in LogicalChildren(root))
        {
            if (!visited.Add(child))
                continue;

            yield return child;
            foreach (var descendant in Descendants(child, visited))
                yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        if (root is Window { Content: DependencyObject windowContent })
            yield return windowContent;

    }

    private static IEnumerable<DependencyObject> DescendantsAndSelf(DependencyObject root)
    {
        yield return root;
        foreach (var descendant in Descendants(root))
            yield return descendant;
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : $"{element.GetType().Name}#{element.Name}";
        return element switch
        {
            Button { Content: string content } => $"{name} \"{Trim(content)}\"",
            TextBlock { Text.Length: > 0 } textBlock => $"{name} \"{Trim(textBlock.Text)}\"",
            TextBox { Text.Length: > 0 } textBox => $"{name} \"{Trim(textBox.Text)}\"",
            _ => name,
        };
    }

    private static string DescribeWithAncestry(FrameworkElement element)
    {
        var parts = new List<string> { Describe(element) };
        var parent = VisualTreeHelper.GetParent(element);
        while (parent is not null && parts.Count < 5)
        {
            if (parent is FrameworkElement frameworkElement)
                parts.Add(Describe(frameworkElement));
            parent = VisualTreeHelper.GetParent(parent);
        }

        return string.Join(" <- ", parts);
    }

    private static HmiLayoutIssue CreateIssue(HmiViewAuditDefinition definition, double dpiScale, string issueType, string target, HmiLayoutIssueSeverity severity, string message)
    {
        var normalizedTarget = NormalizeTarget(target);
        return new HmiLayoutIssue
        {
            Id = $"{definition.Key}|{dpiScale.ToString("0.##", CultureInfo.InvariantCulture)}|{issueType}|{normalizedTarget}",
            ViewKey = definition.Key,
            ViewName = definition.Name,
            DpiScale = dpiScale,
            IssueType = issueType,
            Target = target,
            Severity = severity,
            Message = message,
        };
    }

    private static string NormalizeTarget(string target)
        => string.Concat((target ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '#' ? ch : '_'));

    private static string Trim(string value)
        => value.Length <= 72 ? value : value[..69] + "...";

    private static HmiLayoutApprovedExceptions LoadApprovedExceptions(string? path)
    {
        var exceptionPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(GetRepositoryRoot(), "Tools", "quality-gates", "hmi_layout_approved_exceptions.json")
            : path.Trim();
        if (!File.Exists(exceptionPath))
            return new HmiLayoutApprovedExceptions();

        var file = JsonSerializer.Deserialize<HmiLayoutApprovedExceptionFile>(File.ReadAllText(exceptionPath), JsonOptions)
            ?? new HmiLayoutApprovedExceptionFile();
        if (!file.SchemaVersion.Equals("hmi-layout-approved-exceptions/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported HMI layout approved exceptions schema.");

        foreach (var exception in file.Exceptions)
        {
            if (HasWildcard(exception.ViewKey) || HasWildcard(exception.IssueType) || HasWildcard(exception.Target))
                throw new InvalidDataException("HMI layout approved exceptions must be exact and cannot contain wildcards.");
            if (string.IsNullOrWhiteSpace(exception.ViewKey) ||
                string.IsNullOrWhiteSpace(exception.IssueType) ||
                string.IsNullOrWhiteSpace(exception.Target) ||
                string.IsNullOrWhiteSpace(exception.Reason))
            {
                throw new InvalidDataException("HMI layout approved exceptions require viewKey, issueType, target, and reason.");
            }
        }

        return new HmiLayoutApprovedExceptions(file.Exceptions);
    }

    private static bool HasWildcard(string value)
        => value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

    private static string BuildHtml(HmiLayoutAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>HMI Layout Audit</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%;margin:14px 0}td,th{border:1px solid #cfd8df;padding:6px 8px;text-align:left;vertical-align:top}.Pass{color:#176b3a}.Fail{color:#a12626}.Warn{color:#8a5a00}.approved{color:#66727c}</style></head><body>");
        sb.AppendLine($"<h1>HMI Layout Audit <span class=\"{(report.Passed ? "Pass" : "Fail")}\">{(report.Passed ? "PASS" : "FAIL")}</span></h1>");
        sb.AppendLine($"<p>Generated UTC: {report.GeneratedAtUtc:O}<br>Window: {report.MinimumWindowWidth:N0}x{report.MinimumWindowHeight:N0}<br>DPI profiles: 100%, 125%, 150%</p>");
        sb.AppendLine("<h2>Views</h2><table><tr><th>View</th><th>DPI</th><th>Scroll</th><th>Elements</th><th>Dead space</th><th>Failures</th><th>Warnings</th></tr>");
        foreach (var view in report.Views)
            sb.AppendLine($"<tr><td>{Escape(view.Name)}</td><td>{view.DpiScale:P0}</td><td>{(view.HasScrollViewer ? "Yes" : "No")}</td><td>{view.VisualElementCount}</td><td>{view.TrailingVoidPercent:N1}%</td><td>{view.FailureCount}</td><td>{view.WarningCount}</td></tr>");
        sb.AppendLine("</table><h2>Issues</h2><table><tr><th>Status</th><th>View</th><th>DPI</th><th>Type</th><th>Target</th><th>Message</th></tr>");
        foreach (var issue in report.Issues)
        {
            var status = issue.Approved ? "Approved" : issue.Severity.ToString();
            var css = issue.Approved ? "approved" : issue.Severity.ToString();
            sb.AppendLine($"<tr><td class=\"{css}\">{status}</td><td>{Escape(issue.ViewName)}</td><td>{issue.DpiScale:P0}</td><td>{Escape(issue.IssueType)}</td><td>{Escape(issue.Target)}</td><td>{Escape(issue.Message)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate repository root.");

        return directory.FullName;
    }
}

public sealed record HmiLayoutAuditArtifacts(string JsonPath, string HtmlPath);

public sealed record HmiViewAuditDefinition(
    string Key,
    string Name,
    Func<FrameworkElement> Factory,
    bool RequiresScrollViewer,
    IReadOnlyList<string> RequiredControlNames);

internal sealed class HmiLayoutApprovedExceptionFile
{
    public string SchemaVersion { get; set; } = "hmi-layout-approved-exceptions/v1";
    public List<HmiLayoutApprovedException> Exceptions { get; set; } = new();
}

internal sealed class HmiLayoutApprovedException
{
    public string ViewKey { get; set; } = string.Empty;
    public double? DpiScale { get; set; }
    public string IssueType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

internal sealed class HmiLayoutApprovedExceptions
{
    private readonly IReadOnlyList<HmiLayoutApprovedException> _exceptions;

    public HmiLayoutApprovedExceptions()
        : this(Array.Empty<HmiLayoutApprovedException>())
    {
    }

    public HmiLayoutApprovedExceptions(IReadOnlyList<HmiLayoutApprovedException> exceptions)
    {
        _exceptions = exceptions;
    }

    public bool IsApproved(HmiLayoutIssue issue)
        => _exceptions.Any(exception =>
            exception.ViewKey.Equals(issue.ViewKey, StringComparison.OrdinalIgnoreCase) &&
            exception.IssueType.Equals(issue.IssueType, StringComparison.OrdinalIgnoreCase) &&
            exception.Target.Equals(issue.Target, StringComparison.OrdinalIgnoreCase) &&
            (exception.DpiScale is null || Math.Abs(exception.DpiScale.Value - issue.DpiScale) < 0.001));
}
