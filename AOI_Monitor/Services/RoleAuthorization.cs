namespace AOI_Monitor.Services;

public enum UserRole
{
    Operator,
    Engineer,
    Admin,
}

public static class RoleAuthorization
{
    public static bool CanEditRecipes(UserRole role) => role >= UserRole.Engineer;
    public static bool CanEditCalibration(UserRole role) => role >= UserRole.Engineer;
    public static bool CanRunModelTests(UserRole role) => role >= UserRole.Engineer;
    public static bool CanTestModelConfiguration(UserRole role) => role >= UserRole.Engineer;
    public static bool CanImportImageLearningImages(UserRole role) => role >= UserRole.Engineer;
    public static bool CanRunImageOnlyLearning(UserRole role) => role >= UserRole.Engineer;
    public static bool CanSetActiveLearnedVisualModel(UserRole role) => role >= UserRole.Engineer;
    public static bool CanExportImageLearningReports(UserRole role) => role >= UserRole.Engineer;
    public static bool CanManageImageLearningTrainedData(UserRole role) => role >= UserRole.Engineer;
    public static bool CanArchiveImageLearningProjects(UserRole role) => role >= UserRole.Admin;
    public static bool CanDeleteImageLearningArtifacts(UserRole role) => role >= UserRole.Admin;
    public static bool CanChangeThresholds(UserRole role) => role >= UserRole.Engineer;
    public static bool CanExportLogs(UserRole role) => role >= UserRole.Admin;
    public static bool CanManageSettings(UserRole role) => role >= UserRole.Admin;
    public static bool CanUseMaintenanceActions(UserRole role) => role >= UserRole.Admin;

    /// <summary>
    /// Resolves whether <paramref name="role"/> may open the navigation page identified by
    /// <paramref name="pageKey"/>. Authorization is default-deny: an unknown or unregistered
    /// page key returns <c>false</c> so a newly added route cannot ship silently
    /// operator-accessible. Every real navigation key is enumerated explicitly; the operator-view
    /// pages (home, library, monitor, compare, review, profile, guide) are readable by any
    /// authenticated role, and privileged pages gate to Engineer/Admin via the capability
    /// predicates above. Governed by the AOI Software Architecture, Secure Development, and
    /// Change-Control Standard (Docs/standard, §28 default-deny authorization).
    /// </summary>
    public static bool CanAccessPage(UserRole role, string pageKey)
    {
        return pageKey switch
        {
            // Operator-viewable pages (readable by any authenticated role).
            "home" => role >= UserRole.Operator,
            "library" => role >= UserRole.Operator,
            "monitor" => role >= UserRole.Operator,
            "compare" => role >= UserRole.Operator,
            "review" => role >= UserRole.Operator,
            "profile" => role >= UserRole.Operator,
            "guide" => role >= UserRole.Operator,
            "modeltest" => role >= UserRole.Operator,
            "reports" => role >= UserRole.Operator,
            // Readiness & QA mirrors Export & Trace: readable by any authenticated role,
            // with exports and maintenance actions gated by the capability predicates above.
            "readiness" => role >= UserRole.Operator,
            // Privileged pages.
            "recipe" => CanEditRecipes(role),
            "calibration" => CanEditCalibration(role),
            "spc" => CanRunModelTests(role),
            "pilot" => role >= UserRole.Engineer,
            "settings" => role >= UserRole.Engineer,
            "install" => CanManageSettings(role),
            // Default-deny: an unregistered page key is never accessible.
            _ => false,
        };
    }

    public static string DeniedMessage(UserRole role, string action)
        => $"{action} requires a higher local role. Current role: {role}.";
}
