using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class UiNavigationPerformanceTests : IDisposable
{
    private readonly string _root;

    public UiNavigationPerformanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_UiNavigationPerformance_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("NavigationPerfAdmin", UserRole.Admin);
        FirstRunSettingsService.ResetForTests();
        FirstRunSettingsService.MarkCompleted();
        UiPreferencesService.ResetForTests();
        UiPerformanceMonitorService.ClearForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
        UiPerformanceMonitorService.ClearForTests();
        FirstRunSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Test cleanup failed for {nameof(UiNavigationPerformanceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void MainShellCyclesMajorPagesWithinNavigationThresholds()
    {
        var report = RunOnSta(() =>
        {
            EnsureApplicationResources();
            var shell = new MainWindow();
            var pages = new (string Key, Func<FrameworkElement> Factory)[]
            {
                ("home", () => new HomeView()),
                ("library", () => new LibraryView()),
                ("monitor", () => new MonitorView()),
                ("compare", () => new CompareView()),
                ("review", () => new ReviewView()),
                ("recipe", () => new RecipeView()),
                ("modeltest", () => new AIModelTestView()),
                ("spc", () => new SpcView()),
                ("reports", () => new ReportsView()),
                ("readiness", () => new ReadinessQaView()),
                ("settings", () => new SettingsView(new MainViewModel())),
                ("calibration", () => new CalibrationView()),
                ("profile", () => new ProfileView()),
                ("pilot", () => new PilotWizardView()),
                ("install", () => new InstallView()),
                ("guide", () => new GuideView()),
            };

            var report = new UiNavigationPerformanceReport();
            var cache = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in pages)
            {
                var construction = Stopwatch.StartNew();
                var element = page.Factory();
                construction.Stop();
                cache[page.Key] = element;
                UiPerformanceMonitorService.RecordPageConstruction(page.Key, construction.ElapsedMilliseconds);
            }

            for (var pass = 1; pass <= 3; pass++)
            {
                foreach (var page in pages)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var element = cache[page.Key];
                    UiPerformanceMonitorService.RecordNavigationVisualResponse(page.Key, stopwatch.ElapsedMilliseconds);
                    MeasureElement(element);
                    stopwatch.Stop();
                    UiPerformanceMonitorService.RecordCachedPageSwitch(page.Key, stopwatch.ElapsedMilliseconds);
                    report.Switches.Add(new UiNavigationSwitchResult(page.Key, pass, stopwatch.ElapsedMilliseconds, page.Key));
                }
            }

            report.Events.AddRange(UiPerformanceMonitorService.RecentEvents);
            shell.Close();
            WriteReport(report);
            return report;
        });

        var failures = new List<string>();
        failures.AddRange(report.Events
            .Where(item => item.Category == "NAVIGATION_VISUAL_RESPONSE" &&
                item.DurationMilliseconds > UiPerformanceMonitorService.MenuResponseWarningMilliseconds)
            .Select(item => $"Visual response {item.Name}: {item.DurationMilliseconds} ms"));
        failures.AddRange(report.Events
            .Where(item => item.Category == "CACHED_PAGE_SWITCH" &&
                item.DurationMilliseconds > UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds)
            .Select(item => $"Cached switch {item.Name}: {item.DurationMilliseconds} ms"));
        failures.AddRange(report.Events
            .Where(item => item.Category == "PAGE_CONSTRUCTION" &&
                item.DurationMilliseconds > 1_500)
            .Select(item => $"Constructor {item.Name}: {item.DurationMilliseconds} ms"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RapidShellNavigationLeavesNoOverlayAndKeepsRouteStateStable()
    {
        var result = RunOnSta(() =>
        {
            EnsureApplicationResources();
            UiPerformanceMonitorService.ClearForTests();
            var shell = new MainWindow();
            try
            {
                shell.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                WaitForDispatcherTask(InvokeSwitchPage(shell, "home"));

                var viewModel = Assert.IsType<MainViewModel>(shell.DataContext);
                var routes = FocusedWorkflowRoutes;
                var switchResults = new List<UiNavigationSwitchResult>();

                for (var pass = 1; pass <= 2; pass++)
                {
                    foreach (var route in routes)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var navigationTask = InvokeSwitchPage(shell, route);
                        WaitUntil(
                            () => string.Equals(GetPrivateField<string>(shell, "_activePageKey"), route, StringComparison.OrdinalIgnoreCase),
                            TimeSpan.FromSeconds(8),
                            route);
                        stopwatch.Stop();
                        WaitForDispatcherTask(navigationTask);
                        viewModel.CurrentPage = route;
                        PumpDispatcher(TimeSpan.FromMilliseconds(35));

                        var overlay = GetPrivateField<FrameworkElement>(shell, "LoadingOverlay");
                        var pageTitle = GetPrivateField<TextBlock>(shell, "PageTitleText").Text;
                        // Home has no module-map tile (the map lives on Home), so no nav state to assert.
                        var activeNav = viewModel.NavPages.SingleOrDefault(page => page.Key == route);
                        var content = GetPrivateField<ContentControl>(shell, "PageContent").Content;

                        Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                        if (activeNav is not null)
                            Assert.True(activeNav.IsActive, $"Active nav state was not set for {route}.");
                        Assert.Equal(ExpectedPageTitles[route], pageTitle);
                        Assert.NotNull(content);
                        switchResults.Add(new UiNavigationSwitchResult(route, pass, stopwatch.ElapsedMilliseconds, route));
                    }
                }

                var cacheBeforeDuplicate = GetPrivateField<Dictionary<string, UserControl>>(shell, "_pageCache").Count;
                var firstDuplicate = InvokeSwitchPage(shell, "settings");
                var secondDuplicate = InvokeSwitchPage(shell, "settings");
                WaitForDispatcherTask(firstDuplicate);
                WaitForDispatcherTask(secondDuplicate);
                PumpDispatcher(TimeSpan.FromMilliseconds(250));

                var finalOverlay = GetPrivateField<FrameworkElement>(shell, "LoadingOverlay");
                var cacheAfterDuplicate = GetPrivateField<Dictionary<string, UserControl>>(shell, "_pageCache").Count;
                var activePage = GetPrivateField<string>(shell, "_activePageKey");
                var duplicateIgnoredEvents = UiPerformanceMonitorService.RecentEvents.Count(item =>
                    item.Category.Equals("NAVIGATION_DUPLICATE_IGNORED", StringComparison.OrdinalIgnoreCase));
                var averageSwitchMs = switchResults.Count == 0 ? 0 : switchResults.Average(item => item.DispatcherSwitchMilliseconds);

                return new RapidNavigationResult(
                    activePage,
                    finalOverlay.Visibility,
                    cacheBeforeDuplicate,
                    cacheAfterDuplicate,
                    duplicateIgnoredEvents,
                    averageSwitchMs);
            }
            finally
            {
                shell.Close();
            }
        });

        Assert.Equal("settings", result.ActivePage);
        Assert.Equal(Visibility.Collapsed, result.LoadingOverlayVisibility);
        Assert.Equal(result.CacheBeforeDuplicate, result.CacheAfterDuplicate);
        Assert.True(result.DuplicateIgnoredEvents > 0, "Duplicate route navigation should be ignored and recorded.");
        Assert.True(
            result.AverageSwitchMilliseconds <= UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds,
            $"Average route switch was {result.AverageSwitchMilliseconds:F1} ms, above threshold {UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds} ms.");
    }

    private static readonly string[] FocusedWorkflowRoutes =
    [
        "home",
        "library",
        "monitor",
        "compare",
        "review",
        "recipe",
        "modeltest",
        "spc",
        "reports",
        "readiness",
        "calibration",
        "profile",
        "pilot",
        "settings",
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedPageTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = "AOI DEFECT CONSOLE / HOME",
        ["library"] = "BOARD & IMAGE MANAGEMENT / IMAGE LIBRARY",
        ["monitor"] = "RUN INSPECTION / LOT EXECUTION",
        ["compare"] = "DEFECT REVIEW / GOLDEN TEMPLATE COMPARE",
        ["review"] = "DEFECT REVIEW / EVIDENCE & DISPOSITION",
        ["recipe"] = "RECIPE RULES / ROI, MASKS, TOLERANCES",
        ["modeltest"] = "AI / MODEL EVIDENCE AND FALSE-CALL CONTROL",
        ["spc"] = "YIELD ANALYTICS / SPC, PARETO, TRENDS",
        ["reports"] = "EXPORT & TRACE / PACKAGE, AUDIT, MES",
        ["readiness"] = "READINESS & QA / STAGE GATES, CHECKLISTS, ACCEPTANCE",
        ["calibration"] = "CALIBRATION / 2D TRANSFORM AND STAGE 2 PREP",
        ["profile"] = "3D PROFILE VIEWER / SAMPLE DATA MODE",
        ["pilot"] = "CUSTOMER PILOT WIZARD / STAGE 1-2 EVIDENCE",
        ["settings"] = "SYSTEM SETTINGS / DISPLAY, STORAGE, SECURITY",
    };

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static void MeasureElement(FrameworkElement element)
    {
        element.Width = 1920;
        element.Height = 1080;
        element.Measure(new Size(1920, 1080));
        element.Arrange(new Rect(0, 0, 1920, 1080));
        element.UpdateLayout();
    }

    private static Task InvokeSwitchPage(MainWindow shell, string route)
    {
        var method = typeof(MainWindow).GetMethod("SwitchPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SwitchPageAsync was not found.");
        return (Task?)method.Invoke(shell, new object[] { route })
            ?? throw new InvalidOperationException("SwitchPageAsync did not return a task.");
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)field.GetValue(instance)!;
    }

    private static void WaitForDispatcherTask(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var timedOut = false;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timedOut = true;
            frame.Continue = false;
        };
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        if (timedOut)
            throw new TimeoutException("Shell navigation task did not complete.");

        task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string route)
    {
        if (condition())
            return;

        var timedOut = false;
        var frame = new DispatcherFrame();
        var timeoutTimer = new DispatcherTimer { Interval = timeout };
        var pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timeoutTimer.Tick += (_, _) =>
        {
            timedOut = true;
            frame.Continue = false;
        };
        pollTimer.Tick += (_, _) =>
        {
            if (condition())
                frame.Continue = false;
        };
        timeoutTimer.Start();
        pollTimer.Start();
        Dispatcher.PushFrame(frame);
        pollTimer.Stop();
        timeoutTimer.Stop();

        if (timedOut)
            throw new TimeoutException($"Shell route '{route}' did not become active before the timeout.");
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
            ExceptionDispatchInfo.Capture(exception).Throw();

        return result!;
    }

    private static void ShutdownApplicationIfOwnedByCurrentThread()
    {
        try
        {
            if (Application.Current?.Dispatcher.CheckAccess() == true)
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }

    private static void WriteReport(UiNavigationPerformanceReport report)
    {
        var root = Path.Combine(GetRepositoryRoot(), "TestResults");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "ui_navigation_performance.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
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

    private sealed class UiNavigationPerformanceReport
    {
        public string SchemaVersion { get; } = "ui-navigation-performance/v1";
        public DateTime GeneratedAtUtc { get; } = DateTime.UtcNow;
        public long VisualResponseThresholdMilliseconds { get; } = UiPerformanceMonitorService.MenuResponseWarningMilliseconds;
        public long CachedSwitchThresholdMilliseconds { get; } = UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds;
        public List<UiNavigationSwitchResult> Switches { get; } = new();
        public List<UiPerformanceEvent> Events { get; } = new();
    }

    private sealed record UiNavigationSwitchResult(
        string RequestedPage,
        int Pass,
        long DispatcherSwitchMilliseconds,
        string ActivePage);

    private sealed record RapidNavigationResult(
        string ActivePage,
        Visibility LoadingOverlayVisibility,
        int CacheBeforeDuplicate,
        int CacheAfterDuplicate,
        int DuplicateIgnoredEvents,
        double AverageSwitchMilliseconds);
}
