using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class UiNavigationSmokeTests : IDisposable
{
    private readonly string _root;

    public UiNavigationSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_UiSmoke_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("UiSmokeAdmin", UserRole.Admin);
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
            Trace.WriteLine($"Test cleanup failed for {nameof(UiNavigationSmokeTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void EveryFeatureSectionConstructsOnStaWithoutSlowStartup()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var viewModel = new MainViewModel();
            var sections = new (string Name, Func<FrameworkElement> Factory)[]
            {
                ("MainWindow shell", () => new MainWindow()),
                ("Home", () => new HomeView { DataContext = new MainViewModel() }),
                ("Board & Images", () => new LibraryView()),
                ("Run Inspection", () => new MonitorView()),
                ("Golden Compare", () => new CompareView()),
                ("Defect Review", () => new ReviewView()),
                ("Recipe Rules", () => new RecipeView()),
                ("AI / Models", () => new AIModelTestView()),
                ("Yield Analytics", () => new SpcView()),
                ("Export & Trace", () => new ReportsView()),
                ("Readiness & QA", () => new ReadinessQaView()),
                ("Calibration", () => new CalibrationView()),
                ("3D Profile", () => new ProfileView()),
                ("Hardware Readiness", () => new PilotWizardView()),
                ("System Settings", () => new SettingsView(viewModel)),
                ("Installation Notes", () => new InstallView()),
                ("Guide", () => new GuideView()),
                ("Planned Stage", () => new PlannedStageView("Planned", "Smoke-test placeholder.")),
                ("First Run Wizard", () => new FirstRunWizardView()),
            };

            foreach (var section in sections)
            {
                var stopwatch = Stopwatch.StartNew();
                var element = section.Factory();
                stopwatch.Stop();

                Assert.NotNull(element);
                Assert.True(
                    stopwatch.ElapsedMilliseconds < 3_000,
                    $"{section.Name} constructor took {stopwatch.ElapsedMilliseconds:N0} ms.");

                if (element is Window window)
                    window.Close();
            }
        });
    }

    [Fact]
    public void MajorPagesAreScrollableOrHaveFixedScreenJustification()
    {
        var viewFiles = new Dictionary<string, string>
        {
            ["Main Inspection"] = @"AOI_Monitor\Views\MonitorView.xaml",
            ["Recipe Editor"] = @"AOI_Monitor\Views\RecipeView.xaml",
            ["AI Model Test"] = @"AOI_Monitor\Views\AIModelTestView.xaml",
            ["Export & Trace"] = @"AOI_Monitor\Views\ReportsView.xaml",
            ["Settings"] = @"AOI_Monitor\Views\SettingsView.xaml",
            ["Calibration"] = @"AOI_Monitor\Views\CalibrationView.xaml",
            ["3D Profile Viewer"] = @"AOI_Monitor\Views\ProfileView.xaml",
            ["Factory / Management Dashboards"] = @"AOI_Monitor\Views\ReadinessQaView.xaml",
            ["Model Registry / Acceptance"] = @"AOI_Monitor\Views\SettingsView.xaml",
            ["MES / Central Sync / Hardware Acceptance"] = @"AOI_Monitor\Views\SettingsView.xaml",
        };

        var fixedScreenJustifications = new Dictionary<string, string>
        {
            ["Main Inspection"] = "Fixed HMI viewport with DataGrid scrolling and proportional alarm panel; operator-critical actions stay in a fixed command row.",
            ["Recipe Editor"] = "Fixed editor canvas viewport; ROI table scrolls and commands remain in fixed header/sidebar zones.",
            ["Calibration"] = "Fixed image calibration viewport; point table scrolls and save/reload commands remain in the header.",
            ["3D Profile Viewer"] = "Fixed visual profile viewport; selected detail/status regions wrap and table-like content is not vertically dense.",
            ["Export & Trace"] = "Fixed page grid after the Readiness & QA split: the six log DataGrids scroll internally and the star row keeps the tab strip and footer reachable without page-level scrolling.",
        };

        foreach (var (page, relativePath) in viewFiles)
        {
            var xaml = XDocument.Load(Path.Combine(GetRepositoryRoot(), relativePath));
            var hasScrollViewer = xaml.Descendants().Any(node => node.Name.LocalName == nameof(ScrollViewer));
            Assert.True(
                hasScrollViewer || fixedScreenJustifications.ContainsKey(page),
                $"{page} must contain a ScrollViewer or have a fixed-screen layout justification.");
        }
    }

    [Fact]
    public void OperatorCriticalButtonsAreDiscoverableByName()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var monitor = new MonitorView();

            foreach (var name in new[] { "StartInspectionButton", "StopInspectionButton", "NextBoardButton", "SaveResultButton" })
            {
                var button = Assert.IsType<Button>(monitor.FindName(name));
                Assert.True(button.MinHeight >= 30 || button.ActualHeight >= 30, $"{name} should remain touch-readable at factory DPI.");
            }
        });
    }

    [Fact]
    public void SettingsEvidenceTabKeepsSupportPagesReachable()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var settings = new SettingsView(new MainViewModel());
            settings.Measure(new Size(1920, 1080));
            settings.Arrange(new Rect(0, 0, 1920, 1080));
            settings.UpdateLayout();

            var installNotes = Assert.IsType<Button>(settings.FindName("OpenInstallNotesBtn"));
            var guide = Assert.IsType<Button>(settings.FindName("OpenGuideBtn"));

            Assert.True(installNotes.MinWidth >= 120, "Installation Notes support route should keep an operator-readable action.");
            Assert.True(guide.MinWidth >= 120, "Guide support route should keep an operator-readable action.");
        });
    }

    [Fact]
    public void LayoutStressUtilityCoversRequiredFactoryScreens()
    {
        var names = LayoutStressTestService.MajorViewNames;
        foreach (var required in new[]
        {
            "Home",
            "Board & Images",
            "Run Inspection",
            "Golden Compare",
            "Defect Review",
            "Recipe Rules",
            "AI / Models",
            "Yield Analytics",
            "Export & Trace",
            "Readiness & QA",
            "Management Dashboard",
            "Factory Readiness",
            "Standards & Quality Checklist",
            "Factory Acceptance Checklist",
            "Model Registry / Model Acceptance",
            "MES / Central Sync / Hardware Acceptance",
            "Calibration",
            "3D Profile",
            "Hardware Readiness",
            "System Settings",
            "Installation Notes",
            "Guide",
        })
        {
            Assert.Contains(required, names);
        }
    }

    [Fact]
    public void PerformanceLoggerRecordsSlowPageLoad()
    {
        UiPerformanceMonitorService.ClearForTests();

        UiPerformanceMonitorService.RecordSlowPageLoad("Reports", 425);

        var entry = Assert.Single(UiPerformanceMonitorService.RecentEvents);
        Assert.Equal("PAGE_LOAD", entry.Category);
        Assert.Equal("Reports", entry.Name);
        Assert.True(entry.DurationMilliseconds >= 425);
        Assert.Contains("Slow page load", entry.Message);
    }

    [Fact]
    public async Task HeavyServiceAsyncWrappersHonorPreCanceledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => FactoryReadinessService.EvaluateAsync(cancellationToken: cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ManagementDashboardService.BuildAsync(cancellationToken: cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => CompletionAssessmentService.AssessAsync(cts.Token));
    }

    [Fact]
    public void MajorViewConstructorsDoNotCallBlockingRefreshOrDatabaseScans()
    {
        var guardedFiles = Directory.EnumerateFiles(
                Path.Combine(GetRepositoryRoot(), "AOI_Monitor", "Views"),
                "*.xaml.cs",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blockedCalls = new[]
        {
            "FactoryReadinessService.Evaluate(",
            "ManagementDashboardService.Build(",
            "CustomerValidationPackageService.CreatePackage",
            "ModelAcceptanceService.RunAcceptance",
            "AoiDatabase.GetInspectionHistory",
            "Directory.EnumerateFiles",
            "File.ReadAllBytes",
        };

        foreach (var path in guardedFiles)
        {
            var source = File.ReadAllText(path);
            var typeName = Path.GetFileName(path).Replace(".xaml.cs", string.Empty, StringComparison.OrdinalIgnoreCase);
            var constructorBody = ExtractConstructorBody(source, typeName);

            foreach (var blockedCall in blockedCalls)
            {
                Assert.True(
                    !constructorBody.Contains(blockedCall, StringComparison.Ordinal),
                    $"{Path.GetFileName(path)} constructor must not call heavy work '{blockedCall}'. Move it to OnNavigatedToAsync/RefreshAsync with cancellation.");
            }
        }
    }

    internal static void EnsureApplicationResourcesForTests() => EnsureApplicationResources();

    internal static void RunOnStaForTests(Action action) => RunOnSta(action);

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
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

    private static string ExtractConstructorBody(string source, string typeName)
    {
        var signature = $"public {typeName}(";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var openBrace = source.IndexOf('{', start);
        if (openBrace < 0)
            return string.Empty;

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[openBrace..(i + 1)];
            }
        }

        return source[openBrace..];
    }
}
