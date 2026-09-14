using System.Text.RegularExpressions;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// English/Korean localization parity tests for the operator-facing screens.
///
/// Scope note: only UI chrome translated by UiPreferencesService.ApplyLocalization is
/// covered (TextBlock.Text, ContentControl.Content, headers, tooltips, DataGrid column
/// headers). HTML evidence reports intentionally remain English by design and are NOT
/// covered by these tests.
/// </summary>
public class LocalizationParityTests
{
    private static readonly string[] OperatorScreens =
    {
        "MonitorView.xaml",
        "ReviewView.xaml",
        "RecipeView.xaml",
        "AIModelTestView.xaml",
        // DR-10: the parity scan now covers every operator-facing screen plus the shell.
        "HomeView.xaml",
        "LibraryView.xaml",
        "CompareView.xaml",
        "SpcView.xaml",
        "ReportsView.xaml",
        "ReadinessQaView.xaml",
        "CalibrationView.xaml",
        "ProfileView.xaml",
        "PilotWizardView.xaml",
        "InstallView.xaml",
        "SettingsView.xaml",
        "GuideView.xaml",
        "AiTrainingSetupView.xaml",
        "..\\MainWindow.xaml",
    };

    /// <summary>
    /// Dictionary keys whose Korean value legitimately contains no Hangul because the
    /// term is identical in both languages (technical tokens shown as-is).
    /// </summary>
    private static readonly HashSet<string> IdenticalValueKeys = new(StringComparer.Ordinal)
    {
        "AI",
        "X",
        "Y",
        "ROI",
    };

    /// <summary>
    /// The honest ledger of operator-screen literals that are deliberately NOT translated.
    /// Every entry needs a reason; if a literal can be translated safely, translate it
    /// instead of adding it here.
    /// </summary>
    private static readonly HashSet<string> IntentionallyUntranslated = new(StringComparer.Ordinal)
    {
        // -- Sample/demo data shown as data, not UI chrome (station ids, board models,
        //    lot ids, operator ids, sample image ids, silkscreen reference designators).
        "AOI-LIB-01",
        "TBOX-MAIN",
        "TBOX_TOP",
        "POC-LOT",
        "Engineer01",
        "PIXEL_DIFF_0.2",
        "IMG_0241",
        "IMG_0114 / 96%",
        "IMG_0088 / 92%",
        "RefDes: U107",
        "RefDes: U109",
        "U101",
        "U107",
        "U107 BRIDGE",
        "TOP-03",

        // -- Engine/product display names (proper nouns, kept in English).
        "Pixel Difference Prototype Engine",
        "Pixel Difference Prototype Engine 0.1",

        // -- Industry-standard verdict/state tokens (OK/NG/REVIEW convention; LOCKED/
        //    UNLOCKED is rewritten in English by RecipeView code-behind at runtime).
        "REVIEW",
        "UNLOCKED",

        // -- Industry-standard abbreviation used verbatim in Korean SMT lines.
        "RefDes",

        // -- TextBox.Text placeholders: the localization walker translates TextBlock/
        //    ContentControl/headers/tooltips but intentionally never rewrites TextBox.Text
        //    (editable inputs hold data), so translating these keys would have no effect.

        // -- DR-10 full-app sweep: data tokens, protocol names, and endpoint paths
        //    that must stay English (reasons per entry).
        // Default REST endpoint path value in the MES image-path TextBox; a URL data token, not operator prose.
        "/api/aoi/images",
        // Default REST endpoint path value in the MES result-path TextBox; a URL data token, not operator prose.
        "/api/aoi/results",
        // Product/brand title in the shell header (BrandTitleText); product proper noun, user-replaceable via Program Assets console title
        "AOI MONITOR",
        // Sample station id value displayed after the Station label (StationNameText)
        "AOI-LIB",
        // Default lighting program identifier (TextBox value substituted into the hardware command template SET {view} {program}); translating would change the command sent to hardware.
        "BOTTOM",
        // HTTP authorization scheme name standing alone as an Auth Mode combo item; protocol token paired with the Bearer option, kept English.
        "Basic",
        // HTTP authorization scheme name standing alone as an Auth Mode combo item; protocol token, kept English like ONNX/CSV.
        "Bearer",
        // On-image overlay marker tag on the demo defect canvas (beside U107 BRIDGE); industry AOI token used like OK/NG, kept as-is
        "DIFF",
        // AuthenticationMode enum identifier shown in the auth-mode combo; quoted verbatim in operator warnings and audit/traceability events
        "DemoLocalRoleSelector",
        // Demo sample subtitle: image id plus AI/GT verdict tokens (OK/NG) only, nothing translatable
        "IMG_0241 / AI OK / GT NG",
        // AuthenticationMode enum identifier shown in the auth-mode combo; quoted verbatim in operator warnings and audit/traceability events
        "LocalUsers",
        // AuthenticationMode enum identifier shown in the auth-mode combo; matches the enum name used by code-behind and boundary messaging
        "MesAuthenticationBoundary",
        // Industry verdict tokens standing alone as a fixed label skeleton over the '0 / 0 / 0' count in SpcView; the dictionary keeps OK/NG/REVIEW untranslated everywhere.
        "OK / NG / REVIEW",
        // Default value of the lighting side-program TextBox (LightingSideProgramText, SettingsView.xaml line 761) alongside TOP/BOTTOM; a device program identifier that round-trips as hardware configuration data, not UI chrome.
        "SIDE",
        // Technical proper noun standing alone as the footer DB link value (FooterIndexText)
        "SQLite",
        // Sample-data board program id shown as a footer value
        "TBOX_TOP_V1.2",
        // Default value of the editable Model Version TextBox (ModelVersionText, SettingsView.xaml line 433); a sentinel token persisted as model-version configuration data, so translating it would alter stored data.
        "UNCONFIGURED",
        // Standard HTTP header name; default value of the MES API key header TextBox (MesApiKeyHeaderText) sent on REST requests and persisted as integration configuration.
        "X-API-Key",
        "No folder selected",
        "No CSV selected",

        // Note: RecipeView ComboBoxItem contents (ROI types, IPC class, lighting profile,
        // false-call policy) used to live here because code-behind persisted the Content
        // strings. Persistence now round-trips through invariant Tag tokens (ComboBoxTokens),
        // so those literals are translated normally; see ComboBoxLocalizationRegressionTests.
    };

