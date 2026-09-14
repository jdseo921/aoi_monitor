using AOI_Monitor.Services;

namespace AOI_Monitor.Models;

public record ImageLibraryRecord(
    string Sample,
    string Board,
    string RefDes,
    string Defect,
    string Severity,
    string AiResult,
    string GroundTruth,
    string Risk,
    string ImageLink,
    string Date,
    string VaultPath,
    string OriginalPath,
    string FileHash,
    bool IsDemo);

public record DbHealthRow(string Table, string Count, string Status);

public sealed class DefectTaxonomyRecord
{
    public string TaxonomyId { get; set; } = "default";
    public string Name { get; set; } = "Default AOI Defect Taxonomy";
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DefectTaxonomyEntry
{
    public string TaxonomyId { get; set; } = "default";
    public string CanonicalClass { get; set; } = string.Empty;
    public string CustomerLabel { get; set; } = string.Empty;
    public int? ModelLabelId { get; set; }
    public bool IsRequired { get; set; } = true;

    /// <summary>Customer classification-table Severity (Critical/Major/Minor). Empty when not classified.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Customer classification-table Detection Method (e.g. "AOI / 3D", "Side-View AOI").</summary>
    public string DetectionMethod { get; set; } = string.Empty;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class DefectClassAliasRecord
{
    public string TaxonomyId { get; set; } = "default";
    public string Alias { get; set; } = string.Empty;
    public string CanonicalClass { get; set; } = string.Empty;
}

public sealed class MesDefectCodeMappingRecord
{
    public string TaxonomyId { get; set; } = "default";
    public string CanonicalClass { get; set; } = string.Empty;
    public string MesCode { get; set; } = string.Empty;
}

public sealed class DefectTaxonomySnapshot
{
    public DefectTaxonomyRecord Taxonomy { get; set; } = new();
    public List<DefectTaxonomyEntry> Entries { get; set; } = new();
    public List<DefectClassAliasRecord> Aliases { get; set; } = new();
    public List<MesDefectCodeMappingRecord> MesMappings { get; set; } = new();
}

public sealed record DefectTaxonomyNormalization(
    string Input,
    string CanonicalClass,
    string CustomerLabel,
    string MesCode,
    bool IsKnown,
    string Warning)
{
    /// <summary>Customer classification-table Severity for the resolved class, or empty when unknown.</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>Customer classification-table Detection Method for the resolved class, or empty when unknown.</summary>
    public string DetectionMethod { get; init; } = string.Empty;
}

public sealed class ModelLabelTaxonomyValidation
{
    public string Status { get; set; } = "PASS";
    public List<string> MissingRequiredClasses { get; set; } = new();
    public List<string> UnknownLabels { get; set; } = new();

    /// <summary>
    /// Claimed classes that this software cannot detect from Stage-1 uploaded images alone —
    /// they need 3D or side-view acquisition, or belong to another machine type entirely.
    /// </summary>
    public List<string> HardwareDependentClasses { get; set; } = new();

    public List<string> Messages { get; set; } = new();
}

public record BatchTestRunRecord(
    long Id,
    string ImageFolder,
    string? GroundTruthCsvPath,
    string EngineName,
    string ModelVersion,
    DateTime CreatedAtUtc,
    double Accuracy,
    double Precision,
    double Recall,
    double FalseCallRate,
    int TotalImages,
    int FailedCount,
    string ThresholdProfileId = "",
    string ThresholdProfileRevision = "");

public sealed class InspectionLatencyTrace
{
    public long Id { get; set; }
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FrameCapturedAtUtc { get; set; }
    public DateTime? FrameReceivedAtUtc { get; set; }
    public DateTime? PreprocessingStartUtc { get; set; }
    public DateTime? PreprocessingEndUtc { get; set; }
    public DateTime? InferenceStartUtc { get; set; }
    public DateTime? InferenceEndUtc { get; set; }
    public DateTime? PostprocessStartUtc { get; set; }
    public DateTime? PostprocessEndUtc { get; set; }
    public DateTime? OverlayRenderStartUtc { get; set; }
    public DateTime? OverlayRenderEndUtc { get; set; }
    public DateTime? ResultPersistStartUtc { get; set; }
    public DateTime? ResultPersistEndUtc { get; set; }
    public double TotalFrameToOverlayMs { get; set; }
    public double TotalFrameToSavedResultMs { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

public sealed class InspectionLatencySummary
{
    public int TraceCount { get; set; }
    public int OverOneSecondCount { get; set; }
    public double P50FrameToOverlayMs { get; set; }
    public double P95FrameToOverlayMs { get; set; }
    public double MaxFrameToOverlayMs { get; set; }
    public double P50FrameToSavedResultMs { get; set; }
    public double P95FrameToSavedResultMs { get; set; }
    public double MaxFrameToSavedResultMs { get; set; }
    public double P95InferenceMs { get; set; }
    public double P95OverlayMs { get; set; }
    public double P95SaveMs { get; set; }
    public List<string> Warnings { get; set; } = new();

    public string Status => TraceCount == 0
        ? "NOT RECORDED"
        : OverOneSecondCount > 0
            ? "WARN"
            : "PASS";
}

public enum BenchmarkInspectionSourceKind
{
    ImageFolder,
    FolderCameraSimulation,
    ActiveCameraSource,
}

public sealed class BenchmarkInspectionOptions
{
    public BenchmarkInspectionSourceKind SourceKind { get; set; } = BenchmarkInspectionSourceKind.ImageFolder;
    public string ImageFolder { get; set; } = string.Empty;
    public int RunCount { get; set; } = 10;
    public TimeSpan? Duration { get; set; }
    public double AcceptanceThresholdMs { get; set; } = 1000;
    public string OutputRoot { get; set; } = string.Empty;
    public DetectionPriority DetectionPriority { get; set; } = DetectionPriority.Balanced;

    /// <summary>
    /// Optional golden reference passed to every benchmarked inspection so the measured
    /// workload matches the operator golden-compare flow instead of the lighter
    /// no-reference path. Blank keeps the historical no-golden behavior.
    /// </summary>
    public string GoldenImagePath { get; set; } = string.Empty;

    /// <summary>
    /// Cold-start samples run before the measured loop (clamped 0..3). Warm-up samples
    /// are recorded and reported as cold-start metrics but excluded from steady-state
    /// statistics; threshold overruns during warm-up are always reported, never hidden.
    /// </summary>
    public int WarmupCount { get; set; }
}

public sealed class BenchmarkInspectionResult
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime EndedAtUtc { get; set; } = DateTime.UtcNow;
    public BenchmarkInspectionSourceKind SourceKind { get; set; } = BenchmarkInspectionSourceKind.ImageFolder;
    public string SourceDescription { get; set; } = string.Empty;
    public bool IsRealCameraSource { get; set; }
    public string EngineName { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Inference execution provider; always a CPU variant in this build (no GPU EP is bundled — SD-12/OD-02).</summary>
    public string ExecutionProvider { get; set; } = string.Empty;
    public string DetectionPriority { get; set; } = string.Empty;
    public double ConfidenceThreshold { get; set; }
    public string ActiveModelSha256 { get; set; } = string.Empty;
    public string GoldenImagePath { get; set; } = string.Empty;
    public string ThresholdProfileId { get; set; } = string.Empty;
    public string ThresholdProfileRevision { get; set; } = string.Empty;
    public double AcceptanceThresholdMs { get; set; } = 1000;
    public int RequestedCount { get; set; }

    /// <summary>Wall-clock seconds spent in warm-up sampling; excluded from the throughput denominator.</summary>
    public double WarmupDurationSeconds { get; set; }

    /// <summary>Measured (non-warm-up) sample count; all percentile/threshold statistics cover these samples.</summary>
    public int CompletedCount { get; set; }
    public int ColdStartSampleCount { get; set; }
    public double ColdStartMaxFrameToOverlayMs { get; set; }
    public int ColdStartOverThresholdCount { get; set; }
    public double DurationSeconds { get; set; }
    public double ThroughputImagesPerMinute { get; set; }
    public double P50FrameToOverlayMs { get; set; }
    public double P90FrameToOverlayMs { get; set; }
    public double P95FrameToOverlayMs { get; set; }
    public double P99FrameToOverlayMs { get; set; }
    public double MaxFrameToOverlayMs { get; set; }
    public int OverOneSecondCount { get; set; }
    public double P95LoadMs { get; set; }
    public double P95PreprocessingMs { get; set; }
    public double P95InferenceMs { get; set; }
    public double P95OverlayMs { get; set; }
    public double P95PersistenceMs { get; set; }
    public string Status { get; set; } = "NOT RUN";
    public string ReportFolder { get; set; } = string.Empty;
    public string JsonPath { get; set; } = string.Empty;
    public string HtmlPath { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string CsvPath { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = new();
    public List<BenchmarkInspectionSample> Samples { get; set; } = new();
}

public sealed class BenchmarkInspectionSample
{
    public int Sequence { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string FrameId { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }

    /// <summary>True for cold-start warm-up samples excluded from steady-state statistics.</summary>
    public bool IsWarmup { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public double LoadMs { get; set; }
    public double PreprocessingMs { get; set; }
    public double InferenceMs { get; set; }
    public double OverlayMs { get; set; }
    public double PersistenceMs { get; set; }
    public double FrameToOverlayMs { get; set; }
    public double FrameToSavedResultMs { get; set; }
    public string TraceId { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

public record BatchTestResultRecord(
    long Id,
    long RunId,
    string ImagePath,
    string ImageName,
    string GroundTruth,
    string EngineResult,
    string InspectionEngine,
    string ModelVersion,
    double Score,
    string PassFail,
    string DefectType,
    string NormalizedDefectClass,
    string NormalizedSide,
    string RoiId,
    string RoiType,
    string FailureCategory,
    double RoiX,
    double RoiY,
    double RoiWidth,
    double RoiHeight,
    string Side,
    string RefDes,
    string LotId,
    string BoardModel,
    string Notes,
    double ImageLoadMilliseconds,
    double PreprocessingMilliseconds,
    double InferenceMilliseconds,
    double OverlayRenderingMilliseconds,
    double TotalInspectionMilliseconds,
    DateTime CreatedAtUtc);

public sealed class ValidationBreakdownMetric
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string BreakdownType { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Total { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int WrongDefectClass { get; set; }
    public int WrongSide { get; set; }
    public int UnknownGroundTruth { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
}

public sealed class ValidationBreakdownSummary
{
    public List<ValidationBreakdownMetric> DefectClassMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> SideMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> RoiMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> RoiTypeMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> TopFalseCallContributors { get; set; } = new();
    public List<ValidationBreakdownMetric> TopPossibleEscapeContributors { get; set; } = new();
}

public sealed class ThresholdProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string Revision { get; set; } = "R0001";
    public string BoardModel { get; set; } = "ANY";
    public string BoardProgram { get; set; } = "ANY";
    public string RecipeName { get; set; } = "ANY";
    public string RecipeRevision { get; set; } = "ANY";
    public string Status { get; set; } = "Draft";
    public long? SourceValidationRunId { get; set; }
    public long? SourceFalseCallReductionRunId { get; set; }
    public string CreatedBy { get; set; } = "UNKNOWN";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public List<ThresholdProfileRule> Rules { get; set; } = new();
}

public sealed class ThresholdProfileRule
{
    public long Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string ViewType { get; set; } = "Any";
    public string RoiType { get; set; } = "Any";
    public string DefectClass { get; set; } = "Any";
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.65;
    public double MinimumAreaPixels { get; set; }
    public double MaxAllowedFalseCallRate { get; set; } = 1.0;
    public int SpecificityScore =>
        Specificity(ViewType) + Specificity(RoiType) + Specificity(DefectClass);

    private static int Specificity(string value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}

public sealed class ThresholdProfileRevision
{
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "UNKNOWN";
}

public sealed class EffectiveThresholdRule
{
    public string Source { get; set; } = "Built-in policy default";
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.65;
    public double MinimumAreaPixels { get; set; }
}

public sealed class LogFilter
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? BoardProgram { get; set; }
    public string? OperatorId { get; set; }
    public string? Result { get; set; }
    public string? UserRole { get; set; }
    public string? ActionCategory { get; set; }
}

public record InspectionHistoryRecord(
    long Id,
    DateTime CreatedAtUtc,
    string BoardProgram,
    string OperatorId,
    string InspectionEngine,
    string ModelVersion,
    string ModelFilePath,
    double ConfidenceThreshold,
    string SampleImagePath,
    string GoldenImagePath,
    string Verdict,
    double DifferenceScore,
    double Confidence,
    string SuggestedDefect,
    string DecisionReason,
    double HotspotX,
    double HotspotY,
    double HotspotWidth,
    double HotspotHeight,
    double ImageLoadMilliseconds,
    double PreprocessingMilliseconds,
    double InferenceMilliseconds,
    double OverlayRenderingMilliseconds,
    double TotalInspectionMilliseconds);

public record ReviewEventRecord(
    long Id,
    DateTime EventTimeUtc,
    string Category,
    string OperatorId,
    string Disposition,
    string Message);

public record ExportHistoryRecord(
    long Id,
    DateTime CreatedAtUtc,
    string ExportType,
    string FilePath,
    string Status,
    string OperatorId,
    long? AuditEventId);

public enum ExportVerificationStatus
{
    OK,
    WARN,
    ERROR,
}

public sealed class ExportVerificationResult
{
    public string ExportPath { get; set; } = string.Empty;
    public string ExportType { get; set; } = string.Empty;
    public ExportVerificationStatus Status { get; set; } = ExportVerificationStatus.ERROR;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> Messages { get; set; } = new();
    public Dictionary<string, string> ArtifactChecksums { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public record ExportVerificationRecord(
    long Id,
    long? ExportHistoryId,
    DateTime CheckedAtUtc,
    string ExportType,
    string ExportPath,
    string Status,
    string Sha256,
    long SizeBytes,
    string MessagesJson,
    string ArtifactChecksumsJson);

public sealed class BuildTestEvidenceRecord
{
    public long Id { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string GitCommit { get; set; } = string.Empty;
    public string CommitSha
    {
        get => GitCommit;
        set => GitCommit = value;
    }
    public string Configuration { get; set; } = "Release";
    public string HygieneStatus { get; set; } = "UNKNOWN";
    public string RestoreStatus { get; set; } = "UNKNOWN";
    public string BuildStatus { get; set; } = "UNKNOWN";
    public string TestStatus { get; set; } = "UNKNOWN";
    public string PublishValidationStatus { get; set; } = "UNKNOWN";
    public string TestResultPath { get; set; } = string.Empty;
    public string EvidencePath { get; set; } = string.Empty;
    public string OperatorId { get; set; } = "UNKNOWN";
    public string MachineName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BuildTestEvidenceSummary
{
    public string Status { get; set; } = "NoEvidence";
    public BuildTestEvidenceRecord? Latest { get; set; }
    public bool IsPassing { get; set; }
    public bool HasFailure { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class ModelAcceptanceCriteria
{
    public double MinimumAccuracy { get; set; } = 0.97;
    public double MinimumPrecision { get; set; } = 0.97;
    public double MinimumRecall { get; set; } = 0.97;
    public double MaximumFalseCallRate { get; set; } = 0.05;
    public double MaximumPossibleEscapeRate { get; set; } = 0.02;
    public double MaximumReviewRate { get; set; } = 0.10;
    public double MaximumAverageInferenceMs { get; set; } = 1000;
    public double MaximumP95InferenceMs { get; set; } = 1000;
    public string MinimumDatasetQualityStatus { get; set; } = "PASS";
    public bool RequireFormalManifest { get; set; } = true;
    public bool RequireDefectClassCoverage { get; set; } = true;
}

public sealed class ModelAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string LabelMapPath { get; set; } = string.Empty;
    public string InputTensorName { get; set; } = string.Empty;
    public string OutputTensorName { get; set; } = string.Empty;
    public string OutputShape { get; set; } = string.Empty;
    public string DatasetFolder { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public string GroundTruthCsvPath { get; set; } = string.Empty;
    public bool IsFormalManifest { get; set; }
    public string Status { get; set; } = "FAIL";
    public string OperatorId { get; set; } = "UNKNOWN";
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public bool IsProductionCandidate { get; set; }
    public ModelAcceptanceCriteria Criteria { get; set; } = new();
    public BatchMetrics Metrics { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public CustomerDatasetPreflightResult DatasetPreflightResult { get; set; } = new();
    public DatasetQualitySummary DatasetQualitySummary { get; set; } = new();
    public FalseCallRecommendationSummary FalseCallRecommendation { get; set; } = new();
    public ValidationBreakdownSummary BreakdownSummary { get; set; } = new();
    public BatchPerformanceSummary PerformanceSummary { get; set; } = new(0, 0, 0, 0, 0);
    public double P95InferenceMs { get; set; }
    public List<string> Messages { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}

public sealed record ModelReleasePackageRecord(
    long Id,
    DateTime CreatedAtUtc,
    long AcceptanceRunId,
    string ModelId,
    string ModelVersion,
    string ModelSha256,
    string PackagePath,
    string ManifestPath,
    string ReportPath,
    string Status,
    string ApprovedBy,
    long? AuditEventId);

public record ValidationPackageRecord(
    long Id,
    DateTime CreatedAtUtc,
    string PackageId,
    string PackagePath,
    string ManifestPath,
    string AcceptanceStatus,
    string Summary,
    long? RunId,
    string OperatorId,
    long? AuditEventId);

public sealed class ValidationAcceptanceCriteria
{
    public double MinimumAccuracy { get; set; } = 0.97;
    public double MinimumPrecision { get; set; } = 0.97;
    public double MinimumRecall { get; set; } = 0.97;
    public double MaximumFalseCallRate { get; set; } = 0.05;
    public int MaximumImagesOverOneSecond { get; set; } = 0;
    public bool RequireFormalManifest { get; set; }
}

public sealed class ValidationAcceptanceSummary
{
    public string Status { get; set; } = "CONDITIONAL";
    public bool MetricsComputed { get; set; }
    public bool FormalManifestPresent { get; set; }
    public bool NumericGatesPassed { get; set; }
    public string DatasetQualityStatus { get; set; } = "CONDITIONAL";
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationDatasetQualityCriteria
{
    public int MinimumTotalImages { get; set; } = 50;
    public int MinimumKnownGroundTruthImages { get; set; } = 50;
    public int MinimumOkImages { get; set; } = 20;
    public int MinimumNgImages { get; set; } = 20;
    public int MinimumDefectClasses { get; set; } = 2;
    public bool RequireGoldenImageForPixelDifference { get; set; } = true;
    public double MaximumUnknownLabelRate { get; set; } = 0.05;
    public double MaximumMissingGoldenRate { get; set; } = 0.10;
    public int MinimumImagesPerDefectClass { get; set; } = 5;
}

public sealed class DatasetQualitySummary
{
    public string Status { get; set; } = "CONDITIONAL";
    public int TotalImages { get; set; }
    public int KnownGroundTruthImages { get; set; }
    public int OkImages { get; set; }
    public int NgImages { get; set; }
    public int UnknownLabelImages { get; set; }
    public int MissingGoldenImages { get; set; }
    public int DuplicateImageNames { get; set; }
    public int DuplicateFileHashes { get; set; }
    public int DefectClassCount { get; set; }
    public double UnknownLabelRate { get; set; }
    public double MissingGoldenRate { get; set; }
    public Dictionary<string, int> ImagesPerDefectClass { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = new();
    public List<string> BlockingFailures { get; set; } = new();
}

public sealed class CameraAcceptanceCriteria
{
    public int FramesPerView { get; set; } = 5;
    public List<string> RequiredViews { get; set; } = new() { "Top" };
    public double MaxConnectMs { get; set; } = 2000;
    public double MaxFirstFrameMs { get; set; } = 1000;
    public double MaxAverageFrameIntervalMs { get; set; } = 500;
    public double MaxDroppedFrameRate { get; set; } = 0.05;
    public double MaxTriggerFailureRate { get; set; } = 0.01;
    public int MinimumWidth { get; set; } = 1;
    public int MinimumHeight { get; set; } = 1;
    public List<string> RequiredPixelFormats { get; set; } = new();
}

public sealed class CameraAcceptanceFrameRecord
{
    public string ViewType { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string FrameId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixelFormat { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }
    public double LatencyMs { get; set; }
    public double IntervalMs { get; set; }
    public bool MetadataValid { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class CameraAcceptanceViewMetrics
{
    public string ViewType { get; set; } = string.Empty;
    public int RequestedFrames { get; set; }
    public int ReceivedFrames { get; set; }
    public double ConnectMs { get; set; }
    public double FirstFrameMs { get; set; }
    public double AverageFrameIntervalMs { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public double DroppedFrameRate { get; set; }
    public double TriggerFailureRate { get; set; }
    public string LightingProgramSelected { get; set; } = string.Empty;
    public string TriggerMode { get; set; } = string.Empty;
    public double TriggerToFrameLatencyMs { get; set; }
    public double LightingCommandLatencyMs { get; set; }
    public string SyncStatus { get; set; } = "NOT TESTED";
    public string Status { get; set; } = "FAIL";
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public sealed class CameraAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string AdapterName { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string SettingsSummary { get; set; } = string.Empty;
    public CameraAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public string FactoryReadinessStatus { get; set; } = "NOT VALIDATED";
    public bool IsRealHardware { get; set; }
    public int TotalRequestedFrames { get; set; }
    public int TotalReceivedFrames { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public double MaxConnectMs { get; set; }
    public double MaxFirstFrameMs { get; set; }
    public double AverageFrameIntervalMs { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<CameraAcceptanceViewMetrics> ViewMetrics { get; set; } = new();
    public List<CameraAcceptanceFrameRecord> Frames { get; set; } = new();
}

public sealed class CameraAcceptanceSummary
{
    public string Status { get; set; } = "NOT VALIDATED";
    public string AcceptanceStatus { get; set; } = "NOT RUN";
    public bool IsRealHardware { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public string AdapterName { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public int TotalRequestedFrames { get; set; }
    public int TotalReceivedFrames { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class LightingAcceptanceCriteria
{
    public List<string> RequiredViews { get; set; } = new() { "Top" };
    public double MaxCommandLatencyMs { get; set; } = 1000;
    public double MaxTriggerToFrameLatencyMs { get; set; } = 1000;
    public bool RequireFrameWhenCameraSourceProvided { get; set; } = true;
}

public sealed class LightingAcceptanceStep
{
    public string ViewType { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public double CommandLatencyMs { get; set; }
    public double TriggerToFrameLatencyMs { get; set; }
    public bool CommandAccepted { get; set; }
    public bool FrameReceived { get; set; }
    public string FrameId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public string Status { get; set; } = "FAIL";
    public string Message { get; set; } = string.Empty;
}

public sealed class LightingAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ControllerName { get; set; } = string.Empty;
    public string Mode { get; set; } = LightingModes.None;
    public string SettingsSummary { get; set; } = string.Empty;
    public LightingAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public bool IsSimulated { get; set; }
    public int StepCount { get; set; }
    public int PassedStepCount { get; set; }
    public int FailedStepCount { get; set; }
    public double MaxCommandLatencyMs { get; set; }
    public double MaxTriggerToFrameLatencyMs { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<LightingAcceptanceStep> Steps { get; set; } = new();
}

public sealed class Profile3DFrame
{
    public string FrameId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "None";
    public bool IsSimulated { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Unit { get; set; } = "microns";
    public double XPitchMicrons { get; set; }
    public double YPitchMicrons { get; set; }
    public double[] HeightValues { get; set; } = Array.Empty<double>();
    public string ViewType { get; set; } = "Top";
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
}

public sealed class Profile3DAcceptanceCriteria
{
    public int MinimumWidth { get; set; } = 2;
    public int MinimumHeight { get; set; } = 2;
    public double MaxAcquisitionMs { get; set; } = 1000;
    public List<string> AcceptedUnits { get; set; } = new() { "microns", "um", "micrometer", "micrometers" };
    public bool RequirePositivePitch { get; set; } = true;

    /// <summary>
    /// Dropout tolerance: real profilometers always produce some NaN samples from
    /// occlusion and specular reflection (the repo's own NaN-for-missing convention).
    /// Runs fail above this NaN percentage and warn for any dropout below it; 0 restores
    /// the previous zero-tolerance behavior. Persisted criteria rows without this field
    /// deserialize to the default.
    /// </summary>
    public double MaxNaNFractionPercent { get; set; } = 5.0;
}

public sealed class Profile3DAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceName { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "None";
    public bool IsSimulated { get; set; }
    public string Status { get; set; } = "FAIL";
    public string FactoryReadinessStatus { get; set; } = "NOT VALIDATED";
    public double AcquisitionMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Unit { get; set; } = string.Empty;
    public double XPitchMicrons { get; set; }
    public double YPitchMicrons { get; set; }
    public int MissingHeightCount { get; set; }
    public int NaNHeightCount { get; set; }
    public string FrameId { get; set; } = string.Empty;
    public Profile3DAcceptanceCriteria Criteria { get; set; } = new();
    public Dictionary<string, string> Diagnostics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public sealed class RobotAcceptanceCriteria
{
    public double MaxLoadMs { get; set; } = 1000;
    public double MaxMoveToInspectMs { get; set; } = 1000;
    public double MaxInspectionMs { get; set; } = 1000;
    public double MaxUnloadMs { get; set; } = 1000;
    public double MaxFullCycleMs { get; set; } = 5000;
    public bool RequireEmergencyStopTest { get; set; } = true;
    public bool RequireInvalidTransitionTest { get; set; } = true;
    public bool RequireAuditEvents { get; set; } = true;
}

public sealed class RobotAcceptanceStep
{
    public string StepName { get; set; } = string.Empty;
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public bool Accepted { get; set; }
    public string Status { get; set; } = "FAIL";
    public string Message { get; set; } = string.Empty;
}

public sealed class RobotAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ControllerName { get; set; } = string.Empty;
    public string EmergencyStopName { get; set; } = string.Empty;
    public string SafetyControllerName { get; set; } = string.Empty;
    public string SafetySourceKind { get; set; } = "NotConnected";
    public string SourceKind { get; set; } = "Simulated";
    public RobotAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public string FinalState { get; set; } = string.Empty;
    public double LoadMs { get; set; }
    public double MoveToInspectMs { get; set; }
    public double InspectionMs { get; set; }
    public double UnloadMs { get; set; }
    public double FullCycleMs { get; set; }
    public bool InvalidTransitionRejected { get; set; }
    public bool EmergencyStopBlocked { get; set; }
    public bool SafetyFaultBlocked { get; set; }
    public bool ResetReturnedIdle { get; set; }
    public int AuditEventCount { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<RobotAcceptanceStep> Steps { get; set; } = new();
}

public sealed class RobotAcceptanceSummary
{
    public string Status { get; set; } = "NOT VALIDATED";
    public string SourceKind { get; set; } = "NotValidated";
    public DateTime? CreatedAtUtc { get; set; }
    public string ControllerName { get; set; } = string.Empty;
    public string SafetyControllerName { get; set; } = string.Empty;
    public string SafetySourceKind { get; set; } = "NotValidated";
    public double FullCycleMs { get; set; }
    public bool EmergencyStopBlocked { get; set; }
    public bool SafetyFaultBlocked { get; set; }
    public bool InvalidTransitionRejected { get; set; }
    public bool ResetReturnedIdle { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationPackageManifest
{
    public string SchemaVersion { get; set; } = "stage1-validation-package/v1";
    public string PackageId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = string.Empty;
    public string DeploymentProfile { get; set; } = AOI_Monitor.Models.DeploymentProfile.Stage1ImageValidation.ToString();
    public string StationId { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public string ModelValidationStatus { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string EngineName { get; set; } = string.Empty;
    public double ActiveConfidenceThreshold { get; set; }
    public string DatasetFolderHashOrName { get; set; } = string.Empty;
    public string GroundTruthCsvName { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public ValidationMetricSummary MetricSummary { get; set; } = new();
    public ValidationPackagePerformanceSummary PerformanceSummary { get; set; } = new();
    public InspectionLatencySummary LatencySummary { get; set; } = new();
    public ValidationBreakdownSummary BreakdownSummary { get; set; } = new();
    public DatasetQualitySummary DatasetQualitySummary { get; set; } = new();
    public CameraAcceptanceSummary CameraAcceptanceSummary { get; set; } = new();
    public RobotAcceptanceSummary RobotAcceptanceSummary { get; set; } = new();
    public MesReadinessSummary MesReadinessSummary { get; set; } = new();
    public string AcceptanceStatus { get; set; } = "CONDITIONAL";
    public ValidationAcceptanceCriteria Criteria { get; set; } = new();
    public FalseCallRecommendationSummary? FalseCallRecommendation { get; set; }
    public LearnedVisualModelEvidenceSummary? LearnedVisualModel { get; set; }
    public ThresholdProfileEvidenceSummary ThresholdProfileEvidence { get; set; } = new();
    public string DatasetPreflightStatus { get; set; } = "CONDITIONAL";
    public List<string> DatasetPreflightFailures { get; set; } = new();
    public List<string> DatasetPreflightWarnings { get; set; } = new();
    public List<ValidationIncludedFile> IncludedFiles { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}

public enum FalseCallReductionMode
{
    MinimizeFalsePositives,
    Balanced,
    MaximizeDefectRecall,
}

public sealed class FalseCallReductionCriteria
{
    public double MinimumThreshold { get; set; } = 0.05;
    public double MaximumThreshold { get; set; } = 0.95;
    public double ThresholdStep { get; set; } = 0.05;
    public double ReviewBand { get; set; } = 0.05;
    public int MinimumKnownOk { get; set; } = 1;
    public int MinimumKnownNg { get; set; } = 1;
    public double MaximumPossibleEscapeRate { get; set; } = 0.02;
    public double MaximumFalseCallRate { get; set; } = 0.10;
    public double ManualReviewMinutesPerImage { get; set; } = 2.0;
    public FalseCallReductionMode Mode { get; set; } = FalseCallReductionMode.Balanced;
}

public sealed class ThresholdSweepPoint
{
    public double ConfidenceThreshold { get; set; }
    public double DifferenceThreshold { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int ReviewCount { get; set; }
    public int NgCount { get; set; }
    public int KnownGroundTruthCount { get; set; }
    public int UnknownGroundTruthCount { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public double ReviewRate { get; set; }
    public double NgRate { get; set; }
    public double EstimatedManualReviewMinutes { get; set; }
    public bool MeetsConstraints { get; set; }
    public string Status { get; set; } = "CONDITIONAL";
}

public sealed class OperatingPointRecommendation
{
    public string Status { get; set; } = "INVALID";
    public string Mode { get; set; } = FalseCallReductionMode.Balanced.ToString();
    public ThresholdSweepPoint? Point { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class FalseCallReductionRun
{
    public long Id { get; set; }
    public long? BatchRunId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string EngineName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public FalseCallReductionCriteria Criteria { get; set; } = new();
    public OperatingPointRecommendation Recommendation { get; set; } = new();
    public IReadOnlyList<ThresholdSweepPoint> Points { get; set; } = Array.Empty<ThresholdSweepPoint>();
}

public sealed class FalseCallRecommendationSummary
{
    public long? RunId { get; set; }
    public string Status { get; set; } = "Not available";
    public string Mode { get; set; } = string.Empty;
    public double SelectedThreshold { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public int PossibleEscapeCount { get; set; }
    public double ReviewRate { get; set; }
    public double EstimatedManualReviewMinutes { get; set; }
    public List<string> Limitations { get; set; } = new();
}

public sealed class ThresholdProfileEvidenceSummary
{
    public string Status { get; set; } = "Not deployed";
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string BoardProgram { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string RecipeRevision { get; set; } = string.Empty;
    public long? SourceValidationRunId { get; set; }
    public long? SourceFalseCallReductionRunId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public int RuleCount { get; set; }
    public bool IsDeployed { get; set; }
    public string EvidenceBoundary { get; set; } = "No deployed threshold profile was included.";
}

public sealed class ValidationMetricSummary
{
    public int TotalImages { get; set; }
    public int KnownGroundTruthImages { get; set; }
    public int UnknownGroundTruthImages { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int FalseCall { get; set; }
    public int PossibleEscape { get; set; }
    public int VerifiedNg { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int ReviewCount { get; set; }
}

public sealed class ValidationPackagePerformanceSummary
{
    public double AverageMilliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
    public double MinMilliseconds { get; set; }
    public int CountOverOneSecond { get; set; }
    public int TimedImageCount { get; set; }
}

public sealed class ValidationIncludedFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public enum FactoryReadinessOverallStatus
{
    Go,
    Conditional,
    NoGo,
}

public enum DeploymentProfile
{
    Stage1ImageValidation,
    Stage2CameraPilot,
    Stage3RobotPilot,
    Stage4MesPilot,
    FullFactoryAutomation,
}

public enum OperatingMode
{
    Demo,
    Pilot,
    Production,
}

public enum PilotIssueCategory
{
    UIClipping,
    NavigationPerformance,
    Crash,
    FalseCall,
    PossibleEscape,
    Model,
    Camera,
    Lighting,
    Robot,
    MES,
    Export,
    UI,
    Data,
    Performance,
    Other,
}

public enum PilotIssueStatus
{
    Open,
    Investigating,
    Fixed,
    Verified,
    Waived,
    Closed,
}

public sealed class PilotIssue
{
    public string IssueId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public PilotIssueCategory Category { get; set; } = PilotIssueCategory.Other;
    public string Severity { get; set; } = "Medium";
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public string ReproductionSteps { get; set; } = string.Empty;
    public string ExpectedBehavior { get; set; } = string.Empty;
    public string ActualBehavior { get; set; } = string.Empty;
    public string ScreenshotPath { get; set; } = string.Empty;
    public string RelatedInspectionId { get; set; } = string.Empty;
    public string RelatedAcceptanceRunId { get; set; } = string.Empty;
    public PilotIssueStatus Status { get; set; } = PilotIssueStatus.Open;
    public string Owner { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public DateTime? ClosedAtUtc { get; set; }
}

public sealed class PilotIssueEvent
{
    public long Id { get; set; }
    public string IssueId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string OperatorId { get; set; } = "UNKNOWN";
    public string Message { get; set; } = string.Empty;
    public string PreviousStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
}

public sealed class PilotIssueFilter
{
    public PilotIssueCategory? Category { get; set; }
    public PilotIssueStatus? Status { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public bool OpenOnly { get; set; }
}

public sealed class PilotIssueSummary
{
    public int Total { get; set; }
    public int Open { get; set; }
    public int CriticalOpen { get; set; }
    public Dictionary<string, int> ByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ByStatus { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum CustomerPilotStepStatus
{
    NotStarted,
    Running,
    Passed,
    Conditional,
    Failed,
    Skipped,
}

public enum CustomerPilotStepKind
{
    ConfirmDeploymentProfile,
    RunSystemDiagnostics,
    SelectCustomerDataset,
    RunDatasetPreflight,
    RunBatchValidation,
    RunFalseCallReduction,
    RunModelAcceptance,
    ExportCustomerValidationPackage,
    RunStage2Acceptance,
    ExportReadinessAndChecklist,
}

public sealed class CustomerPilotSessionRecord
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DeploymentProfile DeploymentProfile { get; set; } = DeploymentProfile.Stage1ImageValidation;
    public string Status { get; set; } = "InProgress";
    public string DatasetFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string OperatorId { get; set; } = "UNKNOWN";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class CustomerPilotStepRecord
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public CustomerPilotStepKind StepKey { get; set; }
    public int StepOrder { get; set; }
    public CustomerPilotStepStatus Status { get; set; } = CustomerPilotStepStatus.NotStarted;
    public string EvidencePath { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = new();
    public bool Waived { get; set; }
    public string WaiverReason { get; set; } = string.Empty;
    public string WaivedBy { get; set; } = string.Empty;
    public DateTime? WaivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FactoryReadinessCriteria
{
    public DeploymentProfile DeploymentProfile { get; set; } = DeploymentProfile.Stage1ImageValidation;
    public bool Stage1Only { get; set; } = true;
    public bool RequireSuccessfulLatestValidationPackage { get; set; } = true;
    public bool RequireDatasetQualityEvidence { get; set; } = true;
    public bool RequireProductionModel { get; set; }
    public double MaximumFalseCallRate { get; set; } = 0.10;
    public bool RequireFalseCallEvidence { get; set; }
    public bool RequireNoExportVerificationErrors { get; set; } = true;
    public bool RequireNoPendingMesQueueForProductionMode { get; set; } = true;
    public bool RequireCameraAcceptance { get; set; }
    public bool RequireProfile3DAcceptance { get; set; }
    public bool RequireLightingAcceptance { get; set; }
    public bool RequireRobotAcceptance { get; set; }
    public bool RequireRealHardwareAcceptance { get; set; }
    public bool RequireSoakTestEvidenceForFactoryPilot { get; set; }
    public bool RequirePassingTraceabilityTest { get; set; }
    public bool RequireCentralSyncEvidence { get; set; }
    public bool WarnWhenCentralSyncDisabled { get; set; }
}

public sealed class FactoryReadinessReport
{
    public string SchemaVersion { get; set; } = "factory-readiness/v1";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string DeploymentProfile { get; set; } = AOI_Monitor.Models.DeploymentProfile.Stage1ImageValidation.ToString();
    public string Scope { get; set; } = "Stage 1 readiness";
    public string OverallStatus { get; set; } = FactoryReadinessOverallStatus.Conditional.ToString();
    public List<FactoryReadinessCategory> Categories { get; set; } = new();
    public List<string> BlockingIssues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> UnmetCriteria { get; set; } = new();
    public List<string> RecommendedNextActions { get; set; } = new();
    public List<string> KnownLimitations { get; set; } = new();
}

public sealed class FactoryReadinessCategory
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Conditional";
    public string Evidence { get; set; } = string.Empty;
    public string NextAction { get; set; } = string.Empty;
}

public sealed record FactoryReadinessPackageResult(
    string PackageFolder,
    string SummaryHtmlPath,
    string SummaryJsonPath,
    string ReadmePath);

public record AuditEventRecord(
    long Id,
    DateTime TimestampUtc,
    DateTime LocalTimestamp,
    string UserId,
    string UserRole,
    string StationId,
    string ActionCategory,
    string ActionDetail,
    string RelatedEntityType,
    string RelatedEntityId,
    string RelatedPath);

public record RecipeRevisionRecord(
    long Id,
    string RecipeName,
    string Revision,
    string BoardProgram,
    string OperatorId,
    string DetectionPriority,
    string BackgroundImagePath,
    string RecipeJson,
    DateTime CreatedAtUtc);

public record CalibrationPointInput(
    double ImageX,
    double ImageY,
    double BoardXMillimeters,
    double BoardYMillimeters);

public record CalibrationPointRecord(
    long Id,
    long ProfileId,
    double ImageX,
    double ImageY,
    double BoardXMillimeters,
    double BoardYMillimeters);

public record CalibrationProfileRecord(
    long Id,
    string ProfileName,
    string BoardModel,
    string ViewType,
    string SampleImagePath,
    string OperatorId,
    int PointCount,
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY,
    string TransformSummary,
    DateTime CreatedAtUtc,
    IReadOnlyList<CalibrationPointRecord> Points)
{
    public bool HasTransform => PointCount >= 2;
    public string DisplayName => $"{ProfileName} | {BoardModel} | {ViewType} | {PointCount} pt";
}

public record CalibrationTransform(
    bool IsAvailable,
    int PointCount,
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY,
    string Summary);

public sealed class RecipeDocument
{
    public string RecipeName { get; set; } = "TBOX_TOP";
    public string BoardProgram { get; set; } = "TBOX-MAIN";
    public string BackgroundImagePath { get; set; } = string.Empty;
    public List<RecipeRoiDocument> Rois { get; set; } = new();

    // Recipe-level processing and tolerance settings. Persisted as part of the recipe JSON;
    // missing keys in older recipes fall back to these defaults.
    public double PlacementToleranceMm { get; set; } = 0.020;
    public double RotationToleranceDeg { get; set; } = 1.0;
    public string IpcClass { get; set; } = "IPC Class 2";
    public string LightingProfile { get; set; } = "Top bright field";
    public string FalseCallPolicy { get; set; } = "Balanced: review benign visual noise";
}

public sealed class RecipeRoiDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RoiType { get; set; } = "Presence";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double AiScoreThreshold { get; set; } = 0.65;
    public double HeightMin { get; set; }
    public double HeightMax { get; set; }
    public double VolumeMin { get; set; }
    public double VolumeMax { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RecipeDefinition
{
    public string RecipeName { get; set; } = "AOI_RECIPE";
    public string Revision { get; set; } = string.Empty;
    public string BoardProgram { get; set; } = "UNKNOWN";
    public string DetectionPriority { get; set; } = string.Empty;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public List<RecipeRoi> Rois { get; set; } = new();
}

public sealed class RecipeRoi
{
    public string RoiId { get; set; } = string.Empty;
    public string RoiName { get; set; } = string.Empty;
    public string RoiType { get; set; } = "Presence";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public RecipeThresholds Thresholds { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(RoiName) ? RoiId : RoiName;
}

public sealed class RecipeThresholds
{
    public double AiScoreThreshold { get; set; } = 0.65;
    public double HeightMin { get; set; }
    public double HeightMax { get; set; }
    public double VolumeMin { get; set; }
    public double VolumeMax { get; set; }
}

public sealed class RecipeLoadResult
{
    public RecipeLoadResult(RecipeDefinition? recipe, IReadOnlyList<string> warnings)
    {
        Recipe = recipe;
        Warnings = warnings;
    }

    public RecipeDefinition? Recipe { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool HasEnabledRois => Recipe?.Rois.Any(roi => roi.Enabled) == true;
}

public sealed class LogRetentionPolicy
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public int WarningLeadDays { get; set; } = 7;
}

public readonly record struct LogRetentionResult(int ArchivedRowCount, int PurgedRowCount);
