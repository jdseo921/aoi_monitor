using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class Stage1ExitEvidenceRequest
{
    public string DatasetFolder { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public string OperatorId { get; init; } = "UNKNOWN";
    public string StationId { get; init; } = "AOI-CLI-01";
    public DetectionPriority DetectionPriority { get; init; } = DetectionPriority.Balanced;
    public bool AllowSimulation { get; init; }
}

public sealed class Stage1ExitEvidenceResult
{
    public string Status { get; set; } = "FAIL";
    public bool PrototypeOnly { get; set; }
    public bool DryRun { get; set; }
    public bool ProductionModelAcceptancePassed { get; set; }
    public string EngineName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public long? BatchRunId { get; set; }
    public long? FalseCallReductionRunId { get; set; }
    public string ModelAcceptanceStatus { get; set; } = "NOT RUN";
    public long? ModelAcceptanceRunId { get; set; }
    public string PreflightStatus { get; set; } = "NOT RUN";
    public int ManifestRows { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
    public int FalseCallCount { get; set; }
    public int PossibleEscapeCount { get; set; }
    public string ValidationPackageStatus { get; set; } = "NOT RUN";
    public string ExportVerificationStatus { get; set; } = "NOT RUN";
    public string FactoryReadinessStatus { get; set; } = "NOT RUN";
    public Stage1ExitEvidenceOutputPaths Outputs { get; set; } = new();
    public string EvidenceBoundary { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class Stage1ExitEvidenceOutputPaths
{
    public string OutputFolder { get; set; } = string.Empty;
    public string ValidationPackageFolder { get; set; } = string.Empty;
    public string ValidationManifestPath { get; set; } = string.Empty;
    public string ValidationReportPath { get; set; } = string.Empty;
    public string ValidationResultsCsvPath { get; set; } = string.Empty;
    public string ExportVerificationJsonPath { get; set; } = string.Empty;
    public string ExportVerificationTextPath { get; set; } = string.Empty;
    public string FactoryReadinessFolder { get; set; } = string.Empty;
    public string FactoryReadinessHtmlPath { get; set; } = string.Empty;
    public string FactoryReadinessJsonPath { get; set; } = string.Empty;
    public string SummaryJsonPath { get; set; } = string.Empty;
    public string SummaryTextPath { get; set; } = string.Empty;
}

public sealed class Stage1ExitEvidenceException : Exception
{
    public Stage1ExitEvidenceException(string message)
        : base(message)
    {
    }
}

public static class Stage1ExitEvidenceService
{
    public const string SimulationDryRunNotice = "simulation-dry-run; not customer dataset acceptance";

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    private static readonly HashSet<string> UnsupportedImageLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".tif",
        ".tiff",
        ".webp",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Stage1ExitEvidenceResult Run(
        Stage1ExitEvidenceRequest request,
        IInspectionEngine? engineOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = ValidateAndResolvePaths(request);
        Directory.CreateDirectory(paths.OutputFolder);
        Directory.CreateDirectory(paths.ValidationRoot);
        Directory.CreateDirectory(paths.ExportVerificationRoot);
        Directory.CreateDirectory(paths.FactoryReadinessRoot);

        AoiDatabase.Initialize();
        cancellationToken.ThrowIfCancellationRequested();

        var result = new Stage1ExitEvidenceResult
        {
            DryRun = request.AllowSimulation,
            EvidenceBoundary = request.AllowSimulation
                ? SimulationDryRunNotice
                : "Customer validation evidence requires approved customer data and, when configured, production model acceptance.",
            Outputs =
            {
                OutputFolder = paths.OutputFolder,
                SummaryJsonPath = Path.Combine(paths.OutputFolder, "stage1_exit_summary.json"),
                SummaryTextPath = Path.Combine(paths.OutputFolder, "stage1_exit_summary.txt"),
            },
        };

        var preflight = CustomerDatasetPreflightService.Validate(paths.DatasetFolder, paths.ManifestPath);
        result.PreflightStatus = preflight.Status;
        result.ManifestRows = preflight.ManifestRows;
        result.OkCount = preflight.OkCount;
        result.NgCount = preflight.NgCount;
        result.Warnings.AddRange(preflight.Warnings);

        if (preflight.Status == "FAIL")
        {
            result.Messages.AddRange(preflight.BlockingFailures);
            result.Status = "FAIL";
            WriteSummary(result);
            return result;
        }

        var activeModel = GetActiveReadyOnnxModel();
        result.PrototypeOnly = activeModel is null;
        if (result.PrototypeOnly)
        {
            result.Warnings.Add("No active ONNX model is configured and Ready. Evidence is prototype-only and is not production model acceptance.");
        }
        if (request.AllowSimulation)
        {
            result.Warnings.Add(SimulationDryRunNotice);
        }

        var batch = RunBatchValidation(paths.DatasetFolder, paths.ManifestPath, request.DetectionPriority, engineOverride, cancellationToken);
        result.EngineName = batch.Engine.Name;
        result.ModelVersion = batch.Engine.Version;
        result.BatchRunId = batch.RunId > 0 ? batch.RunId : null;
        result.FalseCallReductionRunId = batch.FalseCallReductionRun.Id > 0 ? batch.FalseCallReductionRun.Id : null;
        result.Accuracy = batch.Metrics.Accuracy;
        result.Precision = batch.Metrics.Precision;
        result.Recall = batch.Metrics.Recall;
        result.FalseCallRate = batch.Metrics.FalseCallRate;
        result.FalseCallCount = batch.Metrics.FalseCall;
        result.PossibleEscapeCount = batch.Metrics.PossibleEscape;
        result.Warnings.AddRange(batch.Warnings);

        ModelAcceptanceRun? modelAcceptance = null;
        if (activeModel is not null)
        {
            modelAcceptance = ModelAcceptanceService.RunAcceptance(
                paths.DatasetFolder,
                paths.ManifestPath,
                operatorId: request.OperatorId,
                role: UserRole.Engineer,
                cancellationToken: cancellationToken);
            result.ModelAcceptanceStatus = modelAcceptance.Status;
            result.ModelAcceptanceRunId = modelAcceptance.Id > 0 ? modelAcceptance.Id : null;
            result.ProductionModelAcceptancePassed = string.Equals(modelAcceptance.Status, "PASS", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            result.ModelAcceptanceStatus = "PROTOTYPE_ONLY";
        }

        var package = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = paths.ValidationRoot,
            RunId = batch.RunId > 0 ? batch.RunId : null,
            RunCreatedAtUtc = DateTime.UtcNow,
            StationId = request.StationId,
            OperatorId = request.OperatorId,
            OperatorRole = "Engineer",
            BoardModel = FirstNonBlank(batch.Rows.Select(row => row.BoardModel), "Not provided"),
            LotId = FirstNonBlank(batch.Rows.Select(row => row.LotId), "Not provided"),
            ModelId = activeModel?.ModelId ?? "Prototype-only - no active ONNX model acceptance",
            ModelSha256 = activeModel?.Sha256 ?? "Not available",
            ModelValidationStatus = activeModel?.ValidationStatus.ToString() ?? "Prototype-only",
            EngineName = batch.Engine.Name,
            ModelVersion = batch.Engine.Version,
            ModelFileName = activeModel is null ? "Not configured" : Path.GetFileName(activeModel.StoredModelPath),
            ConfidenceThreshold = activeModel?.ConfidenceThreshold ?? InspectionModelConfigurationService.Load().ConfidenceThreshold,
            DatasetFolder = paths.DatasetFolder,
            GroundTruthCsvPath = paths.ManifestPath,
            IsFormalManifest = batch.Manifest.IsFormalManifest,
            Metrics = batch.Metrics,
            PerformanceSummary = batch.Performance,
            Rows = batch.Rows,
            Warnings = result.Warnings,
            DatasetPreflightResult = preflight,
            DatasetQualitySummary = DatasetQualityService.Analyze(batch.Rows, batch.Manifest),
            FalseCallReductionRun = batch.FalseCallReductionRun,
        }, cancellationToken);
        result.ValidationPackageStatus = package.Acceptance.Status;
        result.Outputs.ValidationPackageFolder = package.PackageFolder;
        result.Outputs.ValidationManifestPath = package.ManifestPath;
        result.Outputs.ValidationReportPath = package.ReportPath;
        result.Outputs.ValidationResultsCsvPath = package.CsvPath;
        result.Warnings.AddRange(package.Warnings);
        result.Messages.AddRange(package.Acceptance.Messages);

        var exportVerification = ExportVerificationService.Verify(package.PackageFolder, "Stage1ValidationPackage", persist: true);
        var exportReport = ExportVerificationService.ExportReport(exportVerification, paths.ExportVerificationRoot);
        result.ExportVerificationStatus = exportVerification.Status.ToString();
        result.Outputs.ExportVerificationJsonPath = exportReport.JsonPath;
        result.Outputs.ExportVerificationTextPath = exportReport.TextPath;
        result.Warnings.AddRange(exportVerification.Messages.Where(message => !message.Contains("OK", StringComparison.OrdinalIgnoreCase)));

        var readinessReport = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));
        var readinessPackage = FactoryReadinessService.ExportGoNoGoPackage(
            FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation),
            paths.FactoryReadinessRoot);
        result.FactoryReadinessStatus = readinessReport.OverallStatus;
        result.Outputs.FactoryReadinessFolder = readinessPackage.PackageFolder;
        result.Outputs.FactoryReadinessHtmlPath = readinessPackage.SummaryHtmlPath;
        result.Outputs.FactoryReadinessJsonPath = readinessPackage.SummaryJsonPath;
        result.Warnings.AddRange(readinessReport.Warnings);
        result.Messages.AddRange(readinessReport.BlockingIssues);

        var determinedStatus = DetermineStatus(result, modelAcceptance, exportVerification.Status);
        result.Status = request.AllowSimulation && exportVerification.Status != ExportVerificationStatus.ERROR
            ? "DRY RUN"
            : determinedStatus;
        WriteSummary(result);
        return result;
    }

    public static string BuildTextSummary(Stage1ExitEvidenceResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Status: {result.Status}");
        if (result.DryRun)
            sb.AppendLine($"Evidence boundary: {SimulationDryRunNotice}.");
        sb.AppendLine($"Preflight: {result.PreflightStatus}; rows={result.ManifestRows}; OK={result.OkCount}; NG={result.NgCount}");
        sb.AppendLine($"Batch: run={result.BatchRunId?.ToString(CultureInfo.InvariantCulture) ?? "not saved"}; engine={result.EngineName}; model={result.ModelVersion}");
        sb.AppendLine($"Metrics: accuracy={result.Accuracy:P1}; precision={result.Precision:P1}; recall={result.Recall:P1}; false-call={result.FalseCallRate:P1}; possible-escapes={result.PossibleEscapeCount}");
        sb.AppendLine($"Model acceptance: {result.ModelAcceptanceStatus}");
        if (result.PrototypeOnly)
            sb.AppendLine("Production model readiness: NOT CLAIMED. This run is prototype-only because no active Ready ONNX model acceptance PASS was available.");
        else
            sb.AppendLine($"Production model readiness: {(result.ProductionModelAcceptancePassed ? "PASS evidence present" : "NOT CLAIMED")}");
        sb.AppendLine($"Validation package: {result.ValidationPackageStatus} - {result.Outputs.ValidationPackageFolder}");
        sb.AppendLine($"Export verification: {result.ExportVerificationStatus} - {result.Outputs.ExportVerificationTextPath}");
        sb.AppendLine($"Factory readiness: {result.FactoryReadinessStatus} - {result.Outputs.FactoryReadinessFolder}");

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var warning in result.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
                sb.AppendLine($"- {warning}");
        }

        if (result.Messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Messages:");
            foreach (var message in result.Messages.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
                sb.AppendLine($"- {message}");
        }

        return sb.ToString();
    }

    private static Stage1ExitResolvedPaths ValidateAndResolvePaths(Stage1ExitEvidenceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetFolder))
            throw new Stage1ExitEvidenceException("Dataset folder is required. Use --dataset <folder>.");
        if (!Directory.Exists(request.DatasetFolder))
            throw new Stage1ExitEvidenceException($"Dataset folder was not found: {request.DatasetFolder}");
        if (string.IsNullOrWhiteSpace(request.ManifestPath))
            throw new Stage1ExitEvidenceException("Manifest CSV is required. Use --manifest <csv>.");
        if (!File.Exists(request.ManifestPath))
            throw new Stage1ExitEvidenceException($"Manifest CSV was not found: {request.ManifestPath}");
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new Stage1ExitEvidenceException("Output folder is required. Use --output <folder>.");

        var outputFolder = Path.GetFullPath(request.OutputFolder);
        return new Stage1ExitResolvedPaths(
            Path.GetFullPath(request.DatasetFolder),
            Path.GetFullPath(request.ManifestPath),
            outputFolder,
            Path.Combine(outputFolder, "stage1_validation_package"),
            Path.Combine(outputFolder, "export_verification"),
            Path.Combine(outputFolder, "stage1_factory_readiness"));
    }

    private static Stage1ExitBatchEvidence RunBatchValidation(
        string datasetFolder,
        string manifestPath,
        DetectionPriority priority,
        IInspectionEngine? engineOverride,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var imageFolder = Directory.Exists(Path.Combine(datasetFolder, "images"))
            ? Path.Combine(datasetFolder, "images")
            : datasetFolder;
        var folderFiles = Directory.EnumerateFiles(imageFolder, "*.*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        warnings.AddRange(folderFiles
            .Where(path => UnsupportedImageLikeExtensions.Contains(Path.GetExtension(path)))
            .Select(path => $"Unsupported image format skipped: {Path.GetFileName(path)}. Use PNG/JPG/JPEG for Stage 1 validation."));

        var imageFiles = folderFiles
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .ToArray();
        var manifest = BatchValidationService.LoadValidationManifest(manifestPath, datasetFolder);
        warnings.AddRange(manifest.Warnings);
        var runItems = BatchValidationService.BuildRunItems(imageFiles, manifest);
        if (runItems.Count == 0)
            throw new Stage1ExitEvidenceException("No validation images were found from the manifest or dataset folder.");

        var engine = engineOverride ?? InspectionEngineFactory.Create();
        var rows = new List<BatchTestRow>();
        foreach (var item in runItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(item.ImagePath))
                {
                    var message = $"Missing image file: {item.ImagePath}";
                    warnings.Add(message);
                    rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, message, engine.Name, engine.Version));
                    continue;
                }

                AnalysisResult analysis;
                using (InspectionScope.ForBoardModel(item.Manifest.BoardModel))
                {
                    analysis = engine.Analyze(
                        item.ImagePath,
                        string.IsNullOrWhiteSpace(item.Manifest.GoldenPath) ? null : item.Manifest.GoldenPath,
                        priority);
                }

                rows.Add(BatchValidationService.ToRow(item.ImagePath, item.Manifest, analysis));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                var message = $"Validation failed for {Path.GetFileName(item.ImagePath)}: {ex.Message}";
                warnings.Add(message);
                rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, message, engine.Name, engine.Version));
            }
        }

        var metrics = BatchValidationService.CalculateMetrics(rows);
        var performance = BatchValidationService.CalculatePerformanceSummary(rows);
        var runId = AoiDatabase.RecordBatchTestRun(
            datasetFolder,
            manifestPath,
            engine.Name,
            engine.Version,
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            rows.Select(row => row.ToRecord()).ToArray(),
            rows.Select(row => row.ThresholdProfileId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty,
            rows.Select(row => row.ThresholdProfileRevision).FirstOrDefault(revision => !string.IsNullOrWhiteSpace(revision)) ?? string.Empty);
        var falseCallRun = FalseCallReductionService.AnalyzeAndPersist(
            rows,
            engine.Name,
            engine.Version,
            InspectionModelConfigurationService.Load().ActiveModelId,
            InspectionModelConfigurationService.Load().ActiveModelSha256,
            runId,
            FalseCallReductionService.CreateSweepCriteria(ToFalseCallMode(priority)));

        return new Stage1ExitBatchEvidence(engine, manifest, rows, metrics, performance, runId, falseCallRun, warnings);
    }

    private static FalseCallReductionMode ToFalseCallMode(DetectionPriority priority)
        => priority switch
        {
            DetectionPriority.MinimizeFalsePositives => FalseCallReductionMode.MinimizeFalsePositives,
            DetectionPriority.MaximizeDefectRecall => FalseCallReductionMode.MaximizeDefectRecall,
            _ => FalseCallReductionMode.Balanced,
        };

    private static ModelRegistryEntry? GetActiveReadyOnnxModel()
    {
        var configuration = InspectionModelConfigurationService.Load();
        var activeModel = ModelRegistryService.GetActiveModel();
        if (!configuration.IsOnnxSelected ||
            activeModel is null ||
            activeModel.ValidationStatus != ModelConfigurationTestStatus.Ready ||
            !File.Exists(activeModel.StoredModelPath))
        {
            return null;
        }

        return activeModel;
    }

    private static string DetermineStatus(
        Stage1ExitEvidenceResult result,
        ModelAcceptanceRun? modelAcceptance,
        ExportVerificationStatus exportVerificationStatus)
    {
        var hardFailure = string.Equals(result.PreflightStatus, "FAIL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ValidationPackageStatus, "FAIL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.FactoryReadinessStatus, FactoryReadinessOverallStatus.NoGo.ToString(), StringComparison.OrdinalIgnoreCase) ||
            exportVerificationStatus == ExportVerificationStatus.ERROR ||
            (modelAcceptance is not null && string.Equals(modelAcceptance.Status, "FAIL", StringComparison.OrdinalIgnoreCase));
        if (hardFailure)
            return "FAIL";

        var warning = result.PrototypeOnly ||
            string.Equals(result.PreflightStatus, "CONDITIONAL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ValidationPackageStatus, "CONDITIONAL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.FactoryReadinessStatus, FactoryReadinessOverallStatus.Conditional.ToString(), StringComparison.OrdinalIgnoreCase) ||
            exportVerificationStatus == ExportVerificationStatus.WARN ||
            (modelAcceptance is not null && !string.Equals(modelAcceptance.Status, "PASS", StringComparison.OrdinalIgnoreCase)) ||
            result.Warnings.Count > 0;
        return warning ? "WARN" : "PASS";
    }

    private static void WriteSummary(Stage1ExitEvidenceResult result)
    {
        Directory.CreateDirectory(result.Outputs.OutputFolder);
        File.WriteAllText(result.Outputs.SummaryJsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.WriteAllText(result.Outputs.SummaryTextPath, BuildTextSummary(result), Encoding.UTF8);
    }

    private static string FirstNonBlank(IEnumerable<string> values, string fallback)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? fallback;

    private sealed record Stage1ExitResolvedPaths(
        string DatasetFolder,
        string ManifestPath,
        string OutputFolder,
        string ValidationRoot,
        string ExportVerificationRoot,
        string FactoryReadinessRoot);

    private sealed record Stage1ExitBatchEvidence(
        IInspectionEngine Engine,
        ValidationManifest Manifest,
        IReadOnlyList<BatchTestRow> Rows,
        BatchMetrics Metrics,
        BatchPerformanceSummary Performance,
        long RunId,
        FalseCallReductionRun FalseCallReductionRun,
        IReadOnlyList<string> Warnings);
}
