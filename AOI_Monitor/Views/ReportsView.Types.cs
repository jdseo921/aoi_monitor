using System.Globalization;
using System.IO;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class ReportsView
{
    // WorkProgress and ExportOutcome are internal (not private): the Readiness & QA window
    // drives the shared static builders above with the same progress/outcome contracts.
    internal sealed record WorkProgress(int Completed, int Total, string Message);

    internal sealed record ExportOutcome(int Count, IReadOnlyList<string> Errors);

    private sealed record LogLoadSnapshot(
        InspectionLogRow[] Inspections,
        ReviewLogRow[] Reviews,
        ExportHistoryRow[] Exports,
        AuditLogRow[] Audits,
        MesSpoolQueueRow[] MesSpool,
        CentralSyncQueueRow[] CentralSync);

    private sealed record IntegrityOutcome(string ReportPath, string Integrity, string Status);

    private sealed record IndexOutcome(string Path, int Count, IReadOnlyList<string> Errors);

    public sealed class InspectionLogRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string TimestampLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string BoardProgram { get; init; } = "UNKNOWN";
        public string OperatorId { get; init; } = "UNKNOWN";
        public string InspectionEngine { get; init; } = "Pixel Difference Prototype Engine";
        public string ModelVersion { get; init; } = "UNKNOWN";
        public string ModelFilePath { get; init; } = string.Empty;
        public double ConfidenceThreshold { get; init; }
        public string SampleImagePath { get; init; } = string.Empty;
        public string GoldenImagePath { get; init; } = string.Empty;
        public string ImageName => string.IsNullOrWhiteSpace(SampleImagePath) ? "--" : Path.GetFileName(SampleImagePath);
        public string Verdict { get; init; } = "REVIEW";
        public double DifferenceScore { get; init; }
        public string ScoreDisplay => $"{DifferenceScore:F1}%";
        public double Confidence { get; init; }
        public string ConfidenceDisplay => Confidence.ToString("P0", CultureInfo.InvariantCulture);
        public string SuggestedDefect { get; init; } = string.Empty;
        public string DecisionReason { get; init; } = string.Empty;
        public double HotspotX { get; init; }
        public double HotspotY { get; init; }
        public double HotspotWidth { get; init; }
        public double HotspotHeight { get; init; }
        public double ImageLoadMilliseconds { get; init; }
        public double PreprocessingMilliseconds { get; init; }
        public double InferenceMilliseconds { get; init; }
        public double OverlayRenderingMilliseconds { get; init; }
        public double TotalInspectionMilliseconds { get; init; }
        public string TotalTimeDisplay => $"{TotalInspectionMilliseconds:F0} ms";

        public static InspectionLogRow FromRecord(InspectionHistoryRecord record)
        {
            return new InspectionLogRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                BoardProgram = record.BoardProgram,
                OperatorId = record.OperatorId,
                InspectionEngine = record.InspectionEngine,
                ModelVersion = record.ModelVersion,
                ModelFilePath = record.ModelFilePath,
                ConfidenceThreshold = record.ConfidenceThreshold,
                SampleImagePath = record.SampleImagePath,
                GoldenImagePath = record.GoldenImagePath,
                Verdict = record.Verdict,
                DifferenceScore = record.DifferenceScore,
                Confidence = record.Confidence,
                SuggestedDefect = record.SuggestedDefect,
                DecisionReason = record.DecisionReason,
                HotspotX = record.HotspotX,
                HotspotY = record.HotspotY,
                HotspotWidth = record.HotspotWidth,
                HotspotHeight = record.HotspotHeight,
                ImageLoadMilliseconds = record.ImageLoadMilliseconds,
                PreprocessingMilliseconds = record.PreprocessingMilliseconds,
                InferenceMilliseconds = record.InferenceMilliseconds,
                OverlayRenderingMilliseconds = record.OverlayRenderingMilliseconds,
                TotalInspectionMilliseconds = record.TotalInspectionMilliseconds,
            };
        }
    }

    public sealed class ReviewLogRow
    {
        public long Id { get; init; }
        public DateTime EventTimeUtc { get; init; }
        public string TimestampLocal => EventTimeUtc == DateTime.MinValue ? "--" : EventTimeUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string Category { get; init; } = string.Empty;
        public string OperatorId { get; init; } = "UNKNOWN";
        public string Disposition { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static ReviewLogRow FromRecord(ReviewEventRecord record)
        {
            return new ReviewLogRow
            {
                Id = record.Id,
                EventTimeUtc = record.EventTimeUtc,
                Category = record.Category,
                OperatorId = record.OperatorId,
                Disposition = record.Disposition,
                Message = record.Message,
            };
        }
    }

    public sealed class ExportHistoryRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string TimestampLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string ExportType { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string OperatorId { get; init; } = "UNKNOWN";
        public long? AuditEventId { get; init; }
        public string AuditEventDisplay => AuditEventId is null ? "--" : AuditEventId.Value.ToString(CultureInfo.InvariantCulture);
        public string VerificationStatus { get; init; } = "--";
        public string VerificationSha256 { get; init; } = string.Empty;
        public string VerificationShaDisplay => string.IsNullOrWhiteSpace(VerificationSha256) ? "--" : ShortHash(VerificationSha256);

        public static ExportHistoryRow FromRecord(ExportHistoryRecord record, ExportVerificationRecord? verification = null)
        {
            return new ExportHistoryRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                ExportType = record.ExportType,
                FilePath = record.FilePath,
                Status = record.Status,
                OperatorId = record.OperatorId,
                AuditEventId = record.AuditEventId,
                VerificationStatus = verification?.Status ?? "--",
                VerificationSha256 = verification?.Sha256 ?? string.Empty,
            };
        }
    }

    public sealed class MesSpoolQueueRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string CreatedLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string PayloadType { get; init; } = string.Empty;
        public string EndpointUrl { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public int MaxRetryCount { get; init; }
        public string RetryDisplay => $"{RetryCount}/{MaxRetryCount}";
        public string Status { get; init; } = string.Empty;
        public string LastError { get; init; } = string.Empty;
        public string LotId { get; init; } = string.Empty;
        public string BoardModel { get; init; } = string.Empty;
        public string Result { get; init; } = string.Empty;

        public static MesSpoolQueueRow FromRecord(MesSpoolQueueRecord record)
        {
            return new MesSpoolQueueRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                PayloadType = record.PayloadType,
                EndpointUrl = MesIntegrationSettingsService.RedactSecrets(record.EndpointUrl),
                RetryCount = record.RetryCount,
                MaxRetryCount = record.MaxRetryCount,
                Status = record.Status,
                LastError = MesIntegrationSettingsService.RedactSecrets(record.LastError),
                LotId = record.LotId,
                BoardModel = record.BoardModel,
                Result = record.Result,
            };
        }
    }

    public sealed class CentralSyncQueueRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string CreatedLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string ItemType { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string StationId { get; init; } = string.Empty;
        public string EndpointOrFolder { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public int MaxRetryCount { get; init; }
        public string RetryDisplay => $"{RetryCount}/{MaxRetryCount}";
        public string Status { get; init; } = string.Empty;
        public string LastError { get; init; } = string.Empty;

        public static CentralSyncQueueRow FromRecord(CentralSyncQueueRecord record)
        {
            var settings = CentralSyncSettingsService.Load();
            return new CentralSyncQueueRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                ItemType = record.ItemType,
                ItemId = record.ItemId,
                StationId = record.StationId,
                EndpointOrFolder = settings.RedactEndpointInExports && !string.IsNullOrWhiteSpace(record.EndpointOrFolder)
                    ? "***"
                    : CentralSyncSettingsService.RedactSecrets(record.EndpointOrFolder, settings),
                RetryCount = record.RetryCount,
                MaxRetryCount = record.MaxRetryCount,
                Status = record.Status,
                LastError = CentralSyncSettingsService.RedactSecrets(record.LastError, settings),
            };
        }
    }

    public sealed class AuditLogRow
    {
        public long Id { get; init; }
        public DateTime TimestampUtc { get; init; }
        public DateTime LocalTimestamp { get; init; }
        public string TimestampUtcDisplay => TimestampUtc == DateTime.MinValue ? "--" : TimestampUtc.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string LocalTimestampDisplay => LocalTimestamp == DateTime.MinValue ? "--" : LocalTimestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string UserId { get; init; } = "UNKNOWN";
        public string UserRole { get; init; } = "UNKNOWN";
        public string StationId { get; init; } = "UNKNOWN";
        public string ActionCategory { get; init; } = string.Empty;
        public string ActionDetail { get; init; } = string.Empty;
        public string RelatedEntityType { get; init; } = string.Empty;
        public string RelatedEntityId { get; init; } = string.Empty;
        public string RelatedPath { get; init; } = string.Empty;
        public string RelatedEntityDisplay => string.IsNullOrWhiteSpace(RelatedEntityType) && string.IsNullOrWhiteSpace(RelatedEntityId)
            ? "--"
            : $"{RelatedEntityType}:{RelatedEntityId}";

        public static AuditLogRow FromRecord(AuditEventRecord record)
        {
            return new AuditLogRow
            {
                Id = record.Id,
                TimestampUtc = record.TimestampUtc,
                LocalTimestamp = record.LocalTimestamp,
                UserId = record.UserId,
                UserRole = record.UserRole,
                StationId = record.StationId,
                ActionCategory = record.ActionCategory,
                ActionDetail = record.ActionDetail,
                RelatedEntityType = record.RelatedEntityType,
                RelatedEntityId = record.RelatedEntityId,
                RelatedPath = record.RelatedPath,
            };
        }
    }

    public void Dispose()
    {
        DisposeCancellation(ref _workCts);
        DisposeCancellation(ref _refreshCts);
        GC.SuppressFinalize(this);
    }

    internal static void DisposeCancellation(ref CancellationTokenSource? cancellation)
    {
        var source = cancellation;
        cancellation = null;
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere; disposal must stay idempotent.
        }

        source.Dispose();
    }
}
