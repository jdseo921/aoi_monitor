using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class RoleAuthorizationTests
{
    [Fact]
    public void RoleAuthorizationAllowsOperatorToViewAiTrainingSetupButNotRunIt()
    {
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "modeltest"));
        Assert.False(RoleAuthorization.CanImportImageLearningImages(UserRole.Operator));
        Assert.False(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Operator));
        Assert.False(RoleAuthorization.CanExportImageLearningReports(UserRole.Operator));
        Assert.False(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Operator));
        Assert.False(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Operator));
        Assert.False(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Operator));
    }

    [Fact]
    public void RoleAuthorizationAllowsEngineerAndAdminToRunAiTrainingSetup()
    {
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Engineer));
        Assert.False(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Engineer));
        Assert.False(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Admin));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Admin));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Admin));
        Assert.True(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Admin));
        Assert.True(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Admin));
        Assert.True(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Admin));
    }

    [Fact]
    public void ReportsPageIsViewableByEveryRoleButExportStaysAdminOnly()
    {
        // Log & Export is viewable by every role (spec reserves only the export action for Admin).
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "reports"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Engineer, "reports"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Admin, "reports"));

        // Readiness & QA mirrors Export & Trace access (same read-only view, Admin-gated exports).
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "readiness"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Engineer, "readiness"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Admin, "readiness"));

        // Exporting logs remains restricted to Admin.
        Assert.False(RoleAuthorization.CanExportLogs(UserRole.Operator));
        Assert.False(RoleAuthorization.CanExportLogs(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanExportLogs(UserRole.Admin));
    }

    [Fact]
    public void AiModelTestPageIsViewableByOperatorButRunningIsEngineerOrAdmin()
    {
        // Operators may open the AI Model Test page to view results...
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "modeltest"));
        // ...but running batch validation is restricted to Engineer/Admin (RTM AI-001).
        Assert.False(RoleAuthorization.CanRunModelTests(UserRole.Operator));
        Assert.True(RoleAuthorization.CanRunModelTests(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanRunModelTests(UserRole.Admin));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("library")]
    [InlineData("monitor")]
    [InlineData("compare")]
    [InlineData("review")]
    [InlineData("recipe")]
    [InlineData("modeltest")]
    [InlineData("spc")]
    [InlineData("reports")]
    [InlineData("readiness")]
    [InlineData("calibration")]
    [InlineData("profile")]
    [InlineData("pilot")]
    [InlineData("settings")]
    [InlineData("install")]
    [InlineData("guide")]
    public void EveryRegisteredNavigationKeyIsAccessibleToAdmin(string pageKey)
    {
        // Admin holds every capability, so each real route must resolve to an explicit allow.
        // A key that fails here has been removed from the CanAccessPage switch and would be
        // caught by default-deny — the failure signals a routing/authorization drift, not a
        // legitimately forbidden Admin page.
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Admin, pageKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Home")]        // case-sensitive: capitalized variant is not a registered key
    [InlineData("admin-backdoor")]
    [InlineData("../settings")]
    public void UnregisteredPageKeysAreDeniedToEveryRole(string pageKey)
    {
        // Default-deny authorization (§28): an unregistered or spoofed page key must never be
        // accessible, so a newly added route cannot ship silently operator-accessible and a
        // crafted key cannot bypass the switch.
        Assert.False(RoleAuthorization.CanAccessPage(UserRole.Operator, pageKey));
        Assert.False(RoleAuthorization.CanAccessPage(UserRole.Engineer, pageKey));
        Assert.False(RoleAuthorization.CanAccessPage(UserRole.Admin, pageKey));
    }
}
