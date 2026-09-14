using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class DefectTaxonomyServiceTests : IDisposable
{
    private readonly string _root;

    public DefectTaxonomyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Taxonomy_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Taxonomy test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Taxonomy test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void DefaultAliasMapsToCanonicalDefectClass()
    {
        // "Pin Height Error" is now an alias of the first-class Connector Pin Height class.
        var normalized = DefectTaxonomyService.Normalize("Pin Height Error");

        Assert.True(normalized.IsKnown);
        Assert.Equal("Connector Pin Height", normalized.CanonicalClass);
        Assert.Equal("CPH", normalized.MesCode);
    }

    [Fact]
    public void DefaultTaxonomyIncludesMandatoryDefectClasses()
    {
        var classes = DefectTaxonomyService.ActiveCanonicalClasses();

        foreach (var required in new[]
        {
            "Solder Bridge", "Insufficient Solder", "Solder Volume", "Cold Joint",
            "Polarity Error", "Tombstone", "Missing Component", "Misalignment",
            "Height Error", "Connector Pin Height", "3D Coplanarity", "Shield Can Gap",
        })
        {
            Assert.Contains(required, classes);
        }

        // Coplanarity is now its own class rather than an alias of Height Error.
        Assert.Equal("3D Coplanarity", DefectTaxonomyService.Normalize("Coplanarity").CanonicalClass);
    }

    [Fact]
    public void UnknownLabelTriggersTaxonomyWarning()
    {
        var normalized = DefectTaxonomyService.Normalize("Vendor Special Void");

        Assert.False(normalized.IsKnown);
        Assert.Contains("Unknown defect label", normalized.Warning);
    }

    [Fact]
    public void ModelLabelMapMissingRequiredClassBecomesConditional()
    {
        var validation = DefectTaxonomyService.ValidateModelLabels(new Dictionary<int, string>
        {
            [0] = "OK",
            [1] = "Solder Bridge",
        });

        Assert.Equal("CONDITIONAL", validation.Status);
        Assert.Contains("Missing Component", validation.MissingRequiredClasses);
        Assert.Contains(validation.Messages, message => message.Contains("missing required defect class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MesPayloadUsesConfiguredDefectCode()
    {
        var result = new AnalysisResult
        {
            LotId = "LOT-1",
            BoardProgram = "BOARD-A",
            BoardId = "SN-1",
            Verdict = "NG",
            SuggestedDefect = "Bridge",
            DifferenceScore = 22.5,
            Confidence = 0.91,
        };
        result.Defects.Add(new DefectResult
        {
            DefectType = "Solder Bridge",
            Confidence = 0.91,
            JudgmentStatus = "NG",
        });

        var upload = await TraceabilityUploadService.UploadInspectionResultAsync(result);
        var payload = JsonSerializer.Deserialize<TraceabilityPayload>(File.ReadAllText(upload.PayloadPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(payload);
        Assert.Contains("SB", payload!.DefectCodes);
        Assert.Contains("Solder Bridge(SB)=1", payload.DefectSummary);
    }

    [Theory]
    // Every row of the customer classification table (Docs/customer-specs/
    // PCBA_Defect_Classification_Table.md §3.1-§3.6) must exist as a labelling class.
    [InlineData("Solder Bridge", "Critical", "AOI / Visual")]
    [InlineData("Insufficient Solder", "Major", "AOI / 3D")]
    [InlineData("Excess Solder", "Major", "AOI")]
    [InlineData("Cold Joint", "Major", "Visual")]
    [InlineData("Poor Wetting", "Major", "AOI")]
    [InlineData("Solder Crack", "Major", "Visual")]
    [InlineData("Solder Ball", "Minor", "AOI")]
    [InlineData("Fillet Shape Defect", "Minor", "AOI")]
    [InlineData("Missing Component", "Critical", "AOI")]
    [InlineData("Misalignment", "Major", "AOI")]
    [InlineData("Tombstone", "Major", "AOI")]
    [InlineData("Polarity Error", "Critical", "AOI / Visual")]
    [InlineData("Rotation Error", "Major", "AOI")]
    [InlineData("Bent Lead", "Major", "AOI / Visual")]
    [InlineData("Damaged Component", "Major", "Visual")]
    [InlineData("Paste Misalignment", "Major", "SPI / AOI")]
    [InlineData("Paste Insufficient", "Major", "SPI")]
    [InlineData("Paste Excess", "Major", "SPI")]
    [InlineData("Paste Slump", "Major", "SPI")]
    [InlineData("Paste Void", "Minor", "X-ray")]
    [InlineData("Pad Lift", "Critical", "Visual")]
    [InlineData("Contamination", "Major", "AOI / Visual")]
    [InlineData("Scratch", "Minor", "Visual")]
    [InlineData("Silkscreen Error", "Minor", "Visual")]
    [InlineData("Copper Exposure", "Major", "Visual")]
    [InlineData("Open Circuit", "Critical", "ICT / AOI")]
    [InlineData("Trace Damage", "Major", "Visual")]
    [InlineData("Via Defect", "Major", "X-ray")]
    [InlineData("Bent Pin", "Major", "AOI / Visual")]
    [InlineData("Connector Pin Height", "Major", "3D AOI")]
    [InlineData("Partial Insertion", "Critical", "AOI / Visual")]
    [InlineData("Shield Can Gap", "Major", "Side-View AOI")]
    public void DefaultTaxonomyCarriesSpecSeverityAndDetectionMethod(string canonicalClass, string severity, string detectionMethod)
    {
        var normalized = DefectTaxonomyService.Normalize(canonicalClass);

        Assert.True(normalized.IsKnown, $"'{canonicalClass}' is missing from the default defect taxonomy.");
        Assert.Equal(canonicalClass, normalized.CanonicalClass);
        Assert.Equal(severity, normalized.Severity);
        Assert.Equal(detectionMethod, normalized.DetectionMethod);
    }

    [Fact]
    public void ShortCircuitLabelFromSpecNormalizesOntoSolderBridge()
    {
        // Spec §3.5 wording is "Short Circuit"; it is deliberately folded into Solder Bridge for
        // the optically visible case, so a ground-truth CSV using the spec's exact label must not
        // raise an unknown-label warning.
        var normalized = DefectTaxonomyService.Normalize("Short Circuit");

        Assert.True(normalized.IsKnown);
        Assert.Equal("Solder Bridge", normalized.CanonicalClass);
        Assert.Equal(string.Empty, normalized.Warning);
    }

    [Fact]
    public void ExcessSolderIsItsOwnClassAndNoLongerAliasesSolderVolume()
    {
        // Excess solder is a gross 2D-visible blob; folding it into the 3D-only Solder Volume
        // class under-claimed Stage-1 capability and lost a distinct labelling class.
        var excess = DefectTaxonomyService.Normalize("Excess Solder");

        Assert.Equal("Excess Solder", excess.CanonicalClass);
        Assert.Equal("ES", excess.MesCode);
        Assert.Equal("Solder Volume", DefectTaxonomyService.Normalize("Volume Error").CanonicalClass);
    }

    [Fact]
    public void ModelLabelIdsAreUniqueAndStableForPreExistingClasses()
    {
        // Persisted results and customer label maps reference these ids; renumbering would
        // silently re-map history.
        var byId = DefectClassCatalog.Default.ToDictionary(entry => entry.ModelLabelId, entry => entry.CanonicalClass);

        Assert.Equal(DefectClassCatalog.Default.Count, byId.Count);
        Assert.Equal("OK", byId[0]);
        Assert.Equal("Solder Bridge", byId[1]);
        Assert.Equal("Insufficient Solder", byId[2]);
        Assert.Equal("Polarity Error", byId[3]);
        Assert.Equal("Tombstone", byId[4]);
        Assert.Equal("Missing Component", byId[5]);
        Assert.Equal("Height Error", byId[6]);
        Assert.Equal("Anomaly", byId[7]);
        Assert.Equal("Solder Volume", byId[8]);
        Assert.Equal("Cold Joint", byId[9]);
        Assert.Equal("Misalignment", byId[10]);
        Assert.Equal("Connector Pin Height", byId[11]);
        Assert.Equal("3D Coplanarity", byId[12]);
        Assert.Equal("Shield Can Gap", byId[13]);
    }

    [Fact]
    public void MandatoryAoiDefectSetIsFlaggedRequired()
    {
        var required = DefectTaxonomyService.GetActiveTaxonomy().Entries
            .Where(entry => entry.IsActive && entry.IsRequired)
            .Select(entry => entry.CanonicalClass)
            .ToArray();

        foreach (var mandatory in DefectClassCatalog.MandatoryAoiDefectSet)
            Assert.Contains(mandatory, required);
    }

    [Fact]
    public void OtherMachineTypeClassesAreCataloguedButNotOfferedAsInspectableRoiTypes()
    {
        // SPI/X-ray classes must be labellable and MES-codable, but must never appear where the
        // operator would read them as something this software inspects for.
        var all = DefectTaxonomyService.ActiveCanonicalClasses();
        var inspectable = DefectTaxonomyService.InspectableCanonicalClasses();

        foreach (var outOfScope in new[] { "Paste Void", "Paste Slump", "Via Defect" })
        {
            Assert.Contains(outOfScope, all);
            Assert.DoesNotContain(outOfScope, inspectable);
        }

        Assert.Contains("Missing Component", inspectable);
    }

    [Fact]
    public void ModelLabelMapClaimingHardwareDependentClassIsFlagged()
    {
        var validation = DefectTaxonomyService.ValidateModelLabels(new Dictionary<int, string>
        {
            [0] = "OK",
            [1] = "3D Coplanarity",
            [2] = "Shield Can Gap",
        });

        Assert.Equal("CONDITIONAL", validation.Status);
        Assert.Contains("3D Coplanarity", validation.HardwareDependentClasses);
        Assert.Contains("Shield Can Gap", validation.HardwareDependentClasses);
        Assert.Contains(validation.Messages, message => message.Contains("3D acquisition hardware", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Messages, message => message.Contains("side-view acquisition path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageOnlyDetectableClassesAreNotFlaggedAsHardwareDependent()
    {
        var validation = DefectTaxonomyService.ValidateModelLabels(new[] { "OK", "Missing Component", "Solder Bridge" });

        Assert.Empty(validation.HardwareDependentClasses);
    }

    [Fact]
    public void TaxonomyCsvRoundTripsSeverityAndDetectionMethod()
    {
        var exported = DefectTaxonomyService.ExportCsv(Path.Combine(_root, "taxonomy.csv"));
        var header = File.ReadLines(exported).First();

        Assert.Contains("severity", header, StringComparison.Ordinal);
        Assert.Contains("detection_method", header, StringComparison.Ordinal);
        Assert.Contains("detection_capability", header, StringComparison.Ordinal);

        var imported = DefectTaxonomyService.ImportCsv(exported, UserRole.Admin, "Admin01 [Admin]");
        var shieldCanGap = imported.Entries.Single(entry => entry.CanonicalClass == "Shield Can Gap");

        Assert.Equal("Major", shieldCanGap.Severity);
        Assert.Equal("Side-View AOI", shieldCanGap.DetectionMethod);
    }

    [Fact]
    public void TaxonomyCsvImportRejectsUnknownSeverity()
    {
        var path = Path.Combine(_root, "bad-severity.csv");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path,
            "canonical_class,customer_label,model_label_id,mes_code,is_required,severity,detection_method,aliases\n" +
            "Solder Bridge,Solder Bridge,1,SB,true,Catastrophic,AOI,\n");

        var error = Assert.Throws<InvalidDataException>(
            () => DefectTaxonomyService.ImportCsv(path, UserRole.Admin, "Admin01 [Admin]"));

        Assert.Contains("Catastrophic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationUpgradesAnAlreadySeededDefaultTaxonomyInPlace()
    {
        // Simulates an existing install whose stored default taxonomy predates the full
        // classification table: it must gain the new classes without an operator re-import.
        AoiDatabase.Initialize();
        AoiDatabase.SaveDefectTaxonomySnapshot(new DefectTaxonomySnapshot
        {
            Taxonomy = new DefectTaxonomyRecord
            {
                TaxonomyId = DefectTaxonomyService.DefaultTaxonomyId,
                Name = "Default AOI Defect Taxonomy",
                IsActive = true,
            },
            Entries =
            {
                new DefectTaxonomyEntry
                {
                    TaxonomyId = DefectTaxonomyService.DefaultTaxonomyId,
                    CanonicalClass = "Solder Bridge",
                    CustomerLabel = "Solder Bridge",
                    ModelLabelId = 1,
                    IsRequired = true,
                },
            },
        }, "SYSTEM");

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AoiDatabase.DatabasePath }.ToString());
        connection.Open();
        AoiDatabase.UpgradeDefaultDefectTaxonomy(connection);

        var upgraded = DefectTaxonomyService.GetActiveTaxonomy();
        Assert.Equal(DefectClassCatalog.Default.Count, upgraded.Entries.Count);
        Assert.Contains(upgraded.Entries, entry => entry.CanonicalClass == "Paste Void");
        Assert.Equal("Critical", upgraded.Entries.Single(entry => entry.CanonicalClass == "Solder Bridge").Severity);
    }

    [Fact]
    public void CatalogueRefreshRestoresAliasesAddedAfterEarlierMigrations()
    {
        // Simulates an install whose default taxonomy was rewritten by migration 31 before the
        // catalogue gained Height Error's "Height Anomaly" alias: the demo dataset's
        // 'height_anomaly' label must normalize again after the migration-32 refresh.
        AoiDatabase.Initialize();
        var stale = DefectTaxonomyService.CreateDefaultTaxonomy();
        stale.Aliases.RemoveAll(alias => alias.Alias == "Height Anomaly");
        AoiDatabase.SaveDefectTaxonomySnapshot(stale, "SYSTEM");
        Assert.False(DefectTaxonomyService.Normalize("height_anomaly").IsKnown);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AoiDatabase.DatabasePath }.ToString());
        connection.Open();
        AoiDatabase.UpgradeDefaultDefectTaxonomy(connection);

        var normalized = DefectTaxonomyService.Normalize("height_anomaly");
        Assert.True(normalized.IsKnown);
        Assert.Equal("Height Error", normalized.CanonicalClass);
    }

    [Fact]
    public void MigrationLeavesCustomerImportedTaxonomiesAlone()
    {
        AoiDatabase.Initialize();
        AoiDatabase.SaveDefectTaxonomySnapshot(new DefectTaxonomySnapshot
        {
            Taxonomy = new DefectTaxonomyRecord { TaxonomyId = "taxonomy-customer-a", Name = "Customer A", IsActive = true },
            Entries =
            {
                new DefectTaxonomyEntry { TaxonomyId = "taxonomy-customer-a", CanonicalClass = "Customer Special", ModelLabelId = 1 },
            },
        }, "Admin01 [Admin]");

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AoiDatabase.DatabasePath }.ToString());
        connection.Open();
        AoiDatabase.UpgradeDefaultDefectTaxonomy(connection);

        var active = DefectTaxonomyService.GetActiveTaxonomy();
        Assert.Equal("taxonomy-customer-a", active.Taxonomy.TaxonomyId);
        Assert.Single(active.Entries);
        Assert.Equal("Customer Special", active.Entries[0].CanonicalClass);
    }

    [Fact]
    public void EveryCataloguedClassHasAnHonestCapabilityStatement()
    {
        // A class may only be catalogued if the app can also state what it takes to detect it.
        foreach (var definition in DefectClassCatalog.Default)
        {
            Assert.NotNull(DefectDetectionCapability.Find(definition.CanonicalClass));
            Assert.False(
                string.IsNullOrWhiteSpace(DefectDetectionCapability.RequirementSummary(definition.CanonicalClass)),
                $"'{definition.CanonicalClass}' has no capability statement.");
            Assert.True(
                DefectSeverityLevels.IsKnown(definition.Severity),
                $"'{definition.CanonicalClass}' has severity '{definition.Severity}'.");
        }
    }

    [Fact]
    public void InitializeCreatesDefectTaxonomyTables()
    {
        AoiDatabase.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AoiDatabase.DatabasePath }.ToString());
        connection.Open();
        Assert.True(AoiDatabase.TableExists(connection, "DefectTaxonomies"));
        Assert.True(AoiDatabase.TableExists(connection, "DefectTaxonomyEntries"));
        Assert.True(AoiDatabase.TableExists(connection, "DefectClassAliases"));
        Assert.True(AoiDatabase.TableExists(connection, "MesDefectCodeMappings"));
    }
}
