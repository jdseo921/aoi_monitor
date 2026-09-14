using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Headless surface for the threshold-profile lifecycle so the Stage 1 CLI testing kit can
/// run the same calibrated-threshold workflow the AI / Models and Settings screens offer:
/// draft from the latest false-call recommendation, approve, deploy, list. Every action goes
/// through ThresholdProfileService, so role gating and audit events are identical to the UI.
/// </summary>
public static class ThresholdProfileCommand
{
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 2 || IsHelp(args[1]))
        {
            WriteUsage(output);
            return args.Length < 2 ? 2 : 0;
        }

        var action = args[1].ToLowerInvariant();
        var values = ParseOptions(args.Skip(2), error);
        if (values is null)
        {
            WriteUsage(error);
            return 2;
        }

        try
        {
            return action switch
            {
                "draft" => Draft(values, output, error),
                "approve" => Transition(values, output, error, deploy: false),
                "deploy" => Transition(values, output, error, deploy: true),
                "retire" => Retire(values, output, error),
                "list" => List(output),
                _ => Unknown(action, error),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
    }

    private static int Draft(Dictionary<string, string> values, TextWriter output, TextWriter error)
    {
        if (!TryGetOperator(values, error, out var operatorId) || !TryGetRole(values, error, out var role))
            return 2;

        var run = AoiDatabase.GetLatestFalseCallReductionRun();
        if (run is null)
        {
            error.WriteLine("FAIL No false-call reduction run exists. Run stage1-exit (or Analyze Review Tradeoff) on a labeled dataset first.");
            return 1;
        }

        if (!string.Equals(run.Recommendation.Status, "VALID", StringComparison.OrdinalIgnoreCase) || run.Recommendation.Point is null)
        {
            error.WriteLine($"FAIL Latest false-call recommendation (run {run.Id}) is {run.Recommendation.Status}, not VALID. Only calibrated, constraint-meeting recommendations may become threshold profiles.");
            foreach (var message in run.Recommendation.Messages)
                error.WriteLine($"  - {message}");
            return 1;
        }

        if (!RoleAuthorization.CanChangeThresholds(role))
        {
            error.WriteLine($"FAIL {RoleAuthorization.DeniedMessage(role, "drafting threshold profiles")}");
            return 1;
        }

        var profile = ThresholdProfileService.CreateDraftFromFalseCallReductionRecommendation(
            run,
            values.GetValueOrDefault("board-model", "ANY"),
            values.GetValueOrDefault("board-program", "ANY"),
            values.GetValueOrDefault("recipe", "ANY"),
            values.GetValueOrDefault("recipe-revision", "ANY"),
            operatorId);

        output.WriteLine("OK Threshold profile draft created from the latest VALID false-call recommendation.");
        output.WriteLine($"Profile: {profile.ProfileId}/{profile.Revision}; status={profile.Status}");
        output.WriteLine($"Source: false-call run {run.Id} (batch run {run.BatchRunId?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}); mode={run.Recommendation.Mode}");
        var point = run.Recommendation.Point;
        output.WriteLine($"Selected threshold: {point.DifferenceThreshold:F3} (difference score {point.DifferenceThreshold * 100.0:F1}%); false-call={point.FalseCallRate:P1}; possible-escape={point.PossibleEscapeRate:P1}");
        output.WriteLine("Scope reminder: Stage 1 labeled-data calibration only; not production accuracy proof.");
        return 0;
    }

    private static int Transition(Dictionary<string, string> values, TextWriter output, TextWriter error, bool deploy)
    {
        if (!TryGetOperator(values, error, out var operatorId) || !TryGetRole(values, error, out var role))
            return 2;
        if (!values.TryGetValue("profile", out var profileId) || string.IsNullOrWhiteSpace(profileId))
        {
            error.WriteLine("FAIL --profile <id> is required.");
            return 2;
        }

        var revision = values.GetValueOrDefault("revision", "R0001");
        var profile = deploy
            ? ThresholdProfileService.DeployProfile(profileId, revision, role, operatorId)
            : ThresholdProfileService.ApproveProfile(profileId, revision, role, operatorId);

        output.WriteLine($"OK Threshold profile {(deploy ? "deployed" : "approved")}: {profile.ProfileId}/{profile.Revision}; status={profile.Status}");
        output.WriteLine($"Scope: board-model={profile.BoardModel}; board-program={profile.BoardProgram}; recipe={profile.RecipeName}");
        foreach (var rule in profile.Rules)
            output.WriteLine($"Rule: view={rule.ViewType}; roi={rule.RoiType}; class={rule.DefectClass}; Review >= {rule.ReviewThreshold:F1}%; NG >= {rule.NgThreshold:F1}%");
        return 0;
    }

    private static int Retire(Dictionary<string, string> values, TextWriter output, TextWriter error)
    {
        if (!TryGetOperator(values, error, out var operatorId) || !TryGetRole(values, error, out var role))
            return 2;
        if (!values.TryGetValue("profile", out var profileId) || string.IsNullOrWhiteSpace(profileId))
        {
            error.WriteLine("FAIL --profile <id> is required.");
            return 2;
        }
        if (!values.TryGetValue("reason", out var reason) || string.IsNullOrWhiteSpace(reason))
        {
            error.WriteLine("FAIL --reason <text> is required: retirement must be traceable.");
            return 2;
        }

        var revision = values.GetValueOrDefault("revision", "R0001");
        var profile = ThresholdProfileService.RetireProfile(profileId, revision, role, operatorId, reason);

        output.WriteLine($"OK Threshold profile retired: {profile.ProfileId}/{profile.Revision}; status={profile.Status}");
        output.WriteLine($"Scope: board-model={profile.BoardModel}; board-program={profile.BoardProgram}; recipe={profile.RecipeName}");
        output.WriteLine("Its deployments are deactivated; verdicts fall back to the next matching deployed profile or the policy defaults.");
        output.WriteLine("Results it already decided keep their stamped profile id/revision for traceability.");
        return 0;
    }

    private static int List(TextWriter output)
    {
        var profiles = AoiDatabase.GetThresholdProfiles();
        if (profiles.Count == 0)
        {
            output.WriteLine("No threshold profiles exist.");
            return 0;
        }

        foreach (var profile in profiles)
        {
            output.WriteLine($"{profile.ProfileId}/{profile.Revision} status={profile.Status} board-model={profile.BoardModel} board-program={profile.BoardProgram} recipe={profile.RecipeName} created-by={profile.CreatedBy}");
            foreach (var rule in profile.Rules)
                output.WriteLine($"  rule: view={rule.ViewType}; roi={rule.RoiType}; class={rule.DefectClass}; Review >= {rule.ReviewThreshold:F1}%; NG >= {rule.NgThreshold:F1}%");
        }

        return 0;
    }

    private static int Unknown(string action, TextWriter error)
    {
        error.WriteLine($"FAIL Unknown threshold-profile action: {action}");
        WriteUsage(error);
        return 2;
    }

    private static bool TryGetOperator(Dictionary<string, string> values, TextWriter error, out string operatorId)
    {
        operatorId = values.GetValueOrDefault("operator", string.Empty);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            error.WriteLine("FAIL --operator <id> is required for traceability.");
            return false;
        }

        return true;
    }

    private static bool TryGetRole(Dictionary<string, string> values, TextWriter error, out UserRole role)
    {
        var roleText = values.GetValueOrDefault("role", nameof(UserRole.Engineer));
        if (!Enum.TryParse(roleText, ignoreCase: true, out role))
        {
            error.WriteLine($"FAIL Unknown role: {roleText}. Use Operator, Engineer, or Admin.");
            return false;
        }

        return true;
    }

    private static Dictionary<string, string>? ParseOptions(IEnumerable<string> args, TextWriter error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
            {
                error.WriteLine($"FAIL Malformed option: {list[i]}");
                return null;
            }

            values[list[i][2..]] = list[i + 1];
            i++;
        }

        return values;
    }

    private static bool IsHelp(string arg)
        => arg is "--help" or "-h" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools threshold-profile draft   --operator <id> [--role Engineer|Admin]");
        writer.WriteLine("                                              [--board-model ANY] [--board-program ANY] [--recipe ANY] [--recipe-revision ANY]");
        writer.WriteLine("      Creates a Draft profile from the LATEST false-call reduction run; the recommendation must be VALID.");
        writer.WriteLine("  AOI_Monitor.Tools threshold-profile approve --profile <id> [--revision R0001] --operator <id> [--role Engineer|Admin]");
        writer.WriteLine("  AOI_Monitor.Tools threshold-profile deploy  --profile <id> [--revision R0001] --operator <id> [--role Engineer|Admin]");
        writer.WriteLine("  AOI_Monitor.Tools threshold-profile retire  --profile <id> [--revision R0001] --operator <id> [--role Engineer|Admin] --reason <text>");
        writer.WriteLine("      Deactivates the profile's deployments and blocks redeployment; already-decided results keep their stamp.");
        writer.WriteLine("  AOI_Monitor.Tools threshold-profile list");
        writer.WriteLine();
        writer.WriteLine("  Draft/approve/deploy/retire are Engineer/Admin actions; every step writes an audit event.");
        writer.WriteLine("  A deployed profile's rules govern pixel-difference verdict bands (full-frame and recipe-ROI paths)");
        writer.WriteLine("  and are stamped into every result they decide. Stage 1 labeled-data calibration only.");
    }
}
