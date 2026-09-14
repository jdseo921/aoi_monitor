using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public static partial class AoiDatabase
{
    public static void SaveThresholdProfile(ThresholdProfile profile)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ThresholdProfiles
                (ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                 SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc)
            VALUES
                ($profileId, $revision, $boardModel, $boardProgram, $recipeName, $recipeRevision, $status,
                 $sourceValidationRunId, $sourceFalseCallReductionRunId, $createdBy, $createdAtUtc, $approvedBy, $approvedAtUtc)
            ON CONFLICT(ProfileId, Revision) DO UPDATE SET
                BoardModel = excluded.BoardModel,
                BoardProgram = excluded.BoardProgram,
                RecipeName = excluded.RecipeName,
                RecipeRevision = excluded.RecipeRevision,
                Status = excluded.Status,
                SourceValidationRunId = excluded.SourceValidationRunId,
                SourceFalseCallReductionRunId = excluded.SourceFalseCallReductionRunId,
                CreatedBy = excluded.CreatedBy,
                CreatedAtUtc = excluded.CreatedAtUtc,
                ApprovedBy = excluded.ApprovedBy,
                ApprovedAtUtc = excluded.ApprovedAtUtc;
            """;
        BindThresholdProfile(command, profile);
        command.ExecuteNonQuery();

        using var deleteRules = connection.CreateCommand();
        deleteRules.Transaction = transaction;
        deleteRules.CommandText = "DELETE FROM ThresholdProfileRules WHERE ProfileId = $profileId AND Revision = $revision;";
        deleteRules.Parameters.AddWithValue("$profileId", profile.ProfileId);
        deleteRules.Parameters.AddWithValue("$revision", profile.Revision);
        deleteRules.ExecuteNonQuery();

        foreach (var rule in profile.Rules)
        {
            using var ruleCommand = connection.CreateCommand();
            ruleCommand.Transaction = transaction;
            ruleCommand.CommandText =
                """
                INSERT INTO ThresholdProfileRules
                    (ProfileId, Revision, ViewType, RoiType, DefectClass, ReviewThreshold, NgThreshold,
                     ConfidenceThreshold, MinimumAreaPixels, MaxAllowedFalseCallRate)
                VALUES
                    ($profileId, $revision, $viewType, $roiType, $defectClass, $reviewThreshold, $ngThreshold,
                     $confidenceThreshold, $minimumAreaPixels, $maxAllowedFalseCallRate);
                """;
            BindThresholdProfileRule(ruleCommand, profile, rule);
            ruleCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static ThresholdProfile? GetThresholdProfile(string profileId, string revision)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                   SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc
            FROM ThresholdProfiles
            WHERE ProfileId = $profileId AND Revision = $revision
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var profile = ReadThresholdProfile(reader);
        profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
        return profile;
    }

    public static IReadOnlyList<ThresholdProfile> GetThresholdProfiles()
    {
        EnsureInitialized();

        var profiles = new List<ThresholdProfile>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                   SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc
            FROM ThresholdProfiles
            ORDER BY datetime(CreatedAtUtc) DESC, ProfileId ASC, Revision DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profile = ReadThresholdProfile(reader);
            profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
            profiles.Add(profile);
        }

        return profiles;
    }

    public static ThresholdProfile? GetActiveThresholdProfile(string boardModel, string boardProgram, string recipeName)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.ProfileId, p.Revision, p.BoardModel, p.BoardProgram, p.RecipeName, p.RecipeRevision, p.Status,
                   p.SourceValidationRunId, p.SourceFalseCallReductionRunId, p.CreatedBy, p.CreatedAtUtc, p.ApprovedBy, p.ApprovedAtUtc
            FROM ThresholdProfileDeployments d
            INNER JOIN ThresholdProfiles p ON p.ProfileId = d.ProfileId AND p.Revision = d.Revision
            WHERE d.IsActive = 1
              AND (d.BoardModel = $boardModel OR d.BoardModel = 'ANY')
              AND (d.BoardProgram = $boardProgram OR d.BoardProgram = 'ANY')
              AND (d.RecipeName = $recipeName OR d.RecipeName = 'ANY')
            ORDER BY
              CASE WHEN d.BoardModel = $boardModel THEN 1 ELSE 0 END +
              CASE WHEN d.BoardProgram = $boardProgram THEN 1 ELSE 0 END +
              CASE WHEN d.RecipeName = $recipeName THEN 1 ELSE 0 END DESC,
              datetime(d.DeployedAtUtc) DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(boardModel));
        command.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(boardProgram));
        command.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(recipeName));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var profile = ReadThresholdProfile(reader);
        profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
        return profile;
    }

    public static void UpdateThresholdProfileStatus(string profileId, string revision, string status, string? approvedBy = null, DateTime? approvedAtUtc = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ThresholdProfiles
            SET Status = $status,
                ApprovedBy = COALESCE($approvedBy, ApprovedBy),
                ApprovedAtUtc = COALESCE($approvedAtUtc, ApprovedAtUtc)
            WHERE ProfileId = $profileId AND Revision = $revision;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$approvedBy", string.IsNullOrWhiteSpace(approvedBy) ? DBNull.Value : approvedBy);
        command.Parameters.AddWithValue("$approvedAtUtc", approvedAtUtc is { } at ? at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    public static void DeployThresholdProfile(ThresholdProfile profile, string deployedBy)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deactivate = connection.CreateCommand();
        deactivate.Transaction = transaction;
        deactivate.CommandText =
            """
            UPDATE ThresholdProfileDeployments
            SET IsActive = 0
            WHERE BoardModel = $boardModel AND BoardProgram = $boardProgram AND RecipeName = $recipeName;
            """;
        deactivate.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(profile.BoardModel));
        deactivate.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(profile.BoardProgram));
        deactivate.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(profile.RecipeName));
        deactivate.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO ThresholdProfileDeployments
                (ProfileId, Revision, BoardModel, BoardProgram, RecipeName, DeployedAtUtc, DeployedBy, IsActive)
            VALUES
                ($profileId, $revision, $boardModel, $boardProgram, $recipeName, $deployedAtUtc, $deployedBy, 1);
            """;
        insert.Parameters.AddWithValue("$profileId", profile.ProfileId);
        insert.Parameters.AddWithValue("$revision", profile.Revision);
        insert.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(profile.BoardModel));
        insert.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(profile.BoardProgram));
        insert.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(profile.RecipeName));
        insert.Parameters.AddWithValue("$deployedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$deployedBy", deployedBy);
        insert.ExecuteNonQuery();

        using var updateProfile = connection.CreateCommand();
        updateProfile.Transaction = transaction;
        updateProfile.CommandText = "UPDATE ThresholdProfiles SET Status = 'Deployed' WHERE ProfileId = $profileId AND Revision = $revision;";
        updateProfile.Parameters.AddWithValue("$profileId", profile.ProfileId);
        updateProfile.Parameters.AddWithValue("$revision", profile.Revision);
        updateProfile.ExecuteNonQuery();

        transaction.Commit();
    }

    public static void RetireThresholdProfile(string profileId, string revision)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deactivate = connection.CreateCommand();
        deactivate.Transaction = transaction;
        deactivate.CommandText =
            """
            UPDATE ThresholdProfileDeployments
            SET IsActive = 0
            WHERE ProfileId = $profileId AND Revision = $revision;
            """;
        deactivate.Parameters.AddWithValue("$profileId", profileId);
        deactivate.Parameters.AddWithValue("$revision", revision);
        deactivate.ExecuteNonQuery();

        using var updateProfile = connection.CreateCommand();
        updateProfile.Transaction = transaction;
        updateProfile.CommandText = "UPDATE ThresholdProfiles SET Status = 'Retired' WHERE ProfileId = $profileId AND Revision = $revision;";
        updateProfile.Parameters.AddWithValue("$profileId", profileId);
        updateProfile.Parameters.AddWithValue("$revision", revision);
        updateProfile.ExecuteNonQuery();

        transaction.Commit();
    }

    public static long RecordFalseCallReductionRun(FalseCallReductionRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var selected = run.Recommendation.Point;
        var auditEventId = RecordAuditEvent(
            "FALSE_CALL_RECOMMENDATION",
            selected is null
                ? $"False-call reduction recommendation generated: status={run.Recommendation.Status}; mode={run.Recommendation.Mode}."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"False-call reduction recommendation generated: status={run.Recommendation.Status}; mode={run.Recommendation.Mode}; threshold={selected.ConfidenceThreshold:F3}; falseCallRate={selected.FalseCallRate:P1}; possibleEscapes={selected.FalseNegative}."),
            operatorWithRole: effectiveOperator,
            relatedEntityType: "FalseCallReductionRun",
            relatedEntityId: run.BatchRunId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO FalseCallReductionRuns
                (BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                 CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                 SelectedFalseCallRate, SelectedPossibleEscapeRate, SelectedReviewRate,
                 SelectedManualReviewMinutes, SelectedPossibleEscapeCount, RecommendationMessagesJson,
                 OperatorId, AuditEventId)
            VALUES
                ($batchRunId, $createdAtUtc, $engineName, $modelVersion, $modelId, $modelSha256,
                 $criteriaJson, $recommendationStatus, $recommendationMode, $selectedThreshold,
                 $selectedFalseCallRate, $selectedPossibleEscapeRate, $selectedReviewRate,
                 $selectedManualReviewMinutes, $selectedPossibleEscapeCount, $recommendationMessagesJson,
                 $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$batchRunId", run.BatchRunId is { } batchRunId ? (object)batchRunId : DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$engineName", run.EngineName);
        command.Parameters.AddWithValue("$modelVersion", run.ModelVersion);
        command.Parameters.AddWithValue("$modelId", run.ModelId);
        command.Parameters.AddWithValue("$modelSha256", run.ModelSha256);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$recommendationStatus", run.Recommendation.Status);
        command.Parameters.AddWithValue("$recommendationMode", run.Recommendation.Mode);
        command.Parameters.AddWithValue("$selectedThreshold", selected is null ? DBNull.Value : selected.ConfidenceThreshold);
        command.Parameters.AddWithValue("$selectedFalseCallRate", selected is null ? DBNull.Value : selected.FalseCallRate);
        command.Parameters.AddWithValue("$selectedPossibleEscapeRate", selected is null ? DBNull.Value : selected.PossibleEscapeRate);
        command.Parameters.AddWithValue("$selectedReviewRate", selected is null ? DBNull.Value : selected.ReviewRate);
        command.Parameters.AddWithValue("$selectedManualReviewMinutes", selected is null ? DBNull.Value : selected.EstimatedManualReviewMinutes);
        command.Parameters.AddWithValue("$selectedPossibleEscapeCount", selected is null ? DBNull.Value : selected.FalseNegative);
        command.Parameters.AddWithValue("$recommendationMessagesJson", JsonSerializer.Serialize(run.Recommendation.Messages));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);

        var runId = (long)(command.ExecuteScalar() ?? 0L);
        foreach (var point in run.Points)
        {
            using var pointCommand = connection.CreateCommand();
            pointCommand.Transaction = transaction;
            pointCommand.CommandText =
                """
                INSERT INTO FalseCallReductionPoints
                    (RunId, ConfidenceThreshold, DifferenceThreshold, TruePositive, TrueNegative,
                     FalsePositive, FalseNegative, Precision, Recall, FalseCallRate, PossibleEscapeRate,
                     ReviewRate, NgRate, ReviewCount, NgCount, EstimatedManualReviewMinutes,
                     MeetsConstraints, Status)
                VALUES
                    ($runId, $confidenceThreshold, $differenceThreshold, $truePositive, $trueNegative,
                     $falsePositive, $falseNegative, $precision, $recall, $falseCallRate, $possibleEscapeRate,
                     $reviewRate, $ngRate, $reviewCount, $ngCount, $estimatedManualReviewMinutes,
                     $meetsConstraints, $status);
                """;
            pointCommand.Parameters.AddWithValue("$runId", runId);
            BindFalseCallReductionPoint(pointCommand, point);
            pointCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static FalseCallReductionRun? GetLatestFalseCallReductionRun(long? batchRunId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = batchRunId is null
            ? """
              SELECT Id, BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                     CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                     RecommendationMessagesJson
              FROM FalseCallReductionRuns
              ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
              LIMIT 1;
              """
            : """
              SELECT Id, BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                     CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                     RecommendationMessagesJson
              FROM FalseCallReductionRuns
              WHERE BatchRunId = $batchRunId
              ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
              LIMIT 1;
              """;
        if (batchRunId is { } id)
            command.Parameters.AddWithValue("$batchRunId", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadFalseCallReductionRun(reader);
        run.Points = GetFalseCallReductionPoints(run.Id);
        run.Recommendation.Point = run.Points
            .OrderBy(point => Math.Abs(point.ConfidenceThreshold - (run.Recommendation.Point?.ConfidenceThreshold ?? point.ConfidenceThreshold)))
            .FirstOrDefault(point => string.Equals(point.Status, run.Recommendation.Status, StringComparison.OrdinalIgnoreCase)) ?? run.Points.FirstOrDefault();
        return run;
    }

    public static long RecordCameraAcceptanceRun(CameraAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "CAMERA_ACCEPTANCE_TEST",
            $"Camera acceptance test completed: status={run.Status}; readiness={run.FactoryReadinessStatus}; adapter={run.AdapterName}; realHardware={run.IsRealHardware}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "CameraAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO CameraAcceptanceRuns
                (CreatedAtUtc, AdapterName, SourceKey, SettingsSummary, CriteriaJson,
                 Status, FactoryReadinessStatus, IsRealHardware, TotalRequestedFrames,
                 TotalReceivedFrames, DroppedFrameCount, TriggerFailureCount, TimeoutCount,
                 MaxConnectMs, MaxFirstFrameMs, AverageFrameIntervalMs, WarningsJson,
                 FailuresJson, ViewMetricsJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $adapterName, $sourceKey, $settingsSummary, $criteriaJson,
                 $status, $factoryReadinessStatus, $isRealHardware, $totalRequestedFrames,
                 $totalReceivedFrames, $droppedFrameCount, $triggerFailureCount, $timeoutCount,
                 $maxConnectMs, $maxFirstFrameMs, $averageFrameIntervalMs, $warningsJson,
                 $failuresJson, $viewMetricsJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$adapterName", run.AdapterName);
        command.Parameters.AddWithValue("$sourceKey", run.SourceKey);
        command.Parameters.AddWithValue("$settingsSummary", run.SettingsSummary);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$factoryReadinessStatus", run.FactoryReadinessStatus);
        command.Parameters.AddWithValue("$isRealHardware", run.IsRealHardware ? 1 : 0);
        command.Parameters.AddWithValue("$totalRequestedFrames", run.TotalRequestedFrames);
        command.Parameters.AddWithValue("$totalReceivedFrames", run.TotalReceivedFrames);
        command.Parameters.AddWithValue("$droppedFrameCount", run.DroppedFrameCount);
        command.Parameters.AddWithValue("$triggerFailureCount", run.TriggerFailureCount);
        command.Parameters.AddWithValue("$timeoutCount", run.TimeoutCount);
        command.Parameters.AddWithValue("$maxConnectMs", run.MaxConnectMs);
        command.Parameters.AddWithValue("$maxFirstFrameMs", run.MaxFirstFrameMs);
        command.Parameters.AddWithValue("$averageFrameIntervalMs", run.AverageFrameIntervalMs);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$viewMetricsJson", JsonSerializer.Serialize(run.ViewMetrics));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var frame in run.Frames)
        {
            using var frameCommand = connection.CreateCommand();
            frameCommand.Transaction = transaction;
            frameCommand.CommandText =
                """
                INSERT INTO CameraAcceptanceFrames
                    (RunId, ViewType, Sequence, FrameId, CameraId, CapturedAtUtc,
                     Width, Height, PixelFormat, SourceKind, IsSimulated, LatencyMs,
                     IntervalMs, MetadataValid, Message)
                VALUES
                    ($runId, $viewType, $sequence, $frameId, $cameraId, $capturedAtUtc,
                     $width, $height, $pixelFormat, $sourceKind, $isSimulated, $latencyMs,
                     $intervalMs, $metadataValid, $message);
                """;
            frameCommand.Parameters.AddWithValue("$runId", runId);
            frameCommand.Parameters.AddWithValue("$viewType", frame.ViewType);
            frameCommand.Parameters.AddWithValue("$sequence", frame.Sequence);
            frameCommand.Parameters.AddWithValue("$frameId", frame.FrameId);
            frameCommand.Parameters.AddWithValue("$cameraId", frame.CameraId);
            frameCommand.Parameters.AddWithValue("$capturedAtUtc", frame.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            frameCommand.Parameters.AddWithValue("$width", frame.Width);
            frameCommand.Parameters.AddWithValue("$height", frame.Height);
            frameCommand.Parameters.AddWithValue("$pixelFormat", frame.PixelFormat);
            frameCommand.Parameters.AddWithValue("$sourceKind", frame.SourceKind);
            frameCommand.Parameters.AddWithValue("$isSimulated", frame.IsSimulated ? 1 : 0);
            frameCommand.Parameters.AddWithValue("$latencyMs", frame.LatencyMs);
            frameCommand.Parameters.AddWithValue("$intervalMs", frame.IntervalMs);
            frameCommand.Parameters.AddWithValue("$metadataValid", frame.MetadataValid ? 1 : 0);
            frameCommand.Parameters.AddWithValue("$message", frame.Message);
            frameCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static CameraAcceptanceRun? GetLatestCameraAcceptanceRun(bool realHardwareOnly = false)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, AdapterName, SourceKey, SettingsSummary, CriteriaJson,
                   Status, FactoryReadinessStatus, IsRealHardware, TotalRequestedFrames,
                   TotalReceivedFrames, DroppedFrameCount, TriggerFailureCount, TimeoutCount,
                   MaxConnectMs, MaxFirstFrameMs, AverageFrameIntervalMs, WarningsJson,
                   FailuresJson, ViewMetricsJson
            FROM CameraAcceptanceRuns
            WHERE ($realHardwareOnly = 0 OR IsRealHardware = 1)
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$realHardwareOnly", realHardwareOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadCameraAcceptanceRun(reader);
        run.Frames = GetCameraAcceptanceFrames(run.Id).ToList();
        return run;
    }

    public static long RecordLightingAcceptanceRun(LightingAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "LIGHTING_ACCEPTANCE_TEST",
            $"Lighting sync acceptance completed: status={run.Status}; mode={run.Mode}; simulated={run.IsSimulated}; steps={run.PassedStepCount}/{run.StepCount}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "LightingAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO LightingAcceptanceRuns
                (CreatedAtUtc, ControllerName, Mode, SettingsSummary, CriteriaJson, Status,
                 IsSimulated, StepCount, PassedStepCount, FailedStepCount, MaxCommandLatencyMs,
                 MaxTriggerToFrameLatencyMs, WarningsJson, FailuresJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $controllerName, $mode, $settingsSummary, $criteriaJson, $status,
                 $isSimulated, $stepCount, $passedStepCount, $failedStepCount, $maxCommandLatencyMs,
                 $maxTriggerToFrameLatencyMs, $warningsJson, $failuresJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$controllerName", run.ControllerName);
        command.Parameters.AddWithValue("$mode", run.Mode);
        command.Parameters.AddWithValue("$settingsSummary", run.SettingsSummary);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$isSimulated", run.IsSimulated ? 1 : 0);
        command.Parameters.AddWithValue("$stepCount", run.StepCount);
        command.Parameters.AddWithValue("$passedStepCount", run.PassedStepCount);
        command.Parameters.AddWithValue("$failedStepCount", run.FailedStepCount);
        command.Parameters.AddWithValue("$maxCommandLatencyMs", run.MaxCommandLatencyMs);
        command.Parameters.AddWithValue("$maxTriggerToFrameLatencyMs", run.MaxTriggerToFrameLatencyMs);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var step in run.Steps)
        {
            using var stepCommand = connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText =
                """
                INSERT INTO LightingAcceptanceSteps
                    (RunId, ViewType, ProgramName, CommandText, CommandLatencyMs,
                     TriggerToFrameLatencyMs, CommandAccepted, FrameReceived, FrameId,
                     CameraId, Status, Message)
                VALUES
                    ($runId, $viewType, $programName, $commandText, $commandLatencyMs,
                     $triggerToFrameLatencyMs, $commandAccepted, $frameReceived, $frameId,
                     $cameraId, $status, $message);
                """;
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$viewType", step.ViewType);
            stepCommand.Parameters.AddWithValue("$programName", step.ProgramName);
            stepCommand.Parameters.AddWithValue("$commandText", step.CommandText);
            stepCommand.Parameters.AddWithValue("$commandLatencyMs", step.CommandLatencyMs);
            stepCommand.Parameters.AddWithValue("$triggerToFrameLatencyMs", step.TriggerToFrameLatencyMs);
            stepCommand.Parameters.AddWithValue("$commandAccepted", step.CommandAccepted ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$frameReceived", step.FrameReceived ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$frameId", step.FrameId);
            stepCommand.Parameters.AddWithValue("$cameraId", step.CameraId);
            stepCommand.Parameters.AddWithValue("$status", step.Status);
            stepCommand.Parameters.AddWithValue("$message", step.Message);
            stepCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static LightingAcceptanceRun? GetLatestLightingAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ControllerName, Mode, SettingsSummary, CriteriaJson,
                   Status, IsSimulated, StepCount, PassedStepCount, FailedStepCount,
                   MaxCommandLatencyMs, MaxTriggerToFrameLatencyMs, WarningsJson, FailuresJson
            FROM LightingAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadLightingAcceptanceRun(reader);
        run.Steps = GetLightingAcceptanceSteps(run.Id).ToList();
        return run;
    }

    public static long RecordProfile3DAcceptanceRun(Profile3DAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "PROFILE_3D_ACCEPTANCE_TEST",
            $"3D profile acceptance completed: status={run.Status}; readiness={run.FactoryReadinessStatus}; source={run.SourceName}; simulated={run.IsSimulated}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "Profile3DAcceptanceRun");

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Profile3DAcceptanceRuns
                (CreatedAtUtc, SourceName, SourceKind, IsSimulated, Status, FactoryReadinessStatus,
                 AcquisitionMs, Width, Height, Unit, XPitchMicrons, YPitchMicrons, MissingHeightCount,
                 NaNHeightCount, FrameId, CriteriaJson, DiagnosticsJson, WarningsJson, FailuresJson,
                 OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $sourceName, $sourceKind, $isSimulated, $status, $factoryReadinessStatus,
                 $acquisitionMs, $width, $height, $unit, $xPitchMicrons, $yPitchMicrons, $missingHeightCount,
                 $nanHeightCount, $frameId, $criteriaJson, $diagnosticsJson, $warningsJson, $failuresJson,
                 $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceName", run.SourceName);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$isSimulated", run.IsSimulated ? 1 : 0);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$factoryReadinessStatus", run.FactoryReadinessStatus);
        command.Parameters.AddWithValue("$acquisitionMs", run.AcquisitionMs);
        command.Parameters.AddWithValue("$width", run.Width);
        command.Parameters.AddWithValue("$height", run.Height);
        command.Parameters.AddWithValue("$unit", run.Unit);
        command.Parameters.AddWithValue("$xPitchMicrons", run.XPitchMicrons);
        command.Parameters.AddWithValue("$yPitchMicrons", run.YPitchMicrons);
        command.Parameters.AddWithValue("$missingHeightCount", run.MissingHeightCount);
        command.Parameters.AddWithValue("$nanHeightCount", run.NaNHeightCount);
        command.Parameters.AddWithValue("$frameId", run.FrameId);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$diagnosticsJson", JsonSerializer.Serialize(run.Diagnostics));
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static Profile3DAcceptanceRun? GetLatestProfile3DAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, SourceName, SourceKind, IsSimulated, Status,
                   FactoryReadinessStatus, AcquisitionMs, Width, Height, Unit,
                   XPitchMicrons, YPitchMicrons, MissingHeightCount, NaNHeightCount,
                   FrameId, CriteriaJson, DiagnosticsJson, WarningsJson, FailuresJson
            FROM Profile3DAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile3DAcceptanceRun(reader) : null;
    }

    public static long RecordRobotAcceptanceRun(RobotAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "ROBOT_ACCEPTANCE_TEST",
            $"Robot acceptance completed: status={run.Status}; source={run.SourceKind}; controller={run.ControllerName}; cycleMs={run.FullCycleMs:F1}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "RobotAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO RobotAcceptanceRuns
                (CreatedAtUtc, ControllerName, EmergencyStopName, SafetyControllerName, SafetySourceKind, SourceKind, CriteriaJson,
                 Status, FinalState, LoadMs, MoveToInspectMs, InspectionMs, UnloadMs,
                 FullCycleMs, InvalidTransitionRejected, EmergencyStopBlocked, SafetyFaultBlocked, ResetReturnedIdle,
                 AuditEventCount, WarningsJson, FailuresJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $controllerName, $emergencyStopName, $safetyControllerName, $safetySourceKind, $sourceKind, $criteriaJson,
                 $status, $finalState, $loadMs, $moveToInspectMs, $inspectionMs, $unloadMs,
                 $fullCycleMs, $invalidTransitionRejected, $emergencyStopBlocked, $safetyFaultBlocked, $resetReturnedIdle,
                 $auditEventCount, $warningsJson, $failuresJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$controllerName", run.ControllerName);
        command.Parameters.AddWithValue("$emergencyStopName", run.EmergencyStopName);
        command.Parameters.AddWithValue("$safetyControllerName", run.SafetyControllerName);
        command.Parameters.AddWithValue("$safetySourceKind", run.SafetySourceKind);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$finalState", run.FinalState);
        command.Parameters.AddWithValue("$loadMs", run.LoadMs);
        command.Parameters.AddWithValue("$moveToInspectMs", run.MoveToInspectMs);
        command.Parameters.AddWithValue("$inspectionMs", run.InspectionMs);
        command.Parameters.AddWithValue("$unloadMs", run.UnloadMs);
        command.Parameters.AddWithValue("$fullCycleMs", run.FullCycleMs);
        command.Parameters.AddWithValue("$invalidTransitionRejected", run.InvalidTransitionRejected ? 1 : 0);
        command.Parameters.AddWithValue("$emergencyStopBlocked", run.EmergencyStopBlocked ? 1 : 0);
        command.Parameters.AddWithValue("$safetyFaultBlocked", run.SafetyFaultBlocked ? 1 : 0);
        command.Parameters.AddWithValue("$resetReturnedIdle", run.ResetReturnedIdle ? 1 : 0);
        command.Parameters.AddWithValue("$auditEventCount", run.AuditEventCount);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var step in run.Steps)
        {
            using var stepCommand = connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText =
                """
                INSERT INTO RobotAcceptanceSteps
                    (RunId, StepName, FromState, ToState, ElapsedMs, Accepted, Status, Message)
                VALUES
                    ($runId, $stepName, $fromState, $toState, $elapsedMs, $accepted, $status, $message);
                """;
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$stepName", step.StepName);
            stepCommand.Parameters.AddWithValue("$fromState", step.FromState);
            stepCommand.Parameters.AddWithValue("$toState", step.ToState);
            stepCommand.Parameters.AddWithValue("$elapsedMs", step.ElapsedMs);
            stepCommand.Parameters.AddWithValue("$accepted", step.Accepted ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$status", step.Status);
            stepCommand.Parameters.AddWithValue("$message", step.Message);
            stepCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static RobotAcceptanceRun? GetLatestRobotAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ControllerName, EmergencyStopName, SafetyControllerName, SafetySourceKind, SourceKind, CriteriaJson,
                   Status, FinalState, LoadMs, MoveToInspectMs, InspectionMs, UnloadMs,
                   FullCycleMs, InvalidTransitionRejected, EmergencyStopBlocked, SafetyFaultBlocked, ResetReturnedIdle,
                   AuditEventCount, WarningsJson, FailuresJson
            FROM RobotAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadRobotAcceptanceRun(reader);
        run.Steps = GetRobotAcceptanceSteps(run.Id).ToList();
        return run;
    }

    public static long RecordSoakTestRun(SoakTestResult run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? run.OperatorId : operatorId;
        var auditEventId = RecordAuditEvent(
            "SOAK_TEST",
            $"Soak test persisted: run={run.RunId}; cycles={run.TotalCycles}; failures={run.FailedCycles}; canceled={run.WasCanceled}; factoryEvidence={run.IsCompletedFactoryEvidence}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "SoakTestRun",
            relatedEntityId: run.RunId);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO SoakTestRuns
                (RunId, StartedAtUtc, EndedAtUtc, ImageFolder, OutputFolder, EngineKey, EngineName,
                 EngineVersion, SourceKind, IsRealCameraSource, ProfileName, RequestedDurationSeconds,
                 ActualDurationSeconds, DelayBetweenInspectionsMs, OperatorId, BoardModel, LotId,
                 WasCanceled, TotalCycles, SuccessfulCycles, FailedCycles, AverageInspectionMs,
                 MinInspectionMs, MaxInspectionMs, P95InspectionMs, CountOverOneSecond,
                 StartManagedMemoryMb, EndManagedMemoryMb, StartWorkingSetMb, EndWorkingSetMb,
                 PeakWorkingSetMb, IsCompletedFactoryEvidence, ErrorsJson, AuditEventId,
                 AverageTotalCycleMs, MaxTotalCycleMs, P95TotalCycleMs, CancellationReason,
                 FirstCriticalError, MemoryWarningsJson)
            VALUES
                ($runId, $startedAtUtc, $endedAtUtc, $imageFolder, $outputFolder, $engineKey, $engineName,
                 $engineVersion, $sourceKind, $isRealCameraSource, $profileName, $requestedDurationSeconds,
                 $actualDurationSeconds, $delayBetweenInspectionsMs, $operatorId, $boardModel, $lotId,
                 $wasCanceled, $totalCycles, $successfulCycles, $failedCycles, $averageInspectionMs,
                 $minInspectionMs, $maxInspectionMs, $p95InspectionMs, $countOverOneSecond,
                 $startManagedMemoryMb, $endManagedMemoryMb, $startWorkingSetMb, $endWorkingSetMb,
                 $peakWorkingSetMb, $isCompletedFactoryEvidence, $errorsJson, $auditEventId,
                 $averageTotalCycleMs, $maxTotalCycleMs, $p95TotalCycleMs, $cancellationReason,
                 $firstCriticalError, $memoryWarningsJson);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$startedAtUtc", run.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endedAtUtc", run.EndTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$imageFolder", run.ImageFolder);
        command.Parameters.AddWithValue("$outputFolder", run.OutputFolder);
        command.Parameters.AddWithValue("$engineKey", run.EngineKey);
        command.Parameters.AddWithValue("$engineName", run.EngineName);
        command.Parameters.AddWithValue("$engineVersion", run.EngineVersion);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$isRealCameraSource", run.IsRealCameraSource ? 1 : 0);
        command.Parameters.AddWithValue("$profileName", run.ProfileName);
        command.Parameters.AddWithValue("$requestedDurationSeconds", run.RequestedDuration.TotalSeconds);
        command.Parameters.AddWithValue("$actualDurationSeconds", Math.Max(0, run.ActualDuration.TotalSeconds));
        command.Parameters.AddWithValue("$delayBetweenInspectionsMs", run.DelayBetweenInspections.TotalMilliseconds);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$boardModel", run.BoardModel);
        command.Parameters.AddWithValue("$lotId", run.LotId);
        command.Parameters.AddWithValue("$wasCanceled", run.WasCanceled ? 1 : 0);
        command.Parameters.AddWithValue("$totalCycles", run.TotalCycles);
        command.Parameters.AddWithValue("$successfulCycles", run.SuccessfulCycles);
        command.Parameters.AddWithValue("$failedCycles", run.FailedCycles);
        command.Parameters.AddWithValue("$averageInspectionMs", run.AverageInspectionMilliseconds);
        command.Parameters.AddWithValue("$minInspectionMs", run.MinInspectionMilliseconds);
        command.Parameters.AddWithValue("$maxInspectionMs", run.MaxInspectionMilliseconds);
        command.Parameters.AddWithValue("$p95InspectionMs", run.P95InspectionMilliseconds);
        command.Parameters.AddWithValue("$countOverOneSecond", run.CountOverOneSecond);
        command.Parameters.AddWithValue("$startManagedMemoryMb", run.StartManagedMemoryMegabytes);
        command.Parameters.AddWithValue("$endManagedMemoryMb", run.EndManagedMemoryMegabytes);
        command.Parameters.AddWithValue("$startWorkingSetMb", run.StartWorkingSetMegabytes);
        command.Parameters.AddWithValue("$endWorkingSetMb", run.EndWorkingSetMegabytes);
        command.Parameters.AddWithValue("$peakWorkingSetMb", run.PeakWorkingSetMegabytes);
        command.Parameters.AddWithValue("$isCompletedFactoryEvidence", run.IsCompletedFactoryEvidence ? 1 : 0);
        command.Parameters.AddWithValue("$errorsJson", JsonSerializer.Serialize(run.Errors));
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        command.Parameters.AddWithValue("$averageTotalCycleMs", run.AverageTotalCycleMilliseconds);
        command.Parameters.AddWithValue("$maxTotalCycleMs", run.MaxTotalCycleMilliseconds);
        command.Parameters.AddWithValue("$p95TotalCycleMs", run.P95TotalCycleMilliseconds);
        command.Parameters.AddWithValue("$cancellationReason", run.CancellationReason);
        command.Parameters.AddWithValue("$firstCriticalError", run.FirstCriticalError);
        command.Parameters.AddWithValue("$memoryWarningsJson", JsonSerializer.Serialize(run.MemoryWarnings));

        var runRowId = (long)(command.ExecuteScalar() ?? 0L);
        run.Id = runRowId;

        using var iterationCommand = connection.CreateCommand();
        iterationCommand.Transaction = transaction;
        iterationCommand.CommandText =
            """
            INSERT INTO SoakTestIterations
                (RunId, CycleNumber, TimestampUtc, FrameId, ImagePath, EngineName, Verdict,
                 TotalInspectionMs, WorkingSetMb, Success, Message, Error, TotalCycleMs, ExceptionCategory)
            VALUES
                ($runId, $cycleNumber, $timestampUtc, $frameId, $imagePath, $engineName, $verdict,
                 $totalInspectionMs, $workingSetMb, $success, $message, $error, $totalCycleMs, $exceptionCategory);
            """;
        foreach (var cycle in run.Cycles)
        {
            iterationCommand.Parameters.Clear();
            iterationCommand.Parameters.AddWithValue("$runId", runRowId);
            iterationCommand.Parameters.AddWithValue("$cycleNumber", cycle.CycleNumber);
            iterationCommand.Parameters.AddWithValue("$timestampUtc", (cycle.TimestampUtc ?? run.StartTime.ToUniversalTime()).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            iterationCommand.Parameters.AddWithValue("$frameId", cycle.FrameId);
            iterationCommand.Parameters.AddWithValue("$imagePath", cycle.ImagePath);
            iterationCommand.Parameters.AddWithValue("$engineName", cycle.EngineName);
            iterationCommand.Parameters.AddWithValue("$verdict", cycle.Verdict);
            iterationCommand.Parameters.AddWithValue("$totalInspectionMs", cycle.TotalMilliseconds);
            iterationCommand.Parameters.AddWithValue("$workingSetMb", cycle.WorkingSetMegabytes);
            iterationCommand.Parameters.AddWithValue("$success", cycle.Success ? 1 : 0);
            iterationCommand.Parameters.AddWithValue("$message", cycle.Message);
            iterationCommand.Parameters.AddWithValue("$error", cycle.Error);
            iterationCommand.Parameters.AddWithValue("$totalCycleMs", cycle.TotalCycleMilliseconds);
            iterationCommand.Parameters.AddWithValue("$exceptionCategory", cycle.ExceptionCategory);
            iterationCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runRowId;
    }

    public static SoakTestResult? GetLatestSoakTestRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, StartedAtUtc, EndedAtUtc, ImageFolder, OutputFolder, EngineKey, EngineName,
                   EngineVersion, SourceKind, IsRealCameraSource, ProfileName, RequestedDurationSeconds,
                   ActualDurationSeconds, DelayBetweenInspectionsMs, OperatorId, BoardModel, LotId,
                   WasCanceled, TotalCycles, SuccessfulCycles, FailedCycles, AverageInspectionMs,
                   MinInspectionMs, MaxInspectionMs, P95InspectionMs, CountOverOneSecond,
                   StartManagedMemoryMb, EndManagedMemoryMb, StartWorkingSetMb, EndWorkingSetMb,
                   PeakWorkingSetMb, IsCompletedFactoryEvidence, ErrorsJson, AverageTotalCycleMs,
                   MaxTotalCycleMs, P95TotalCycleMs, CancellationReason, FirstCriticalError,
                   MemoryWarningsJson
            FROM SoakTestRuns
            ORDER BY datetime(StartedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadSoakTestRun(reader);
        run.Cycles.AddRange(GetSoakTestIterations(run.Id));
        return run;
    }

    public static IReadOnlyList<ModelRegistryRecord> GetModelRegistryRecords()
    {
        EnsureInitialized();

        var records = new List<ModelRegistryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, DisplayName, Version, CreatedAtUtc, RegisteredAtUtc, SourceFileName,
                   StoredModelPath, StoredLabelMapPath, MetadataPath, Sha256, InputTensorName, OutputTensorName,
                   InputWidth, InputHeight, ConfidenceThreshold, LabelsJson, ValidationStatus, LastValidatedAtUtc,
                   ValidationMessage, Notes, IsActive, AuditEventId, LifecycleState, LatestAcceptanceStatus,
                   LatestAcceptanceRunId, LatestReleasePackageId, LatestReleasePackagePath, DeploymentWaiverReason,
                   WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeploymentWaiverRiskClassification,
                   DeployedAtUtc, RetiredReason, RetiredAtUtc
            FROM ModelRegistry
            ORDER BY IsActive DESC, datetime(RegisteredAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadModelRegistryRecord(reader));

        return records;
    }

    public static ModelRegistryRecord? GetActiveModelRegistryRecord()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, DisplayName, Version, CreatedAtUtc, RegisteredAtUtc, SourceFileName,
                   StoredModelPath, StoredLabelMapPath, MetadataPath, Sha256, InputTensorName, OutputTensorName,
                   InputWidth, InputHeight, ConfidenceThreshold, LabelsJson, ValidationStatus, LastValidatedAtUtc,
                   ValidationMessage, Notes, IsActive, AuditEventId, LifecycleState, LatestAcceptanceStatus,
                   LatestAcceptanceRunId, LatestReleasePackageId, LatestReleasePackagePath, DeploymentWaiverReason,
                   WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeploymentWaiverRiskClassification,
                   DeployedAtUtc, RetiredReason, RetiredAtUtc
            FROM ModelRegistry
            WHERE IsActive = 1
            ORDER BY datetime(RegisteredAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelRegistryRecord(reader) : null;
    }

    public static ModelAcceptanceRun? GetLatestModelAcceptanceRun(string? modelId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(modelId) ? string.Empty : "WHERE ModelId = $modelId";
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, ModelId, ModelVersion, ModelSha256, ModelPath, LabelMapPath,
                   InputTensorName, OutputTensorName, OutputShape, DatasetFolder, DatasetName,
                   GroundTruthCsvPath, IsFormalManifest, Status, OperatorId, ApprovedBy, ApprovedAtUtc,
                   IsProductionCandidate, CriteriaJson, MetricsJson, DatasetQualityJson,
                   FalseCallRecommendationJson, BreakdownJson, PerformanceJson, P95InferenceMs,
                   MessagesJson, LimitationsJson
            FROM ModelAcceptanceRuns
            {filter}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelAcceptanceRun(reader) : null;
    }

    public static ModelAcceptanceRun? GetLatestPassingProductionModelAcceptance(string? modelId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(modelId) ? string.Empty : "AND ModelId = $modelId";
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, ModelId, ModelVersion, ModelSha256, ModelPath, LabelMapPath,
                   InputTensorName, OutputTensorName, OutputShape, DatasetFolder, DatasetName,
                   GroundTruthCsvPath, IsFormalManifest, Status, OperatorId, ApprovedBy, ApprovedAtUtc,
                   IsProductionCandidate, CriteriaJson, MetricsJson, DatasetQualityJson,
                   FalseCallRecommendationJson, BreakdownJson, PerformanceJson, P95InferenceMs,
                   MessagesJson, LimitationsJson
            FROM ModelAcceptanceRuns
            WHERE Status = 'PASS'
              AND IsProductionCandidate = 1
              {filter}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelAcceptanceRun(reader) : null;
    }

    public static IReadOnlyList<AuditEventRecord> GetAuditEvents(LogFilter filter, int limit = 500)
    {
        EnsureInitialized();

        var records = new List<AuditEventRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildAuditWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, TimestampUtc, LocalTimestamp, UserId, UserRole, StationId,
                   ActionCategory, ActionDetail, RelatedEntityType, RelatedEntityId, RelatedPath
            FROM AuditEvents
            {where}
            ORDER BY datetime(TimestampUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadAuditEvent(reader));

        return records;
    }

    public static RecipeRevisionRecord? GetLatestRecipeRevision(string boardProgram)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                   BackgroundImagePath, RecipeJson, CreatedAtUtc
            FROM RecipeRevisions
            WHERE BoardProgram = $boardProgram
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$boardProgram", boardProgram);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecipeRevision(reader) : null;
    }

    public static IReadOnlyList<RecipeRevisionRecord> GetRecipeRevisions()
    {
        EnsureInitialized();

        var revisions = new List<RecipeRevisionRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                   BackgroundImagePath, RecipeJson, CreatedAtUtc
            FROM RecipeRevisions
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
            revisions.Add(ReadRecipeRevision(reader));

        return revisions;
    }

    public static long SaveRecipeRevision(
        string recipeName,
        string boardProgram,
        string operatorId,
        string detectionPriority,
        string backgroundImagePath,
        string recipeJson)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var revision = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        command.CommandText =
            """
            INSERT INTO RecipeRevisions
                (RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                 BackgroundImagePath, RecipeJson, Notes, CreatedAtUtc)
            VALUES
                ($recipeName, $revision, $boardProgram, $operatorId, $detectionPriority,
                 $backgroundImagePath, $recipeJson, $notes, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$recipeName", recipeName);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$boardProgram", boardProgram);
        command.Parameters.AddWithValue("$operatorId", operatorId);
        command.Parameters.AddWithValue("$detectionPriority", detectionPriority);
        command.Parameters.AddWithValue("$backgroundImagePath", backgroundImagePath);
        command.Parameters.AddWithValue("$recipeJson", recipeJson);
        command.Parameters.AddWithValue("$notes", "Recipe editor revision");
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)(command.ExecuteScalar() ?? 0L);
        RecordAuditEvent(
            "RECIPE_SAVE",
            $"Recipe revision saved: {recipeName}; board={boardProgram}; priority={detectionPriority}.",
            operatorWithRole: operatorId,
            relatedEntityType: "RecipeRevision",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: backgroundImagePath);
        return id;
    }

    public static long SaveCalibrationProfile(
        string profileName,
        string boardModel,
        string viewType,
        string sampleImagePath,
        string operatorId,
        IReadOnlyList<CalibrationPointInput> points)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Calibration profile name is required.", nameof(profileName));
        if (points.Count == 0)
            throw new ArgumentException("At least one calibration point is required.", nameof(points));

        var transform = CalibrationTransformService.Calculate(points);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO CalibrationProfiles
                (ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                 ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc)
            VALUES
                ($profileName, $boardModel, $viewType, $sampleImagePath, $operatorId, $pointCount,
                 $scaleX, $offsetX, $scaleY, $offsetY, $transformSummary, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$profileName", profileName.Trim());
        command.Parameters.AddWithValue("$boardModel", string.IsNullOrWhiteSpace(boardModel) ? "UNKNOWN" : boardModel.Trim());
        command.Parameters.AddWithValue("$viewType", string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim());
        command.Parameters.AddWithValue("$sampleImagePath", string.IsNullOrWhiteSpace(sampleImagePath) ? string.Empty : sampleImagePath.Trim());
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId.Trim());
        command.Parameters.AddWithValue("$pointCount", points.Count);
        command.Parameters.AddWithValue("$scaleX", transform.ScaleX);
        command.Parameters.AddWithValue("$offsetX", transform.OffsetX);
        command.Parameters.AddWithValue("$scaleY", transform.ScaleY);
        command.Parameters.AddWithValue("$offsetY", transform.OffsetY);
        command.Parameters.AddWithValue("$transformSummary", transform.Summary);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var profileId = (long)(command.ExecuteScalar() ?? 0L);
        foreach (var point in points)
        {
            using var pointCommand = connection.CreateCommand();
            pointCommand.Transaction = transaction;
            pointCommand.CommandText =
                """
                INSERT INTO CalibrationPoints
                    (ProfileId, ImageX, ImageY, BoardXMillimeters, BoardYMillimeters, CreatedAtUtc)
                VALUES
                    ($profileId, $imageX, $imageY, $boardXMillimeters, $boardYMillimeters, $createdAtUtc);
                """;
            pointCommand.Parameters.AddWithValue("$profileId", profileId);
            pointCommand.Parameters.AddWithValue("$imageX", point.ImageX);
            pointCommand.Parameters.AddWithValue("$imageY", point.ImageY);
            pointCommand.Parameters.AddWithValue("$boardXMillimeters", point.BoardXMillimeters);
            pointCommand.Parameters.AddWithValue("$boardYMillimeters", point.BoardYMillimeters);
            pointCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            pointCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        RecordAuditEvent(
            "CALIBRATION_SAVE",
            $"2D calibration profile saved: {profileName}; board={boardModel}; view={viewType}; points={points.Count}.",
            operatorWithRole: operatorId,
            relatedEntityType: "CalibrationProfile",
            relatedEntityId: profileId.ToString(CultureInfo.InvariantCulture),
            relatedPath: sampleImagePath);
        return profileId;
    }

    public static IReadOnlyList<CalibrationProfileRecord> GetCalibrationProfiles()
    {
        EnsureInitialized();

        var profiles = new List<CalibrationProfileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                   ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc
            FROM CalibrationProfiles
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profileId = reader.GetInt64(0);
            profiles.Add(ReadCalibrationProfile(reader, GetCalibrationPoints(profileId)));
        }

        return profiles;
    }

    public static CalibrationProfileRecord? GetCalibrationProfile(long profileId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                   ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc
            FROM CalibrationProfiles
            WHERE Id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadCalibrationProfile(reader, GetCalibrationPoints(profileId))
            : null;
    }

    public static IReadOnlyList<DbHealthRow> GetDatabaseHealthRows()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        return new[]
        {
            CountTable(connection, "Images", "OK"),
            CountTable(connection, "InspectionResults", "OK"),
            CountTable(connection, "Defects", "OK"),
            CountTable(connection, "ReviewEvents", "OK"),
            CountTable(connection, "AuditEvents", "OK"),
            CountTable(connection, "RecipeRevisions", "OK"),
            CountTable(connection, "CalibrationProfiles", "OK"),
            CountTable(connection, "CalibrationPoints", "OK"),
            CountTable(connection, "ImageLearningProjects", "OK"),
            CountTable(connection, "ImageLearningProjectImages", "OK"),
            CountTable(connection, "LearnedPcbVisualModels", "OK"),
            CountTable(connection, "ImageLearningInspectionResults", "OK"),
            CountTable(connection, "BatchTestRuns", "OK"),
            CountTable(connection, "ValidationBreakdownMetrics", "OK"),
            CountTable(connection, "FalseCallReductionRuns", "OK"),
            CountTable(connection, "ThresholdProfiles", "OK"),
            CountTable(connection, "ModelRegistry", "OK"),
            CountTable(connection, "ExportHistory", "OK"),
            CountTable(connection, "ValidationPackages", "OK"),
            CountTable(connection, "SoakTestRuns", "OK"),
            CountTable(connection, "SoakTestIterations", "OK"),
            CountTable(connection, "MesUploadAttempts", "OK"),
            CountTable(connection, "LogArchive", "OK"),
        };
    }

    public static void RecordWorkflowEvent(string category, string message, DateTime timestamp, string? operatorId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ReviewEvents (Category, Message, OperatorId, EventTimeUtc)
            VALUES ($category, $message, $operatorId, $eventTimeUtc);
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(operatorId) ? DBNull.Value : operatorId);
        command.Parameters.AddWithValue("$eventTimeUtc", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

}
