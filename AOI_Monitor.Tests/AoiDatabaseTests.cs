using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class AoiDatabaseTests : IDisposable
{
    private readonly string _root;

    public AoiDatabaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"AOI database test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"AOI database test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void StorageRootSettingsPersistAndApplyConfiguredRoot()
    {
        var settingsDir = Path.Combine(_root, "settings");
        var configuredRoot = Path.Combine(_root, "configured-storage");
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(settingsDir);

        StorageRootSettingsService.SaveStorageRoot(configuredRoot);
        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "other-storage"));

        var loadedRoot = StorageRootSettingsService.ApplySavedStorageRoot();

        Assert.Equal(Path.GetFullPath(configuredRoot), loadedRoot);
        Assert.Equal(Path.GetFullPath(configuredRoot), AoiDatabase.StorageRoot);
        Assert.True(Directory.Exists(configuredRoot));
        Assert.True(File.Exists(StorageRootSettingsService.SettingsPath));
    }

    [Fact]
    public void InitializeCreatesSqliteDatabaseAndExpectedTables()
    {
        AoiDatabase.Initialize();

        Assert.True(File.Exists(AoiDatabase.DatabasePath));
        Assert.True(Directory.Exists(AoiDatabase.ImageVaultPath));
        Assert.Equal("ok", AoiDatabase.RunIntegrityCheck(), ignoreCase: true);

        var tables = ReadTableNames();
        Assert.Contains("Images", tables);
        Assert.Contains("InspectionResults", tables);
        Assert.Contains("InspectionLatencyTraces", tables);
        Assert.Contains("BatchTestRuns", tables);
        Assert.Contains("ModelRegistry", tables);
        Assert.Contains("ModelAcceptanceMetrics", tables);
        Assert.Contains("RecipeRevisions", tables);
        Assert.Contains("CalibrationProfiles", tables);
        Assert.Contains("CalibrationPoints", tables);
        Assert.Contains("ImageLearningProjects", tables);
        Assert.Contains("ImageLearningProjectImages", tables);
        Assert.Contains("LearnedPcbVisualModels", tables);
        Assert.Contains("LearnedPcbVisualModelArtifacts", tables);
        Assert.Contains("ImageLearningInspectionResults", tables);
        Assert.Contains("ImageLearningAnomalyRegions", tables);
        Assert.Contains("ImageLearningCalibrationResults", tables);
        Assert.Contains("ImageLearningComparisonResults", tables);
        Assert.Contains("ExportHistory", tables);
        Assert.Contains("ExportVerification", tables);
        Assert.Contains("Profile3DAcceptanceRuns", tables);
        Assert.Contains("TraceabilityTestReports", tables);
        Assert.Contains("CentralSyncQueue", tables);
        Assert.Contains("CentralSyncAttempts", tables);
        Assert.Contains("CustomerPilotSessions", tables);
        Assert.Contains("CustomerPilotSteps", tables);
        Assert.Contains("ValidationPackages", tables);
        Assert.Contains("FalseCallReductionRuns", tables);
        Assert.Contains("FalseCallReductionPoints", tables);
        Assert.Contains("ThresholdProfiles", tables);
        Assert.Contains("ThresholdProfileRules", tables);
        Assert.Contains("ThresholdProfileDeployments", tables);
        Assert.Contains("CameraAcceptanceRuns", tables);
        Assert.Contains("CameraAcceptanceFrames", tables);
        Assert.Contains("LightingAcceptanceRuns", tables);
        Assert.Contains("LightingAcceptanceSteps", tables);
        Assert.Contains("RobotAcceptanceRuns", tables);
        Assert.Contains("RobotAcceptanceSteps", tables);
        Assert.Contains("SoakTestRuns", tables);
        Assert.Contains("SoakTestIterations", tables);
        Assert.Contains("AuditEvents", tables);
        Assert.Contains("LocalUsers", tables);
        Assert.Contains("LocalUserSessions", tables);
        Assert.Contains("SchemaInfo", tables);
    }

    [Fact]
    public void NewDatabaseInitializesToLatestSchemaVersion()
    {
        AoiDatabase.Initialize();

        using var connection = OpenTestConnection();
        Assert.Equal(AoiDatabaseMigrations.LatestVersion, AoiDatabase.GetSchemaVersion(connection));
        Assert.True(AoiDatabase.TableExists(connection, "SchemaInfo"));
        Assert.True(AoiDatabase.ColumnExists(connection, "InspectionResults", "BoardProgram"));
        Assert.True(AoiDatabase.IndexExists(connection, "IX_InspectionResults_CreatedAtUtc"));
    }

    [Fact]
    public void CsvProfile3DSourceProducesProfileFrame()
    {
        var csvPath = Path.Combine(_root, "height.csv");
        Directory.CreateDirectory(_root);
        File.WriteAllText(csvPath, "x,y,height\n0,0,10\n1,0,20\n0,1,30\n1,1,40\n", Encoding.UTF8);

        var source = new CsvProfile3DSource(csvPath);
        source.StartAcquisition();
        var frame = source.GetNextHeightMap();

        Assert.NotNull(frame);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal("CSV Sample", frame.SourceKind);
        Assert.True(frame.IsSimulated);
        Assert.True(source.IsAcquiring);
        Assert.Equal(new[] { 10d, 20d, 30d, 40d }, frame.HeightValues);
    }

    [Fact]
    public void NullProfile3DSourceAcceptanceFailsAsNotConnected()
    {
        var run = Profile3DAcceptanceTestService.Run(new NullProfile3DSource());

        Assert.Equal("FAIL", run.Status);
        Assert.Equal("NOT VALIDATED", run.FactoryReadinessStatus);
        Assert.Contains(run.Failures, failure => failure.Contains("No height map", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Profile3DAcceptanceFailsOnInvalidHeightData()
    {
        var source = new TestProfile3DSource(new Profile3DFrame
        {
            FrameId = "BAD-HEIGHTS",
            SourceKind = "Test",
            IsSimulated = true,
            Width = 2,
            Height = 2,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
            HeightValues = new[] { 1d, double.NaN, 3d, 4d },
            ViewType = "Top",
        });

        var run = Profile3DAcceptanceTestService.Run(source);

        Assert.Equal("FAIL", run.Status);
        Assert.Equal(1, run.NaNHeightCount);
        Assert.Contains(run.Failures, failure => failure.Contains("NaN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeightRoiProducesNgWhenHeightExceedsConfiguredLimit()
    {
        var frame = new Profile3DFrame
        {
            FrameId = "HEIGHT-ROI",
            SourceKind = "Test",
            IsSimulated = true,
            Width = 2,
            Height = 2,
            Unit = "microns",
            XPitchMicrons = 10,
            YPitchMicrons = 10,
            HeightValues = new[] { 75d, 80d, 150d, 90d },
            ViewType = "Top",
        };
        var roi = new RecipeRoi
        {
            RoiId = "ROI-H1",
            RoiName = "Height check",
            RoiType = "Height",
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
            Thresholds = new RecipeThresholds
            {
                HeightMax = 100,
            },
        };

        var defect = Profile3DAcceptanceTestService.EvaluateHeightRoi(frame, roi);

        Assert.Equal("NG", defect.JudgmentStatus);
        Assert.Equal(150d, defect.HeightMicrons);
        Assert.Equal("ROI-H1", defect.SourceRoiId);
    }

    [Fact]
    public void FakeVisionCameraPluginLoadsAndDiscoversDevices()
    {
        var pluginFolder = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(pluginFolder);
        var manifestPath = Path.Combine(pluginFolder, "fake.camera-adapter.json");
        var assemblyPath = typeof(FakeVisionCameraAdapterFactory).Assembly.Location;
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new VisionCameraAdapterManifest
            {
                AdapterId = FakeVisionCameraAdapterFactory.Id,
                DisplayName = "Fake Vision Plugin",
                Version = "1.0.0",
                AssemblyFile = assemblyPath,
                FactoryTypeName = typeof(FakeVisionCameraAdapterFactory).FullName!,
                SupportedInterfaces = new() { "GigE Vision", "USB3 Vision" },
                SupportedPixelFormats = new() { "Mono8" },
            }),
            Encoding.UTF8);

        var load = VisionCameraPluginLoader.LoadFactory(pluginFolder);
        var discovery = VisionCameraPluginLoader.CreateDiscovery(new CameraSourceSettings { AdapterFolder = pluginFolder }, out var message);
        var devices = discovery.DiscoverDevices();

        Assert.True(load.Success, load.Message);
        Assert.Contains("Loaded camera adapter", message);
        Assert.Contains(devices, device => device.SuggestedView == "Top" && device.DeviceId == "FAKE-TOP" && device.Capabilities!.Contains("SoftwareTrigger"));
        Assert.Contains(devices, device => device.SuggestedView == "Side" && device.DeviceId == "FAKE-SIDE");
        Assert.Contains(devices, device => device.SuggestedView == "Bottom" && device.DeviceId == "FAKE-BOTTOM");
    }

    [Fact]
    public void InvalidVisionCameraPluginFailsSafely()
    {
        var pluginFolder = Path.Combine(_root, "bad-plugins");
        Directory.CreateDirectory(pluginFolder);
        File.WriteAllText(
            Path.Combine(pluginFolder, "bad.camera-adapter.json"),
            JsonSerializer.Serialize(new VisionCameraAdapterManifest
            {
                AdapterId = "bad",
                DisplayName = "Bad",
                Version = "1",
                AssemblyFile = "missing.dll",
                FactoryTypeName = "Missing.Factory",
                SupportedInterfaces = new() { "GigE Vision" },
                SupportedPixelFormats = new() { "Mono8" },
            }),
            Encoding.UTF8);

        var load = VisionCameraPluginLoader.LoadFactory(pluginFolder);
        var adapter = VisionCameraPluginLoader.CreateAdapterOrNull(new CameraSourceSettings { AdapterFolder = pluginFolder }, out var message);

        Assert.False(load.Success);
        Assert.Contains("not found", load.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<DiagnosticNullVisionCameraAdapter>(adapter);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimulatedOlderDatabaseMissingNewerColumnsMigratesSuccessfully()
    {
        Directory.CreateDirectory(_root);
        using (var connection = OpenTestConnection())
        {
            ExecuteSql(
                connection,
                """
                CREATE TABLE SchemaInfo
                (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );

                INSERT INTO SchemaInfo (Key, Value) VALUES ('SchemaVersion', '0');

                CREATE TABLE InspectionResults
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SampleImagePath TEXT NOT NULL,
                    GoldenImagePath TEXT NULL,
                    DifferenceScore REAL NOT NULL,
                    MeanBrightness REAL NOT NULL,
                    Verdict TEXT NOT NULL,
                    Confidence REAL NOT NULL,
                    SuggestedDefect TEXT NOT NULL,
                    PolicyName TEXT NOT NULL,
                    ModelVersion TEXT NOT NULL,
                    DecisionReason TEXT NOT NULL,
                    HotspotX REAL NOT NULL,
                    HotspotY REAL NOT NULL,
                    HotspotWidth REAL NOT NULL,
                    HotspotHeight REAL NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE Defects
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    InspectionResultId INTEGER NULL,
                    ImageId INTEGER NULL,
                    RefDes TEXT NULL,
                    DefectType TEXT NOT NULL,
                    Severity TEXT NOT NULL,
                    RoiX REAL NULL,
                    RoiY REAL NULL,
                    RoiWidth REAL NULL,
                    RoiHeight REAL NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE RecipeRevisions
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RecipeName TEXT NOT NULL,
                    Revision TEXT NOT NULL,
                    DetectionPriority TEXT NOT NULL,
                    Notes TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE BatchTestRuns
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ImageFolder TEXT NOT NULL,
                    GroundTruthCsvPath TEXT NULL,
                    EngineName TEXT NOT NULL,
                    Accuracy REAL NOT NULL,
                    Precision REAL NOT NULL,
                    Recall REAL NOT NULL,
                    FalseCallRate REAL NOT NULL,
                    TotalImages INTEGER NOT NULL,
                    FailedCount INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE BatchTestResults
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunId INTEGER NOT NULL,
                    ImagePath TEXT NOT NULL,
                    ImageName TEXT NOT NULL,
                    GroundTruth TEXT NOT NULL,
                    EngineResult TEXT NOT NULL,
                    Score REAL NOT NULL,
                    PassFail TEXT NOT NULL,
                    DefectType TEXT NOT NULL,
                    RoiX REAL NOT NULL,
                    RoiY REAL NOT NULL,
                    RoiWidth REAL NOT NULL,
                    RoiHeight REAL NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE ExportHistory
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ExportType TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                """);
        }

        AoiDatabase.Initialize();

        using var migrated = OpenTestConnection();
        Assert.Equal(AoiDatabaseMigrations.LatestVersion, AoiDatabase.GetSchemaVersion(migrated));
        Assert.True(AoiDatabase.ColumnExists(migrated, "InspectionResults", "BoardProgram"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "InspectionResults", "TotalInspectionMs"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "InspectionResults", "ThresholdProfileId"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "Defects", "RoiId"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "RecipeRevisions", "RecipeJson"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "BatchTestRuns", "ModelVersion"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "BatchTestRuns", "ThresholdProfileId"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "BatchTestResults", "TotalInspectionMs"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "BatchTestResults", "NormalizedDefectClass"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "BatchTestResults", "FailureCategory"));
        Assert.True(AoiDatabase.ColumnExists(migrated, "ExportHistory", "OperatorId"));
        Assert.True(AoiDatabase.TableExists(migrated, "ModelRegistry"));
        Assert.True(AoiDatabase.TableExists(migrated, "ValidationPackages"));
        Assert.True(AoiDatabase.TableExists(migrated, "ValidationBreakdownMetrics"));
        Assert.True(AoiDatabase.TableExists(migrated, "FalseCallReductionRuns"));
        Assert.True(AoiDatabase.TableExists(migrated, "FalseCallReductionPoints"));
        Assert.True(AoiDatabase.TableExists(migrated, "ThresholdProfiles"));
        Assert.True(AoiDatabase.TableExists(migrated, "ThresholdProfileRules"));
        Assert.True(AoiDatabase.TableExists(migrated, "ThresholdProfileDeployments"));
        Assert.True(AoiDatabase.TableExists(migrated, "CameraAcceptanceRuns"));
        Assert.True(AoiDatabase.TableExists(migrated, "CameraAcceptanceFrames"));
        Assert.True(AoiDatabase.TableExists(migrated, "LightingAcceptanceRuns"));
        Assert.True(AoiDatabase.TableExists(migrated, "LightingAcceptanceSteps"));
        Assert.True(AoiDatabase.TableExists(migrated, "RobotAcceptanceRuns"));
        Assert.True(AoiDatabase.TableExists(migrated, "RobotAcceptanceSteps"));
        Assert.True(AoiDatabase.TableExists(migrated, "SoakTestRuns"));
        Assert.True(AoiDatabase.TableExists(migrated, "SoakTestIterations"));
        Assert.True(AoiDatabase.TableExists(migrated, "MesSpoolQueue"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningProjects"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningProjectImages"));
        Assert.True(AoiDatabase.TableExists(migrated, "LearnedPcbVisualModels"));
        Assert.True(AoiDatabase.TableExists(migrated, "LearnedPcbVisualModelArtifacts"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningInspectionResults"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningAnomalyRegions"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningCalibrationResults"));
        Assert.True(AoiDatabase.TableExists(migrated, "ImageLearningComparisonResults"));
        Assert.True(AoiDatabase.TableExists(migrated, "LocalUsers"));
        Assert.True(AoiDatabase.TableExists(migrated, "LocalUserSessions"));
        Assert.True(AoiDatabase.IndexExists(migrated, "IX_InspectionResults_BoardProgram"));
    }

    [Fact]
    public void RerunningInitializeIsIdempotent()
    {
        AoiDatabase.Initialize();
        var version = AoiDatabase.GetSchemaVersion();

        AoiDatabase.Initialize();

        Assert.Equal(version, AoiDatabase.GetSchemaVersion());
        Assert.Equal(AoiDatabaseMigrations.LatestVersion, version);
        Assert.Equal("ok", AoiDatabase.RunIntegrityCheck(), ignoreCase: true);
    }

    [Fact]
    public void MigrationFailureDoesNotLeavePartialVersionIncrement()
    {
        Directory.CreateDirectory(_root);
        using var connection = OpenTestConnection();
        var failingMigration = new AoiDatabaseMigration(
            1,
            "Intentional failure after a transactional change.",
            (db, transaction) =>
            {
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE PartialMigrationMarker (Id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("boom");
            });

        Assert.Throws<InvalidOperationException>(
            () => AoiDatabaseMigrations.ApplyPending(connection, new[] { failingMigration }));

        Assert.Equal(0, AoiDatabase.GetSchemaVersion(connection));
        Assert.False(AoiDatabase.TableExists(connection, "PartialMigrationMarker"));
    }

    [Fact]
    public void ExportVerificationCsvWithCorrectHeadersPassesAndPersists()
    {
        AoiDatabase.Initialize();
        var csv = Path.Combine(_root, "validation_results.csv");
        File.WriteAllText(
            csv,
            "Image,Ground Truth,AI/Engine Result,Inspection Engine,Model Version,Recipe Name,Recipe Revision,Score,Pass/Fail,Defect Type,ROI ID,ROI Type,Side,RefDes,LotId,BoardModel,Notes,Image Path,RoiX,RoiY,RoiWidth,RoiHeight,ImageLoadMs,PreprocessingMs,InferenceMs,OverlayRenderingMs,TotalInspectionMs" + Environment.NewLine,
            Encoding.UTF8);

        var recorded = ExportVerificationService.RecordVerifiedExport("Stage1ValidationCsv", csv);
        var persisted = AoiDatabase.GetLatestExportVerification(recorded.ExportHistoryId);

        Assert.Equal(ExportVerificationStatus.OK, recorded.Verification.Status);
        Assert.NotEmpty(recorded.Verification.Sha256);
        Assert.NotNull(persisted);
        Assert.Equal("OK", persisted.Status);
        Assert.Equal(recorded.Verification.Sha256, persisted.Sha256);
    }

    [Fact]
    public void ExportVerificationMissingFileFails()
    {
        var result = ExportVerificationService.Verify(Path.Combine(_root, "missing.csv"), "Stage1ValidationCsv");

        Assert.Equal(ExportVerificationStatus.ERROR, result.Status);
        Assert.Contains(result.Messages, message => message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportVerificationJsonMissingRequiredFieldsFails()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "latest_decision.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"inspection-result/v1\"}", Encoding.UTF8);

        var result = ExportVerificationService.Verify(path, "MachineInterfaceDecisionJson");

        Assert.Equal(ExportVerificationStatus.ERROR, result.Status);
        Assert.Contains(result.Messages, message => message.Contains("inspectionId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportVerificationPngSignaturePasses()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "overlay.png");
        File.WriteAllBytes(path, TinyPngBytes());

        var result = ExportVerificationService.Verify(path, "AnnotatedImageOverlay");

        Assert.Equal(ExportVerificationStatus.OK, result.Status);
        Assert.NotEmpty(result.Sha256);
        Assert.Contains(result.Messages, message => message.Contains("PNG signature verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PdfExportServiceCreatesNonEmptyPdfFromTinyHtmlReport()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "tiny_report.pdf");

        PdfExportService.ExportHtmlToPdf(
            "<html><body><h1>Factory Readiness</h1><p>Status: Go</p></body></html>",
            path,
            "Tiny Test Report");

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes.Take(4).ToArray()));
    }

    [Fact]
    public void ExportVerificationPdfSignaturePasses()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "customer_validation_report.pdf");
        PdfExportService.ExportHtmlToPdf("<h1>Customer Validation</h1>", path, "Customer Validation Report");

        var result = ExportVerificationService.Verify(path, "CustomerValidationPdfReport");

        Assert.Equal(ExportVerificationStatus.OK, result.Status);
        Assert.NotEmpty(result.Sha256);
        Assert.Contains(result.Messages, message => message.Contains("PDF signature verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PdfExportPreservesKoreanTextViaUnicodeFont()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "korean_report.pdf");

        PdfExportService.ExportHtmlToPdf(
            "<html><body><h1>검사 보고서</h1><p>결과: 합격</p></body></html>",
            path,
            "검사 보고서");

        var bytes = File.ReadAllBytes(path);
        var content = Encoding.ASCII.GetString(bytes);

        Assert.Equal("%PDF", content[..4]);
        // Non-ASCII (Korean) content must select the Type0 CID font path and be encoded as hex text,
        // instead of being silently dropped to blanks by the ASCII-only path.
        Assert.Contains("/Type0", content, StringComparison.Ordinal);
        Assert.Contains("/UniKS-UCS2-H", content, StringComparison.Ordinal);
        // "검사" ("검사") encodes to big-endian UCS-2 hex AC80C0AC.
        Assert.Contains("AC80C0AC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportVerificationCustomerPackageManifestMismatchFails()
    {
        var package = Path.Combine(_root, "stage1_validation_package");
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "package_manifest.json"),
            """
            {
              "schemaVersion": "stage1-customer-package/v1",
              "packageId": "unit-test-package",
              "generatedAtUtc": "2026-06-21T00:00:00Z",
              "includedFiles": [
                { "relativePath": "missing.csv", "fileType": "CSV", "bytes": 12 }
              ]
            }
            """,
            Encoding.UTF8);

        var result = ExportVerificationService.Verify(package, "Stage1CustomerPackage");

        Assert.Equal(ExportVerificationStatus.ERROR, result.Status);
        Assert.Contains(result.Messages, message => message.Contains("missing.csv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportVerificationPrefersRootPackageManifestWhenNestedPackagesExist()
    {
        var package = Path.Combine(_root, "stage2_package");
        var nested = Path.Combine(package, "factory_readiness", "factory_readiness_20260701_000000");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(package, "README.txt"), "stage 2 root package");
        File.WriteAllText(Path.Combine(nested, "nested.txt"), "nested factory readiness package");
        File.WriteAllText(
            Path.Combine(nested, "package_manifest.json"),
            """
            {
              "schemaVersion": "factory-readiness-package/v1",
              "packageId": "nested-package",
              "generatedAtUtc": "2026-07-01T00:00:00Z",
              "includedFiles": [
                { "relativePath": "nested.txt", "fileType": "Nested text", "bytes": 32 }
              ]
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(package, "package_manifest.json"),
            """
            {
              "schemaVersion": "stage2-camera-pilot-package/v1",
              "packageId": "root-package",
              "generatedAtUtc": "2026-07-01T00:00:00Z",
              "includedFiles": [
                { "relativePath": "README.txt", "fileType": "README", "bytes": 20 },
                { "relativePath": "factory_readiness/factory_readiness_20260701_000000/nested.txt", "fileType": "Nested text", "bytes": 32 },
                { "relativePath": "factory_readiness/factory_readiness_20260701_000000/package_manifest.json", "fileType": "Nested manifest", "bytes": 224 }
              ]
            }
            """,
            Encoding.UTF8);

        var result = ExportVerificationService.Verify(package, "Stage2CameraPilotEvidencePackage");

        Assert.Equal(ExportVerificationStatus.OK, result.Status);
        Assert.Contains(result.Messages, message => message.Contains("manifest file list matches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsReturnOkForTempWritableStorage()
    {
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        CameraSourceSettingsService.ApplyActiveSource();
        LightingSettingsService.ApplyIntegrationBoundary();
        MesIntegrationSettingsService.ApplyIntegrationBoundary();

        var report = SystemDiagnosticService.RunDiagnostics();

        Assert.Contains(report.Checks, check => check.Category == "Storage" && check.Status == DiagnosticStatus.OK);
        Assert.Contains(report.Checks, check => check.Category == "Database" && check.Status == DiagnosticStatus.OK);
        Assert.Contains(report.Checks, check => check.Category == "Image Vault" && check.Name == "Image vault folder" && check.Status == DiagnosticStatus.OK);
        Assert.Contains(report.Checks, check => check.Category == "Image Vault" && check.Name == "Training folder" && check.Status == DiagnosticStatus.OK);
        Assert.Contains(report.Checks, check => check.Category == "Exports" && check.Status == DiagnosticStatus.OK);
        Assert.Contains(report.Checks, check => check.Category == "Inspection Engine" && check.Status != DiagnosticStatus.Error);
    }

    [Fact]
    public void DiagnosticsReturnErrorForInvalidStorageRootPath()
    {
        AoiDatabase.Initialize();
        var filePath = Path.Combine(_root, "not-a-folder.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(filePath, "not a directory");

        var report = SystemDiagnosticService.RunDiagnostics(filePath);

        Assert.Contains(report.Checks, check => check.Category == "Storage" && check.Status == DiagnosticStatus.Error);
    }

    [Fact]
    public void FirstRunSettingsPersistCompletionFlag()
    {
        AoiDatabase.Initialize();

        FirstRunSettingsService.MarkCompleted();
        FirstRunSettingsService.ResetForTests();
        var settings = FirstRunSettingsService.Load();

        Assert.True(settings.Completed);
        Assert.Equal(FirstRunSettings.CurrentWizardVersion, settings.VersionCompleted);
        Assert.True(FirstRunSettingsService.IsCompleted());
    }

    [Fact]
    public void DiagnosticExportContainsAllCategories()
    {
        AoiDatabase.Initialize();
        var report = SystemDiagnosticService.RunDiagnostics();
        var exportRoot = Path.Combine(_root, "diagnostic_exports");

        var export = SystemDiagnosticService.ExportReport(report, exportRoot);
        var json = File.ReadAllText(export.JsonPath);
        var html = File.ReadAllText(export.HtmlPath);
        var text = File.ReadAllText(export.TextPath);

        Assert.True(File.Exists(export.JsonPath));
        Assert.True(File.Exists(export.HtmlPath));
        Assert.True(File.Exists(export.TextPath));
        foreach (var category in new[]
                 {
                     "Storage",
                     "Database",
                     "Image Vault",
                     "Exports",
                     "Inspection Engine",
                     "Camera",
                     "Lighting",
                     "Robot",
                     "MES / Traceability",
                     "E-Stop",
                     "Model Registry",
                 })
        {
            Assert.Contains(category, json);
            Assert.Contains(category, html);
            Assert.Contains(category, text);
        }
    }

    [Fact]
    public void TryImportImageValidatesMissingUnsupportedAndInvalidFiles()
    {
        AoiDatabase.Initialize();
        var unsupported = Path.Combine(_root, "notes.txt");
        var invalidImage = Path.Combine(_root, "not-image.png");
        File.WriteAllText(unsupported, "hello");
        File.WriteAllText(invalidImage, "not a png");

        Assert.Equal("Missing", AoiDatabase.TryImportImage(Path.Combine(_root, "missing.png"), "BM", "LOT", "top").Status);
        Assert.Equal("Unsupported", AoiDatabase.TryImportImage(unsupported, "BM", "LOT", "top").Status);
        Assert.Equal("Invalid", AoiDatabase.TryImportImage(invalidImage, "BM", "LOT", "top").Status);
    }

    [Fact]
    public void TryImportImageDetectsDuplicateByHash()
    {
        AoiDatabase.Initialize();
        var source = WriteTinyPng("sample.png");

        var first = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");
        var second = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");

        Assert.True(first.Imported);
        Assert.Equal("Imported", first.Status);
        Assert.False(second.Imported);
        Assert.Equal("Duplicate", second.Status);
        Assert.Equal(first.Image?.FileHash, second.Image?.FileHash);
        Assert.Single(AoiDatabase.GetImportedImages());
    }

    [Fact]
    public void TryImportImageCopiesImageIntoConfiguredVault()
    {
        AoiDatabase.Initialize();
        var source = WriteTinyPng("vault-copy.png");

        var result = AoiDatabase.TryImportImage(source, "TBOX", "LOT-1", "top");

        Assert.True(result.Imported);
        Assert.NotNull(result.Image);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(result.Image.VaultPath));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(result.Image.VaultPath));
        Assert.StartsWith(Path.GetFullPath(AoiDatabase.ImageVaultPath), Path.GetFullPath(result.Image.VaultPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.Image.VaultPath));
        Assert.Equal(source, result.Image.OriginalPath);
    }

    [Fact]
    public void RecordInspectionResultPersistsHistory()
    {
        AoiDatabase.Initialize();
        var result = new AnalysisResult
        {
            SamplePath = @"C:\temp\sample.png",
            GoldenPath = @"C:\temp\golden.png",
            BoardProgram = "TBOX-MAIN",
            OperatorId = "Engineer01",
            InspectionEngine = "Unit Test Engine",
            DifferenceScore = 18.5,
            MeanBrightness = 112.25,
            Verdict = "NG",
            Confidence = 0.91,
            SuggestedDefect = "Bridge",
            PolicyName = "Balanced",
            ModelVersion = "TEST-1",
            ModelFilePath = @"C:\models\unit-test.onnx",
            ConfidenceThreshold = 0.73,
            DecisionReason = "Synthetic test result",
            Hotspot = new Rect(0.1, 0.2, 0.3, 0.4),
            Timing = new InspectionTiming
            {
                ImageLoadMilliseconds = 12,
                PreprocessingMilliseconds = 23,
                InferenceMilliseconds = 34,
                OverlayRenderingMilliseconds = 5,
                TotalInspectionMilliseconds = 74,
            },
            Defects =
            {
                new DefectResult
                {
                    DefectType = "Bridge",
                    Confidence = 0.88,
                    BoundingBox = new Rect(0.1, 0.2, 0.3, 0.4),
                    XPosition = 12,
                    YPosition = 34,
                    SideOrViewType = "top",
                    RoiId = "R1",
                    JudgmentStatus = "NG",
                },
            },
        };

        AoiDatabase.RecordInspectionResult(result);

        var history = AoiDatabase.GetInspectionHistory(new LogFilter());
        Assert.Single(history);
        Assert.Equal("TBOX-MAIN", history[0].BoardProgram);
        Assert.Equal("NG", history[0].Verdict);
        Assert.Equal("Unit Test Engine", history[0].InspectionEngine);
        Assert.Equal("TEST-1", history[0].ModelVersion);
        Assert.Equal(@"C:\models\unit-test.onnx", history[0].ModelFilePath);
        Assert.Equal(0.73, history[0].ConfidenceThreshold, precision: 3);
        Assert.Equal(18.5, history[0].DifferenceScore, precision: 3);
        Assert.Equal(12, history[0].ImageLoadMilliseconds, precision: 3);
        Assert.Equal(23, history[0].PreprocessingMilliseconds, precision: 3);
        Assert.Equal(34, history[0].InferenceMilliseconds, precision: 3);
        Assert.Equal(5, history[0].OverlayRenderingMilliseconds, precision: 3);
        Assert.Equal(74, history[0].TotalInspectionMilliseconds, precision: 3);
    }

    [Fact]
    public void CentralSyncQueuesLocalInspectionPayload()
    {
        AoiDatabase.Initialize();
        CentralSyncSettingsService.Save(new CentralSyncSettings { StationId = "STATION-01" });
        AoiDatabase.RecordInspectionResult(CreateCentralSyncInspectionResult());

        var queued = CentralSyncService.QueueLocalChangesForSync();

        var queue = AoiDatabase.GetCentralSyncQueue();
        Assert.True(queued >= 1);
        var item = Assert.Single(queue, row => row.ItemType == "InspectionResult");
        Assert.Equal("Pending", item.Status);
        Assert.Contains("STATION-01", item.PayloadJson);
        Assert.Contains("REDACTED", item.PayloadJson);
        Assert.DoesNotContain("Operator42", item.PayloadJson);
    }

    [Fact]
    public async Task CentralSyncFileDropWritesJsonPayload()
    {
        AoiDatabase.Initialize();
        var dropFolder = Path.Combine(_root, "central-drop");
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.FileDrop,
            EndpointOrFolder = dropFolder,
            StationId = "STATION-FILE",
            RedactEndpointInExports = false,
        });
        AoiDatabase.RecordInspectionResult(CreateCentralSyncInspectionResult());
        CentralSyncService.QueueLocalChangesForSync();

        var summary = await CentralSyncService.RetryEligibleAsync();

        Assert.True(summary.Attempted >= 1);
        Assert.True(summary.Sent >= 1);
        Assert.Contains(AoiDatabase.GetCentralSyncQueue(), row => row.Status == "Sent" && File.Exists(row.PayloadPath));
        Assert.Contains(Directory.GetFiles(dropFolder, "*.json"), path => Path.GetFileName(path).StartsWith("InspectionResult_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CentralSyncFailedBoundaryRemainsPending()
    {
        AoiDatabase.Initialize();
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.PostgreSqlBoundary,
            EndpointOrFolder = "central-postgres-boundary",
            StationId = "STATION-OFFLINE",
        });
        AoiDatabase.RecordInspectionResult(CreateCentralSyncInspectionResult());
        CentralSyncService.QueueLocalChangesForSync();

        var summary = await CentralSyncService.RetryEligibleAsync();

        Assert.True(summary.Failed >= 1);
        var item = Assert.Single(AoiDatabase.GetCentralSyncQueue(), row => row.ItemType == "InspectionResult");
        Assert.Equal("Pending", item.Status);
        Assert.Equal(1, item.RetryCount);
        Assert.Contains("not configured", item.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CentralSyncExportRedactsSecretsAndEndpoint()
    {
        AoiDatabase.Initialize();
        const string secret = "super-secret-token";
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.FileDrop,
            EndpointOrFolder = $"C:\\drop\\{secret}",
            SharedSecret = secret,
            RedactEndpointInExports = true,
        });
        AoiDatabase.EnqueueCentralSyncItem(
            "InspectionResult",
            "123",
            "{}",
            string.Empty,
            $"C:\\drop\\{secret}",
            "STATION-SECRET",
            5,
            lastError: $"failed with {secret}");

        var report = CentralSyncService.ExportQueueReport(Path.Combine(_root, "reports"));
        var json = File.ReadAllText(report.JsonPath);
        var html = File.ReadAllText(report.HtmlPath);

        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain(secret, html);
        Assert.Contains("***", json);
        Assert.Contains("***", html);
    }

    [Fact]
    public void CentralSyncQueuesManagementEvidencePayloads()
    {
        AoiDatabase.Initialize();
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            StationId = "STATION-MGMT",
            RedactOperatorId = true,
            RedactImagePaths = true,
        });
        AoiDatabase.RecordAuditEvent("UNIT_TEST_AUDIT", "Central sync audit evidence.", operatorWithRole: "Engineer42 [Engineer]");
        AoiDatabase.RecordBuildTestEvidence(new BuildTestEvidenceRecord
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Configuration = "Release",
            HygieneStatus = "PASS",
            RestoreStatus = "PASS",
            BuildStatus = "PASS",
            TestStatus = "PASS",
            PublishValidationStatus = "PASS",
            OperatorId = "Engineer42 [Engineer]",
            EvidencePath = @"C:\customer\evidence\build.json",
        });
        AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Fake Camera",
            SourceKey = "folder",
            Status = "PASS",
            FactoryReadinessStatus = "NOT VALIDATED",
            TotalRequestedFrames = 1,
            TotalReceivedFrames = 1,
        });
        AoiDatabase.RecordRobotAcceptanceRun(new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Robot",
            SourceKind = "Simulated",
            Status = "PASS",
            EmergencyStopBlocked = true,
            SafetyFaultBlocked = true,
            ResetReturnedIdle = true,
        });
        AoiDatabase.RecordTraceabilityTestReport(new TraceabilityTestReport
        {
            CreatedAtUtc = DateTime.UtcNow,
            Status = "PASS",
            Mode = "REST",
            ResultStatus = "PASS",
            ImageStatus = "NOT SENT",
            EndpointUrl = "https://mes.test/api/aoi/results",
            OperatorId = "Engineer42 [Engineer]",
            ReportJsonPath = @"C:\customer\evidence\traceability.json",
        });

        var queued = CentralSyncService.QueueLocalChangesForSync();
        var queue = AoiDatabase.GetCentralSyncQueue(1000);

        Assert.True(queued >= 5);
        Assert.Contains(queue, row => row.ItemType == "AuditEvent");
        Assert.Contains(queue, row => row.ItemType == "BuildTestEvidence" && row.PayloadJson.Contains("REDACTED"));
        Assert.Contains(queue, row => row.ItemType == "CameraAcceptanceRun");
        Assert.Contains(queue, row => row.ItemType == "RobotAcceptanceRun");
        Assert.Contains(queue, row => row.ItemType == "TraceabilityAcceptanceRun");
        Assert.DoesNotContain(queue, row => row.PayloadJson.Contains("Engineer42", StringComparison.Ordinal));
        Assert.DoesNotContain(queue, row => row.PayloadJson.Contains(@"C:\customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecordInspectionResultPersistsDefectRows()
    {
        AoiDatabase.Initialize();
        var result = new AnalysisResult
        {
            SamplePath = @"C:\temp\sample.png",
            GoldenPath = @"C:\temp\golden.png",
            BoardProgram = "TBOX-MAIN",
            OperatorId = "Engineer01 [Engineer]",
            InspectionEngine = "Unit Test Engine",
            DifferenceScore = 18.5,
            MeanBrightness = 112.25,
            Verdict = "NG",
            Confidence = 0.91,
            SuggestedDefect = "Bridge",
            PolicyName = "Balanced",
            ModelVersion = "TEST-1",
            DecisionReason = "Synthetic test result",
            Hotspot = new Rect(0.1, 0.2, 0.3, 0.4),
            Defects =
            {
                new DefectResult
                {
                    DefectType = "Bridge",
                    Confidence = 0.88,
                    BoundingBox = new Rect(0.1, 0.2, 0.3, 0.4),
                    XPosition = 12,
                    YPosition = 34,
                    SideOrViewType = "top",
                    RoiId = "R1",
                    JudgmentStatus = "NG",
                },
            },
        };

        AoiDatabase.RecordInspectionResult(result);

        using var connection = new SqliteConnection($"Data Source={AoiDatabase.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.DefectType, d.Confidence, d.RoiX, d.RoiY, d.RoiWidth, d.RoiHeight,
                   d.XPosition, d.YPosition, d.SideOrViewType, d.RoiId, d.JudgmentStatus,
                   r.Verdict
            FROM Defects d
            INNER JOIN InspectionResults r ON r.Id = d.InspectionResultId;
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Bridge", reader.GetString(0));
        Assert.Equal(0.88, reader.GetDouble(1), precision: 3);
        Assert.Equal(0.1, reader.GetDouble(2), precision: 3);
        Assert.Equal(0.2, reader.GetDouble(3), precision: 3);
        Assert.Equal(0.3, reader.GetDouble(4), precision: 3);
        Assert.Equal(0.4, reader.GetDouble(5), precision: 3);
        Assert.Equal(12, reader.GetDouble(6), precision: 3);
        Assert.Equal(34, reader.GetDouble(7), precision: 3);
        Assert.Equal("top", reader.GetString(8));
        Assert.Equal("R1", reader.GetString(9));
        Assert.Equal("NG", reader.GetString(10));
        Assert.Equal("NG", reader.GetString(11));
        Assert.False(reader.Read());
    }

    [Fact]
    public void CalculateMetricsBuildsConfusionMatrixAndRates()
    {
        var rows = new[]
        {
            Row("NG", "NG"),
            Row("OK", "OK"),
            Row("OK", "NG"),
            Row("NG", "OK"),
            Row("UNKNOWN", "NG"),
        };

        var metrics = BatchValidationService.CalculateMetrics(rows);

        Assert.Equal(0.5, metrics.Accuracy, precision: 3);
        Assert.Equal(0.5, metrics.Precision, precision: 3);
        Assert.Equal(0.5, metrics.Recall, precision: 3);
        Assert.Equal(0.5, metrics.FalseCallRate, precision: 3);
        Assert.Equal(1, metrics.TruePositive);
        Assert.Equal(1, metrics.TrueNegative);
        Assert.Equal(1, metrics.FalsePositive);
        Assert.Equal(1, metrics.FalseNegative);
        Assert.Equal(1, metrics.Unknown);
        Assert.Equal(2, metrics.OkCount);
        Assert.Equal(3, metrics.NgCount);
        Assert.Equal(0, metrics.ReviewCount);

        rows[0].TotalInspectionMilliseconds = 850;
        rows[1].TotalInspectionMilliseconds = 1250;
        rows[2].TotalInspectionMilliseconds = 500;
        rows[3].TotalInspectionMilliseconds = 0;
        rows[4].TotalInspectionMilliseconds = 0;
        var performance = BatchValidationService.CalculatePerformanceSummary(rows);
        Assert.Equal(866.666, performance.AverageMilliseconds, precision: 2);
        Assert.Equal(1250, performance.MaxMilliseconds, precision: 3);
        Assert.Equal(500, performance.MinMilliseconds, precision: 3);
        Assert.Equal(1, performance.CountOverOneSecond);
    }

    [Fact]
    public void ReviewVerdictIsNotCountedAsFalseCallOrDetection()
    {
        var rows = new[]
        {
            Row("OK", "OK"),
            Row("OK", "REVIEW"), // pending review on a good board must not be a false call
            Row("NG", "NG"),
            Row("NG", "REVIEW"), // pending review on a defective board is not scored either way
        };

        var metrics = BatchValidationService.CalculateMetrics(rows);

        // Only the two definitive verdicts feed the confusion matrix.
        Assert.Equal(1, metrics.TruePositive);
        Assert.Equal(1, metrics.TrueNegative);
        Assert.Equal(0, metrics.FalsePositive);
        Assert.Equal(0, metrics.FalseNegative);
        Assert.Equal(0.0, metrics.FalseCallRate, precision: 3);
        Assert.Equal(2, metrics.ReviewCount);

        // A reviewed board is a pending-review request, not a false call or a possible escape.
        Assert.Equal("REVIEW_PENDING", BatchValidationService.DetermineFailureCategory(
            "OK", "REVIEW", "UNASSIGNED", "UNASSIGNED", "UNASSIGNED", "UNASSIGNED", false));
    }

    [Fact]
    public void ValidationAcceptanceCriteriaReturnsPassFailAndConditional()
    {
        var passingRows = new[]
        {
            Row("NG", "NG", "ng.png"),
            Row("OK", "OK", "ok.png"),
        };
        var passingMetrics = BatchValidationService.CalculateMetrics(passingRows);
        var passingPerformance = BatchValidationService.CalculatePerformanceSummary(passingRows);

        var pass = CustomerValidationPackageService.EvaluateAcceptance(
            passingMetrics,
            passingPerformance,
            passingRows.Length,
            isFormalManifest: true);

        Assert.Equal("PASS", pass.Status);
        Assert.True(pass.NumericGatesPassed);

        var failRows = new[]
        {
            Row("NG", "OK", "escape.png"),
            Row("OK", "NG", "false-call.png"),
        };
        var fail = CustomerValidationPackageService.EvaluateAcceptance(
            BatchValidationService.CalculateMetrics(failRows),
            BatchValidationService.CalculatePerformanceSummary(failRows),
            failRows.Length,
            isFormalManifest: true);

        Assert.Equal("FAIL", fail.Status);
        Assert.Contains(fail.Messages, message => message.Contains("Accuracy", StringComparison.OrdinalIgnoreCase));

        var conditional = CustomerValidationPackageService.EvaluateAcceptance(
            passingMetrics,
            passingPerformance,
            passingRows.Length,
            isFormalManifest: false);

        Assert.Equal("CONDITIONAL", conditional.Status);
        Assert.Contains(conditional.Messages, message => message.Contains("Formal validation manifest", StringComparison.OrdinalIgnoreCase));

        var unknownRows = new[] { Row("UNKNOWN", "OK", "unknown.png") };
        var unknown = CustomerValidationPackageService.EvaluateAcceptance(
            BatchValidationService.CalculateMetrics(unknownRows),
            BatchValidationService.CalculatePerformanceSummary(unknownRows),
            unknownRows.Length,
            isFormalManifest: true);

        Assert.Equal("CONDITIONAL", unknown.Status);
        Assert.False(unknown.MetricsComputed);
    }

    [Fact]
    public void DatasetQualityGatePreventsTinyDatasetPassAcceptance()
    {
        var goldenPath = WriteTinyPng("quality-golden.png");
        var rows = new[]
        {
            QualityRow("OK", 0, "OK", goldenPath),
            QualityRow("NG", 1, "BRIDGE", goldenPath),
        };
        var metrics = BatchValidationService.CalculateMetrics(rows);
        var quality = DatasetQualityService.Analyze(rows);

        var acceptance = CustomerValidationPackageService.EvaluateAcceptance(
            metrics,
            BatchValidationService.CalculatePerformanceSummary(rows),
            rows.Length,
            isFormalManifest: true,
            criteria: new ValidationAcceptanceCriteria
            {
                MinimumAccuracy = 1,
                MinimumPrecision = 1,
                MinimumRecall = 1,
                MaximumFalseCallRate = 0,
            },
            datasetQuality: quality);

        Assert.Equal("FAIL", quality.Status);
        Assert.NotEqual("PASS", acceptance.Status);
        Assert.Equal("FAIL", acceptance.DatasetQualityStatus);
        Assert.Contains(acceptance.Messages, message => message.Contains("Dataset quality gate failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatasetQualityGateRejectsAllOkDataset()
    {
        var goldenPath = WriteTinyPng("all-ok-golden.png");
        var rows = Enumerable.Range(0, 50)
            .Select(index => QualityRow("OK", index, "OK", goldenPath))
            .ToArray();

        var quality = DatasetQualityService.Analyze(rows);

        Assert.Equal("FAIL", quality.Status);
        Assert.Contains(quality.BlockingFailures, failure => failure.Contains("NG image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatasetQualityGateRejectsAllNgDataset()
    {
        var goldenPath = WriteTinyPng("all-ng-golden.png");
        var rows = Enumerable.Range(0, 50)
            .Select(index => QualityRow("NG", index, index % 2 == 0 ? "BRIDGE" : "MISSING", goldenPath))
            .ToArray();

        var quality = DatasetQualityService.Analyze(rows);

        Assert.Equal("FAIL", quality.Status);
        Assert.Contains(quality.BlockingFailures, failure => failure.Contains("OK image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatasetQualityGateRejectsTooManyUnknownLabels()
    {
        var goldenPath = WriteTinyPng("unknown-heavy-golden.png");
        var rows = Enumerable.Range(0, 45)
            .Select(index => QualityRow(index < 20 ? "OK" : "NG", index, index % 2 == 0 ? "BRIDGE" : "MISSING", goldenPath))
            .Concat(Enumerable.Range(45, 5).Select(index => QualityRow("UNKNOWN", index, "UNASSIGNED", goldenPath)))
            .ToArray();

        var quality = DatasetQualityService.Analyze(rows);

        Assert.Equal("FAIL", quality.Status);
        Assert.True(quality.UnknownLabelRate > new ValidationDatasetQualityCriteria().MaximumUnknownLabelRate);
        Assert.Contains(quality.BlockingFailures, failure => failure.Contains("Unknown label rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatasetQualityGatePassesBalancedSyntheticDataset()
    {
        var goldenPath = WriteTinyPng("balanced-golden.png");
        var rows = Enumerable.Range(0, 20)
            .Select(index => QualityRow("OK", index, "OK", goldenPath))
            .Concat(Enumerable.Range(20, 15).Select(index => QualityRow("NG", index, "BRIDGE", goldenPath)))
            .Concat(Enumerable.Range(35, 15).Select(index => QualityRow("NG", index, "MISSING", goldenPath)))
            .ToArray();

        var quality = DatasetQualityService.Analyze(rows);

        Assert.Equal("PASS", quality.Status);
        Assert.Equal(50, quality.TotalImages);
        Assert.Equal(20, quality.OkImages);
        Assert.Equal(30, quality.NgImages);
        Assert.Equal(2, quality.DefectClassCount);
        Assert.Empty(quality.BlockingFailures);
        Assert.Empty(quality.Warnings);
    }

    [Fact]
    public void GenericDetectionOutputParserParsesDetectionRows()
    {
        var tensor = new DenseTensor<float>(new[] { 2, 6 });
        tensor[0, 0] = 1;
        tensor[0, 1] = 0.91f;
        tensor[0, 2] = 0.10f;
        tensor[0, 3] = 0.20f;
        tensor[0, 4] = 0.30f;
        tensor[0, 5] = 0.40f;
        tensor[1, 0] = 6;
        tensor[1, 1] = 0.20f;
        tensor[1, 2] = 10;
        tensor[1, 3] = 20;
        tensor[1, 4] = 30;
        tensor[1, 5] = 40;

        var parser = new GenericDetectionOutputParser();
        var detections = parser.Parse(
            tensor,
            new Dictionary<int, string>
            {
                [1] = "Solder Bridge",
                [6] = "Anomaly",
            },
            0.65,
            100,
            100);

        Assert.Single(detections);
        Assert.Equal("Solder Bridge", detections[0].Label);
        Assert.Equal(0.91, detections[0].Confidence, precision: 2);
        Assert.Equal(0.10, detections[0].BoundingBox.X, precision: 2);
        Assert.Equal(0.20, detections[0].BoundingBox.Y, precision: 2);
        Assert.Equal(0.30, detections[0].BoundingBox.Width, precision: 2);
        Assert.Equal(0.40, detections[0].BoundingBox.Height, precision: 2);
    }

    [Fact]
    public void OnnxEngineWithoutModelReturnsFriendlyReviewResult()
    {
        var engine = new OnnxInspectionEngine(new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = Path.Combine(_root, "missing.onnx"),
            ModelVersion = "UNIT-ONNX",
            ConfidenceThreshold = 0.7,
            InputTensorName = "images",
            OutputTensorName = "detections",
        });

        var result = engine.Analyze(Path.Combine(_root, "missing-sample.png"), null, DetectionPriority.Balanced);

        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("ONNX ML Model", result.InspectionEngine);
        Assert.Equal("UNIT-ONNX", result.ModelVersion);
        Assert.Equal(0.7, result.ConfidenceThreshold, precision: 3);
        Assert.Equal(Path.Combine(_root, "missing.onnx"), result.ModelFilePath);
        Assert.Contains(result.Evidence, line => line.Contains("No ML inference", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Defects, defect => defect.DefectType == "ML Model Missing");
    }

    [Fact]
    public void OnnxEngineInvalidModelReturnsFriendlyReviewResult()
    {
        var samplePath = WriteTinyPng("onnx-sample.png");
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var engine = new OnnxInspectionEngine(new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            ModelVersion = "INVALID-ONNX",
            ConfidenceThreshold = 0.65,
        });

        var result = engine.Analyze(samplePath, null, DetectionPriority.Balanced);

        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("ML Runtime Error", result.SuggestedDefect);
        Assert.Equal(modelPath, result.ModelFilePath);
        Assert.Contains(result.Evidence, line => line.Contains("Error type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadValidationManifestParsesGroundTruthCsv()
    {
        var imageFolder = Path.Combine(_root, "images");
        Directory.CreateDirectory(imageFolder);
        var samplePath = Path.Combine(imageFolder, "board-1.png");
        var goldenPath = Path.Combine(imageFolder, "golden-1.png");
        File.WriteAllBytes(samplePath, TinyPngBytes());
        File.WriteAllBytes(goldenPath, TinyPngBytes());
        var csv = Path.Combine(_root, "ground_truth.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes",
                "\"board-1.png\",NG,\"golden-1.png\",\"Solder, Bridge\",top,U10,ROI-U10,\"Solder Bridge\",LOT-7,TBOX,\"customer sample\""),
            Encoding.UTF8);

        var manifest = BatchValidationService.LoadValidationManifest(csv, imageFolder);

        Assert.True(manifest.IsFormalManifest);
        Assert.Single(manifest.OrderedEntries);
        Assert.True(manifest.ByImageName.ContainsKey("board-1.png"));
        var entry = manifest.ByImageName["board-1.png"];
        Assert.Equal("NG", entry.Label);
        Assert.Equal("Solder, Bridge", entry.DefectType);
        Assert.Equal("ROI-U10", entry.RoiId);
        Assert.Equal("Solder Bridge", entry.RoiType);
        Assert.Equal("top", entry.Side);
        Assert.Equal("U10", entry.RefDes);
        Assert.Equal("LOT-7", entry.LotId);
        Assert.Equal("TBOX", entry.BoardModel);
        Assert.Equal("customer sample", entry.Notes);
        Assert.Equal(samplePath, entry.ImagePath);
        Assert.Equal(goldenPath, entry.GoldenPath);
    }

    [Fact]
    public void FormalValidationManifestPreservesMissingImageRowsForErrorReporting()
    {
        var imageFolder = Path.Combine(_root, "images");
        Directory.CreateDirectory(imageFolder);
        var csv = Path.Combine(_root, "ground_truth_missing.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes",
                "missing-board.png,NG,missing-golden.png,Bridge,top,U10,ROI-U10,Solder Bridge,LOT-7,TBOX,missing sample"),
            Encoding.UTF8);

        var manifest = BatchValidationService.LoadValidationManifest(csv, imageFolder);
        var runItems = BatchValidationService.BuildRunItems(Array.Empty<string>(), manifest);

        Assert.True(manifest.IsFormalManifest);
        Assert.Single(runItems);
        Assert.EndsWith("missing-board.png", runItems[0].ImagePath);
        Assert.False(File.Exists(runItems[0].ImagePath));
        Assert.EndsWith("missing-golden.png", runItems[0].Manifest.GoldenPath);
    }

    [Fact]
    public void ProfileViewParsesHeightMapCsvWithRequiredColumns()
    {
        Directory.CreateDirectory(_root);
        var csv = Path.Combine(_root, "height_map.csv");
        File.WriteAllText(
            csv,
            string.Join(
                Environment.NewLine,
                "x,y,height",
                "0,0,0.125",
                "1,0,0.250",
                "0,1,-0.050"),
            Encoding.UTF8);

        var points = Views.ProfileView.ParseHeightMap(csv);

        Assert.Equal(3, points.Count);
        Assert.Contains(points, p => p.X == 1 && p.Y == 0 && Math.Abs(p.Height - 0.250) < 0.0001);
        Assert.Contains(points, p => p.X == 0 && p.Y == 1 && Math.Abs(p.Height + 0.050) < 0.0001);
    }

    [Fact]
    public void ProfileDispositionEventsPersistUserAndRole()
    {
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("Engineer3D", UserRole.Engineer);

        WorkflowState.Instance.AddDisposition("Accept Defect: 3D sample-data defect type=Height High, x=1, y=2, height=0.250. 3D camera not connected.");

        var events = AoiDatabase.GetReviewEvents(new LogFilter { Result = "Height High" });
        Assert.Contains(events, e =>
            e.Category == "DISPOSITION" &&
            e.OperatorId == "Engineer3D [Engineer]" &&
            e.Message.Contains("3D camera not connected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecordExportPersistsExportHistory()
    {
        AoiDatabase.Initialize();

        AoiDatabase.RecordExport("InspectionHistoryCsv", @"C:\exports\history.csv", "WARN", "Admin01 [Admin]");

        var history = AoiDatabase.GetExportHistory();
        Assert.Single(history);
        Assert.Equal("InspectionHistoryCsv", history[0].ExportType);
        Assert.Equal(@"C:\exports\history.csv", history[0].FilePath);
        Assert.Equal("WARN", history[0].Status);
        Assert.Equal("Admin01 [Admin]", history[0].OperatorId);
        Assert.NotNull(history[0].AuditEventId);

        var audit = AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "EXPORT", OperatorId = "Admin01" });
        Assert.Contains(audit, row =>
            row.Id == history[0].AuditEventId &&
            row.UserId == "Admin01" &&
            row.UserRole == "Admin" &&
            row.RelatedPath == @"C:\exports\history.csv");
    }

    [Fact]
    public void ValidationPackageServiceCreatesManifestFilesAndPersistenceWithoutRawImageCopy()
    {
        AoiDatabase.Initialize();
        var datasetFolder = Path.Combine(_root, "customer-dataset");
        Directory.CreateDirectory(datasetFolder);
        var rawCustomerImage = Path.Combine(datasetFolder, "raw-customer-board.png");
        File.WriteAllBytes(rawCustomerImage, TinyPngBytes());
        var groundTruth = Path.Combine(datasetFolder, "ground_truth.csv");
        File.WriteAllText(
            groundTruth,
            "image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes",
            Encoding.UTF8);

        var rows = new[]
        {
            Row("NG", "NG", "board-ng.png"),
            Row("OK", "OK", "board-ok.png"),
        };
        rows[0].ImagePath = Path.Combine(datasetFolder, "missing-board-ng.png");
        rows[1].ImagePath = Path.Combine(datasetFolder, "missing-board-ok.png");
        var metrics = BatchValidationService.CalculateMetrics(rows);
        var performance = BatchValidationService.CalculatePerformanceSummary(rows);
        var runId = AoiDatabase.RecordBatchTestRun(
            datasetFolder,
            groundTruth,
            "Unit Test Engine",
            "TEST-1",
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            rows.Select(row => row.ToRecord()).ToArray());
        var outputRoot = Path.Combine(_root, "exports", "packages");

        var result = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = outputRoot,
            RunId = runId,
            RunCreatedAtUtc = DateTime.UtcNow,
            StationId = "AOI-UNIT",
            OperatorId = "Engineer01",
            OperatorRole = "Engineer",
            BoardModel = "TBOX",
            LotId = "LOT-42",
            EngineName = "Unit Test Engine",
            ModelVersion = "TEST-1",
            DatasetFolder = datasetFolder,
            GroundTruthCsvPath = groundTruth,
            IsFormalManifest = true,
            Metrics = metrics,
            PerformanceSummary = performance,
            Rows = rows,
        });

        Assert.True(Directory.Exists(result.PackageFolder));
        Assert.True(File.Exists(result.CsvPath));
        Assert.True(File.Exists(result.ReportPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "README.txt")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "print_to_pdf_instructions.txt")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "validation_summary.html")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "validation_summary.pdf")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "benchmark_results.csv")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "customer_validation_manifest.csv")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "limitations.txt")));
        Assert.True(Directory.Exists(result.AnnotatedImagesFolder));
        Assert.Equal("FAIL", result.Acceptance.Status);
        Assert.Equal("FAIL", result.Acceptance.DatasetQualityStatus);
        Assert.Contains(result.Acceptance.Messages, message => message.Contains("Dataset quality gate failed", StringComparison.OrdinalIgnoreCase));

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        var root = manifestDocument.RootElement;
        Assert.Equal("stage1-validation-package/v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(result.PackageId, root.GetProperty("packageId").GetString());
        Assert.Equal("FAIL", root.GetProperty("acceptanceStatus").GetString());
        Assert.Equal("TBOX", root.GetProperty("boardModel").GetString());
        Assert.Equal("ground_truth.csv", root.GetProperty("groundTruthCsvName").GetString());
        var quality = root.GetProperty("datasetQualitySummary");
        Assert.Equal("FAIL", quality.GetProperty("status").GetString());
        Assert.True(quality.GetProperty("blockingFailures").GetArrayLength() > 0);
        Assert.Equal("FAIL", root.GetProperty("datasetPreflightStatus").GetString());
        Assert.True(root.GetProperty("datasetPreflightFailures").GetArrayLength() > 0);
        var included = root.GetProperty("includedFiles").EnumerateArray().ToArray();
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "validation_results.csv");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "validation_summary.html");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "benchmark_results.csv");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "customer_validation_manifest.csv");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "limitations.txt");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "customer_validation_report.html");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "validation_manifest.json");
        Assert.Contains(included, file => file.GetProperty("relativePath").GetString() == "dataset_preflight_summary.json");
        var summaryHtml = File.ReadAllText(Path.Combine(result.PackageFolder, "validation_summary.html"));
        Assert.Contains("P95 frame-to-overlay", summaryHtml);
        Assert.Contains("Stage 1", summaryHtml);
        var reportHtml = File.ReadAllText(result.ReportPath);
        Assert.Contains("Dataset Preflight Gate", reportHtml);
        Assert.Contains("Dataset preflight has blocking failures", reportHtml);

        var packageFiles = Directory.EnumerateFiles(result.PackageFolder, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain("raw-customer-board.png", packageFiles);

        var packages = AoiDatabase.GetValidationPackages();
        Assert.Contains(packages, package =>
            package.PackageId == result.PackageId &&
            package.ManifestPath == result.ManifestPath &&
            package.AcceptanceStatus == "FAIL");
    }

    [Fact]
    public void RoleChangeWritesFactoryAuditFields()
    {
        AoiDatabase.Initialize();

        WorkflowState.Instance.SetCurrentUser("AuditEngineer", UserRole.Engineer);

        var audit = AoiDatabase.GetAuditEvents(new LogFilter
        {
            OperatorId = "AuditEngineer",
            UserRole = "Engineer",
            ActionCategory = "LOGIN",
        });

        Assert.Contains(audit, row =>
            row.UserId == "AuditEngineer" &&
            row.UserRole == "Engineer" &&
            row.StationId == WorkflowState.Instance.StationId &&
            row.TimestampUtc != DateTime.MinValue &&
            row.LocalTimestamp != DateTime.MinValue &&
            row.ActionDetail.Contains("authMode=DemoLocalRoleSelector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SoakTestReportCanBeWrittenWithStabilityMetrics()
    {
        var result = new SoakTestResult
        {
            ImageFolder = @"C:\soak\images",
            OutputFolder = _root,
            EngineName = "Unit Test Engine",
            EngineVersion = "TEST-1",
            OperatorId = "Admin01 [Admin]",
            RequestedDuration = TimeSpan.FromMinutes(2),
            DelayBetweenInspections = TimeSpan.FromMilliseconds(250),
            TotalCycles = 3,
            SuccessfulCycles = 2,
            FailedCycles = 1,
            AverageInspectionMilliseconds = 740,
            MinInspectionMilliseconds = 610,
            MaxInspectionMilliseconds = 1205,
            P95InspectionMilliseconds = 1158.5,
            AverageTotalCycleMilliseconds = 800,
            MaxTotalCycleMilliseconds = 1300,
            P95TotalCycleMilliseconds = 1240,
            CountOverOneSecond = 1,
            StartManagedMemoryMegabytes = 20,
            EndManagedMemoryMegabytes = 22,
            StartWorkingSetMegabytes = 80,
            EndWorkingSetMegabytes = 85,
            PeakWorkingSetMegabytes = 90,
        };
        result.Errors.Add("Synthetic inspection exception");
        result.MemoryWarnings.Add("Synthetic memory warning");
        result.FirstCriticalError = "Synthetic inspection exception";
        result.Cycles.Add(new SoakTestCycleRecord(1, "Top-000001", @"C:\soak\images\board.png", "REVIEW", 740, true, "Synthetic cycle", TotalCycleMilliseconds: 800));

        var reportPath = SoakTestService.WriteHtmlReport(result, _root);
        var jsonPath = SoakTestService.WriteJsonReport(result, _root);
        var csvPath = SoakTestService.WriteIterationsCsv(result, _root);
        var report = File.ReadAllText(reportPath);

        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(csvPath));
        Assert.Contains("AOI Monitor Soak Test Report", report);
        Assert.Contains("Stability Metrics", report);
        Assert.Contains("Total cycles", report);
        Assert.Contains("P95 inspection time", report);
        Assert.Contains("P95 total cycle time", report);
        Assert.Contains("Count over 1 second", report);
        Assert.Contains("Synthetic inspection exception", report);
        Assert.Contains("Synthetic memory warning", report);
        Assert.Contains("Simulated source", report);
        Assert.Contains("total_cycle_ms", File.ReadAllText(csvPath));
    }

    [Fact]
    public async Task ShortFakeSoakRunRecordsIterations()
    {
        var imageFolder = Path.Combine(_root, "soak_images");
        Directory.CreateDirectory(imageFolder);
        WriteTinyPng(Path.Combine(imageFolder, "board_001.png"));

        var options = new SoakTestOptions(
            imageFolder,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            InspectionEngineFactory.DefaultEngineKey,
            _root,
            "Admin01 [Admin]",
            "TBOX-MAIN",
            "SOAK-TEST")
        {
            MaxIterations = 3,
        };

        var result = await SoakTestService.RunAsync(options, progress: null, CancellationToken.None);
        AoiDatabase.RecordSoakTestRun(result, result.OperatorId);
        var persisted = AoiDatabase.GetLatestSoakTestRun();

        Assert.Equal(3, result.TotalCycles);
        Assert.Equal(3, result.Cycles.Count);
        Assert.Equal(3, persisted?.Cycles.Count);
        Assert.Equal("FolderSimulation", persisted?.SourceKind);
        Assert.True(persisted?.P95TotalCycleMilliseconds >= 0);
    }

    [Fact]
    public async Task FailedSoakIterationDoesNotCrashRun()
    {
        var imageFolder = Path.Combine(_root, "bad_soak_images");
        Directory.CreateDirectory(imageFolder);
        File.WriteAllText(Path.Combine(imageFolder, "bad.png"), "not a real png");

        var options = new SoakTestOptions(
            imageFolder,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            InspectionEngineFactory.DefaultEngineKey,
            _root,
            "Admin01 [Admin]",
            "TBOX-MAIN",
            "SOAK-TEST")
        {
            MaxIterations = 1,
        };

        var result = await SoakTestService.RunAsync(options, progress: null, CancellationToken.None);

        Assert.Equal(1, result.TotalCycles);
        Assert.Equal(1, result.FailedCycles);
        Assert.Single(result.Errors);
        Assert.Equal("ERROR", Assert.Single(result.Cycles).Verdict);
    }

    [Fact]
    public void SoakSummaryComputesP95MaxAndAverage()
    {
        var values = new[] { 100d, 200d, 300d, 400d };

        Assert.Equal(250, values.Average());
        Assert.Equal(400, values.Max());
        Assert.Equal(385, SoakTestService.Percentile(values, 0.95), precision: 6);
    }

    [Fact]
    public void SoakProfileOptionsUseFormalFactoryEvidenceNames()
    {
        var smoke = SoakTestService.CreateProfileOptions(SoakTestProfile.Smoke, _root, "pixel", _root, "Admin", "TBOX", "LOT");
        var shortStability = SoakTestService.CreateProfileOptions(SoakTestProfile.ThirtyMinute, _root, "pixel", _root, "Admin", "TBOX", "LOT");
        var factory = SoakTestService.CreateProfileOptions(SoakTestProfile.EightHourFactoryPoC, _root, "pixel", _root, "Admin", "TBOX", "LOT");

        Assert.Equal(TimeSpan.FromMinutes(5), smoke.Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), shortStability.Duration);
        Assert.Equal(TimeSpan.FromHours(8), factory.Duration);
        Assert.Equal("Smoke", SoakTestService.ProfileDisplayName(SoakTestProfile.QuickSmoke));
        Assert.Equal("30Minute", SoakTestService.ProfileDisplayName(SoakTestProfile.ShortStability));
        Assert.Equal("8HourFactoryPoC", SoakTestService.ProfileDisplayName(SoakTestProfile.FactoryPoc));
    }

    [Fact]
    public void SoakRunPersistsFactoryEvidenceDetails()
    {
        AoiDatabase.Initialize();
        var start = DateTime.Now.AddMinutes(-30);
        var result = new SoakTestResult
        {
            StartTime = start,
            EndTime = start.AddMinutes(30),
            ImageFolder = @"C:\soak\images",
            OutputFolder = _root,
            EngineName = "Unit Test Engine",
            EngineVersion = "TEST-1",
            SourceKind = "FolderSimulation",
            RequestedDuration = TimeSpan.FromMinutes(30),
            OperatorId = "Admin01 [Admin]",
            AverageTotalCycleMilliseconds = 110,
            MaxTotalCycleMilliseconds = 220,
            P95TotalCycleMilliseconds = 210,
            CancellationReason = "Synthetic cancellation detail",
            FirstCriticalError = "Synthetic first critical error",
            TotalCycles = 1,
            FailedCycles = 1,
        };
        result.MemoryWarnings.Add("Synthetic GC warning");
        result.Cycles.Add(new SoakTestCycleRecord(1, "F1", @"C:\soak\bad.png", "ERROR", 100, false, "failed", DateTime.UtcNow, "Unit Test Engine", 123, "Synthetic first critical error", 120, "InvalidData"));

        AoiDatabase.RecordSoakTestRun(result, result.OperatorId);
        var persisted = AoiDatabase.GetLatestSoakTestRun();

        Assert.NotNull(persisted);
        Assert.Equal(210, persisted.P95TotalCycleMilliseconds, precision: 3);
        Assert.Equal("Synthetic cancellation detail", persisted.CancellationReason);
        Assert.Equal("Synthetic first critical error", persisted.FirstCriticalError);
        Assert.Contains("Synthetic GC warning", persisted.MemoryWarnings);
        var cycle = Assert.Single(persisted.Cycles);
        Assert.Equal(120, cycle.TotalCycleMilliseconds, precision: 3);
        Assert.Equal("InvalidData", cycle.ExceptionCategory);
    }

    [Fact]
    public void SimulatedEightHourSoakRunIsNotAcceptedAsFactoryEvidence()
    {
        var start = DateTime.Now.AddHours(-8);
        var result = new SoakTestResult
        {
            StartTime = start,
            EndTime = start.AddHours(8),
            ImageFolder = @"C:\soak\images",
            OutputFolder = _root,
            EngineName = "Unit Test Engine",
            EngineVersion = "TEST-1",
            SourceKind = "FolderSimulation",
            IsRealCameraSource = false,
            RequestedDuration = TimeSpan.FromHours(8),
            OperatorId = "Admin01 [Admin]",
            TotalCycles = 100,
            SuccessfulCycles = 100,
        };

        Assert.False(result.IsCompletedFactoryEvidence);
    }

    [Fact]
    public void CanceledSoakRunIsNotAcceptedAsFactoryEvidence()
    {
        var start = DateTime.Now.AddHours(-8);
        var result = new SoakTestResult
        {
            StartTime = start,
            EndTime = start.AddHours(8),
            ImageFolder = @"C:\soak\images",
            OutputFolder = _root,
            EngineName = "Unit Test Engine",
            EngineVersion = "TEST-1",
            SourceKind = "Simulated source",
            RequestedDuration = TimeSpan.FromHours(8),
            OperatorId = "Admin01 [Admin]",
            BoardModel = "TBOX-MAIN",
            LotId = "SOAK-TEST",
            WasCanceled = true,
            TotalCycles = 100,
            SuccessfulCycles = 100,
        };

        AoiDatabase.RecordSoakTestRun(result, result.OperatorId);
        var persisted = AoiDatabase.GetLatestSoakTestRun();

        Assert.False(result.IsCompletedFactoryEvidence);
        Assert.False(persisted?.IsCompletedFactoryEvidence);
    }

    [Fact]
    public async Task TraceabilityMockUploadWritesLocalPayloadAndAuditRecord()
    {
        AoiDatabase.Initialize();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings
        {
            Mode = MesIntegrationMode.MockRest,
            MockEndpointUrl = string.Empty,
            UploadTimeoutSeconds = 2,
        });

        var payload = new TraceabilityPayload
        {
            LotId = "LOT-99",
            BoardModel = "TBOX",
            StationId = "AOI-LIB-01",
            OperatorId = "Admin01 [Admin]",
            Result = "NG",
            TimestampUtc = DateTime.UtcNow,
            DefectSummary = "Solder Bridge; score=80.0%",
            ImagePath = @"C:\images\board.png",
        };

        var outcome = await TraceabilityUploadService.UploadAsync(payload);
        var attempts = AoiDatabase.GetMesUploadAttempts();

        Assert.True(outcome.Result.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Simulated, outcome.Result.Status);
        Assert.True(File.Exists(outcome.PayloadPath));
        Assert.Single(attempts);
        Assert.Equal("Mock REST", attempts[0].Mode);
        Assert.Equal("OK", attempts[0].Status);
        Assert.Equal("LOT-99", attempts[0].LotId);
        Assert.Equal("TBOX", attempts[0].BoardModel);
        Assert.Equal("NG", attempts[0].Result);
        Assert.Contains("local JSON", attempts[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalRolesEnforceExpectedPageAndActionPermissions()
    {
        WorkflowState.Instance.SetCurrentUser("Operator01", UserRole.Operator);

        Assert.Equal("Operator01 [Operator]", WorkflowState.Instance.OperatorWithRole);
        Assert.False(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanTestModelConfiguration(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanAccessPage(WorkflowState.Instance.CurrentRole, "guide"));

        WorkflowState.Instance.SetCurrentUser("Engineer01", UserRole.Engineer);
        Assert.True(RoleAuthorization.CanEditRecipes(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanRunModelTests(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanTestModelConfiguration(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole));
        Assert.False(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));

        WorkflowState.Instance.SetCurrentUser("Admin01", UserRole.Admin);
        Assert.True(RoleAuthorization.CanExportLogs(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole));
        Assert.True(RoleAuthorization.CanUseMaintenanceActions(WorkflowState.Instance.CurrentRole));
    }

    [Fact]
    public void ModelConfigurationValidatorReportsMissingModel()
    {
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = Path.Combine(_root, "missing.onnx"),
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.MissingModel, result.Status);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelConfigurationValidatorReportsInvalidLabelMapBeforeRuntime()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            LabelMapPath = Path.Combine(_root, "missing-labels.json"),
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.InvalidLabelMap, result.Status);
    }

    [Fact]
    public void ModelConfigurationValidatorRequiresTensorNames()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
        };

        var result = ModelConfigurationValidator.Test(config);

        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, result.Status);
        Assert.Contains("tensor names", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelRegistryRegisterCalculatesShaAndPersistsMetadata()
    {
        AoiDatabase.Initialize();
        var modelPath = Path.Combine(_root, "registry-invalid.onnx");
        var labelPath = Path.Combine(_root, "labels.txt");
        File.WriteAllText(modelPath, "not a production model");
        File.WriteAllText(
            labelPath,
            string.Join(Environment.NewLine, "# AOI labels", "", "0,OK", "1,Solder Bridge", "2,Insufficient Solder"),
            Encoding.UTF8);
        var expectedSha = ModelRegistryService.ComputeSha256(modelPath);

        var entry = ModelRegistryService.Register(new ModelRegistrationRequest
        {
            ModelFilePath = modelPath,
            LabelMapPath = labelPath,
            DisplayName = "Unit Test Model",
            Version = "REG-1",
            InputTensorName = "images",
            OutputTensorName = "detections",
            InputWidth = 320,
            InputHeight = 240,
            ConfidenceThreshold = 0.72,
            Notes = "unit test registry entry",
        });

        Assert.Equal(expectedSha, entry.Sha256);
        Assert.Equal("REG-1", entry.Version);
        Assert.True(File.Exists(entry.StoredModelPath));
        Assert.True(File.Exists(entry.StoredLabelMapPath));
        Assert.True(File.Exists(entry.MetadataPath));
        Assert.StartsWith(Path.GetFullPath(ModelRegistryService.ModelsRoot), Path.GetFullPath(entry.StoredModelPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Solder Bridge", entry.Labels);

        var persisted = Assert.Single(AoiDatabase.GetModelRegistryRecords());
        Assert.Equal(entry.ModelId, persisted.ModelId);
        Assert.Equal(expectedSha, persisted.Sha256);
        Assert.Equal(0.72, persisted.ConfidenceThreshold, precision: 3);
        Assert.Contains("Insufficient Solder", persisted.Labels);

        using var metadata = JsonDocument.Parse(File.ReadAllText(entry.MetadataPath));
        Assert.Equal(entry.ModelId, metadata.RootElement.GetProperty("modelId").GetString());
        Assert.Equal(expectedSha, metadata.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("NotTested", metadata.RootElement.GetProperty("validationStatus").GetString());
    }

    [Fact]
    public void ActiveRegistryModelChangesFactorySelectionAndUnvalidatedModelReviewsSafely()
    {
        AoiDatabase.Initialize();
        var modelPath = Path.Combine(_root, "active-invalid.onnx");
        File.WriteAllText(modelPath, "invalid onnx model");
        var entry = ModelRegistryService.Register(new ModelRegistrationRequest
        {
            ModelFilePath = modelPath,
            DisplayName = "Factory Model",
            Version = "REG-FACTORY",
            InputTensorName = "images",
            OutputTensorName = "detections",
        });

        Assert.True(ModelRegistryService.SetActiveModel(entry.ModelId));

        var config = InspectionModelConfigurationService.Load();
        var engine = InspectionEngineFactory.Create();
        var result = engine.Analyze(Path.Combine(_root, "sample.png"), null, DetectionPriority.Balanced);

        Assert.Equal(InspectionEngineFactory.OnnxEngineKey, config.SelectedEngineKey);
        Assert.Equal(entry.ModelId, config.ActiveModelId);
        Assert.Equal("ONNX ML Model", engine.Name);
        Assert.Equal("REG-FACTORY", engine.Version);
        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("MODEL_REGISTRY_NOT_READY", result.ErrorCode);
        Assert.Contains(result.Evidence, line => line.Contains(entry.ModelId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LabelMapParserSkipsCommentsBlanksAndAllowsDuplicateLabels()
    {
        Directory.CreateDirectory(_root);
        var labelPath = Path.Combine(_root, "duplicate-labels.txt");
        File.WriteAllText(
            labelPath,
            string.Join(Environment.NewLine, "# comment", "", "0,OK", "1,Solder Bridge", "2,Solder Bridge", "Loose Label"),
            Encoding.UTF8);

        var labels = ModelConfigurationValidator.LoadLabels(labelPath);

        Assert.Equal("OK", labels[0]);
        Assert.Equal("Solder Bridge", labels[1]);
        Assert.Equal("Solder Bridge", labels[2]);
        Assert.Contains(labels.Values, value => value == "Loose Label");
        Assert.DoesNotContain(labels.Values, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void TestAndSavePersistsRuntimeErrorReadiness()
    {
        Directory.CreateDirectory(_root);
        var modelPath = Path.Combine(_root, "invalid.onnx");
        File.WriteAllText(modelPath, "not an onnx model");
        var labelMapPath = Path.Combine(_root, "labels.txt");
        File.WriteAllLines(labelMapPath, new[] { "OK", "Solder Bridge" });
        var config = new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ModelFilePath = modelPath,
            LabelMapPath = labelMapPath,
            InputTensorName = "images",
            OutputTensorName = "detections",
        };

        var result = InspectionModelConfigurationService.TestAndSave(config);
        var loaded = InspectionModelConfigurationService.Load();

        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, result.Status);
        Assert.Equal(ModelConfigurationTestStatus.RuntimeError, loaded.LastModelCheckResult);
        Assert.NotNull(loaded.LastModelCheckTimestampUtc);
        Assert.Equal(InspectionEngineStatus.MlRuntimeError, InspectionModelConfigurationService.GetStatus(loaded));
    }

    [Fact]
    public void SaveRecipeRevisionCanBeLoadedByBoardProgram()
    {
        AoiDatabase.Initialize();
        var document = new RecipeDocument
        {
            RecipeName = "TBOX_TOP",
            BoardProgram = "TBOX-MAIN",
            BackgroundImagePath = @"C:\images\board.png",
            Rois =
            {
                new RecipeRoiDocument
                {
                    Id = "ROI-1",
                    RoiType = "Presence",
                    X = 0.1,
                    Y = 0.2,
                    Width = 0.3,
                    Height = 0.4,
                    AiScoreThreshold = 0.75,
                },
            },
        };
        var json = JsonSerializer.Serialize(document);

        var id = AoiDatabase.SaveRecipeRevision(
            document.RecipeName,
            document.BoardProgram,
            "Engineer01",
            "Balanced",
            document.BackgroundImagePath,
            json);

        var loaded = AoiDatabase.GetLatestRecipeRevision("TBOX-MAIN");
        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
        Assert.Equal("TBOX_TOP", loaded.RecipeName);
        Assert.Equal("Balanced", loaded.DetectionPriority);

        var loadedDocument = JsonSerializer.Deserialize<RecipeDocument>(loaded.RecipeJson);
        Assert.NotNull(loadedDocument);
        Assert.Equal("ROI-1", loadedDocument.Rois.Single().Id);
    }

    [Fact]
    public void RecipeServiceParsesRecipeJsonRoundTrip()
    {
        var document = new RecipeDocument
        {
            RecipeName = "TBOX_TOP",
            BoardProgram = "TBOX-MAIN",
            BackgroundImagePath = @"C:\images\board.png",
            Rois =
            {
                new RecipeRoiDocument
                {
                    Id = "ROI-1",
                    Name = "U10 bridge check",
                    RoiType = "Solder Bridge",
                    X = 0.1,
                    Y = 0.2,
                    Width = 0.3,
                    Height = 0.4,
                    AiScoreThreshold = 0.12,
                    Enabled = true,
                },
            },
        };
        var revision = new RecipeRevisionRecord(
            1,
            "TBOX_TOP",
            "R0001",
            "TBOX-MAIN",
            "Engineer01",
            "Balanced",
            document.BackgroundImagePath,
            JsonSerializer.Serialize(document),
            DateTime.UtcNow);

        var parsed = RecipeService.ParseRevision(revision);

        Assert.Empty(parsed.Warnings);
        Assert.NotNull(parsed.Recipe);
        Assert.Equal("TBOX_TOP", parsed.Recipe.RecipeName);
        Assert.Equal("R0001", parsed.Recipe.Revision);
        var roi = Assert.Single(parsed.Recipe.Rois);
        Assert.Equal("ROI-1", roi.RoiId);
        Assert.Equal("U10 bridge check", roi.RoiName);
        Assert.Equal("Solder Bridge", roi.RoiType);
        Assert.Equal(0.12, roi.Thresholds.AiScoreThreshold, precision: 3);
    }

    [Fact]
    public void PixelDifferenceEngineProducesPerRoiDefectForRecipeDifference()
    {
        AoiDatabase.Initialize();
        var golden = WriteBmp("golden.bmp", 40, 40, (_, _) => White());
        var sample = WriteBmp("sample-left-diff.bmp", 40, 40, (x, _) => x < 20 ? Black() : White());
        SaveTestRecipe(
            new RecipeRoiDocument { Id = "ROI-LEFT", Name = "Left component", RoiType = "Solder Bridge", X = 0, Y = 0, Width = 0.5, Height = 1, AiScoreThreshold = 0.01 },
            new RecipeRoiDocument { Id = "ROI-RIGHT", Name = "Right component", RoiType = "Presence", X = 0.5, Y = 0, Width = 0.5, Height = 1, AiScoreThreshold = 0.01 });

        var result = new PixelDifferenceInspectionEngine().Analyze(sample, golden, DetectionPriority.Balanced);

        Assert.Equal("NG", result.Verdict);
        Assert.Equal("TBOX_TOP", result.RecipeName);
        Assert.Contains(result.Evidence, line => line.Contains("Checked 2 enabled recipe ROI", StringComparison.OrdinalIgnoreCase));
        var defect = Assert.Single(result.Defects);
        Assert.Equal("ROI-LEFT", defect.RoiId);
        Assert.Equal("Solder Bridge", defect.RoiType);
        Assert.Equal("Left component", defect.RoiName);
        Assert.Equal("NG", defect.JudgmentStatus);
    }

    [Fact]
    public void PixelDifferenceEngineReturnsOkWhenRecipeRoisAreBelowThreshold()
    {
        AoiDatabase.Initialize();
        var golden = WriteBmp("golden-ok.bmp", 40, 40, (_, _) => White());
        var sample = WriteBmp("sample-ok.bmp", 40, 40, (_, _) => White());
        SaveTestRecipe(
            new RecipeRoiDocument { Id = "ROI-A", RoiType = "Presence", X = 0, Y = 0, Width = 0.5, Height = 1, AiScoreThreshold = 0.01 },
            new RecipeRoiDocument { Id = "ROI-B", RoiType = "Polarity", X = 0.5, Y = 0, Width = 0.5, Height = 1, AiScoreThreshold = 0.01 });

        var result = new PixelDifferenceInspectionEngine().Analyze(sample, golden, DetectionPriority.Balanced);

        Assert.Equal("OK", result.Verdict);
        Assert.Empty(result.Defects);
        Assert.Contains(result.Evidence, line => line.Contains("Checked 2 enabled recipe ROI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedRecipeJsonAddsWarningAndDoesNotCrashInspection()
    {
        AoiDatabase.Initialize();
        AoiDatabase.SaveRecipeRevision(
            "BROKEN",
            "TBOX-MAIN",
            "Engineer01",
            "Balanced",
            string.Empty,
            "{not valid json");
        RecipeService.Invalidate("TBOX-MAIN");
        var golden = WriteBmp("golden-malformed.bmp", 40, 40, (_, _) => White());
        var sample = WriteBmp("sample-malformed.bmp", 40, 40, (x, _) => x < 20 ? Black() : White());

        var result = new PixelDifferenceInspectionEngine().Analyze(sample, golden, DetectionPriority.Balanced);

        Assert.Contains(result.Evidence, line => line.Contains("Recipe warning", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(string.Empty, result.Verdict);
    }

    [Fact]
    public void SaveCalibrationProfilePersistsPointsAndApproximateTransform()
    {
        AoiDatabase.Initialize();
        var imagePath = Path.Combine(_root, "calibration.png");
        File.WriteAllText(imagePath, "placeholder path only");

        var id = AoiDatabase.SaveCalibrationProfile(
            "Unit Test 2D Calibration",
            "TBOX-MAIN",
            "Top",
            imagePath,
            "Engineer01 [Engineer]",
            new[]
            {
                new CalibrationPointInput(0, 0, 0, 0),
                new CalibrationPointInput(100, 50, 10, 5),
                new CalibrationPointInput(200, 100, 20, 10),
            });

        var loaded = AoiDatabase.GetCalibrationProfile(id);

        Assert.NotNull(loaded);
        Assert.Equal("Unit Test 2D Calibration", loaded.ProfileName);
        Assert.Equal(3, loaded.Points.Count);
        Assert.True(loaded.HasTransform);
        Assert.Contains("Stage 2 preparation only", loaded.TransformSummary);
        Assert.True(CalibrationTransformService.TryConvertImageToBoard(loaded, 150, 75, out var boardX, out var boardY));
        Assert.Equal(15, boardX, precision: 3);
        Assert.Equal(7.5, boardY, precision: 3);
        Assert.Contains(AoiDatabase.GetCalibrationProfiles(), profile => profile.Id == id && profile.Points.Count == 3);
    }

    [Fact]
    public void RecordBatchTestRunPersistsResultsWithMetrics()
    {
        AoiDatabase.Initialize();
        var rows = new[]
        {
            Row("NG", "NG", "a.png"),
            Row("OK", "NG", "b.png"),
        };
        var metrics = BatchValidationService.CalculateMetrics(rows);

        var runId = AoiDatabase.RecordBatchTestRun(
            @"C:\validation",
            @"C:\validation\ground_truth.csv",
            "Unit Test Engine",
            "TEST-1",
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            rows.Select(row => row.ToRecord()).ToArray());

        var run = AoiDatabase.GetLatestBatchTestRun();
        var persistedRows = AoiDatabase.GetBatchTestResults(runId);
        Assert.NotNull(run);
        Assert.Equal(runId, run.Id);
        Assert.Equal(2, run.TotalImages);
        Assert.Equal(1, run.FailedCount);
        Assert.Equal(metrics.Accuracy, run.Accuracy, precision: 3);
        Assert.Equal("TEST-1", run.ModelVersion);
        Assert.Equal(2, persistedRows.Count);
        Assert.Equal("FAIL", persistedRows[1].PassFail);
        Assert.Equal("Unit Test Engine", persistedRows[0].InspectionEngine);
        Assert.Equal("TEST-1", persistedRows[0].ModelVersion);
        Assert.Equal("SYNTHETIC", persistedRows[0].NormalizedDefectClass);
        Assert.Equal("TOP", persistedRows[0].NormalizedSide);
        Assert.Equal("PASS", persistedRows[0].FailureCategory);
        Assert.NotEmpty(AoiDatabase.GetValidationBreakdownMetrics(runId));
        Assert.StartsWith("Synthetic note", persistedRows[0].Notes, StringComparison.Ordinal);
        Assert.Equal(11, persistedRows[0].ImageLoadMilliseconds, precision: 3);
        Assert.Equal(22, persistedRows[0].PreprocessingMilliseconds, precision: 3);
        Assert.Equal(33, persistedRows[0].InferenceMilliseconds, precision: 3);
        Assert.Equal(4, persistedRows[0].OverlayRenderingMilliseconds, precision: 3);
        Assert.Equal(70, persistedRows[0].TotalInspectionMilliseconds, precision: 3);
    }

    [Fact]
    public void FalseCallSweepComputesExpectedCounts()
    {
        var rows = new[]
        {
            ScoredRow("NG", 0.70),
            ScoredRow("NG", 0.40),
            ScoredRow("OK", 0.80),
            ScoredRow("OK", 0.20),
        };

        var run = FalseCallReductionService.Analyze(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5, ThresholdStep = 0.1, MaximumFalseCallRate = 1, MaximumPossibleEscapeRate = 1 });

        var point = Assert.Single(run.Points);
        Assert.Equal(1, point.TruePositive);
        Assert.Equal(1, point.TrueNegative);
        Assert.Equal(1, point.FalsePositive);
        Assert.Equal(1, point.FalseNegative);
        Assert.Equal(0.5, point.FalseCallRate, precision: 3);
        Assert.Equal(0.5, point.PossibleEscapeRate, precision: 3);
    }

    [Fact]
    public void ClassMetricsServiceBuildsPerClassConfusionMatrix()
    {
        var rows = new[]
        {
            ScoredRow("NG", 0.8, expectedClass: "BRIDGE", predictedClass: "BRIDGE"),
            ScoredRow("OK", 0.8, expectedClass: "OK", predictedClass: "BRIDGE"),
            ScoredRow("NG", 0.2, expectedClass: "MISSING", predictedClass: "UNASSIGNED"),
        };

        var summary = ClassMetricsService.Calculate(rows);
        var bridge = Assert.Single(summary.DefectClassMetrics, metric => metric.Key == "BRIDGE");
        var missing = Assert.Single(summary.DefectClassMetrics, metric => metric.Key == "MISSING");

        Assert.Equal(1, bridge.TruePositive);
        Assert.Equal(1, bridge.FalsePositive);
        Assert.Equal(1, missing.FalseNegative);
        Assert.Contains(summary.TopFalseCallContributors, metric => metric.Key.Contains("BRIDGE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.TopPossibleEscapeContributors, metric => metric.Key.Contains("MISSING", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WrongDefectClassIsSeparateFailureCategory()
    {
        var row = ScoredRow("NG", 0.9, expectedClass: "BRIDGE", predictedClass: "MISSING");

        Assert.Equal("WRONG_DEFECT_CLASS", row.FailureCategory);
        var classMetric = Assert.Single(ClassMetricsService.Calculate(new[] { row }).DefectClassMetrics);
        Assert.Equal(1, classMetric.WrongDefectClass);
        Assert.Equal(0, classMetric.FalseNegative);
    }

    [Fact]
    public void ValidationPackageIncludesBreakdownCsvAndManifestSummary()
    {
        AoiDatabase.Initialize();
        var rows = new[]
        {
            ScoredRow("NG", 0.8, expectedClass: "BRIDGE", predictedClass: "BRIDGE"),
            ScoredRow("OK", 0.8, expectedClass: "OK", predictedClass: "BRIDGE"),
        };
        var metrics = BatchValidationService.CalculateMetrics(rows);
        var package = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = Path.Combine(_root, "packages"),
            RunId = null,
            OperatorId = "Engineer01",
            OperatorRole = "Engineer",
            Metrics = metrics,
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(rows),
            Rows = rows,
        });

        Assert.True(File.Exists(Path.Combine(package.PackageFolder, "validation_breakdown.csv")));
        Assert.Contains(package.Manifest.IncludedFiles, file => file.RelativePath == "validation_breakdown.csv");
        Assert.Contains(package.Manifest.BreakdownSummary.DefectClassMetrics, metric => metric.Key == "BRIDGE");
    }

    [Fact]
    public void ActiveThresholdLookupSelectsMostSpecificRule()
    {
        AoiDatabase.Initialize();
        var profile = new ThresholdProfile
        {
            ProfileId = "TP-SPECIFIC",
            Revision = "R0001",
            BoardModel = "ANY",
            BoardProgram = "TBOX-MAIN",
            RecipeName = "TBOX_TOP",
            RecipeRevision = "R1",
            Status = "Approved",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules =
            [
                new ThresholdProfileRule { ViewType = "Any", RoiType = "Any", DefectClass = "Any", ReviewThreshold = 8, NgThreshold = 18, ConfidenceThreshold = 0.65 },
                new ThresholdProfileRule { ViewType = "Top", RoiType = "Solder Bridge", DefectClass = "Any", ReviewThreshold = 4, NgThreshold = 9, ConfidenceThreshold = 0.7 },
                new ThresholdProfileRule { ViewType = "Top", RoiType = "Solder Bridge", DefectClass = "SOLDER_BRIDGE", ReviewThreshold = 2, NgThreshold = 5, ConfidenceThreshold = 0.8 },
            ],
        };
        AoiDatabase.SaveThresholdProfile(profile);
        ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var effective = ThresholdProfileService.GetEffectiveThreshold("ANY", "TBOX-MAIN", "TBOX_TOP", "Top", "Solder Bridge", "Solder Bridge");

        Assert.NotNull(effective);
        Assert.Equal("TP-SPECIFIC", effective.ProfileId);
        Assert.Equal(2, effective.ReviewThreshold, precision: 3);
        Assert.Equal(5, effective.NgThreshold, precision: 3);
    }

    [Fact]
    public void ProfileFromFalseCallRecommendationPersistsSourceRun()
    {
        AoiDatabase.Initialize();
        var sourceRows = new[] { ScoredRow("OK", 0.1), ScoredRow("NG", 0.8) };
        var sourceMetrics = BatchValidationService.CalculateMetrics(sourceRows);
        var sourceRunId = AoiDatabase.RecordBatchTestRun(
            @"C:\validation",
            null,
            "Unit Test Engine",
            "TEST-1",
            sourceMetrics.Accuracy,
            sourceMetrics.Precision,
            sourceMetrics.Recall,
            sourceMetrics.FalseCallRate,
            sourceRows.Select(row => row.ToRecord()).ToArray());
        var run = FalseCallReductionService.AnalyzeAndPersist(
            sourceRows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            sourceRunId,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5, MaximumFalseCallRate = 0, MaximumPossibleEscapeRate = 0 },
            "Engineer01 [Engineer]");

        var profile = ThresholdProfileService.CreateDraftFromFalseCallReductionRecommendation(
            run,
            "TBOX",
            "TBOX-MAIN",
            "TBOX_TOP",
            "R1",
            "Engineer01 [Engineer]");
        var persisted = AoiDatabase.GetThresholdProfile(profile.ProfileId, profile.Revision);

        Assert.NotNull(persisted);
        Assert.Equal(run.Id, persisted.SourceFalseCallReductionRunId);
        Assert.Equal(sourceRunId, persisted.SourceValidationRunId);
        Assert.Single(persisted.Rules);
        Assert.Equal(50, persisted.Rules[0].NgThreshold, precision: 3);
    }

    [Fact]
    public void OperatorCannotApproveOrDeployThresholdProfile()
    {
        AoiDatabase.Initialize();
        var profile = new ThresholdProfile
        {
            ProfileId = "TP-OPERATOR-DENIED",
            Revision = "R0001",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules = [new ThresholdProfileRule { ReviewThreshold = 8, NgThreshold = 18, ConfidenceThreshold = 0.65 }],
        };
        AoiDatabase.SaveThresholdProfile(profile);

        Assert.Throws<UnauthorizedAccessException>(() =>
            ThresholdProfileService.ApproveProfile(profile.ProfileId, profile.Revision, UserRole.Operator, "Operator01 [Operator]"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Operator, "Operator01 [Operator]"));
    }

    [Fact]
    public void PixelDifferenceEngineEvidenceIncludesThresholdProfileSource()
    {
        AoiDatabase.Initialize();
        var golden = WriteBmp("profile-golden.bmp", 40, 40, (_, _) => White());
        var sample = WriteBmp("profile-sample.bmp", 40, 40, (x, _) => x < 20 ? Black() : White());
        SaveTestRecipe(new RecipeRoiDocument { Id = "ROI-LEFT", RoiType = "Solder Bridge", X = 0, Y = 0, Width = 0.5, Height = 1, AiScoreThreshold = 0.9 });
        var profile = new ThresholdProfile
        {
            ProfileId = "TP-ENGINE",
            Revision = "R0001",
            BoardModel = "ANY",
            BoardProgram = "TBOX-MAIN",
            RecipeName = "TBOX_TOP",
            RecipeRevision = "ANY",
            Status = "Approved",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules = [new ThresholdProfileRule { ViewType = "Top", RoiType = "Solder Bridge", DefectClass = "Any", ReviewThreshold = 1, NgThreshold = 2, ConfidenceThreshold = 0.7 }],
        };
        AoiDatabase.SaveThresholdProfile(profile);
        ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var result = new PixelDifferenceInspectionEngine().Analyze(sample, golden, DetectionPriority.Balanced);

        Assert.Equal("TP-ENGINE", result.ThresholdProfileId);
        Assert.Equal("R0001", result.ThresholdProfileRevision);
        Assert.Contains(result.Evidence, line => line.Contains("Active threshold profile TP-ENGINE/R0001", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PixelDifferenceFullFrameHonorsDeployedThresholdProfile()
    {
        AoiDatabase.Initialize();
        var golden = WriteBmp("frame-profile-golden.bmp", 40, 40, (_, _) => White());
        // A 5x5 patch 8 gray levels darker: worst 8x8-grid cell ~3% difference — below
        // every built-in review band, above the calibrated profile's NG band.
        var sample = WriteBmp("frame-profile-sample.bmp", 40, 40,
            (x, y) => x < 5 && y < 5 ? ((byte)247, (byte)247, (byte)247) : White());
        var engine = new PixelDifferenceInspectionEngine();

        var before = engine.Analyze(sample, golden, DetectionPriority.Balanced);
        Assert.Equal("OK", before.Verdict);
        Assert.Equal("Built-in policy default", before.ThresholdSource);
        Assert.Contains(before.Evidence, line => line.Contains("Threshold profile fallback (full frame)", StringComparison.OrdinalIgnoreCase));

        var profile = new ThresholdProfile
        {
            ProfileId = "TP-FRAME",
            Revision = "R0001",
            BoardModel = "ANY",
            BoardProgram = "ANY",
            RecipeName = "ANY",
            RecipeRevision = "ANY",
            Status = "Approved",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules = [new ThresholdProfileRule { ViewType = "Any", RoiType = "Any", DefectClass = "Any", ReviewThreshold = 0.5, NgThreshold = 1.0, ConfidenceThreshold = 0.7 }],
        };
        AoiDatabase.SaveThresholdProfile(profile);
        ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var after = engine.Analyze(sample, golden, DetectionPriority.Balanced);

        Assert.Equal("NG", after.Verdict);
        Assert.Equal("TP-FRAME", after.ThresholdProfileId);
        Assert.Equal("R0001", after.ThresholdProfileRevision);
        Assert.Equal(1.0, after.NgThreshold);
        Assert.Contains("deployed threshold profile TP-FRAME/R0001", after.DecisionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(after.Evidence, line => line.Contains("Threshold profile (full frame): TP-FRAME/R0001", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PixelDifferenceFullFrameScopeRulesResolveBySpecificity()
    {
        AoiDatabase.Initialize();
        var golden = WriteBmp("frame-scope-golden.bmp", 40, 40, (_, _) => White());
        var sample = WriteBmp("frame-scope-sample.bmp", 40, 40,
            (x, y) => x < 5 && y < 5 ? ((byte)247, (byte)247, (byte)247) : White());
        var engine = new PixelDifferenceInspectionEngine();

        // A FULL_FRAME-scoped rule outranks the wildcard rule on the frame path.
        var scoped = new ThresholdProfile
        {
            ProfileId = "TP-FRAME-SCOPED",
            Revision = "R0001",
            BoardModel = "ANY",
            BoardProgram = "ANY",
            RecipeName = "ANY",
            RecipeRevision = "ANY",
            Status = "Approved",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules =
            [
                new ThresholdProfileRule { ViewType = "Any", RoiType = "Any", DefectClass = "Any", ReviewThreshold = 4, NgThreshold = 9, ConfidenceThreshold = 0.6 },
                new ThresholdProfileRule { ViewType = "Any", RoiType = ThresholdScopes.FullFrame, DefectClass = "Any", ReviewThreshold = 0.5, NgThreshold = 1.0, ConfidenceThreshold = 0.7 },
            ],
        };
        AoiDatabase.SaveThresholdProfile(scoped);
        ThresholdProfileService.DeployProfile(scoped.ProfileId, scoped.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var viaScoped = engine.Analyze(sample, golden, DetectionPriority.Balanced);
        Assert.Equal(1.0, viaScoped.NgThreshold);
        Assert.Equal("NG", viaScoped.Verdict);

        // A profile holding only a concrete component-ROI rule never governs frame verdicts.
        var concreteOnly = new ThresholdProfile
        {
            ProfileId = "TP-FRAME-CONCRETE",
            Revision = "R0001",
            BoardModel = "ANY",
            BoardProgram = "ANY",
            RecipeName = "ANY",
            RecipeRevision = "ANY",
            Status = "Approved",
            CreatedBy = "Engineer01",
            CreatedAtUtc = DateTime.UtcNow,
            Rules = [new ThresholdProfileRule { ViewType = "Any", RoiType = "Solder Bridge", DefectClass = "Any", ReviewThreshold = 0.5, NgThreshold = 1.0, ConfidenceThreshold = 0.7 }],
        };
        AoiDatabase.SaveThresholdProfile(concreteOnly);
        ThresholdProfileService.DeployProfile(concreteOnly.ProfileId, concreteOnly.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var viaConcrete = engine.Analyze(sample, golden, DetectionPriority.Balanced);
        Assert.Equal("Built-in policy default", viaConcrete.ThresholdSource);
        Assert.Equal("OK", viaConcrete.Verdict);
        Assert.Contains(viaConcrete.Evidence, line => line.Contains("Threshold profile fallback (full frame)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FalseCallSweepSeesSubFivePercentBoundary()
    {
        // Percent-scale scores 0.8 / 3.5 / 5.0 (normalized 0.008 / 0.035 / 0.05): the old
        // mixed-scale NormalizeScore treated 0.8% as 0.8 and the old 5% sweep floor could
        // not see this boundary at all; the calibration grid must find a VALID point.
        var rows = new[]
        {
            ScoredRow("OK", 0.008),
            ScoredRow("OK", 0.006),
            ScoredRow("NG", 0.035),
            ScoredRow("NG", 0.05),
        };

        var run = FalseCallReductionService.Analyze(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            FalseCallReductionService.CreateSweepCriteria(FalseCallReductionMode.MaximizeDefectRecall));

        Assert.Equal("VALID", run.Recommendation.Status);
        Assert.NotNull(run.Recommendation.Point);
        Assert.InRange(run.Recommendation.Point!.DifferenceThreshold, 0.01, 0.035);
        Assert.Equal(0, run.Recommendation.Point.FalseNegative);
        Assert.Equal(0, run.Recommendation.Point.FalsePositive);
    }

    [Fact]
    public void CreateSweepCriteriaPinsCalibrationGrid()
    {
        var recall = FalseCallReductionService.CreateSweepCriteria(FalseCallReductionMode.MaximizeDefectRecall);
        Assert.Equal(0.01, recall.MinimumThreshold);
        Assert.Equal(0.005, recall.ThresholdStep);
        // Review band tied to the step: a fine grid must not predict the whole
        // below-threshold population into REVIEW.
        Assert.Equal(recall.ThresholdStep, recall.ReviewBand);
        Assert.Equal(0.0, recall.MaximumPossibleEscapeRate);

        var balanced = FalseCallReductionService.CreateSweepCriteria(FalseCallReductionMode.Balanced);
        Assert.Equal(0.05, balanced.MaximumPossibleEscapeRate);
    }

    [Fact]
    public void MinimizeFalsePositivesPrefersLowerFpEvenIfRecallDecreases()
    {
        var rows = new[]
        {
            ScoredRow("NG", 0.60),
            ScoredRow("OK", 0.70),
        };

        var run = FalseCallReductionService.Analyze(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria
            {
                MinimumThreshold = 0.5,
                MaximumThreshold = 0.8,
                ThresholdStep = 0.3,
                Mode = FalseCallReductionMode.MinimizeFalsePositives,
                MaximumFalseCallRate = 1,
                MaximumPossibleEscapeRate = 1,
            });

        Assert.Equal("VALID", run.Recommendation.Status);
        Assert.NotNull(run.Recommendation.Point);
        Assert.Equal(0, run.Recommendation.Point.FalsePositive);
        Assert.Equal(1, run.Recommendation.Point.FalseNegative);
    }

    [Fact]
    public void MaximizeDefectRecallRejectsHighPossibleEscapes()
    {
        var rows = new[]
        {
            ScoredRow("NG", 0.40),
            ScoredRow("NG", 0.90),
            ScoredRow("OK", 0.20),
        };

        var run = FalseCallReductionService.Analyze(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria
            {
                MinimumThreshold = 0.3,
                MaximumThreshold = 0.5,
                ThresholdStep = 0.2,
                Mode = FalseCallReductionMode.MaximizeDefectRecall,
                MaximumFalseCallRate = 1,
                MaximumPossibleEscapeRate = 0,
            });

        Assert.Equal("VALID", run.Recommendation.Status);
        Assert.NotNull(run.Recommendation.Point);
        Assert.Equal(0, run.Recommendation.Point.FalseNegative);
        Assert.Equal(0.3, run.Recommendation.Point.ConfidenceThreshold, precision: 3);
    }

    [Fact]
    public void InsufficientGroundTruthReturnsInvalidRecommendation()
    {
        var rows = new[]
        {
            ScoredRow("OK", 0.10),
            ScoredRow("UNKNOWN", 0.90),
        };

        var run = FalseCallReductionService.Analyze(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5 });

        Assert.Equal("INVALID", run.Recommendation.Status);
        Assert.Null(run.Recommendation.Point);
    }

    [Fact]
    public void ApplyingFalseCallThresholdRecordsAuditEventAndUpdatesConfig()
    {
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("EngineerApply", UserRole.Engineer);
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration { ConfidenceThreshold = 0.65 });
        var rows = new[]
        {
            ScoredRow("OK", 0.10),
            ScoredRow("NG", 0.90),
        };
        var run = FalseCallReductionService.AnalyzeAndPersist(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5, MaximumFalseCallRate = 0, MaximumPossibleEscapeRate = 0 },
            WorkflowState.Instance.OperatorWithRole);

        FalseCallReductionService.ApplyRecommendedThreshold(run, WorkflowState.Instance.OperatorWithRole);

        Assert.Equal(0.5, InspectionModelConfigurationService.Load().ConfidenceThreshold, precision: 3);
        var audit = AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "FALSE_CALL_THRESHOLD_APPLIED", OperatorId = "EngineerApply" });
        Assert.Contains(audit, row => row.UserRole == "Engineer" && row.RelatedEntityId == run.Id.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(AoiDatabase.GetLatestFalseCallReductionRun());
    }

    [Fact]
    public void ModelAcceptanceMissingModelFailsClearly()
    {
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());

        var run = ModelAcceptanceService.RunAcceptance(Path.Combine(_root, "missing-dataset"), Path.Combine(_root, "gt.csv"), operatorId: "Engineer01 [Engineer]");

        Assert.Equal("FAIL", run.Status);
        Assert.Contains(run.Messages, message => message.Contains("No active ONNX model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrototypePixelDifferenceCannotBePromotedAsProductionModel()
    {
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration { SelectedEngineKey = InspectionEngineFactory.DefaultEngineKey });
        var run = ModelAcceptanceService.RunAcceptance(_root, Path.Combine(_root, "gt.csv"), operatorId: "Engineer01 [Engineer]");

        Assert.Equal("FAIL", run.Status);
        Assert.Throws<InvalidOperationException>(() =>
            ModelAcceptanceService.PromoteToProductionCandidate(run.Id, UserRole.Engineer, "Engineer01 [Engineer]"));
    }

    [Fact]
    public void OperatorCannotRunModelAcceptance()
    {
        AoiDatabase.Initialize();

        Assert.Throws<UnauthorizedAccessException>(() =>
            ModelAcceptanceService.RunAcceptance(_root, Path.Combine(_root, "gt.csv"), operatorId: "Operator01 [Operator]", role: UserRole.Operator));
    }

    [Fact]
    public void ModelReleasePackageContainsShaCriteriaMetricsAndLimitations()
    {
        AoiDatabase.Initialize();
        var run = PassingModelAcceptanceRun();
        run.Id = AoiDatabase.RecordModelAcceptanceRun(run);

        var package = ModelAcceptanceService.CreateReleasePackage(run.Id, _root, "Engineer01 [Engineer]");
        var manifest = File.ReadAllText(package.ManifestPath);

        Assert.Contains(run.ModelSha256, manifest);
        Assert.Contains("criteria", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metrics", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limitations", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validationDataset", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sample_customer_image.png", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(package.PackagePath, "model_acceptance_metrics.csv")));
        Assert.True(File.Exists(Path.Combine(package.PackagePath, "validation_breakdown.csv")));
        Assert.False(Directory.EnumerateFiles(package.PackagePath, "*.png", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void FactoryReadinessRecognizesAcceptedProductionCandidateModel()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel();
        Assert.True(ModelRegistryService.SetActiveModel(model.ModelId));
        var run = PassingModelAcceptanceRun();
        run.ModelId = model.ModelId;
        run.ModelVersion = model.Version;
        run.ModelSha256 = model.Sha256;
        run.Id = AoiDatabase.RecordModelAcceptanceRun(run);
        ModelLifecycleService.RecordAcceptanceResult(run, run.OperatorId);
        ModelLifecycleService.PromoteProductionCandidate(run.Id, UserRole.Engineer, "Engineer01 [Engineer]");

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireProductionModel = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
        });

        Assert.Contains(report.Categories, category =>
            category.Name == "Active model readiness" &&
            category.Status == "Go" &&
            category.Evidence.Contains("PASS acceptance run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisteredModelCannotBeDeployedDirectly()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel(validateRuntime: false);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelLifecycleService.DeployModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]"));

        Assert.Contains("PASS model acceptance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModelLifecycleState.Registered, ModelRegistryService.GetModel(model.ModelId)!.LifecycleState);
    }

    [Fact]
    public void PassAcceptanceAllowsProductionCandidateLifecycleState()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel();
        var run = PassingModelAcceptanceRun();
        run.ModelId = model.ModelId;
        run.ModelVersion = model.Version;
        run.ModelSha256 = model.Sha256;
        run.Id = AoiDatabase.RecordModelAcceptanceRun(run);
        ModelLifecycleService.RecordAcceptanceResult(run, run.OperatorId);

        ModelLifecycleService.PromoteProductionCandidate(run.Id, UserRole.Engineer, "Engineer01 [Engineer]");

        var updated = ModelRegistryService.GetModel(model.ModelId)!;
        Assert.Equal(ModelLifecycleState.ProductionCandidate, updated.LifecycleState);
        Assert.Equal("PASS", updated.LatestAcceptanceStatus);
    }

    [Fact]
    public void AdminWaiverIsRecordedAndKeepsFactoryReadinessConditional()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel();
        var expiresAt = DateTime.UtcNow.AddDays(14);

        Assert.Throws<ArgumentException>(() =>
            ModelLifecycleService.DeployModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]", "Missing risk classification.", expiresAt, string.Empty));

        ModelLifecycleService.DeployModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]", "Customer pilot waiver while acceptance dataset is pending.", expiresAt, "Medium");

        var stage1 = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));
        var full = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireProductionModel = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
        });

        Assert.Contains(stage1.Categories, category =>
            category.Name == "Active model readiness" &&
            category.Status == "Conditional" &&
            category.Evidence.Contains("waiver", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(full.Categories, category =>
            category.Name == "Active model readiness" &&
            category.Status == "Conditional" &&
            category.Evidence.Contains("waiver", StringComparison.OrdinalIgnoreCase));
        var updated = ModelRegistryService.GetModel(model.ModelId)!;
        Assert.NotNull(updated.WaiverExpiresAtUtc);
        Assert.Equal("Medium", updated.DeploymentWaiverRiskClassification);
        Assert.InRange(
            Math.Abs((updated.WaiverExpiresAtUtc!.Value.ToUniversalTime() - expiresAt.ToUniversalTime()).TotalSeconds),
            0,
            1);
        Assert.NotNull(updated.DeployedAtUtc);
    }

    [Fact]
    public void ExpiredAdminWaiverBlocksFactoryReadiness()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel();
        ModelLifecycleService.DeployModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]", "Short pilot waiver.", DateTime.UtcNow.AddDays(1), "High");
        AoiDatabase.UpdateModelLifecycle(
            model.ModelId,
            ModelLifecycleState.Deployed,
            deploymentWaiverReason: "Expired pilot waiver.",
            waiverExpiresAtUtc: DateTime.UtcNow.AddDays(-1),
            deploymentWaivedBy: "Admin01 [Admin]",
            deploymentWaivedAtUtc: DateTime.UtcNow.AddDays(-2),
            deploymentWaiverRiskClassification: "High",
            replaceDeploymentWaiver: true);

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireProductionModel = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
        });

        Assert.Contains(report.Categories, category =>
            category.Name == "Active model readiness" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("Expired pilot waiver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetiredModelCannotRemainActive()
    {
        AoiDatabase.Initialize();
        var model = RegisterLifecycleModel();
        var run = PassingModelAcceptanceRun();
        run.ModelId = model.ModelId;
        run.ModelVersion = model.Version;
        run.ModelSha256 = model.Sha256;
        run.Id = AoiDatabase.RecordModelAcceptanceRun(run);
        ModelLifecycleService.RecordAcceptanceResult(run, run.OperatorId);
        ModelLifecycleService.DeployModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]");

        Assert.NotNull(ModelRegistryService.GetActiveModel());

        ModelLifecycleService.RetireModel(model.ModelId, UserRole.Admin, "Admin01 [Admin]", "Superseded by newer model.");

        var retired = ModelRegistryService.GetModel(model.ModelId)!;
        Assert.Equal(ModelLifecycleState.Retired, retired.LifecycleState);
        Assert.False(retired.IsActive);
        Assert.Null(ModelRegistryService.GetActiveModel());
    }

    [Fact]
    public void OperatorCannotApplyFalseCallThreshold()
    {
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("OperatorApply", UserRole.Operator);
        var run = FalseCallReductionService.Analyze(
            new[] { ScoredRow("OK", 0.10), ScoredRow("NG", 0.90) },
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5, MaximumFalseCallRate = 0, MaximumPossibleEscapeRate = 0 });

        Assert.Throws<UnauthorizedAccessException>(() =>
            FalseCallReductionService.ApplyRecommendedThreshold(run, UserRole.Operator, WorkflowState.Instance.OperatorWithRole));
    }

    [Fact]
    public void ValidationPackageIncludesFalseCallAndThresholdProfileEvidence()
    {
        AoiDatabase.Initialize();
        var rows = new[] { ScoredRow("OK", 0.10), ScoredRow("NG", 0.90) };
        var run = FalseCallReductionService.AnalyzeAndPersist(
            rows,
            "Unit Test Engine",
            "TEST-1",
            "MODEL-1",
            "HASH",
            null,
            new FalseCallReductionCriteria { MinimumThreshold = 0.5, MaximumThreshold = 0.5, MaximumFalseCallRate = 0, MaximumPossibleEscapeRate = 0 },
            "Engineer01 [Engineer]");
        var profile = ThresholdProfileService.CreateDraftFromFalseCallReductionRecommendation(
            run,
            "TBOX-MAIN",
            "TBOX-MAIN",
            "ANY",
            "ANY",
            "Engineer01 [Engineer]");
        ThresholdProfileService.ApproveProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");
        ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");

        var metrics = BatchValidationService.CalculateMetrics(rows);
        var package = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = _root,
            BoardModel = "TBOX-MAIN",
            EngineName = "Unit Test Engine",
            ModelVersion = "TEST-1",
            Metrics = metrics,
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(rows),
            Rows = rows,
            FalseCallReductionRun = run,
            DatasetQualityCriteria = new ValidationDatasetQualityCriteria
            {
                MinimumTotalImages = 1,
                MinimumKnownGroundTruthImages = 1,
                MinimumOkImages = 1,
                MinimumNgImages = 1,
                MinimumDefectClasses = 0,
                MinimumImagesPerDefectClass = 0,
                RequireGoldenImageForPixelDifference = false,
            },
        });

        Assert.Equal(run.Id, package.Manifest.FalseCallRecommendation?.RunId);
        Assert.Equal(profile.ProfileId, package.Manifest.ThresholdProfileEvidence.ProfileId);
        Assert.Equal(profile.Revision, package.Manifest.ThresholdProfileEvidence.Revision);
        Assert.Equal(run.Id, package.Manifest.ThresholdProfileEvidence.SourceFalseCallReductionRunId);
        var html = File.ReadAllText(package.ReportPath);
        Assert.Contains("Deployed Threshold Profile Evidence", html);
        Assert.Contains(profile.ProfileId, html);
        Assert.Contains("not universal production proof", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FolderCameraSourceReturnsFramesInSortedOrderAndCyclesByView()
    {
        var topFolder = Path.Combine(_root, "camera", "top");
        var sideFolder = Path.Combine(_root, "camera", "side");
        Directory.CreateDirectory(topFolder);
        Directory.CreateDirectory(sideFolder);
        File.WriteAllBytes(Path.Combine(topFolder, "002_top.png"), TinyPngBytes());
        File.WriteAllBytes(Path.Combine(topFolder, "001_top.png"), TinyPngBytes());
        File.WriteAllBytes(Path.Combine(sideFolder, "001_side.png"), TinyPngBytes());

        var source = new FolderCameraSource(
            new Dictionary<CameraViewType, string>
            {
                [CameraViewType.Top] = topFolder,
                [CameraViewType.Side] = sideFolder,
            },
            "BOARD-X",
            "LOT-42");

        Assert.Equal(CameraSourceStatus.Simulated, source.ConnectionStatus);
        source.StartAcquisition();

        source.SelectedView = CameraViewType.Top;
        var firstTop = source.GetNextFrame();
        var secondTop = source.GetNextFrame();
        var cycledTop = source.GetNextFrame();

        source.SelectedView = CameraViewType.Side;
        var firstSide = source.GetNextFrame();

        Assert.NotNull(firstTop);
        Assert.NotNull(secondTop);
        Assert.NotNull(cycledTop);
        Assert.NotNull(firstSide);
        Assert.EndsWith("001_top.png", firstTop.SourcePath);
        Assert.EndsWith("002_top.png", secondTop.SourcePath);
        Assert.Equal(firstTop.SourcePath, cycledTop.SourcePath);
        Assert.EndsWith("001_side.png", firstSide.SourcePath);
        Assert.Equal(CameraViewType.Top, firstTop.ViewType);
        Assert.Equal(CameraViewType.Side, firstSide.ViewType);
        Assert.Equal("BOARD-X", firstTop.BoardModel);
        Assert.Equal("LOT-42", firstTop.LotId);

        source.StopAcquisition();
        Assert.Null(source.GetNextFrame());
    }

    [Fact]
    public void CameraSourceFactoryReturnsConfiguredSourceTypes()
    {
        Assert.IsType<NullCameraSource>(CameraSourceFactory.Create(new CameraSourceSettings { SourceKey = CameraSourceFactory.NullSourceKey }));
        Assert.IsType<FolderCameraSource>(CameraSourceFactory.Create(new CameraSourceSettings { SourceKey = CameraSourceFactory.FolderSimulationSourceKey }));
        Assert.IsType<GenericVisionCameraSource>(CameraSourceFactory.Create(new CameraSourceSettings { SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey }));
        Assert.Equal(CameraSourceFactory.GenericVisionAdapterSourceKey, CameraSourceFactory.NormalizeSourceKey("generic"));
    }

    [Fact]
    public void GenericVisionCameraSourceWithNullAdapterReportsNotConnected()
    {
        var source = new GenericVisionCameraSource(new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            TopDeviceId = "CAM-TOP-1",
            AcquisitionMode = CameraAcquisitionMode.SoftwareTrigger,
        });

        Assert.Equal(CameraSourceStatus.NotConnected, source.ConnectionStatus);
        source.StartAcquisition();

        Assert.False(source.IsAcquiring);
        Assert.Null(source.GetNextFrame());
        Assert.Contains("no vendor SDK adapter", source.StatusMessage, StringComparison.OrdinalIgnoreCase);
        source.StopAcquisition();
    }

    [Fact]
    public void GenericVisionCameraSourceWithFakeAdapterCanAcquireTriggeredFrame()
    {
        var imagePath = WriteTinyPng("fake-camera.png");
        var adapter = new FakeVisionCameraAdapter(imagePath);
        var source = new GenericVisionCameraSource(
            new CameraSourceSettings
            {
                SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
                TopDeviceId = "FAKE-TOP",
                AcquisitionMode = CameraAcquisitionMode.SoftwareTrigger,
                TriggerTimeoutMs = 50,
                FrameTimeoutMs = 100,
                BoardModel = "BOARD-X",
                LotId = "LOT-X",
            },
            adapter);

        source.StartAcquisition();
        var frame = source.GetNextFrame();
        source.StopAcquisition();

        Assert.True(adapter.ConnectCalled);
        Assert.True(adapter.StartCalled);
        Assert.True(adapter.TriggerCalled);
        Assert.True(adapter.StopCalled);
        Assert.NotNull(frame);
        Assert.Equal(CameraSourceStatus.NotConnected, source.ConnectionStatus);
        Assert.Equal("FakeVisionAdapter", frame.SourceKind);
        Assert.True(frame.IsSimulated);
        Assert.Equal("FAKE-TOP", frame.CameraId);
        Assert.Equal(CameraViewType.Top, frame.ViewType);
        Assert.Equal("BOARD-X", frame.BoardModel);
        Assert.Equal("LOT-X", frame.LotId);
    }

    [Fact]
    public void CameraAcceptanceFakeAdapterWithDeterministicFramesPassesAsSimulationOnly()
    {
        AoiDatabase.Initialize();
        var adapter = new FakeVisionCameraAdapter(WriteTinyPng("acceptance-fake.png"));
        var run = CameraAcceptanceTestService.Run(
            new CameraSourceSettings
            {
                SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
                TopDeviceId = "FAKE-TOP",
                AcquisitionMode = CameraAcquisitionMode.SoftwareTrigger,
            },
            new CameraAcceptanceCriteria
            {
                FramesPerView = 3,
                RequiredViews = new() { "Top" },
                MaxDroppedFrameRate = 0,
                MaxTriggerFailureRate = 0,
            },
            adapter);

        var id = AoiDatabase.RecordCameraAcceptanceRun(run, "UnitTest [Engineer]");
        var persisted = AoiDatabase.GetLatestCameraAcceptanceRun();
        var summary = CameraAcceptanceTestService.ToSummary(persisted);

        Assert.True(id > 0);
        Assert.Equal("PASS", run.Status);
        Assert.False(run.IsRealHardware);
        Assert.Equal("NOT VALIDATED", run.FactoryReadinessStatus);
        Assert.Equal(3, run.TotalReceivedFrames);
        Assert.Equal("NOT VALIDATED", summary.Status);
        Assert.Contains(summary.Messages, message => message.Contains("simulation", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(persisted);
        Assert.Equal(3, persisted.Frames.Count);
    }

    [Fact]
    public void CameraAcceptanceNullAdapterFailsWithNotConnectedMessage()
    {
        var run = CameraAcceptanceTestService.Run(
            new CameraSourceSettings
            {
                SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
                TopDeviceId = "NULL-TOP",
            },
            new CameraAcceptanceCriteria
            {
                FramesPerView = 2,
                RequiredViews = new() { "Top" },
            });

        Assert.Equal("FAIL", run.Status);
        Assert.Equal("NOT VALIDATED", run.FactoryReadinessStatus);
        Assert.Contains(run.Failures, failure => failure.Contains("did not enter acquisition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(run.Failures, failure => failure.Contains("no frames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CameraAcceptanceMissingRequiredViewsFails()
    {
        var topFolder = Path.Combine(_root, "camera-top-only");
        Directory.CreateDirectory(topFolder);
        File.WriteAllBytes(Path.Combine(topFolder, "top.png"), TinyPngBytes());
        var run = CameraAcceptanceTestService.Run(
            new CameraSourceSettings
            {
                SourceKey = CameraSourceFactory.FolderSimulationSourceKey,
                TopFolder = topFolder,
            },
            new CameraAcceptanceCriteria
            {
                FramesPerView = 1,
                RequiredViews = new() { "Top", "Side", "Bottom" },
            });

        Assert.Equal("FAIL", run.Status);
        Assert.Contains(run.ViewMetrics, view => view.ViewType == "Top" && view.ReceivedFrames == 1);
        Assert.Contains(run.Failures, failure => failure.Contains("Side", StringComparison.OrdinalIgnoreCase) && failure.Contains("no frames", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(run.Failures, failure => failure.Contains("Bottom", StringComparison.OrdinalIgnoreCase) && failure.Contains("no frames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CameraAcceptanceDroppedFrameSimulationWarnsOrFailsByCriteria()
    {
        var warnRun = CameraAcceptanceTestService.Run(
            new CameraSourceSettings { SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey },
            new CameraAcceptanceCriteria
            {
                FramesPerView = 5,
                RequiredViews = new() { "Top" },
                MaxDroppedFrameRate = 0.5,
            },
            sourceOverride: new DroppingCameraSource(dropEvery: 3));

        var failRun = CameraAcceptanceTestService.Run(
            new CameraSourceSettings { SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey },
            new CameraAcceptanceCriteria
            {
                FramesPerView = 5,
                RequiredViews = new() { "Top" },
                MaxDroppedFrameRate = 0,
            },
            sourceOverride: new DroppingCameraSource(dropEvery: 3));

        Assert.Equal("WARN", warnRun.Status);
        Assert.Equal(1, warnRun.DroppedFrameCount);
        Assert.Contains(warnRun.Warnings, warning => warning.Contains("dropped frame", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("FAIL", failRun.Status);
        Assert.Contains(failRun.Failures, failure => failure.Contains("dropped-frame rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LightingAcceptanceSimulatedLightingPasses()
    {
        AoiDatabase.Initialize();
        var settings = new LightingSettings
        {
            Mode = LightingModes.Simulated,
            TopProgram = "TOP-A",
            SideProgram = "SIDE-A",
            BottomProgram = "BOTTOM-A",
            ResponseTimeoutMs = 500,
        };

        var run = await LightingAcceptanceTestService.RunAsync(
            settings,
            new LightingAcceptanceCriteria { RequiredViews = new() { "Top", "Side", "Bottom" } });
        var id = AoiDatabase.RecordLightingAcceptanceRun(run, "UnitTest [Engineer]");
        var persisted = AoiDatabase.GetLatestLightingAcceptanceRun();

        Assert.True(id > 0);
        Assert.Equal("PASS", run.Status);
        Assert.True(run.IsSimulated);
        Assert.Equal(3, run.PassedStepCount);
        Assert.All(run.Steps, step => Assert.Equal("PASS", step.Status));
        Assert.NotNull(persisted);
        Assert.Equal(3, persisted.Steps.Count);
    }

    [Fact]
    public async Task LightingAcceptanceNullLightingFailsAsNotConnected()
    {
        var run = await LightingAcceptanceTestService.RunAsync(
            new LightingSettings { Mode = LightingModes.None, ResponseTimeoutMs = 100 },
            new LightingAcceptanceCriteria { RequiredViews = new() { "Top" } });

        Assert.Equal("FAIL", run.Status);
        Assert.Contains(run.Failures, failure => failure.Contains("No real lighting hardware", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(run.Steps, step => step.Status == "FAIL" && !step.CommandAccepted);
    }

    [Fact]
    public async Task LightingAcceptanceTimeoutProducesFail()
    {
        var run = await LightingAcceptanceTestService.RunAsync(
            new LightingSettings { Mode = LightingModes.Simulated, ResponseTimeoutMs = 25 },
            new LightingAcceptanceCriteria { RequiredViews = new() { "Top" } },
            controller: new SlowLightingController());

        Assert.Equal("FAIL", run.Status);
        Assert.Contains(run.Failures, failure => failure.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LightingAcceptanceReportContainsPerViewTiming()
    {
        var run = await LightingAcceptanceTestService.RunAsync(
            new LightingSettings { Mode = LightingModes.Simulated, ResponseTimeoutMs = 500 },
            new LightingAcceptanceCriteria { RequiredViews = new() { "Top", "Side" } });

        var html = LightingAcceptanceTestService.BuildHtml(run);

        Assert.Contains("Per-View Timing", html);
        Assert.Contains("Top", html);
        Assert.Contains("Side", html);
        Assert.Contains("Command ms", html);
        Assert.Contains("Trigger-to-frame ms", html);
    }

    [Fact]
    public void FolderCameraSourcePopulatesFrameMetadata()
    {
        var topFolder = Path.Combine(_root, "metadata-camera", "top");
        Directory.CreateDirectory(topFolder);
        File.WriteAllBytes(Path.Combine(topFolder, "001_top.png"), TinyPngBytes());
        var source = new FolderCameraSource(
            new Dictionary<CameraViewType, string> { [CameraViewType.Top] = topFolder },
            "BOARD-META",
            "LOT-META");

        source.StartAcquisition();
        var frame = source.GetNextFrame();

        Assert.NotNull(frame);
        Assert.Equal("FolderCameraSimulation", frame.SourceKind);
        Assert.True(frame.IsSimulated);
        Assert.Equal("Top-FolderSimulation", frame.CameraId);
        Assert.Equal("BOARD-META", frame.BoardModel);
        Assert.Equal("LOT-META", frame.LotId);
        Assert.True(frame.Width >= 0);
        Assert.True(frame.Height >= 0);
        Assert.NotEqual(DateTime.MinValue, frame.EffectiveCapturedAtUtc);
    }

    private IReadOnlySet<string> ReadTableNames()
    {
        using var connection = OpenTestConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }

    private static SqliteConnection OpenTestConnection()
    {
        var connection = new SqliteConnection($"Data Source={AoiDatabase.DatabasePath}");
        connection.Open();
        return connection;
    }

    private static void ExecuteSql(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private string WriteTinyPng(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, TinyPngBytes());
        return path;
    }

    private long SaveTestRecipe(params RecipeRoiDocument[] rois)
    {
        var document = new RecipeDocument
        {
            RecipeName = "TBOX_TOP",
            BoardProgram = "TBOX-MAIN",
            BackgroundImagePath = @"C:\images\board.png",
            Rois = rois.ToList(),
        };

        var id = AoiDatabase.SaveRecipeRevision(
            document.RecipeName,
            document.BoardProgram,
            "Engineer01",
            "Balanced",
            document.BackgroundImagePath,
            JsonSerializer.Serialize(document));
        RecipeService.Invalidate(document.BoardProgram);
        return id;
    }

    private string WriteBmp(string fileName, int width, int height, Func<int, int, (byte R, byte G, byte B)> colorAt)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        var pixelBytes = width * height * 4;
        var fileSize = 54 + pixelBytes;
        var bytes = new byte[fileSize];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        WriteInt32(bytes, 2, fileSize);
        WriteInt32(bytes, 10, 54);
        WriteInt32(bytes, 14, 40);
        WriteInt32(bytes, 18, width);
        WriteInt32(bytes, 22, height);
        bytes[26] = 1;
        bytes[28] = 32;
        WriteInt32(bytes, 34, pixelBytes);

        var offset = 54;
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colorAt(x, y);
                bytes[offset++] = b;
                bytes[offset++] = g;
                bytes[offset++] = r;
                bytes[offset++] = 255;
            }
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static (byte R, byte G, byte B) White() => (255, 255, 255);
    private static (byte R, byte G, byte B) Black() => (0, 0, 0);

    private static BatchTestRow Row(string groundTruth, string engineResult, string imageName = "image.png")
    {
        var passFail = BatchValidationService.CalculatePassFail(groundTruth, engineResult);
        var expectedClass = BatchValidationService.NormalizeDefectClass("Synthetic");
        var side = BatchValidationService.NormalizeSide("top");
        return new BatchTestRow
        {
            Image = imageName,
            ImagePath = Path.Combine(@"C:\validation", imageName),
            GroundTruth = groundTruth,
            EngineResult = engineResult,
            InspectionEngine = "Unit Test Engine",
            ModelVersion = "TEST-1",
            Score = engineResult == "NG" ? 80 : 5,
            PassFail = passFail,
            DefectType = "Synthetic",
            NormalizedDefectClass = expectedClass,
            NormalizedSide = side,
            FailureCategory = BatchValidationService.DetermineFailureCategory(
                groundTruth,
                engineResult,
                expectedClass,
                expectedClass,
                side,
                side,
                false),
            Side = "top",
            RefDes = "U1",
            LotId = "LOT",
            BoardModel = "TBOX",
            Notes = "Synthetic note",
            RoiId = "ROI-TEST",
            RoiType = "Synthetic ROI",
            RoiX = 0.1,
            RoiY = 0.2,
            RoiWidth = 0.3,
            RoiHeight = 0.4,
            ImageLoadMilliseconds = 11,
            PreprocessingMilliseconds = 22,
            InferenceMilliseconds = 33,
            OverlayRenderingMilliseconds = 4,
            TotalInspectionMilliseconds = 70,
        };
    }

    private ModelAcceptanceRun PassingModelAcceptanceRun()
    {
        return new ModelAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ModelId = "MODEL-ACCEPTED",
            ModelVersion = "1.0.0",
            ModelSha256 = "abc123",
            ModelPath = Path.Combine(_root, "accepted.onnx"),
            InputTensorName = "input",
            OutputTensorName = "output",
            OutputShape = "class, confidence, x, y, width, height rows",
            DatasetFolder = Path.Combine(_root, "customer-dataset"),
            DatasetName = "customer-dataset",
            GroundTruthCsvPath = Path.Combine(_root, "ground_truth.csv"),
            IsFormalManifest = true,
            Status = "PASS",
            OperatorId = "Engineer01 [Engineer]",
            Criteria = new ModelAcceptanceCriteria(),
            Metrics = new BatchMetrics(1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 2),
            DatasetQualitySummary = new DatasetQualitySummary
            {
                Status = "PASS",
                TotalImages = 50,
                KnownGroundTruthImages = 50,
                OkImages = 25,
                NgImages = 25,
                DefectClassCount = 2,
            },
            FalseCallRecommendation = new FalseCallRecommendationSummary
            {
                Status = "VALID",
                RunId = 10,
                SelectedThreshold = 0.65,
                FalseCallRate = 0,
                PossibleEscapeRate = 0,
            },
            BreakdownSummary = new ValidationBreakdownSummary(),
            PerformanceSummary = new BatchPerformanceSummary(25, 40, 10, 0, 2),
            P95InferenceMs = 35,
            Messages = ["PASS: Synthetic accepted model evidence for tests."],
            Limitations =
            [
                "Model acceptance is limited to the supplied customer validation dataset and criteria.",
                "No raw customer images are included in the model release package.",
            ],
        };
    }

    private ModelRegistryEntry RegisterLifecycleModel(bool validateRuntime = true)
    {
        var modelPath = Path.Combine(_root, $"lifecycle-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(modelPath, "placeholder model bytes");
        var model = ModelRegistryService.Register(new ModelRegistrationRequest
        {
            ModelFilePath = modelPath,
            DisplayName = "Lifecycle Model",
            Version = "1.0.0",
            InputTensorName = "input",
            OutputTensorName = "output",
            InputWidth = 640,
            InputHeight = 640,
            ConfidenceThreshold = 0.65,
        });
        if (validateRuntime)
        {
            AoiDatabase.UpdateModelRegistryValidation(
                model.ModelId,
                ModelConfigurationTestStatus.Ready,
                DateTime.UtcNow,
                "Runtime validated for lifecycle test.");
        }
        return ModelRegistryService.GetModel(model.ModelId)!;
    }

    private static BatchTestRow ScoredRow(
        string groundTruth,
        double score,
        string expectedClass = "Synthetic",
        string predictedClass = "Synthetic",
        string side = "top",
        string roiId = "ROI-TEST")
    {
        // Callers express the score on the sweep's normalized 0-1 scale; the persisted
        // row carries the production 0-100 percent DifferenceScore scale, which
        // FalseCallReductionService.NormalizeScore divides by 100.
        var engineResult = score >= 0.5 ? "NG" : "OK";
        score *= 100.0;
        var normalizedExpectedClass = BatchValidationService.NormalizeDefectClass(expectedClass);
        var normalizedPredictedClass = BatchValidationService.NormalizeDefectClass(predictedClass);
        var normalizedSide = BatchValidationService.NormalizeSide(side);
        return new BatchTestRow
        {
            Image = $"{groundTruth}_{score:F2}.png",
            ImagePath = Path.Combine(@"C:\validation", $"{groundTruth}_{score:F2}.png"),
            GroundTruth = groundTruth,
            EngineResult = engineResult,
            InspectionEngine = "Unit Test Engine",
            ModelVersion = "TEST-1",
            Score = score,
            PassFail = BatchValidationService.CalculatePassFail(groundTruth, engineResult),
            DefectType = predictedClass,
            NormalizedDefectClass = normalizedExpectedClass,
            NormalizedSide = normalizedSide,
            FailureCategory = BatchValidationService.DetermineFailureCategory(
                groundTruth,
                engineResult,
                normalizedExpectedClass,
                normalizedPredictedClass,
                normalizedSide,
                normalizedSide,
                false),
            Side = side,
            RoiId = roiId,
            RoiType = "Synthetic ROI",
        };
    }

    private BatchTestRow QualityRow(string groundTruth, int index, string defectClass, string goldenPath)
    {
        var engineResult = string.Equals(groundTruth, "NG", StringComparison.OrdinalIgnoreCase) ? "NG" : "OK";
        var imageName = $"quality-{index:D3}.png";
        var row = Row(groundTruth, engineResult, imageName);
        row.ImagePath = Path.Combine(_root, "quality-images", imageName);
        row.GoldenImagePath = goldenPath;
        row.DefectType = defectClass;
        row.NormalizedDefectClass = BatchValidationService.NormalizeDefectClass(defectClass);
        row.RoiId = $"ROI-{index:D3}";
        row.RoiType = index % 2 == 0 ? "Component" : "Solder";
        return row;
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    private sealed class DroppingCameraSource : ICameraSource
    {
        private readonly int _dropEvery;
        private int _attempts;

        public DroppingCameraSource(int dropEvery)
        {
            _dropEvery = dropEvery;
        }

        public string Name => "Dropping Fake Camera";
        public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
        public CameraSourceStatus ConnectionStatus => CameraSourceStatus.Simulated;
        public string StatusMessage => "Deterministic dropping fake camera.";
        public bool IsAcquiring { get; private set; }

        public void StartAcquisition()
        {
            IsAcquiring = true;
            _attempts = 0;
        }

        public void StopAcquisition()
        {
            IsAcquiring = false;
        }

        public CameraFrame? GetNextFrame()
        {
            if (!IsAcquiring)
                return null;

            _attempts++;
            if (_dropEvery > 0 && _attempts % _dropEvery == 0)
                return null;

            var capturedAtUtc = DateTime.UtcNow.AddMilliseconds(_attempts * 10);
            return new CameraFrame(
                $"DROP-{_attempts:000}",
                string.Empty,
                SelectedView,
                capturedAtUtc.ToLocalTime(),
                Name,
                "BOARD",
                "LOT",
                CameraId: "DROP-CAM",
                CapturedAtUtc: capturedAtUtc,
                Width: 640,
                Height: 480,
                PixelFormat: "Mono8",
                SourceKind: "DroppingFakeCamera",
                IsSimulated: true);
        }
    }

    private sealed class SlowLightingController : ILightingController
    {
        public string Name => "Slow Fake Lighting Controller";
        public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
        public string StatusMessage => "Slow fake lighting controller.";

        public async Task<IntegrationCommandResult> SetProgramAsync(
            string viewType,
            string programName,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new IntegrationCommandResult(true, IntegrationConnectionStatus.Simulated, "Unexpected slow fake lighting completion.");
        }
    }

    private sealed class TestProfile3DSource : IProfile3DSource
    {
        private readonly Profile3DFrame? _frame;

        public TestProfile3DSource(Profile3DFrame? frame)
        {
            _frame = frame;
        }

        public string Name => "Test 3D Source";
        public string Status => _frame is null ? "NotConnected" : "Ready";
        public string StatusMessage => _frame is null ? "No test frame." : "Test frame ready.";
        public bool IsAcquiring { get; private set; }
        public void StartAcquisition()
        {
            IsAcquiring = true;
        }

        public void StopAcquisition()
        {
            IsAcquiring = false;
        }

        public Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default) => _frame;

        public IReadOnlyDictionary<string, string> GetDiagnostics()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "test",
                ["status"] = Status,
            };
    }

    public sealed class FakeVisionCameraAdapterFactory : IVisionCameraAdapterFactory
    {
        public const string Id = "fake-vision-plugin";

        public string AdapterId => Id;
        public string DisplayName => "Fake Vision Plugin";
        public string Version => "1.0.0";
        public IReadOnlyList<string> SupportedInterfaces { get; } = new[] { "GigE Vision", "USB3 Vision" };
        public IReadOnlyList<string> SupportedViews { get; } = new[] { "Top", "Side", "Bottom" };
        public IReadOnlyList<string> SupportedPixelFormats { get; } = new[] { "Mono8" };

        public IVisionCameraAdapter CreateAdapter(CameraSourceSettings settings)
            => new FakeVisionCameraAdapter(string.Empty);

        public IVisionDeviceDiscovery? CreateDeviceDiscovery(CameraSourceSettings settings)
            => new FakeVisionDeviceDiscovery();
    }

    public sealed class FakeVisionDeviceDiscovery : IVisionDeviceDiscovery
    {
        public IReadOnlyList<VisionDeviceInfo> DiscoverDevices()
            => new[]
            {
                new VisionDeviceInfo("FAKE-TOP", "FakeVendor", "FakeGigE", "001", "GigE Vision", "192.0.2.10", "Top", "Ready", new[] { "SoftwareTrigger", "Mono8" }),
                new VisionDeviceInfo("FAKE-SIDE", "FakeVendor", "FakeUSB3", "002", "USB3 Vision", string.Empty, "Side", "Ready", new[] { "Continuous", "Mono8" }),
                new VisionDeviceInfo("FAKE-BOTTOM", "FakeVendor", "FakeGigE", "003", "GigE Vision", "192.0.2.12", "Bottom", "Ready", new[] { "HardwareTrigger", "Mono8" }),
            };
    }

    private sealed class FakeVisionCameraAdapter : IVisionCameraAdapter
    {
        private readonly string _imagePath;
        private string _deviceId = string.Empty;
        private bool _connected;

        public FakeVisionCameraAdapter(string imagePath)
        {
            _imagePath = imagePath;
        }

        public bool ConnectCalled { get; private set; }
        public bool StartCalled { get; private set; }
        public bool TriggerCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs)
        {
            ConnectCalled = true;
            _connected = true;
            _deviceId = deviceId;
            return true;
        }

        public void Disconnect()
        {
            _connected = false;
        }

        public bool Start()
        {
            StartCalled = true;
            return _connected;
        }

        public void Stop()
        {
            StopCalled = true;
        }

        public bool Trigger(int timeoutMs)
        {
            TriggerCalled = true;
            return _connected;
        }

        public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
        {
            if (!_connected)
            {
                frame = null;
                return false;
            }

            var capturedAtUtc = DateTime.UtcNow;
            frame = new CameraFrame(
                "FAKE-000001",
                _imagePath,
                viewType,
                capturedAtUtc.ToLocalTime(),
                "Fake Adapter",
                string.Empty,
                string.Empty,
                CameraId: _deviceId,
                CapturedAtUtc: capturedAtUtc,
                Width: 640,
                Height: 480,
                PixelFormat: "Mono8",
                SourceKind: "FakeVisionAdapter",
                IsSimulated: true);
            return true;
        }

        public VisionCameraDiagnostics GetDiagnostics()
            => _connected
                ? new VisionCameraDiagnostics(CameraSourceStatus.Ready, "Fake adapter ready.", Array.Empty<string>())
                : new VisionCameraDiagnostics(CameraSourceStatus.NotConnected, "Fake adapter disconnected.", Array.Empty<string>());
    }

    private static AnalysisResult CreateCentralSyncInspectionResult()
        => new()
        {
            SamplePath = @"C:\customer\images\sample.png",
            GoldenPath = @"C:\customer\images\golden.png",
            BoardProgram = "SYNC-BOARD",
            OperatorId = "Operator42",
            InspectionEngine = "Unit Test Engine",
            DifferenceScore = 4.2,
            Verdict = "OK",
            Confidence = 0.97,
            SuggestedDefect = "None",
            PolicyName = "Balanced",
            ModelVersion = "SYNC-1",
            ModelFilePath = @"C:\models\sync.onnx",
            ConfidenceThreshold = 0.65,
            DecisionReason = "Synthetic central sync test result",
            Hotspot = new Rect(0.1, 0.2, 0.3, 0.4),
            Timing = new InspectionTiming
            {
                ImageLoadMilliseconds = 1,
                PreprocessingMilliseconds = 2,
                InferenceMilliseconds = 3,
                OverlayRenderingMilliseconds = 4,
                TotalInspectionMilliseconds = 10,
            },
        };
}
