using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;

namespace AOI_Monitor.Services;

public static class LayoutStressTestService
{
    public static string RunDeveloperStressTest()
    {
        var exportDir = Path.Combine(AppContext.BaseDirectory, "exports", "layout-stress");
        Directory.CreateDirectory(exportDir);
        var path = Path.Combine(exportDir, $"layout_stress_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var report = RunStressTestReport();
        File.WriteAllText(path, report, Encoding.UTF8);
        CreateIssuesForLayoutCandidates(report, path);
        return path;
    }

    public static string RunStressTestReport()
    {
        var report = new StringBuilder();
        report.AppendLine("AOI Monitor Layout Stress Test");
        report.AppendLine($"Generated: {DateTime.Now:O}");
        report.AppendLine("Simulated effective page size starts at 1420x720 DIP, then shrinks for 125% and 150% DPI pressure.");
        report.AppendLine();

        foreach (var page in CreateMajorViews())
        {
            report.AppendLine($"[{page.Name}]");
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                var width = 1420 / scale;
                var height = 720 / scale;
                var candidates = MeasureAndScan(page.Factory(), width, height).Take(40).ToList();
                report.AppendLine($"  Scale {scale:P0}, effective {width:N0}x{height:N0}: {candidates.Count} candidate(s)");
                foreach (var candidate in candidates)
                    report.AppendLine($"    - {candidate}");
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    public static IReadOnlyList<string> MajorViewNames => CreateMajorViews().Select(view => view.Name).ToList();

    private static IReadOnlyList<(string Name, Func<FrameworkElement> Factory)> CreateMajorViews()
    {
        return new (string Name, Func<FrameworkElement> Factory)[]
        {
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
            ("Management Dashboard", () => new ReadinessQaView()),
            ("Factory Readiness", () => new ReadinessQaView()),
            ("Standards & Quality Checklist", () => new ReadinessQaView()),
            ("Factory Acceptance Checklist", () => new ReadinessQaView()),
            ("Model Registry / Model Acceptance", () => new SettingsView(new MainViewModel())),
            ("MES / Central Sync / Hardware Acceptance", () => new SettingsView(new MainViewModel())),
            ("Calibration", () => new CalibrationView()),
            ("3D Profile", () => new ProfileView()),
            ("Hardware Readiness", () => new PilotWizardView()),
            ("System Settings", () => new SettingsView(new MainViewModel())),
            ("Installation Notes", () => new InstallView()),
            ("Guide", () => new GuideView()),
        };
    }

    private static IEnumerable<string> MeasureAndScan(FrameworkElement root, double width, double height)
    {
        root.Width = width;
        root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var issues = new List<string>();
        ScanElement(root, issues);
        return issues;
    }

    private static void ScanElement(DependencyObject node, ICollection<string> issues)
    {
        if (node is FrameworkElement element)
        {
            var desired = element.DesiredSize;
            var actualWidth = element.ActualWidth;
            var actualHeight = element.ActualHeight;
            if (actualWidth > 0 && actualHeight > 0 &&
                (desired.Width - actualWidth > 2 || desired.Height - actualHeight > 2) &&
                element is TextBlock or Button or DataGrid or TabControl or ListBox)
            {
                issues.Add($"{Describe(element)} desired {desired.Width:N0}x{desired.Height:N0}, actual {actualWidth:N0}x{actualHeight:N0}");
            }

            if (element is TextBlock textBlock &&
                textBlock.TextTrimming != TextTrimming.None &&
                textBlock.ToolTip is null)
            {
                issues.Add($"{Describe(element)} trims text without tooltip fallback");
            }
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            ScanElement(VisualTreeHelper.GetChild(node, i), issues);
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : $"{element.GetType().Name}#{element.Name}";
        return element switch
        {
            Button { Content: string content } => $"{name} \"{content}\"",
            TextBlock { Text: { Length: > 0 } text } => $"{name} \"{TrimForReport(text)}\"",
            _ => name,
        };
    }

    private static string TrimForReport(string value)
        => value.Length <= 64 ? value : value[..61] + "...";

    private static void CreateIssuesForLayoutCandidates(string report, string reportPath)
    {
        var currentPage = string.Empty;
        foreach (var rawLine in report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentPage = line.Trim('[', ']');
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentPage) ||
                !line.Contains("candidate(s)", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(": 0 candidate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PilotIssueService.CreateLayoutIssue(currentPage, reportPath, line);
            currentPage = string.Empty;
        }
    }
}
