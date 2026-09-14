using System.Collections.ObjectModel;
using System.Windows.Threading;
using AOI_Monitor.Services;

namespace AOI_Monitor.ViewModels;

public class NavPage : ViewModelBase
{
    private bool _isActive;
    private bool _isEnabled = true;
    private string _title = "";
    private string _subtitle = "";
    public string Key { get; set; } = "";
    public string Number { get; set; } = "";

    /// <summary>Home menu cluster: "operate", "analyze", or "system".</summary>
    public string Group { get; set; } = "operate";

    /// <summary>
    /// UIA item containers announce ToString(); without this override every workflow tile row
    /// read as "AOI_Monitor.ViewModels.NavPage" to assistive tech and UI automation.
    /// </summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Key : Title;
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }
    public string Subtitle
    {
        get => _subtitle;
        set => SetField(ref _subtitle, value);
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}

public class MainViewModel : ViewModelBase
{
    private string _currentPage = "home";
    private string _clockText = "00:00";

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            SetField(ref _currentPage, value);
            foreach (var p in NavPages) p.IsActive = IsTopLevelActive(p.Key, value);
        }
    }

    public string ClockText
    {
        get => _clockText;
        set => SetField(ref _clockText, value);
    }

    // Home itself is not listed: the module map lives on the Home page, so a self-referential
    // tile would do nothing. The "home" route stays valid via the shell Home button.
    public ObservableCollection<NavPage> NavPages { get; } = new()
    {
        new NavPage { Key="library",     Number="01", Group="operate", Title="Board & Images",     Subtitle="library, folders, golden refs" },
        new NavPage { Key="monitor",     Number="02", Group="operate", Title="Run Inspection",     Subtitle="embedded run, large image view" },
        new NavPage { Key="compare",     Number="03", Group="operate", Title="Golden Compare",     Subtitle="embedded compare, large image view" },
        new NavPage { Key="review",      Number="04", Group="operate", Title="Defect Review",      Subtitle="queue, evidence, disposition" },
        new NavPage { Key="recipe",      Number="05", Group="analyze", Title="Recipe Rules",       Subtitle="ROI, masks, tolerances" },
        new NavPage { Key="modeltest",   Number="06", Group="analyze", Title="AI / Models",        Subtitle="model checks, false calls" },
        new NavPage { Key="spc",         Number="07", Group="analyze", Title="Yield Analytics",    Subtitle="SPC, Pareto, trends" },
        new NavPage { Key="reports",     Number="08", Group="analyze", Title="Export & Trace",     Subtitle="CSV, PDF, audit, MES" },
        new NavPage { Key="calibration", Number="09", Group="system",  Title="Calibration",        Subtitle="2D transform, Stage 2 prep" },
        new NavPage { Key="profile",     Number="10", Group="system",  Title="3D Profile",         Subtitle="height data, acceptance" },
        new NavPage { Key="pilot",       Number="11", Group="system",  Title="Hardware Readiness", Subtitle="camera, lighting, robot gates" },
        new NavPage { Key="settings",    Number="12", Group="system",  Title="System Settings",    Subtitle="display, storage, security" },
    };

    public IEnumerable<NavPage> OperatePages => NavPages.Where(p => p.Group == "operate");
    public IEnumerable<NavPage> AnalyzePages => NavPages.Where(p => p.Group == "analyze");
    public IEnumerable<NavPage> SystemPages => NavPages.Where(p => p.Group == "system");

    public RelayCommand NavigateCommand { get; }

    public MainViewModel()
    {
        // Startup page is Home; no destination tile is active until the operator navigates.
        RefreshLanguage(UiPreferencesService.Load().Language);
        RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
        NavigateCommand = new RelayCommand(key =>
        {
            var pageKey = (string?)key ?? "monitor";
            var navPage = NavPages.FirstOrDefault(p => p.Key == pageKey);
            if (navPage?.IsEnabled == false)
                return;

            CurrentPage = pageKey;
        });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => TickClock();
        timer.Start();
        TickClock();
    }

    public void RefreshLanguage(UiLanguage language)
    {
        foreach (var page in NavPages)
        {
            (page.Title, page.Subtitle) = page.Key switch
            {
                "library" => T(language, "Board & Images", "library, folders, golden refs", "\uBCF4\uB4DC / \uC774\uBBF8\uC9C0", "\uB77C\uC774\uBE0C\uB7EC\uB9AC/\uD3F4\uB354/\uAE30\uC900 \uC774\uBBF8\uC9C0"),
                "monitor" => T(language, "Run Inspection", "embedded run, large image view", "\uAC80\uC0AC \uC2E4\uD589", "\uB0B4\uC7A5 \uC2E4\uD589/\uD070 \uC774\uBBF8\uC9C0 \uBCF4\uAE30"),
                "compare" => T(language, "Golden Compare", "embedded compare, large image view", "\uAE30\uC900 \uBE44\uAD50", "\uB0B4\uC7A5 \uBE44\uAD50/\uD070 \uC774\uBBF8\uC9C0 \uBCF4\uAE30"),
                "review" => T(language, "Defect Review", "queue, evidence, disposition", "\uACB0\uD568 \uAC80\uD1A0", "\uB300\uAE30\uC5F4/\uC99D\uBE59/\uCC98\uBD84"),
                "recipe" => T(language, "Recipe Rules", "ROI, masks, tolerances", "\uB808\uC2DC\uD53C \uADDC\uCE59", "ROI/\uB9C8\uC2A4\uD06C/\uACF5\uCC28"),
                "modeltest" => T(language, "AI / Models", "model checks, false calls", "AI / \uBAA8\uB378", "\uBAA8\uB378 \uC810\uAC80/\uD5C8\uC704 \uAC80\uCD9C"),
                "spc" => T(language, "Yield Analytics", "SPC, Pareto, trends", "\uC218\uC728 \uBD84\uC11D", "SPC/\uD30C\uB808\uD1A0/\uCD94\uC138"),
                "reports" => T(language, "Export & Trace", "CSV, PDF, audit, MES", "\uB0B4\uBCF4\uB0B4\uAE30 / \uCD94\uC801", "CSV/PDF/\uAC10\uC0AC/MES"),
                "calibration" => T(language, "Calibration", "2D transform, Stage 2 prep", "\uBCF4\uC815", "2D \uBCC0\uD658/2\uB2E8\uACC4 \uC900\uBE44"),
                "profile" => T(language, "3D Profile", "height data, acceptance", "3D \uD504\uB85C\uD30C\uC77C", "\uB192\uC774 \uB370\uC774\uD130/\uC2B9\uC778"),
                "pilot" => T(language, "Hardware Readiness", "camera, lighting, robot gates", "\uD558\uB4DC\uC6E8\uC5B4 \uC900\uBE44\uC131", "\uCE74\uBA54\uB77C/\uC870\uBA85/\uB85C\uBD07 \uAC8C\uC774\uD2B8"),
                "settings" => T(language, "System Settings", "display, storage, security", "\uC2DC\uC2A4\uD15C \uC124\uC815", "\uD654\uBA74/\uC800\uC7A5\uC18C/\uBCF4\uC548"),
                _ => T(language, page.Key, "", page.Key, ""),
            };
        }
    }

    private static (string Title, string Subtitle) T(UiLanguage language, string englishTitle, string englishSubtitle, string koreanTitle, string koreanSubtitle)
        => language == UiLanguage.Korean ? (koreanTitle, koreanSubtitle) : (englishTitle, englishSubtitle);

    public void RefreshRolePermissions(UserRole role)
    {
        foreach (var page in NavPages)
            page.IsEnabled = RoleAuthorization.CanAccessPage(role, page.Key);

        if (!RoleAuthorization.CanAccessPage(role, CurrentPage))
            CurrentPage = "home";
    }

    private void TickClock()
    {
        var n = DateTime.Now;
        ClockText = $"{n.Hour:D2}:{n.Minute:D2}";
    }

    private static bool IsTopLevelActive(string navKey, string currentPage)
    {
        if (navKey == currentPage)
            return true;

        return navKey switch
        {
            "settings" => currentPage is "settings" or "install" or "guide",
            _ => false,
        };
    }
}