    [Fact]
    public void AllDictionaryValuesAreNonEmptyKoreanText()
    {
        var offenders = new List<string>();

        foreach (var (english, korean) in UiPreferencesService.KoreanTranslations)
        {
            if (string.IsNullOrWhiteSpace(english))
                offenders.Add("<empty key>");

            if (string.IsNullOrWhiteSpace(korean))
            {
                offenders.Add($"'{english}' has an empty translation");
                continue;
            }

            if (IdenticalValueKeys.Contains(english))
                continue;

            if (!korean.Any(IsHangulSyllable))
                offenders.Add($"'{english}' -> '{korean}' contains no Hangul syllable");
        }

        Assert.True(offenders.Count == 0,
            "Korean dictionary quality violations:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void CriticalOperatorStringsExistAsDictionaryKeys()
    {
        // Drift catcher: these keys must byte-match the XAML literals. Renaming a button
        // in XAML without updating the KoreanText dictionary must fail this test.
        var expectedKeys = new[]
        {
            // MonitorView hotkey buttons.
            "Start (F5)",
            "Stop (F6)",
            "Next Board (F7)",
            "Save Result (Ctrl+S)",
            // ReviewView disposition controls.
            "Confirm NG (1)",
            "Mark False Call (2)",
            "Mark Possible Escape (3)",
            "Hold for 2nd Review (4)",
            "Queue Candidate",
            "Disposition Controls",
            // RecipeView centroid import.
            "Import Centroid CSV",
        };

        var missing = expectedKeys
            .Where(key => !UiPreferencesService.KoreanTranslations.ContainsKey(key))
            .ToList();

        Assert.True(missing.Count == 0,
            "Critical operator strings missing from KoreanText dictionary:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void OperatorScreenXamlLiteralsAreTranslatedOrExplicitlyAllowlisted()
    {
        var viewsDirectory = FindViewsDirectory();
        var missing = new List<string>();

        foreach (var screen in OperatorScreens)
        {
            var path = Path.Combine(viewsDirectory, screen);
            Assert.True(File.Exists(path), $"Expected operator screen XAML not found: {path}");

            foreach (var literal in ExtractDisplayLiterals(File.ReadAllText(path)))
            {
                if (UiPreferencesService.KoreanTranslations.ContainsKey(literal))
                    continue;

                if (IntentionallyUntranslated.Contains(literal))
                    continue;

                missing.Add($"{screen}: \"{literal}\"");
            }
        }

        Assert.True(missing.Count == 0,
            "Operator-facing XAML literals missing from the KoreanText dictionary " +
            "(translate them, or add to IntentionallyUntranslated with a reason):\n" +
            string.Join("\n", missing.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void AllowlistDoesNotShadowDictionaryKeys()
    {
        // An allowlisted literal that is also a dictionary key means the ledger is stale.
        var shadowed = IntentionallyUntranslated
            .Where(UiPreferencesService.KoreanTranslations.ContainsKey)
            .ToList();

        Assert.True(shadowed.Count == 0,
            "Allowlist entries are also dictionary keys (remove them from the allowlist):\n" +
            string.Join("\n", shadowed));
    }

    private static bool IsHangulSyllable(char c)
        => c >= '가' && c <= '힣';

    private static IEnumerable<string> ExtractDisplayLiterals(string xaml)
    {
        // Text="..." and Content="..." attribute literals only; skip bindings/resources
        // (anything containing '{'), pure numbers/symbols, and very short tokens.
        var matches = Regex.Matches(xaml, "[\\s](?:Text|Content)=\"([^\"]*)\"");
        foreach (Match match in matches)
        {
            var literal = DecodeXmlEntities(match.Groups[1].Value);
            if (literal.Contains('{'))
                continue;
            if (literal.Length < 4)
                continue;
            if (!literal.Any(char.IsLetter))
                continue;

            yield return literal;
        }
    }

    private static string DecodeXmlEntities(string value)
        => value
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&#xA;", "\n", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string FindViewsDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var marker = Path.Combine(directory.FullName, "AOI_Monitor", "Views", "ReviewView.xaml");
            if (File.Exists(marker))
                return Path.Combine(directory.FullName, "AOI_Monitor", "Views");

            directory = directory.Parent;
        }

        Assert.Fail($"Repository root containing AOI_Monitor\\Views\\ReviewView.xaml was not found above {AppContext.BaseDirectory}.");
        return string.Empty; // unreachable
    }
}
