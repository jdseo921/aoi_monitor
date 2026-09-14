using System.Diagnostics;
using System.Text;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class Stage1ReadinessGateServiceTests : IDisposable
{
    private readonly string _root;

    public Stage1ReadinessGateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Stage1Readiness_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "storage"));
        AoiDatabase.Initialize();
        CrashReportService.ClearForTests();
        WorkflowState.Instance.SetCurrentUser("Stage1Admin", UserRole.Admin);
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        CrashReportService.ClearForTests();
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Stage 1 readiness test cleanup failed: {ex.Message}");
        }
    }

    [Fact]
    public void Stage1ReadinessPassesWithValidationPackageBenchmarkAndNoRealHardware()
    {
        var seed = SeedStage1Evidence(includeBuildEvidence: true);

        var report = Stage1ReadinessGateService.Evaluate(new Stage1ReadinessGateOptions
        {
            RepositoryRoot = seed.RepositoryRoot,
            DatasetFolder = seed.DatasetFolder,
            ManifestPath = seed.ManifestPath,
        });

        Assert.Equal(Stage1ReadinessGateService.Pass, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "Stage 1 hardware scope" && check.Status == Stage1ReadinessGateService.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Validation package export" && check.Status == Stage1ReadinessGateService.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Inspection performance benchmark" && check.Status == Stage1ReadinessGateService.Pass);
        Assert.Equal(50, report.TotalImages);
        Assert.Equal(0, report.OverOneSecondCount);
    }

    [Fact]
    public void MissingBuildEvidenceIsConditionalNotHardwareFailure()
    {
        var seed = SeedStage1Evidence(includeBuildEvidence: false);

        var report = Stage1ReadinessGateService.Evaluate(new Stage1ReadinessGateOptions
        {
            RepositoryRoot = seed.RepositoryRoot,
            DatasetFolder = seed.DatasetFolder,
            ManifestPath = seed.ManifestPath,
        });

        Assert.Equal(Stage1ReadinessGateService.Conditional, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "App build/test evidence" && check.Status == Stage1ReadinessGateService.Conditional);
        Assert.DoesNotContain(report.MissingEvidence, item => item.Contains("real camera", StringComparison.OrdinalIgnoreCase) && item.Contains("FAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingValidationRunFailsStage1Readiness()
    {
        var repositoryRoot = CreateRepositoryRoot();
        BuildTestEvidenceService.CreateLocalEvidence(operatorId: "Stage1Admin [Admin]");

        var report = Stage1ReadinessGateService.Evaluate(new Stage1ReadinessGateOptions
        {
            RepositoryRoot = repositoryRoot,
        });

        Assert.Equal(Stage1ReadinessGateService.Fail, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "Latest batch validation run" && check.Status == Stage1ReadinessGateService.Fail);
    }

    [Fact]
    public void CriticalPilotIssueFailsStage1Readiness()
    {
        var seed = SeedStage1Evidence(includeBuildEvidence: true);
        PilotIssueService.Create(new PilotIssue
        {
            Category = PilotIssueCategory.Crash,
            Severity = "Critical",
            PageName = "AI / Models",
            ReproductionSteps = "Synthetic critical issue for Stage 1 readiness gate.",
            ExpectedBehavior = "No critical issues are open.",
            ActualBehavior = "Critical issue remains open.",
        }, "Stage1Admin [Admin]");

        var report = Stage1ReadinessGateService.Evaluate(new Stage1ReadinessGateOptions
        {
            RepositoryRoot = seed.RepositoryRoot,
            DatasetFolder = seed.DatasetFolder,
            ManifestPath = seed.ManifestPath,
        });

        Assert.Equal(Stage1ReadinessGateService.Fail, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "Open critical pilot issues" && check.Status == Stage1ReadinessGateService.Fail);
    }

    [Fact]
    public void Stage1ReadinessReportExportsHtmlPdfAndJson()
    {
        var seed = SeedStage1Evidence(includeBuildEvidence: true);
        var exportRoot = Path.Combine(_root, "stage1-readiness-export");

        var export = Stage1ReadinessGateService.ExportReport(exportRoot, new Stage1ReadinessGateOptions
        {
            RepositoryRoot = seed.RepositoryRoot,
            DatasetFolder = seed.DatasetFolder,
            ManifestPath = seed.ManifestPath,
        });

        Assert.Equal(Stage1ReadinessGateService.Pass, export.Report.OverallStatus);
        Assert.True(File.Exists(export.HtmlPath));
        Assert.True(File.Exists(export.PdfPath));
        Assert.True(File.Exists(export.JsonPath));
        Assert.Contains("Stage 1 Readiness Report", File.ReadAllText(export.HtmlPath));
        Assert.Contains(AoiDatabase.GetExportVerifications(), record => record.ExportType == "Stage1ReadinessReport");
    }

    [Fact]
    public void GeneratedSampleManifestHasRequiredColumns()
    {
        var repositoryRoot = FindRepositoryRoot();
        var generator = Path.Combine(repositoryRoot, "SampleData", "demo_dataset_generator.ps1");
        Assert.True(File.Exists(generator), $"Demo dataset generator not found: {generator}");

        var outputRoot = Path.Combine(_root, "generated-demo");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(generator);
        startInfo.ArgumentList.Add("-OutputRoot");
        startInfo.ArgumentList.Add(outputRoot);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell demo dataset generator.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);
        var manifest = Path.Combine(outputRoot, "customer_validation_manifest.csv");
        Assert.True(File.Exists(manifest));
        var headers = File.ReadLines(manifest)
            .First()
            .Split(',')
            .Select(header => header.Trim().Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in CustomerDatasetPreflightService.RequiredManifestColumns)
            Assert.Contains(column, headers);
    }

    [Fact]
    public void Stage1SmokeGeneratedDatasetCompletesPreflightAndBenchmarkWhenProvided()
    {
        var datasetRoot = Environment.GetEnvironmentVariable("AOI_STAGE1_DEMO_DATASET_ROOT");
        if (string.IsNullOrWhiteSpace(datasetRoot))
            return;

        var manifest = Path.Combine(datasetRoot, "customer_validation_manifest.csv");
        var images = Path.Combine(datasetRoot, "images");
        var preflight = CustomerDatasetPreflightService.Validate(datasetRoot, manifest);
        Assert.Contains(preflight.Status, new[] { "PASS", "CONDITIONAL" });
        Assert.Empty(preflight.BlockingFailures);

        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "stage1-smoke-storage"));
        AoiDatabase.Initialize();
        var benchmark = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = images,
                RunCount = 5,
                OutputRoot = Path.Combine(_root, "stage1-smoke-benchmark"),
            },
            engineOverride: new FixedTimingEngine(10, 20, 30, 40));

        Assert.Equal(5, benchmark.CompletedCount);
        Assert.True(benchmark.P95FrameToOverlayMs > 0);
        Assert.Equal(0, benchmark.OverOneSecondCount);
        Assert.True(File.Exists(benchmark.CsvPath));
    }

    [Fact]
    public void AcceptanceCriteriaDefaultMetricGatesAre97Percent()
    {
        // Product decision 2026-09-14: acceptance metric gates raised from 90% to 97%.
        // A silent default drift here would loosen every validation package and model
        // acceptance verdict, so the defaults are pinned.
        var validation = new ValidationAcceptanceCriteria();
        Assert.Equal(0.97, validation.MinimumAccuracy);
        Assert.Equal(0.97, validation.MinimumPrecision);
        Assert.Equal(0.97, validation.MinimumRecall);

        var model = new ModelAcceptanceCriteria();
        Assert.Equal(0.97, model.MinimumAccuracy);
        Assert.Equal(0.97, model.MinimumPrecision);
        Assert.Equal(0.97, model.MinimumRecall);
    }

    private Stage1EvidenceSeed SeedStage1Evidence(bool includeBuildEvidence)
    {
        var repositoryRoot = CreateRepositoryRoot();
        var dataset = CreateDataset(Path.Combine(_root, "customer-dataset"));
        if (includeBuildEvidence)
            BuildTestEvidenceService.CreateLocalEvidence(testResultPath: Path.Combine(_root, "stage1-tests.trx"), operatorId: "Stage1Admin [Admin]");

        var manifest = BatchValidationService.LoadValidationManifest(dataset.ManifestPath, dataset.DatasetFolder);
        var rows = manifest.OrderedEntries.Select((entry, index) =>
        {
            var label = BatchValidationService.NormalizeBinaryLabel(entry.Label);
            return new BatchTestRow
            {
                ImagePath = entry.ImagePath ?? string.Empty,
                Image = Path.GetFileName(entry.ImagePath ?? string.Empty),
                GoldenImagePath = entry.GoldenPath ?? string.Empty,
                GroundTruth = label,
                EngineResult = label,
                InspectionEngine = "Pixel Difference Prototype Engine",
                ModelVersion = "PIXEL_DIFF_TEST",
                Score = label == "NG" ? 92 : 4,
                PassFail = "PASS",
                DefectType = entry.DefectType,
                NormalizedDefectClass = BatchValidationService.NormalizeDefectClass(entry.DefectType),
                NormalizedSide = BatchValidationService.NormalizeSide(entry.Side),
                RoiId = entry.RoiId,
                RoiType = entry.RoiType,
                FailureCategory = "PASS",
                Side = entry.Side,
                RefDes = entry.RefDes,
                LotId = entry.LotId,
                BoardModel = entry.BoardModel,
                Notes = entry.Notes,
                RoiX = 0.2,
                RoiY = 0.2,
                RoiWidth = 0.2,
                RoiHeight = 0.2,
                ImageLoadMilliseconds = 10 + index % 3,
                PreprocessingMilliseconds = 20,
                InferenceMilliseconds = 30,
                OverlayRenderingMilliseconds = 40,
                TotalInspectionMilliseconds = 100 + index % 3,
            };
        }).ToArray();

        var metrics = BatchValidationService.CalculateMetrics(rows);
        var performance = BatchValidationService.CalculatePerformanceSummary(rows);
        var runId = AoiDatabase.RecordBatchTestRun(
            dataset.ImagesFolder,
            dataset.ManifestPath,
            "Pixel Difference Prototype Engine",
            "PIXEL_DIFF_TEST",
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            rows.Select(row => row.ToRecord()).ToArray());
        FalseCallReductionService.AnalyzeAndPersist(
            rows,
            "Pixel Difference Prototype Engine",
            "PIXEL_DIFF_TEST",
            "PIXEL_DIFF_TEST",
            "Not available",
            runId,
            operatorId: "Stage1Admin [Admin]");
        var benchmark = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = dataset.ImagesFolder,
                RunCount = 5,
                OutputRoot = Path.Combine(_root, "benchmark-output"),
            },
            engineOverride: new FixedTimingEngine(10, 20, 30, 40));
        Assert.Equal("PASS", benchmark.Status);

        var preflight = CustomerDatasetPreflightService.Validate(dataset.DatasetFolder, dataset.ManifestPath);
        Assert.Equal("PASS", preflight.Status);
        var package = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = Path.Combine(_root, "packages"),
            RunId = runId,
            RunCreatedAtUtc = DateTime.UtcNow,
            StationId = "AOI-STAGE1-UNIT",
            OperatorId = "Stage1Admin",
            OperatorRole = "Admin",
            BoardModel = "TBOX-STAGE1",
            LotId = "LOT-STAGE1",
            EngineName = "Pixel Difference Prototype Engine",
            ModelVersion = "PIXEL_DIFF_TEST",
            DatasetFolder = dataset.DatasetFolder,
            GroundTruthCsvPath = dataset.ManifestPath,
            IsFormalManifest = true,
            Metrics = metrics,
            PerformanceSummary = performance,
            Rows = rows,
            DatasetPreflightResult = preflight,
            DatasetQualitySummary = DatasetQualityService.Analyze(rows, manifest),
            FalseCallReductionRun = AoiDatabase.GetLatestFalseCallReductionRun(runId),
        });
        Assert.Equal("PASS", package.Acceptance.Status);

        return new Stage1EvidenceSeed(repositoryRoot, dataset.DatasetFolder, dataset.ManifestPath);
    }

    private string CreateRepositoryRoot()
    {
        var repo = Path.Combine(_root, "repo");
        var sampleData = Path.Combine(repo, "SampleData");
        Directory.CreateDirectory(sampleData);
        File.WriteAllText(Path.Combine(repo, "AOI_PCB_Database.slnx"), string.Empty);
        File.WriteAllText(
            Path.Combine(sampleData, "customer_validation_manifest_template.csv"),
            "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes" + Environment.NewLine,
            Encoding.UTF8);
        return repo;
    }

    private static DatasetSeed CreateDataset(string datasetFolder)
    {
        var images = Path.Combine(datasetFolder, "images");
        var golden = Path.Combine(datasetFolder, "golden");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(golden);
        WritePng(Path.Combine(golden, "tbox_ref_top.png"), 1000);
        WritePng(Path.Combine(golden, "tbox_ref_bottom.png"), 1001);
        var rows = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var name = $"tbox_stage1_{i:D4}_top_ok.png";
            WritePng(Path.Combine(images, name), i);
            rows.Add(Row($"images/{name}", "OK", "golden/tbox_ref_top.png", "OK", "top", "BOARD", $"ROI-OK-{i:D4}", "board"));
        }

        for (var i = 0; i < 15; i++)
        {
            var name = $"tbox_stage1_bridge_{i:D4}_top.png";
            WritePng(Path.Combine(images, name), 100 + i);
            rows.Add(Row($"images/{name}", "NG", "golden/tbox_ref_top.png", "solder_bridge", "top", "U107", "ROI-U107", "pad"));
        }

        for (var i = 0; i < 15; i++)
        {
            var name = $"tbox_stage1_missing_{i:D4}_bottom.png";
            WritePng(Path.Combine(images, name), 200 + i);
            rows.Add(Row($"images/{name}", "NG", "golden/tbox_ref_bottom.png", "missing_component", "bottom", "R12", "ROI-R12", "component"));
        }

        var manifest = Path.Combine(datasetFolder, "customer_validation_manifest.csv");
        File.WriteAllLines(
            manifest,
            new[] { "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes" }.Concat(rows),
            Encoding.UTF8);
        return new DatasetSeed(datasetFolder, images, manifest);
    }

    private static string Row(string image, string groundTruth, string golden, string defectType, string side, string refdes, string roiId, string roiType)
        => $"{image},{groundTruth},{golden},{defectType},{side},{refdes},{roiId},{roiType},LOT-STAGE1,TBOX-STAGE1,synthetic Stage 1 validation row";

    private static void WritePng(string path, int suffix)
    {
        var bytes = TinyPngBytes().Concat(Encoding.UTF8.GetBytes(suffix.ToString())).ToArray();
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AOI_PCB_Database.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class FixedTimingEngine : IInspectionEngine
    {
        private readonly double _load;
        private readonly double _preprocess;
        private readonly double _inference;
        private readonly double _overlay;

        public FixedTimingEngine(double load, double preprocess, double inference, double overlay)
        {
            _load = load;
            _preprocess = preprocess;
            _inference = inference;
            _overlay = overlay;
        }

        public string Name => "Fixed Timing Engine";
        public string Version => "TEST-STAGE1";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
        {
            var timing = new InspectionTiming
            {
                ImageLoadMilliseconds = _load,
                PreprocessingMilliseconds = _preprocess,
                InferenceMilliseconds = _inference,
                OverlayRenderingMilliseconds = _overlay,
            };
            timing.RecalculateTotal();
            return new AnalysisResult
            {
                SamplePath = samplePath,
                GoldenPath = goldenPath,
                Verdict = "OK",
                InspectionEngine = Name,
                ModelVersion = Version,
                Timing = timing,
            };
        }
    }

    private sealed record DatasetSeed(string DatasetFolder, string ImagesFolder, string ManifestPath);

    private sealed record Stage1EvidenceSeed(string RepositoryRoot, string DatasetFolder, string ManifestPath);
}
