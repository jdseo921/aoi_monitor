using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class UiServiceCoverageTests
{
    [Fact]
    public void MajorBackendServicesHaveDocumentedUiCoverage()
    {
        var doc = ReadRepoFile("Docs", "ARCHITECTURE.md");
        foreach (var service in new[]
        {
            "ModelAcceptanceService",
            "ModelRegistryService",
            "ModelLifecycleService",
            "FalseCallReductionService",
            "ThresholdProfileService",
            "CameraAcceptanceTestService",
            "Profile3DAcceptanceTestService",
            "LightingAcceptanceTestService",
            "RobotAcceptanceTestService / RobotCellAcceptanceTestService",
            "TraceabilityAcceptanceTestService",
            "CentralSyncService",
            "FactoryReadinessService",
            "FactoryAcceptanceChecklistService",
            "BuildTestEvidenceService",
            "ExportVerificationService",
        })
        {
            Assert.Contains(service, doc);
        }
    }

    [Fact]
    public void RequiredOperatorVisibleActionsExistInXaml()
    {
        var settings = ReadRepoFile("AOI_Monitor", "Views", "SettingsView.xaml");
        var modelTest = ReadRepoFile("AOI_Monitor", "Views", "AIModelTestView.xaml");
        var profile = ReadRepoFile("AOI_Monitor", "Views", "ProfileView.xaml");
        var reports = ReadRepoFile("AOI_Monitor", "Views", "ReportsView.xaml");
        var readiness = ReadRepoFile("AOI_Monitor", "Views", "ReadinessQaView.xaml");
        var combined = settings + modelTest + profile + reports + readiness;

        foreach (var label in new[]
        {
            "Run Model Acceptance",
            "Cancel Acceptance",
            "Open Manifest Template",
            "Create Model Release Package",
            "Promote to Production Candidate",
            "Deploy",
            "Deploy with Waiver",
            "Retire",
            "Run 3D Acceptance Test",
            "Discover Adapters",
            "Discover Cameras",
            "Run Camera Acceptance Test",
            "Run Lighting Sync Test",
            "Run Robot Cell Acceptance",
            "Run Traceability Test",
            "Retry Selected Central",
            "Retry All Central",
            "Generate Checklist",
            "Import Build/Test Evidence",
            "Backup Configuration",
            "Restore Configuration Preview",
        })
        {
            Assert.Contains(label, combined);
        }
    }

    [Fact]
    public void AcceptanceActionsAreRoleGatedAwayFromOperator()
    {
        Assert.False(RoleAuthorization.CanTestModelConfiguration(UserRole.Operator));
        Assert.False(RoleAuthorization.CanChangeThresholds(UserRole.Operator));
        Assert.False(RoleAuthorization.CanManageSettings(UserRole.Operator));
        Assert.False(RoleAuthorization.CanExportLogs(UserRole.Operator));

        Assert.True(RoleAuthorization.CanTestModelConfiguration(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanChangeThresholds(UserRole.Engineer));
        Assert.False(RoleAuthorization.CanManageSettings(UserRole.Engineer));

        Assert.True(RoleAuthorization.CanManageSettings(UserRole.Admin));
        Assert.True(RoleAuthorization.CanExportLogs(UserRole.Admin));
    }

    [Fact]
    public void UiHandlersAuditEngineerAndAdminAcceptanceActions()
    {
        var settingsCode = ReadViewCodeBehind("SettingsView");
        var profileCode = ReadViewCodeBehind("ProfileView");
        var reportsCode = ReadViewCodeBehind("ReportsView");
        var readinessCode = ReadViewCodeBehind("ReadinessQaView");
        var combined = settingsCode + profileCode + reportsCode + readinessCode;

        foreach (var auditEvent in new[]
        {
            "MODEL_ACCEPTANCE",
            "MODEL_RELEASE_PACKAGE",
            "MODEL_PRODUCTION_CANDIDATE",
            "MODEL_DEPLOYMENT",
            "MODEL_DEPLOYMENT_WAIVER",
            "MODEL_RETIRED",
            "PROFILE_3D_ACCEPTANCE",
            "CAMERA_ACCEPTANCE",
            "LIGHTING_ACCEPTANCE",
            "ROBOT_CELL_ACCEPTANCE",
            "MES_TRACEABILITY_TEST",
            "CENTRAL_SYNC_RETRY",
            "FACTORY_READINESS_EXPORT",
            "FACTORY_ACCEPTANCE_EXPORT",
            "BUILD_TEST_EVIDENCE",
        })
        {
            Assert.Contains(auditEvent, combined);
        }
    }

    [Fact]
    public void UiLabelsDoNotPresentSimulationAsProductionReadiness()
    {
        var settings = ReadRepoFile("AOI_Monitor", "Views", "SettingsView.xaml");
        var profile = ReadRepoFile("AOI_Monitor", "Views", "ProfileView.xaml");
        var reports = ReadRepoFile("AOI_Monitor", "Views", "ReportsView.xaml");
        var readiness = ReadRepoFile("AOI_Monitor", "Views", "ReadinessQaView.xaml");
        var combined = settings + profile + reports + readiness;

        Assert.Contains("Simulation is not production robot validation", combined);
        Assert.Contains("Simulated mode is not real hardware", combined);
        Assert.Contains("Real Hardware", combined);
        Assert.Contains("Sample Data Mode", combined);
        Assert.Contains("3D Camera Not Connected", combined);
        Assert.Contains("real hardware readiness", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverflowProneDocumentationAndSettingsPagesHaveVerticalScrollRegions()
    {
        foreach (var file in new[]
        {
            ReadRepoFile("AOI_Monitor", "Views", "SettingsView.xaml"),
            ReadRepoFile("AOI_Monitor", "Views", "GuideView.xaml"),
            ReadRepoFile("AOI_Monitor", "Views", "InstallView.xaml"),
            ReadRepoFile("AOI_Monitor", "Views", "FirstRunWizardView.xaml"),
        })
        {
            Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", file);
            Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", file);
        }
    }

    [Fact]
    public void NavigationShellHasBilingualPreferenceWiring()
    {
        var mainWindowCode = ReadRepoFile("AOI_Monitor", "MainWindow.xaml.cs");
        var viewModelCode = ReadRepoFile("AOI_Monitor", "ViewModels", "MainViewModel.cs");
        var settingsCode = ReadViewCodeBehind("SettingsView");

        Assert.Contains("UiPreferencesService.PreferencesChanged", mainWindowCode);
        Assert.Contains("RefreshLanguage(UiLanguage language)", viewModelCode);
        Assert.Contains("SaveUiPreferenceSelection", settingsCode);
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(parts)));

    // Reads a view's code-behind including any `ViewName.*.cs` partial-class files, so
    // source-inspecting assertions stay correct after a code-behind is split into partials.
    private static string ReadViewCodeBehind(string viewName)
    {
        var viewsDir = Path.Combine(FindRepoRoot(), "AOI_Monitor", "Views");
        var files = Directory.GetFiles(viewsDir, $"{viewName}.xaml.cs")
            .Concat(Directory.GetFiles(viewsDir, $"{viewName}.*.cs"))
            .Distinct();
        return string.Concat(files.Select(File.ReadAllText));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
