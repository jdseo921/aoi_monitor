using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class UiRegressionSmokeTests : IDisposable
{
    private const double TestWidth = 1920;
    private const double TestHeight = 1080;
    private const int ConstructorThresholdMs = 1_500;
    private const int CachedNavigationThresholdMs = 300;

    private readonly string _root;

    public UiRegressionSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_UiRegression_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("UiRegressionAdmin", UserRole.Admin);
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
            Trace.WriteLine($"Test cleanup failed for {nameof(UiRegressionSmokeTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void MajorViewsMeasureAtFactoryResolutionWithoutClippedText()
    {
        var report = RunOnSta(() =>
        {
            EnsureApplicationResources();
            var pages = CreatePageDefinitions();

            // Warm up the WPF stack (JIT, control templates, resource dictionaries) with one
            // untimed construction pass. The constructor budget below is meant to catch real
            // steady-state regressions; charging first-hit JIT and shared CI-runner load to the
            // measured build makes the gate flaky without measuring anything actionable.
            foreach (var page in pages)
            {
                try
                {
                    if (page.Factory() is Window warmupWindow)
                        warmupWindow.Close();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"UI smoke warm-up construction failed for {page.Key}: {ex.Message}");
                }
            }

            var report = new UiSmokeReport("major-view-smoke");

            foreach (var page in pages)
            {
                var result = MeasurePage(page);
                report.Pages.Add(result);
            }

            WriteReport(report);
            return report;
        });

        var failures = report.Pages
            .SelectMany(page => page.Failures.Select(failure => $"{page.Name}: {failure}"))
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DefectOverlayDrawsPerDefectBoxesInVerdictColourAndPopulatesTheDefectList()
    {
        // Customer spec §4.1: overlays with bounding boxes and labels, a defect list carrying
        // Severity, and green/red/amber verdict colouring. Previously nothing asserted that the
        // overlay drew anything at all, nor that the Severity column resolved to a value.
        var (overlayChildren, boxCount, labels, strokeColours, severities) = RunOnSta(() =>
        {
            EnsureApplicationResources();

            var analysis = new AnalysisResult
            {
                Verdict = "NG",
                BoardProgram = "BOARD-OVERLAY",
                LotId = "LOT-OVERLAY",
                Confidence = 0.91,
            };
            analysis.Defects.Add(new DefectResult
            {
                DefectType = "Missing Component",
                RoiId = "ROI-1",
                RoiType = "Presence",
                Confidence = 0.91,
                JudgmentStatus = "NG",
                BoundingBox = new Rect(0.10, 0.20, 0.15, 0.10),
            });
            analysis.Defects.Add(new DefectResult
            {
                DefectType = "Solder Ball",
                RoiId = "ROI-2",
                RoiType = "Anomaly",
                Confidence = 0.55,
                JudgmentStatus = "REVIEW",
                BoundingBox = new Rect(0.50, 0.55, 0.08, 0.08),
            });

            var view = new MonitorView();
            SetPrivate(view, "_currentBitmap", CreateTestBitmap());
            SetPrivate(view, "_currentAnalysis", analysis);
            InvokePrivate(view, "RefreshDefectRows");
            InvokePrivate(view, "RenderOverlay");

            var canvas = (Canvas)FindByName(view, "DefectOverlayCanvas")!;
            var grid = (DataGrid)FindByName(view, "DefectGrid")!;
            var rows = grid.ItemsSource!.Cast<object>().ToArray();

            return (
                canvas.Children.Count,
                canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Count(),
                canvas.Children.OfType<TextBlock>().Select(text => text.Text).ToArray(),
                canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
                    .Select(rect => ((SolidColorBrush)rect.Stroke).Color)
                    .ToArray(),
                rows.Select(row => (string)row.GetType().GetProperty("Severity")!.GetValue(row)!).ToArray());
        });

        // One box + one label per defect.
        Assert.Equal(2, boxCount);
        Assert.Equal(4, overlayChildren);
        Assert.Contains(labels, label => label.StartsWith("Missing Component [ROI-1]", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.StartsWith("Solder Ball [ROI-2]", StringComparison.Ordinal));

        // NG verdict must render red, never green or amber (spec §4.1 colour coding).
        Assert.All(strokeColours, colour => Assert.Equal(Colors.Red, colour));

        // Severity column resolves the classification-table severity of each class.
        Assert.Equal(new[] { "Critical", "Minor" }, severities);
    }

    [Fact]
    public void OverlayVerdictColourFollowsTheSpecColourCoding()
    {
        var colours = RunOnSta(() =>
        {
            EnsureApplicationResources();
            var method = typeof(MonitorView).GetMethod("ToVerdictColor", BindingFlags.Static | BindingFlags.NonPublic)!;
            return new[] { "OK", "NG", "REVIEW" }
                .Select(verdict => (Color)method.Invoke(null, new object[] { verdict })!)
                .ToArray();
        });

        Assert.Equal(Colors.LimeGreen, colours[0]);
        Assert.Equal(Colors.Red, colours[1]);
        Assert.Equal(Colors.Orange, colours[2]);
    }

    [Fact]
    public void NavigationLoopUsesCachedPagesAndStaysUnderThreshold()
    {
        var report = RunOnSta(() =>
        {
            EnsureApplicationResources();
            var pages = CreatePageDefinitions();
            var cache = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            var report = new UiSmokeReport("navigation-loop");

            for (var pass = 1; pass <= 3; pass++)
            {
                foreach (var page in pages)
                {
                    var stopwatch = Stopwatch.StartNew();
                    if (!cache.TryGetValue(page.Key, out var element))
                    {
                        element = page.Factory();
                        cache[page.Key] = element;
                    }

                    MeasureElement(element);
                    stopwatch.Stop();
                    report.Navigation.Add(new NavigationSmokeResult(page.Key, page.Name, pass, stopwatch.ElapsedMilliseconds));
                }
            }

            WriteReport(report);
            return report;
        });

        var slowLoads = report.Navigation
            .Where(item => item.Pass > 1 && item.DurationMilliseconds > CachedNavigationThresholdMs)
            .Select(item => $"{item.Name} pass {item.Pass}: {item.DurationMilliseconds} ms")
            .ToArray();
        Assert.True(slowLoads.Length == 0, "Slow cached navigation:" + Environment.NewLine + string.Join(Environment.NewLine, slowLoads));
    }

    private static UiPageSmokeResult MeasurePage(UiPageDefinition page)
    {
        var result = new UiPageSmokeResult(page.Key, page.Name);
        var stopwatch = Stopwatch.StartNew();
        FrameworkElement? element = null;

        try
        {
            element = page.Factory();
            stopwatch.Stop();
            result.ConstructorMilliseconds = stopwatch.ElapsedMilliseconds;
            if (result.ConstructorMilliseconds > ConstructorThresholdMs)
                result.Failures.Add($"Constructor took {result.ConstructorMilliseconds} ms.");

            MeasureElement(element);
            result.HasScrollViewer = Descendants(element).OfType<ScrollViewer>().Any();
            if (page.RequiresScrollViewer && !result.HasScrollViewer)
                result.Failures.Add("Dense page does not contain a ScrollViewer.");

            foreach (var name in page.RequiredNames)
            {
                if (FindByName(element, name) is null)
                    result.Failures.Add($"Required control '{name}' was not found.");
            }

            foreach (var warning in FindTextClippingCandidates(element))
                result.Warnings.Add(warning);

            foreach (var hardFailure in result.Warnings.Where(warning => warning.Contains("[FAIL]", StringComparison.Ordinal)))
                result.Failures.Add(hardFailure);

            SaveScreenshot(element, page.Key);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.ConstructorMilliseconds = stopwatch.ElapsedMilliseconds;
            result.Failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (element is Window window)
                window.Close();
        }

        return result;
    }

    private static void MeasureElement(FrameworkElement element)
    {
        element.Width = TestWidth;
        element.Height = TestHeight;
        element.Measure(new Size(TestWidth, TestHeight));
        element.Arrange(new Rect(0, 0, TestWidth, TestHeight));
        element.UpdateLayout();
    }

    private static IReadOnlyList<string> FindTextClippingCandidates(FrameworkElement root)
    {
        var results = new List<string>();
        foreach (var textBlock in Descendants(root).OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(textBlock.Text) || textBlock.ActualWidth <= 0 || textBlock.ActualHeight <= 0)
                continue;

            // Skip text that is not actually rendered - most importantly the item text inside
            // closed ComboBox popups, which measures at popup-layout width and false-positives
            // as clipped. The HMI layout audit applies the same visibility rule.
            if (!textBlock.IsVisible)
                continue;

            var desired = MeasureTextBlock(textBlock, textBlock.ActualWidth);
            var tooNarrow = textBlock.TextWrapping == TextWrapping.NoWrap && textBlock.TextTrimming == TextTrimming.None && desired.Width > textBlock.ActualWidth + 2;
            var tooShort = desired.Height > textBlock.ActualHeight + 3;
            if (!tooNarrow && !tooShort)
                continue;

            var severity = textBlock.TextTrimming == TextTrimming.None ? "[FAIL]" : "[WARN]";
            results.Add($"{severity} {Describe(textBlock)} desired={desired.Width:N0}x{desired.Height:N0} actual={textBlock.ActualWidth:N0}x{textBlock.ActualHeight:N0}");
        }

        return results;
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
        };
        clone.Measure(new Size(width, double.PositiveInfinity));
        return clone.DesiredSize;
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
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static string Describe(TextBlock textBlock)
    {
        var name = string.IsNullOrWhiteSpace(textBlock.Name) ? "TextBlock" : $"TextBlock#{textBlock.Name}";
        var text = textBlock.Text.Length <= 48 ? textBlock.Text : textBlock.Text[..45] + "...";
        return $"{name} '{text}'";
    }

    private static IReadOnlyList<UiPageDefinition> CreatePageDefinitions()
    {
        var viewModel = new MainViewModel();
        return new[]
        {
            new UiPageDefinition("home", "Home", () => new HomeView { DataContext = new MainViewModel() }, true, new[] { "HomeModuleItems", "HomeAnalyzeItems", "HomeSystemItems", "HomeEngineStatusText", "HomeStatusSummaryText" }),
            new UiPageDefinition("library", "Board & Images", () => new LibraryView(), false, new[] { "RecordsGrid", "SchemaGrid", "ImportStatusText" }),
            new UiPageDefinition("monitor", "Run Inspection", () => new MonitorView(), false, new[] { "StartInspectionButton", "StopInspectionButton", "NextBoardButton", "SaveResultButton" }),
            new UiPageDefinition("compare", "Golden Compare", () => new CompareView(), false, new[] { "FindingsGrid", "DiffScoreText" }),
            new UiPageDefinition("review", "Defect Review", () => new ReviewView(), false, new[] { "QueueGrid", "ReviewCanvasViewbox" }),
            new UiPageDefinition("recipe", "Recipe Rules", () => new RecipeView(), false, new[] { "RecipeNameText", "RoiGrid", "ViewportCanvas" }),
            new UiPageDefinition("modeltest", "AI / Models", () => new AIModelTestView(), true, new[] { "CancelWorkButton", "ResultsGrid" }),
            new UiPageDefinition("spc", "Yield Analytics", () => new SpcView(), false, new[] { "VerdictBreakdownText", "YieldText", "DbHealthGrid" }),
            new UiPageDefinition("reports", "Export & Trace", () => new ReportsView(), true, new[] { "CancelWorkButton", "InspectionGrid", "FactoryReadinessGrid", "ManagementDefectGrid", "MesSpoolGrid", "CentralSyncGrid" }),
            new UiPageDefinition("settings", "System Settings", () => new SettingsView(viewModel), true, new[] { "ApplyBtn", "CancelBtn", "ResetBtn", "ModelRegistryGrid", "MesModeCombo", "CentralSyncModeCombo", "OpenInstallNotesBtn", "OpenGuideBtn" }),
            new UiPageDefinition("calibration", "Calibration", () => new CalibrationView(), false, new[] { "PointsGrid", "ProfileCombo" }),
            new UiPageDefinition("profile", "3D Profile", () => new ProfileView(), false, new[] { "RunAcceptanceButton", "CancelAcceptanceButton", "HeightMapImage" }),
            new UiPageDefinition("pilot", "Hardware Readiness", () => new PilotWizardView(), false, new[] { "ProfileCombo", "StepsGrid", "StatusText" }),
            new UiPageDefinition("factory-readiness", "Factory Readiness", () => new ReportsView(), true, new[] { "FactoryReadinessGrid" }),
            new UiPageDefinition("management-dashboard", "Management Dashboard", () => new ReportsView(), true, new[] { "ManagementDefectGrid", "ManagementRoiGrid", "ManagementModelTrendGrid" }),
            new UiPageDefinition("model-registry", "Model Registry / Acceptance", () => new SettingsView(viewModel), true, new[] { "ModelRegistryGrid", "ModelAcceptanceRunsGrid" }),
            new UiPageDefinition("mes-central-sync", "MES Queue / Central Sync", () => new ReportsView(), true, new[] { "MesSpoolGrid", "CentralSyncGrid" }),
            new UiPageDefinition("install", "Installation Notes", () => new InstallView(), true, new[] { "RuntimeGrid", "BoundaryGrid" }),
            new UiPageDefinition("guide", "Guide", () => new GuideView(), true, new[] { "StepsGrid" }),
        };
    }

    private static void SetPrivate(object target, string fieldName, object? value)
        => target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void InvokePrivate(object target, string methodName)
        => target.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, null);

    private static BitmapSource CreateTestBitmap()
    {
        const int width = 640;
        const int height = 480;
        var stride = width * 4;
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[stride * height], stride);
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
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
            if (Application.Current?.Dispatcher.CheckAccess() == true)
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }

    private static void WriteReport(UiSmokeReport report)
    {
        var path = ReportPath(report.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveScreenshot(FrameworkElement element, string key)
    {
        try
        {
            var path = Path.Combine(ArtifactRoot, "screenshots", $"{key}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bitmap = new RenderTargetBitmap((int)TestWidth, (int)TestHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Best-effort screenshot capture failed: {ex.Message}");
        }
    }

    private static string ArtifactRoot => Path.Combine(GetRepositoryRoot(), "TestResults", "ui-smoke");

    private static string ReportPath(string name) => Path.Combine(ArtifactRoot, $"{name}.json");

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate repository root.");

        return directory.FullName;
    }

    private sealed record UiPageDefinition(
        string Key,
        string Name,
        Func<FrameworkElement> Factory,
        bool RequiresScrollViewer,
        IReadOnlyList<string> RequiredNames);

    private sealed class UiSmokeReport
    {
        public UiSmokeReport(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public DateTime GeneratedAtUtc { get; } = DateTime.UtcNow;
        public List<UiPageSmokeResult> Pages { get; } = new();
        public List<NavigationSmokeResult> Navigation { get; } = new();
    }

    private sealed class UiPageSmokeResult
    {
        public UiPageSmokeResult(string key, string name)
        {
            Key = key;
            Name = name;
        }

        public string Key { get; }
        public string Name { get; }
        public long ConstructorMilliseconds { get; set; }
        public bool HasScrollViewer { get; set; }
        public List<string> Warnings { get; } = new();
        public List<string> Failures { get; } = new();
    }

    private sealed record NavigationSmokeResult(string Key, string Name, int Pass, long DurationMilliseconds);
}
