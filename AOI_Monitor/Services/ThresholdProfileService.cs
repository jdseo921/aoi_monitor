using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ThresholdProfileService
{
    public static ThresholdProfile CreateDraftFromCurrentRecipe(
        RecipeDefinition? recipe,
        string boardModel,
        string createdBy)
    {
        var profile = NewDraft(
            boardModel,
            recipe?.BoardProgram ?? "ANY",
            recipe?.RecipeName ?? "ANY",
            recipe?.Revision ?? "ANY",
            createdBy);

        if (recipe?.Rois.Count > 0)
        {
            foreach (var roi in recipe.Rois.Where(roi => roi.Enabled))
            {
                var reviewThreshold = roi.Thresholds.AiScoreThreshold > 0
                    ? Math.Clamp(roi.Thresholds.AiScoreThreshold * 100.0, 0.1, 100.0)
                    : 8.0;
                profile.Rules.Add(new ThresholdProfileRule
                {
                    ProfileId = profile.ProfileId,
                    Revision = profile.Revision,
                    ViewType = "Any",
                    RoiType = string.IsNullOrWhiteSpace(roi.RoiType) ? "Any" : roi.RoiType,
                    DefectClass = string.IsNullOrWhiteSpace(roi.RoiType) ? "Any" : BatchValidationService.NormalizeDefectClass(roi.RoiType),
                    ReviewThreshold = reviewThreshold,
                    NgThreshold = Math.Max(reviewThreshold, Math.Min(100.0, reviewThreshold * 2.0)),
                    ConfidenceThreshold = Math.Clamp(roi.Thresholds.AiScoreThreshold, 0.0, 1.0),
                    MinimumAreaPixels = 0,
                    MaxAllowedFalseCallRate = 1.0,
                });
            }
        }

        if (profile.Rules.Count == 0)
            profile.Rules.Add(DefaultRule(profile));

        ValidateOrThrow(profile);
        AoiDatabase.SaveThresholdProfile(profile);
        return profile;
    }

    public static ThresholdProfile CreateDraftFromFalseCallReductionRecommendation(
        FalseCallReductionRun run,
        string boardModel,
        string boardProgram,
        string recipeName,
        string recipeRevision,
        string createdBy)
    {
        if (run.Recommendation.Point is null)
            throw new InvalidOperationException("False-call reduction run has no recommendation point.");

        var point = run.Recommendation.Point;
        var profile = NewDraft(boardModel, boardProgram, recipeName, recipeRevision, createdBy);
        profile.SourceFalseCallReductionRunId = run.Id > 0 ? run.Id : null;
        profile.SourceValidationRunId = run.BatchRunId;
        var threshold = Math.Clamp(point.DifferenceThreshold > 0 ? point.DifferenceThreshold * 100.0 : point.ConfidenceThreshold * 100.0, 0.1, 100.0);
        profile.Rules.Add(new ThresholdProfileRule
        {
            ProfileId = profile.ProfileId,
            Revision = profile.Revision,
            ViewType = "Any",
            RoiType = "Any",
            DefectClass = "Any",
            ReviewThreshold = threshold,
            NgThreshold = threshold,
            ConfidenceThreshold = point.ConfidenceThreshold,
            MaxAllowedFalseCallRate = point.FalseCallRate,
        });

        ValidateOrThrow(profile);
        AoiDatabase.SaveThresholdProfile(profile);
        AoiDatabase.RecordAuditEvent(
            "THRESHOLD_PROFILE_DRAFT",
            $"Threshold profile draft created from false-call recommendation run {run.Id}: {profile.ProfileId}/{profile.Revision}.",
            operatorWithRole: createdBy,
            relatedEntityType: "ThresholdProfile",
            relatedEntityId: $"{profile.ProfileId}/{profile.Revision}");
        return profile;
    }

    public static IReadOnlyList<string> ValidateThresholds(ThresholdProfile profile)
    {
        var errors = new List<string>();
        if (profile.Rules.Count == 0)
            errors.Add("At least one threshold rule is required.");

        foreach (var rule in profile.Rules)
        {
            if (rule.ReviewThreshold < 0 || rule.ReviewThreshold > 100)
                errors.Add($"Rule {RuleLabel(rule)} review threshold must be between 0 and 100.");
            if (rule.NgThreshold < 0 || rule.NgThreshold > 100)
                errors.Add($"Rule {RuleLabel(rule)} NG threshold must be between 0 and 100.");
            if (rule.NgThreshold < rule.ReviewThreshold)
                errors.Add($"Rule {RuleLabel(rule)} NG threshold must be greater than or equal to review threshold.");
            if (rule.ConfidenceThreshold < 0 || rule.ConfidenceThreshold > 1)
                errors.Add($"Rule {RuleLabel(rule)} confidence threshold must be between 0 and 1.");
            if (rule.MinimumAreaPixels < 0)
                errors.Add($"Rule {RuleLabel(rule)} minimum area must be zero or greater.");
            if (rule.MaxAllowedFalseCallRate < 0 || rule.MaxAllowedFalseCallRate > 1)
                errors.Add($"Rule {RuleLabel(rule)} max allowed false-call rate must be between 0 and 1.");
        }

        return errors;
    }

    public static ThresholdProfile ApproveProfile(string profileId, string revision, UserRole role, string operatorId)
    {
        EnsureThresholdPermission(role, "approve threshold profiles");
        var profile = AoiDatabase.GetThresholdProfile(profileId, revision)
            ?? throw new InvalidOperationException("Threshold profile was not found.");
        ValidateOrThrow(profile);

        var now = DateTime.UtcNow;
        AoiDatabase.UpdateThresholdProfileStatus(profileId, revision, "Approved", operatorId, now);
        AoiDatabase.RecordAuditEvent(
            "THRESHOLD_PROFILE_APPROVED",
            $"Threshold profile approved: {profileId}/{revision}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ThresholdProfile",
            relatedEntityId: $"{profileId}/{revision}");
        return AoiDatabase.GetThresholdProfile(profileId, revision)!;
    }

    public static ThresholdProfile DeployProfile(string profileId, string revision, UserRole role, string operatorId)
    {
        EnsureThresholdPermission(role, "deploy threshold profiles");
        var profile = AoiDatabase.GetThresholdProfile(profileId, revision)
            ?? throw new InvalidOperationException("Threshold profile was not found.");
        if (!string.Equals(profile.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(profile.Status, "Deployed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only approved threshold profiles can be deployed.");
        }

        AoiDatabase.DeployThresholdProfile(profile, operatorId);
        AoiDatabase.RecordAuditEvent(
            "THRESHOLD_PROFILE_DEPLOYED",
            $"Threshold profile deployed: {profileId}/{revision}; board={profile.BoardModel}; recipe={profile.RecipeName}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ThresholdProfile",
            relatedEntityId: $"{profileId}/{revision}");
        return AoiDatabase.GetThresholdProfile(profileId, revision)!;
    }

    /// <summary>
    /// Retires a profile revision: its deployments are deactivated so it no longer governs any
    /// verdicts, and its status becomes Retired so it can never be redeployed. Results it already
    /// decided keep their stamped profile id/revision, so retirement never breaks traceability.
    /// </summary>
    public static ThresholdProfile RetireProfile(string profileId, string revision, UserRole role, string operatorId, string reason)
    {
        EnsureThresholdPermission(role, "retire threshold profiles");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("A retire reason is required for traceability.");

        var profile = AoiDatabase.GetThresholdProfile(profileId, revision)
            ?? throw new InvalidOperationException("Threshold profile was not found.");

        AoiDatabase.RetireThresholdProfile(profileId, revision);
        AoiDatabase.RecordAuditEvent(
            "THRESHOLD_PROFILE_RETIRED",
            $"Threshold profile retired: {profileId}/{revision}; board={profile.BoardModel}; recipe={profile.RecipeName}; reason: {reason.Trim()}",
            operatorWithRole: operatorId,
            relatedEntityType: "ThresholdProfile",
            relatedEntityId: $"{profileId}/{revision}");
        return AoiDatabase.GetThresholdProfile(profileId, revision)!;
    }

    public static EffectiveThresholdRule? GetEffectiveThreshold(
        string boardModel,
        string boardProgram,
        string recipeName,
        string viewType,
        string roiType,
        string defectClass)
    {
        var profile = AoiDatabase.GetActiveThresholdProfile(boardModel, boardProgram, recipeName);
        if (profile is null)
            return null;

        var normalizedView = NormalizeScope(viewType);
        var normalizedRoiType = NormalizeScope(roiType);
        var normalizedDefect = NormalizeScope(BatchValidationService.NormalizeDefectClass(defectClass));
        var rule = profile.Rules
            .Where(rule => Matches(rule.ViewType, normalizedView) &&
                           Matches(rule.RoiType, normalizedRoiType) &&
                           Matches(rule.DefectClass, normalizedDefect))
            .OrderByDescending(rule => rule.SpecificityScore)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();

        if (rule is null)
            return null;

        return new EffectiveThresholdRule
        {
            Source = "Active threshold profile",
            ProfileId = profile.ProfileId,
            Revision = profile.Revision,
            ReviewThreshold = rule.ReviewThreshold,
            NgThreshold = rule.NgThreshold,
            ConfidenceThreshold = rule.ConfidenceThreshold,
            MinimumAreaPixels = rule.MinimumAreaPixels,
        };
    }

    public static ThresholdProfileEvidenceSummary ToEvidenceSummary(ThresholdProfile? profile)
    {
        if (profile is null)
            return new ThresholdProfileEvidenceSummary();

        var deployed = string.Equals(profile.Status, "Deployed", StringComparison.OrdinalIgnoreCase);
        return new ThresholdProfileEvidenceSummary
        {
            Status = profile.Status,
            ProfileId = profile.ProfileId,
            Revision = profile.Revision,
            BoardModel = profile.BoardModel,
            BoardProgram = profile.BoardProgram,
            RecipeName = profile.RecipeName,
            RecipeRevision = profile.RecipeRevision,
            SourceValidationRunId = profile.SourceValidationRunId,
            SourceFalseCallReductionRunId = profile.SourceFalseCallReductionRunId,
            CreatedBy = profile.CreatedBy,
            ApprovedBy = profile.ApprovedBy,
            ApprovedAtUtc = profile.ApprovedAtUtc,
            RuleCount = profile.Rules.Count,
            IsDeployed = deployed,
            EvidenceBoundary = deployed
                ? "Deployed threshold profile evidence is scoped to the customer-labeled validation and approval records that created it; it is not universal production proof."
                : "Threshold profile exists but is not deployed for production use.",
        };
    }

    public static ThresholdProfileEvidenceSummary GetActiveEvidenceSummary(
        string boardModel,
        string boardProgram,
        string recipeName)
    {
        var profile = AoiDatabase.GetActiveThresholdProfile(boardModel, boardProgram, recipeName)
            ?? AoiDatabase.GetActiveThresholdProfile(boardModel, boardProgram, "ANY")
            ?? AoiDatabase.GetActiveThresholdProfile("ANY", boardProgram, recipeName)
            ?? AoiDatabase.GetActiveThresholdProfile("ANY", boardProgram, "ANY")
            ?? AoiDatabase.GetActiveThresholdProfile("ANY", "ANY", "ANY");
        return ToEvidenceSummary(profile);
    }

    private static ThresholdProfile NewDraft(
        string boardModel,
        string boardProgram,
        string recipeName,
        string recipeRevision,
        string createdBy)
        => new()
        {
            ProfileId = $"TP-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Revision = "R0001",
            BoardModel = NormalizeScope(boardModel),
            BoardProgram = NormalizeScope(boardProgram),
            RecipeName = NormalizeScope(recipeName),
            RecipeRevision = NormalizeScope(recipeRevision),
            Status = "Draft",
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "UNKNOWN" : createdBy,
            CreatedAtUtc = DateTime.UtcNow,
        };

    private static ThresholdProfileRule DefaultRule(ThresholdProfile profile)
        => new()
        {
            ProfileId = profile.ProfileId,
            Revision = profile.Revision,
            ViewType = "Any",
            RoiType = "Any",
            DefectClass = "Any",
            ReviewThreshold = 8,
            NgThreshold = 18,
            ConfidenceThreshold = 0.65,
            MaxAllowedFalseCallRate = 1,
        };

    private static void ValidateOrThrow(ThresholdProfile profile)
    {
        var errors = ValidateThresholds(profile);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private static void EnsureThresholdPermission(UserRole role, string action)
    {
        if (!RoleAuthorization.CanChangeThresholds(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }

    private static bool Matches(string ruleValue, string actual)
        => string.Equals(NormalizeScope(ruleValue), "ANY", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(NormalizeScope(ruleValue), actual, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeScope(string? value)
        => string.IsNullOrWhiteSpace(value) ? "ANY" : value.Trim();

    private static string RuleLabel(ThresholdProfileRule rule)
        => string.Create(CultureInfo.InvariantCulture, $"{rule.ViewType}/{rule.RoiType}/{rule.DefectClass}");
}
