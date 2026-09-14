using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class FactoryReadinessService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<FactoryReadinessReport> EvaluateAsync(
        FactoryReadinessCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Evaluate(criteria);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken).ConfigureAwait(false);
        return report;
    }

    public static FactoryReadinessReport Evaluate(FactoryReadinessCriteria? criteria = null)
    {
        criteria ??= CriteriaForProfile(DeploymentProfileSettingsService.Load());
        criteria = ApplyOperatingModeCriteria(criteria, OperatingModeSettingsService.Load());
        AoiDatabase.Initialize();

        var report = new FactoryReadinessReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            DeploymentProfile = criteria.DeploymentProfile.ToString(),
            Scope = ScopeFor(criteria.DeploymentProfile),
            KnownLimitations = CustomerValidationReportContext.DefaultPrototypeLimitations.ToList(),
        };

        var operatingMode = OperatingModeSettingsService.Load();
        var diagnostics = SystemDiagnosticService.RunDiagnostics();
        var latestPackage = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestRun = AoiDatabase.GetLatestBatchTestRun();
        var latestFalseCall = AoiDatabase.GetLatestFalseCallReductionRun();
        var latestVerifications = AoiDatabase.GetExportVerifications(100);
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var mes = MesSpoolService.EvaluateReadiness(new MesReadinessCriteria
        {
            FailOnPendingQueue = criteria.RequireNoPendingMesQueueForProductionMode && !criteria.Stage1Only,
            FailOnFailedQueue = true,
            RequirePassingTraceabilityTest = criteria.RequirePassingTraceabilityTest && !criteria.Stage1Only,
        });
        var centralSync = CentralSyncService.EvaluateReadiness();

        AddOperatingMode(report, operatingMode);
        AddBuildTest(report);
        AddDiagnosticCategory(report, "Database health", diagnostics, "Database");
        AddImageVault(report, diagnostics);
        AddModelReadiness(report, criteria);
        AddValidationPackage(report, criteria, latestPackage, latestRun);
        AddFalseCall(report, criteria, latestFalseCall);
        AddDatasetQuality(report, criteria, latestPackage);
        AddLatency(report, criteria);
        AddExportVerification(report, criteria, latestVerifications);
        AddSoakTest(report, criteria);
        AddCamera(report, criteria, camera);
        AddProfile3D(report, criteria, profile3D);
        AddLighting(report, criteria, lighting);
        AddRobot(report, criteria, robot);
        AddMes(report, criteria, mes);
        AddCentralSync(report, criteria, centralSync);
        AddAlarmEvents(report);
        AddPilotIssues(report);
        AddAuthenticationMode(report, operatingMode);
        AddSecurityAudit(report);
        AddKnownLimitations(report, criteria);

        report.BlockingIssues.AddRange(report.Categories
            .Where(category => category.Status == "No-Go")
            .Select(category => $"{category.Name}: {category.Evidence}"));
        report.Warnings.AddRange(report.Categories
            .Where(category => category.Status == "Conditional")
            .Select(category => $"{category.Name}: {category.Evidence}"));
        report.UnmetCriteria.AddRange(report.BlockingIssues.Concat(report.Warnings));
        report.RecommendedNextActions.AddRange(report.Categories
            .Where(category => category.Status != "Go" && !string.IsNullOrWhiteSpace(category.NextAction))
            .Select(category => $"{category.Name}: {category.NextAction}")
            .Distinct(StringComparer.OrdinalIgnoreCase));

        report.OverallStatus = report.BlockingIssues.Count > 0
            ? FactoryReadinessOverallStatus.NoGo.ToString()
            : report.Warnings.Count > 0
                ? FactoryReadinessOverallStatus.Conditional.ToString()
                : FactoryReadinessOverallStatus.Go.ToString();
        return report;
    }

    public static FactoryReadinessCriteria CriteriaForProfile(DeploymentProfile profile)
        => profile switch
        {
            DeploymentProfile.Stage1ImageValidation => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = true,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireFalseCallEvidence = false,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage2CameraPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireProfile3DAcceptance = true,
                RequireLightingAcceptance = true,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage3RobotPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireProfile3DAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage4MesPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireProfile3DAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireNoPendingMesQueueForProductionMode = true,
                RequireNoExportVerificationErrors = true,
                RequirePassingTraceabilityTest = true,
            },
            DeploymentProfile.FullFactoryAutomation => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = true,
                RequireFalseCallEvidence = true,
                RequireCameraAcceptance = true,
                RequireProfile3DAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireRealHardwareAcceptance = true,
                RequireNoPendingMesQueueForProductionMode = true,
                RequireSoakTestEvidenceForFactoryPilot = true,
                RequireNoExportVerificationErrors = true,
                RequirePassingTraceabilityTest = true,
                RequireCentralSyncEvidence = true,
                WarnWhenCentralSyncDisabled = true,
            },
            _ => new FactoryReadinessCriteria { DeploymentProfile = DeploymentProfile.Stage1ImageValidation },
        };

    private static FactoryReadinessCriteria ApplyOperatingModeCriteria(FactoryReadinessCriteria source, OperatingMode mode)
    {
        var criteria = new FactoryReadinessCriteria
        {
            DeploymentProfile = source.DeploymentProfile,
            Stage1Only = source.Stage1Only,
            RequireSuccessfulLatestValidationPackage = source.RequireSuccessfulLatestValidationPackage,
            RequireDatasetQualityEvidence = source.RequireDatasetQualityEvidence,
            RequireProductionModel = source.RequireProductionModel,
            MaximumFalseCallRate = source.MaximumFalseCallRate,
            RequireFalseCallEvidence = source.RequireFalseCallEvidence,
            RequireNoExportVerificationErrors = source.RequireNoExportVerificationErrors,
            RequireNoPendingMesQueueForProductionMode = source.RequireNoPendingMesQueueForProductionMode,
            RequireCameraAcceptance = source.RequireCameraAcceptance,
            RequireProfile3DAcceptance = source.RequireProfile3DAcceptance,
            RequireLightingAcceptance = source.RequireLightingAcceptance,
            RequireRobotAcceptance = source.RequireRobotAcceptance,
            RequireRealHardwareAcceptance = source.RequireRealHardwareAcceptance,
            RequireSoakTestEvidenceForFactoryPilot = source.RequireSoakTestEvidenceForFactoryPilot,
            RequirePassingTraceabilityTest = source.RequirePassingTraceabilityTest,
            RequireCentralSyncEvidence = source.RequireCentralSyncEvidence,
            WarnWhenCentralSyncDisabled = source.WarnWhenCentralSyncDisabled,
        };

        if (mode == OperatingMode.Production)
        {
            criteria.Stage1Only = false;
            criteria.RequireSuccessfulLatestValidationPackage = true;
            criteria.RequireDatasetQualityEvidence = true;
            criteria.RequireProductionModel = true;
            criteria.RequireFalseCallEvidence = true;
            criteria.RequireNoExportVerificationErrors = true;
            criteria.RequireNoPendingMesQueueForProductionMode = true;
            criteria.RequireCameraAcceptance = true;
            criteria.RequireProfile3DAcceptance = true;
            criteria.RequireLightingAcceptance = true;
            criteria.RequireRobotAcceptance = true;
            criteria.RequireRealHardwareAcceptance = true;
            criteria.RequireSoakTestEvidenceForFactoryPilot = true;
            criteria.RequirePassingTraceabilityTest = true;
            criteria.RequireCentralSyncEvidence = true;
            criteria.WarnWhenCentralSyncDisabled = true;
        }

        if (mode == OperatingMode.Pilot)
        {
            criteria.RequireSuccessfulLatestValidationPackage = true;
            criteria.RequireDatasetQualityEvidence = true;
            criteria.RequireNoExportVerificationErrors = true;
        }

        return criteria;
    }

    public static string DisplayName(DeploymentProfile profile)
        => profile switch
        {
            DeploymentProfile.Stage1ImageValidation => "Stage 1 Customer Data Validation",
            DeploymentProfile.Stage2CameraPilot => "Stage 2 Camera Pilot",
            DeploymentProfile.Stage3RobotPilot => "Stage 3 Robot Cell Pilot",
            DeploymentProfile.Stage4MesPilot => "Stage 4 MES Traceability Pilot",
            DeploymentProfile.FullFactoryAutomation => "Full Factory Automation",
            _ => profile.ToString(),
        };

    public static FactoryReadinessPackageResult ExportGoNoGoPackage(
        FactoryReadinessCriteria? criteria = null,
        string? outputRoot = null)
    {
        var report = Evaluate(criteria);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "factory_readiness")
            : outputRoot.Trim();
        var packageFolder = EnsureUniqueFolder(Path.Combine(root, $"factory_readiness_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(packageFolder);

        var jsonPath = Path.Combine(packageFolder, "factory_readiness_summary.json");
        var htmlPath = Path.Combine(packageFolder, "factory_readiness_summary.html");
        var pdfPath = Path.Combine(packageFolder, "factory_readiness_summary.pdf");
        var readmePath = Path.Combine(packageFolder, "README.txt");
        var clientDemoGate = ClientDemoReadinessGateService.Evaluate(criteria?.DeploymentProfile);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Factory Readiness Go/No-Go Summary");
        File.WriteAllText(readmePath, BuildReadme(report), Encoding.UTF8);
        File.WriteAllText(Path.Combine(packageFolder, "client_demo_readiness_gate.json"), JsonSerializer.Serialize(clientDemoGate, JsonOptions), Encoding.UTF8);
        File.WriteAllText(Path.Combine(packageFolder, "client_demo_readiness_gate.html"), ClientDemoReadinessGateService.BuildHtml(clientDemoGate), Encoding.UTF8);
        StandardsTraceabilityService.ExportToFolder(packageFolder, profile: criteria?.DeploymentProfile, recordExport: false);

        CopyLatestValidationManifest(packageFolder);
        WriteLatestExportVerification(packageFolder);
        CopyLatestBuildTestEvidence(packageFolder);
        WriteLatestAcceptanceReports(packageFolder);
        FactoryAcceptanceChecklistService.ExportToFolder(criteria?.DeploymentProfile ?? DeploymentProfileSettingsService.Load(), packageFolder);
        WritePackageManifest(packageFolder, report);

        ExportVerificationService.RecordVerifiedExport("FactoryReadinessPackage", packageFolder, report.OverallStatus == "NoGo" ? "WARN" : "OK");
        AoiDatabase.RecordAuditEvent("FACTORY_READINESS_EXPORT", $"Factory readiness Go/No-Go package exported: {Path.GetFileName(packageFolder)}; status={report.OverallStatus}.", relatedPath: packageFolder);
        return new FactoryReadinessPackageResult(packageFolder, htmlPath, jsonPath, readmePath);
    }

    public static string BuildHtml(FactoryReadinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Factory Readiness Go/No-Go</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b;background:#f7f9fb}.hero{background:#fff;border:1px solid #d7e0e8;padding:18px;margin-bottom:16px}.Go{color:#176b3a}.Conditional{color:#8a5a00}.No-Go{color:#a12626}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #d7e0e8;padding:8px;text-align:left;vertical-align:top}th{background:#eef3f7}li{margin:4px 0}</style></head><body>");
        sb.AppendLine("<div class=\"hero\">");
        sb.AppendLine($"<h1>Factory Readiness Go/No-Go Summary</h1><p><strong class=\"{Css(report.OverallStatus)}\">{Html(report.OverallStatus)}</strong> for {Html(report.Scope)}<br>Deployment profile: {Html(report.DeploymentProfile)}<br>Generated UTC: {report.GeneratedAtUtc:O}</p>");
        if (report.DeploymentProfile == DeploymentProfile.Stage1ImageValidation.ToString() && report.OverallStatus != FactoryReadinessOverallStatus.NoGo.ToString())
            sb.AppendLine("<p><strong>Only Stage 1 is ready.</strong> Production camera, robot, lighting, MES, and full automation readiness are not implied by this profile.</p>");
        sb.AppendLine("<p>This package separates Stage 1 validation evidence from full factory readiness. Simulated camera, lighting, robot, or MES evidence is not real production equipment validation.</p>");
        sb.AppendLine("</div>");
        AppendList(sb, "Blocking Issues", report.BlockingIssues);
        AppendList(sb, "Warnings", report.Warnings);
        AppendList(sb, "Unmet Criteria", report.UnmetCriteria);
        AppendList(sb, "Recommended Next Actions", report.RecommendedNextActions);
        sb.AppendLine("<h2>Readiness Categories</h2><table><tr><th>Category</th><th>Status</th><th>Evidence</th><th>Next Action</th></tr>");
        foreach (var category in report.Categories)
            sb.AppendLine($"<tr><td>{Html(category.Name)}</td><td class=\"{Css(category.Status)}\">{Html(category.Status)}</td><td>{Html(category.Evidence)}</td><td>{Html(category.NextAction)}</td></tr>");
        sb.AppendLine("</table>");
        AppendList(sb, "Known Limitations", report.KnownLimitations);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void AddBuildTest(FactoryReadinessReport report)
    {
        var summary = BuildTestEvidenceService.GetSummary();
        if (summary.Latest is null)
        {
            Add(report, "Build/Test status", "Conditional", "No local build/test evidence artifact is recorded. The dashboard cannot independently prove the latest repository build from runtime data.", "Run hygiene, restore, build, test, and publish validation, then import the generated build/test evidence JSON.");
            return;
        }

        var latest = summary.Latest;
        var commit = string.IsNullOrWhiteSpace(latest.GitCommit) ? "unknown" : latest.GitCommit;
        var status = summary.IsPassing ? "Go" : "No-Go";
        Add(
            report,
            "Build/Test status",
            status,
            $"Latest evidence generated {latest.GeneratedAtUtc:O} on {latest.MachineName}; commit={commit}; configuration={latest.Configuration}; hygiene={latest.HygieneStatus}; restore={latest.RestoreStatus}; build={latest.BuildStatus}; test={latest.TestStatus}; publishValidation={latest.PublishValidationStatus}; TRX={latest.TestResultPath}; evidence={latest.EvidencePath}.",
            status == "Go" ? "No action required." : "Fix failing command(s), rerun the full validation chain, and import passing build/test evidence.");
    }

    private static void AddOperatingMode(FactoryReadinessReport report, OperatingMode mode)
    {
        var settings = OperatingModeSettingsService.LoadSettings();
        switch (mode)
        {
            case OperatingMode.Demo:
                Add(
                    report,
                    "Operating mode",
                    "Conditional",
                    "Demo Mode active. Demo role selector, sample/fallback data, and simulated sources are allowed. Do not present Demo Mode evidence as customer pilot or production readiness.",
                    "Switch to Pilot or Production before customer review or factory deployment.");
                return;
            case OperatingMode.Pilot:
                Add(
                    report,
                    "Operating mode",
                    "Conditional",
                    $"Pilot Mode active. Demo rows are hidden by default; customer dataset preflight and readiness-package evidence are required. Simulated hardware is allowed only when clearly labeled. Pilot authentication waiver active={OperatingModeSettingsService.HasActivePilotAuthenticationWaiver()}; waivedBy={settings.PilotAuthenticationWaivedBy}; expires={settings.PilotAuthenticationWaiverExpiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "none"}.",
                    "Use LocalUsers/MES authentication or record an explicit audited pilot waiver; complete customer dataset preflight and export readiness evidence.");
                return;
            case OperatingMode.Production:
                Add(
                    report,
                    "Operating mode",
                    "Go",
                    "Production Mode active. Demo/fallback rows are not allowed; production model, real hardware, MES/central sync, export verification, and signoff gates are enforced.",
                    "No action required for operating-mode selection; resolve any failing production readiness categories.");
                return;
        }
    }

    private static void AddDiagnosticCategory(FactoryReadinessReport report, string name, SystemDiagnosticReport diagnostics, string diagnosticCategory)
    {
        var checks = diagnostics.Checks.Where(check => check.Category.Equals(diagnosticCategory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (checks.Length == 0)
        {
            Add(report, name, "Conditional", $"No {diagnosticCategory} diagnostic check was available.", "Run system diagnostics.");
            return;
        }

        var status = checks.Any(check => check.Status == DiagnosticStatus.Error)
            ? "No-Go"
            : checks.Any(check => check.Status == DiagnosticStatus.Warn)
                ? "Conditional"
                : "Go";
        Add(report, name, status, string.Join(" ", checks.Select(check => $"{check.Name}: {check.Message}")), checks.FirstOrDefault(check => check.Status != DiagnosticStatus.OK)?.Remediation ?? "No action required.");
    }

    private static void AddImageVault(FactoryReadinessReport report, SystemDiagnosticReport diagnostics)
    {
        var rows = AoiDatabase.GetDatabaseHealthRows();
        var imageCount = rows.FirstOrDefault(row => row.Table == "Images")?.Count ?? "0";
        var checks = diagnostics.Checks.Where(check => check.Category == "Image Vault").ToArray();
        var status = checks.Any(check => check.Status == DiagnosticStatus.Error)
            ? "No-Go"
            : imageCount == "0"
                ? "Conditional"
                : "Go";
        Add(report, "Image vault health", status, $"Image records: {imageCount}. {string.Join(" ", checks.Select(check => check.Message))}", imageCount == "0" ? "Import or index validation images before factory/customer evidence review." : "No action required.");
    }

    private static void AddModelReadiness(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var status = InspectionModelConfigurationService.GetStatus(configuration);
        var text = InspectionModelConfigurationService.GetStatusText(status);
        if (!criteria.RequireProductionModel && status == InspectionEngineStatus.PrototypeEngine)
        {
            Add(report, "Active model readiness", "Conditional", PlainLanguageGlossaryService.EvidenceMissing(
                "The simple Pixel Difference engine is active.",
                "This is acceptable for Stage 1 image evidence, but it is not an accepted AI model for factory automation.",
                $"{PlainLanguageGlossaryService.Explain("ONNXModel")} Run model acceptance before making factory accuracy claims."),
                "Use an accepted inspection model before claiming full factory accuracy.");
            return;
        }

        if (status == InspectionEngineStatus.MlModelReady)
        {
            var activeModel = ModelRegistryService.GetActiveModel();
            var acceptance = AoiDatabase.GetLatestPassingProductionModelAcceptance(configuration.ActiveModelId);
            var latestAcceptance = AoiDatabase.GetLatestModelAcceptanceRun(configuration.ActiveModelId);
            var hasPassAcceptance = latestAcceptance is not null &&
                string.Equals(latestAcceptance.Status, "PASS", StringComparison.OrdinalIgnoreCase);
            if (activeModel is not null && !string.IsNullOrWhiteSpace(activeModel.DeploymentWaiverReason))
            {
                var waiverExpired = activeModel.WaiverExpiresAtUtc is not null && activeModel.WaiverExpiresAtUtc.Value.ToUniversalTime() <= DateTime.UtcNow;
                Add(
                    report,
                    "Active model readiness",
                    waiverExpired ? "No-Go" : "Conditional",
                    PlainLanguageGlossaryService.EvidenceMissing(
                        $"The active AI model is running under an Admin waiver by {activeModel.DeploymentWaivedBy}. Risk={activeModel.DeploymentWaiverRiskClassification}; reason={activeModel.DeploymentWaiverReason}; expires={activeModel.WaiverExpiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "not recorded"}.",
                        "A waiver means the normal evidence gate has not been fully completed.",
                        "Record PASS model acceptance, release package, and lifecycle approval."),
                    waiverExpired
                        ? "Waiver has expired. Retire or replace the model, or record a new Admin waiver after review."
                        : "Active waiver downgrades readiness to Conditional at best. Complete PASS model acceptance, release packaging, and approved lifecycle promotion before Go readiness claims.");
                return;
            }

            if (criteria.RequireProductionModel)
            {
                if (activeModel is null)
                {
                    Add(report, "Active model readiness", "No-Go", PlainLanguageGlossaryService.EvidenceMissing(
                        $"The selected AI model has no matching model registry record. ModelId={configuration.ActiveModelId}; version={configuration.ModelVersion}.",
                        "The app cannot prove which model file is approved for inspection.",
                        "Register the model and complete lifecycle promotion."),
                        "Register the model and complete lifecycle promotion before full factory automation.");
                    return;
                }

                if (!hasPassAcceptance)
                {
                    Add(report, "Active model readiness", "No-Go", PlainLanguageGlossaryService.EvidenceMissing(
                        $"The active AI model can load, but no PASS model acceptance run was found. ModelId={configuration.ActiveModelId}; version={configuration.ModelVersion}.",
                        $"{PlainLanguageGlossaryService.Explain("AcceptanceTest")} A load check alone does not prove customer validation results.",
                        "Run model acceptance on customer validation data and save the release package."),
                        "Run model acceptance on customer validation data, create a release package, and promote the PASS run to production candidate.");
                    return;
                }

                var lifecycleOk = activeModel.LifecycleState == ModelLifecycleState.Deployed ||
                    activeModel.LifecycleState == ModelLifecycleState.ProductionCandidate;
                if (!lifecycleOk)
                {
                    Add(report, "Active model readiness", "No-Go", PlainLanguageGlossaryService.EvidenceMissing(
                        $"The active model lifecycle state is {activeModel.LifecycleState}. Latest acceptance={activeModel.LatestAcceptanceStatus}; releasePackage={activeModel.LatestReleasePackagePath}.",
                        "The model has not reached an approved lifecycle state for the selected readiness profile.",
                        "Promote a PASS acceptance run to Production Candidate or deploy through lifecycle approval."),
                        "Promote a PASS model acceptance run to Production Candidate or deploy the model through lifecycle approval.");
                    return;
                }
            }

            var evidenceRun = acceptance ?? latestAcceptance;
            Add(report, "Active model readiness", "Go", evidenceRun is null
                ? $"Active model is runtime-ready for non-production profile. ModelId={configuration.ActiveModelId}; version={configuration.ModelVersion}; lifecycle={activeModel?.LifecycleState.ToString() ?? "unregistered"}."
                : $"Active model has PASS acceptance run {evidenceRun.Id}. ModelId={configuration.ActiveModelId}; version={configuration.ModelVersion}; lifecycle={activeModel?.LifecycleState.ToString() ?? "unregistered"}; releasePackage={activeModel?.LatestReleasePackagePath ?? string.Empty}; dataset={evidenceRun.DatasetName}.",
                "No action required.");
        }
        else
            Add(report, "Active model readiness", criteria.RequireProductionModel ? "No-Go" : "Conditional", PlainLanguageGlossaryService.EvidenceMissing(
                $"The active inspection model is not ready for this profile: {text}.",
                "Operators need a validated inspection engine before relying on factory results.",
                "Register and validate the AI model, or keep the review scoped to Stage 1 prototype evidence."),
                "Register and validate the inspection model or keep the review scoped to Stage 1 prototype evidence.");
    }

    private static void AddValidationPackage(FactoryReadinessReport report, FactoryReadinessCriteria criteria, ValidationPackageRecord? package, BatchTestRunRecord? run)
    {
        if (package is null)
        {
            Add(report, "Stage 1 validation package status", criteria.RequireSuccessfulLatestValidationPackage ? "No-Go" : "Conditional", "No Stage 1 validation package has been recorded.", "Create a Stage 1 customer validation package.");
            return;
        }

        var packageOk = package.AcceptanceStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
            package.AcceptanceStatus.Equals("CONDITIONAL", StringComparison.OrdinalIgnoreCase) && criteria.Stage1Only;
        var status = packageOk ? (package.AcceptanceStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? "Go" : "Conditional") : "No-Go";
        Add(report, "Stage 1 validation package status", status, $"Latest package {package.PackageId}: acceptance={package.AcceptanceStatus}; run={package.RunId?.ToString(CultureInfo.InvariantCulture) ?? "none"}; latest batch images={run?.TotalImages ?? 0}; false-call-rate={run?.FalseCallRate.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a"}.", status == "Go" ? "No action required." : "Review validation package failures and regenerate after fixes.");
    }

    private static void AddFalseCall(FactoryReadinessReport report, FactoryReadinessCriteria criteria, FalseCallReductionRun? run)
    {
        if (run?.Recommendation?.Point is not { } point)
        {
            Add(report, "False-call reduction status", criteria.RequireFalseCallEvidence ? "No-Go" : "Conditional", PlainLanguageGlossaryService.EvidenceMissing(
                "No false-call reduction recommendation is available.",
                PlainLanguageGlossaryService.Explain("FalseCall"),
                "Run false-call reduction on customer-labeled OK/NG data."),
                "Run False Call Reduction Workbench on customer-labeled Stage 1 data.");
            return;
        }

        var status = run.Recommendation.Status == "VALID" && point.FalseCallRate <= criteria.MaximumFalseCallRate
            ? "Go"
            : run.Recommendation.Status == "INVALID"
                ? "No-Go"
                : "Conditional";
        Add(report, "False-call reduction status", status, $"{PlainLanguageGlossaryService.Explain("FalseCall")} {PlainLanguageGlossaryService.Explain("PossibleEscape")} Recommendation={run.Recommendation.Status}; threshold={point.DifferenceThreshold:F2}; good-board false-call rate={point.FalseCallRate:P1}; possible escape rate={point.PossibleEscapeRate:P1}; manual review rate={point.ReviewRate:P1}.", status == "Go" ? "No action required." : "Tune thresholds with enough labeled OK/NG data and review possible escapes before reducing manual review.");
    }

    private static void AddDatasetQuality(FactoryReadinessReport report, FactoryReadinessCriteria criteria, ValidationPackageRecord? package)
    {
        var manifest = TryReadManifest(package?.ManifestPath);
        if (manifest is null)
        {
            Add(report, "Dataset quality status", criteria.RequireDatasetQualityEvidence ? "No-Go" : "Conditional", PlainLanguageGlossaryService.EvidenceMissing(
                "No validation manifest with dataset-quality summary is available.",
                "The app cannot prove which images and labels were used for validation.",
                "Generate a validation package from a labeled dataset."),
                "Generate a validation package from a labeled dataset.");
            return;
        }

        var preflightStatus = string.IsNullOrWhiteSpace(manifest.DatasetPreflightStatus)
            ? "CONDITIONAL"
            : manifest.DatasetPreflightStatus;
        var datasetStatus = manifest.DatasetQualitySummary.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? "Go"
            : manifest.DatasetQualitySummary.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
                ? "No-Go"
                : "Conditional";
        var preflightGate = preflightStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? "Go"
            : preflightStatus.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
                ? "No-Go"
                : "Conditional";
        var status = datasetStatus == "No-Go" || preflightGate == "No-Go"
            ? "No-Go"
            : datasetStatus == "Conditional" || preflightGate == "Conditional"
                ? "Conditional"
                : "Go";
        var preflightEvidence = preflightStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? "preflight=PASS"
            : $"preflight={preflightStatus}; failures={manifest.DatasetPreflightFailures.Count}; warnings={manifest.DatasetPreflightWarnings.Count}";
        Add(report, "Dataset quality status", status, $"Dataset check={manifest.DatasetQualitySummary.Status}; {preflightEvidence}; total images={manifest.DatasetQualitySummary.TotalImages}; images with known labels={manifest.DatasetQualitySummary.KnownGroundTruthImages}; good boards={manifest.DatasetQualitySummary.OkImages}; defect boards={manifest.DatasetQualitySummary.NgImages}.", status == "Go" ? "No action required." : "Run Dataset Preflight, balance OK/NG labels, reduce UNKNOWN labels, and include required golden images.");
    }

    private static void AddExportVerification(FactoryReadinessReport report, FactoryReadinessCriteria criteria, IReadOnlyList<ExportVerificationRecord> verifications)
    {
        if (verifications.Count == 0)
        {
            Add(report, "Export verification status", criteria.RequireNoExportVerificationErrors ? "No-Go" : "Conditional", "No export verification records are available.", "Export and verify customer/factory evidence artifacts.");
            return;
        }

        var errors = verifications.Count(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        var warns = verifications.Count(item => item.Status.Equals("WARN", StringComparison.OrdinalIgnoreCase));
        var status = errors > 0 && criteria.RequireNoExportVerificationErrors ? "No-Go" : warns > 0 || errors > 0 ? "Conditional" : "Go";
        Add(report, "Export verification status", status, $"Latest {verifications.Count} verification record(s): errors={errors}, warnings={warns}.", status == "Go" ? "No action required." : "Re-export failed artifacts and resolve checksum/manifest/header errors.");
    }

    private static void AddLatency(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var benchmark = BenchmarkInspectionService.GetLatestBenchmark();
        if (benchmark is not null)
        {
            // The one-second criterion is the ceiling: a run recorded against a looser
            // threshold cannot satisfy this gate because its over-threshold count was
            // computed with that looser value.
            var thresholdAcceptable = benchmark.AcceptanceThresholdMs <= 1000;
            var benchmarkOk = thresholdAcceptable &&
                benchmark.CompletedCount > 0 &&
                benchmark.P95FrameToOverlayMs <= benchmark.AcceptanceThresholdMs &&
                benchmark.OverOneSecondCount == 0;
            var workload = string.IsNullOrWhiteSpace(benchmark.GoldenImagePath)
                ? "workload=no-reference (lighter path)"
                : "workload=golden-compare";
            var coldStart = $"cold-start={benchmark.ColdStartSampleCount} sample(s), max {benchmark.ColdStartMaxFrameToOverlayMs:F0} ms, over-threshold {benchmark.ColdStartOverThresholdCount}";
            var realCameraBenchmarkRequired = !criteria.Stage1Only &&
                (criteria.RequireCameraAcceptance || criteria.RequireRealHardwareAcceptance);
            if (realCameraBenchmarkRequired)
            {
                var realCameraOk = benchmarkOk && benchmark.IsRealCameraSource;
                Add(
                    report,
                    "Inspection performance benchmark",
                    realCameraOk ? "Go" : "No-Go",
                    $"Latest benchmark source={benchmark.SourceKind}; realCamera={benchmark.IsRealCameraSource}; count={benchmark.CompletedCount}; p95 frame-to-overlay={benchmark.P95FrameToOverlayMs:F0} ms; p99={benchmark.P99FrameToOverlayMs:F0} ms; max={benchmark.MaxFrameToOverlayMs:F0} ms; over-threshold={benchmark.OverOneSecondCount} (threshold={benchmark.AcceptanceThresholdMs:F0} ms); {coldStart}; {workload}; throughput={benchmark.ThroughputImagesPerMinute:F1}/min; report={benchmark.ReportFolder}.",
                    realCameraOk ? "No action required." : "Run Performance Benchmark against the active real camera source. Folder simulation evidence cannot satisfy Stage 2 or full factory real-camera latency acceptance.");
                return;
            }

            var localStatus = benchmarkOk
                ? benchmark.ColdStartOverThresholdCount > 0 ? "Conditional" : "Go"
                : "Conditional";
            Add(
                report,
                "Inspection performance benchmark",
                localStatus,
                $"Latest local benchmark source={benchmark.SourceKind}; count={benchmark.CompletedCount}; p50={benchmark.P50FrameToOverlayMs:F0} ms; p95={benchmark.P95FrameToOverlayMs:F0} ms; p99={benchmark.P99FrameToOverlayMs:F0} ms; max={benchmark.MaxFrameToOverlayMs:F0} ms; over-threshold={benchmark.OverOneSecondCount} (threshold={benchmark.AcceptanceThresholdMs:F0} ms); {coldStart}; {workload}; throughput={benchmark.ThroughputImagesPerMinute:F1}/min; report={benchmark.ReportFolder}.",
                localStatus == "Go"
                    ? "No action required."
                    : benchmarkOk
                        ? "Steady-state timing passes; cold-start sample(s) exceeded the threshold — review the cold-start figures in the benchmark report."
                        : thresholdAcceptable
                            ? "Reduce load/preprocessing/inference/overlay/persistence time and rerun the image-folder benchmark."
                            : "Benchmark was recorded against a threshold looser than 1000 ms; rerun with the default acceptance threshold.");
            return;
        }

        if (!criteria.Stage1Only && (criteria.RequireCameraAcceptance || criteria.RequireRealHardwareAcceptance))
        {
            Add(report, "Inspection performance benchmark", "No-Go", "No performance benchmark evidence has been recorded. Stage 2 and full factory readiness require a real-camera benchmark under the one-second frame-to-overlay threshold.", "Run Readiness & QA > Performance Benchmark against the active real camera source.");
            return;
        }

        var summary = InspectionLatencyService.GetRecentSummary();
        if (summary.TraceCount == 0)
        {
            Add(report, "Inspection latency trace", criteria.Stage1Only ? "Conditional" : "No-Go", "No end-to-end inspection latency traces have been recorded.", "Run main inspection or validation with latency tracing before claiming the one-second visualization target.");
            return;
        }

        var status = summary.OverOneSecondCount > 0
            ? (criteria.Stage1Only ? "Conditional" : "No-Go")
            : "Go";
        Add(
            report,
            "Inspection latency trace",
            status,
            $"Traces={summary.TraceCount}; p50 frame-to-overlay={summary.P50FrameToOverlayMs:F0} ms; p95 frame-to-overlay={summary.P95FrameToOverlayMs:F0} ms; max={summary.MaxFrameToOverlayMs:F0} ms; p95 saved-result={summary.P95FrameToSavedResultMs:F0} ms; over1s={summary.OverOneSecondCount}; warnings={string.Join(" ", summary.Warnings.Take(5))}",
            status == "Go" ? "No action required." : "Reduce preprocessing/inference/overlay/persist latency and rerun timed inspection evidence.");
    }

    private static void AddSoakTest(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var latest = AoiDatabase.GetLatestSoakTestRun();
        if (latest is null)
        {
            Add(report, "Soak test status", criteria.RequireSoakTestEvidenceForFactoryPilot ? "No-Go" : "Conditional", "No soak-test report export was found.", "Run a controlled soak test before factory pilot.");
            return;
        }

        var factoryAccepted = latest.IsCompletedFactoryEvidence;
        var status = factoryAccepted
            ? "Go"
            : criteria.RequireSoakTestEvidenceForFactoryPilot
                ? "No-Go"
                : latest.WasCanceled || latest.FailedCycles > 0 ? "Conditional" : "Go";
        Add(
            report,
            "Soak test status",
            status,
            $"Latest soak run {latest.RunId}: profile={latest.ProfileName}; source={latest.SourceKind}; realCamera={latest.IsRealCameraSource}; duration={latest.ActualDuration.TotalHours:F2} h / requested={latest.RequestedDuration.TotalHours:F2} h; iterations={latest.TotalCycles}; failures={latest.FailedCycles}; canceled={latest.WasCanceled}; cancelReason={latest.CancellationReason}; avg={latest.AverageInspectionMilliseconds:F0} ms; p95={latest.P95InspectionMilliseconds:F0} ms; max={latest.MaxInspectionMilliseconds:F0} ms; cycleP95={latest.P95TotalCycleMilliseconds:F0} ms; workingSetPeak={latest.PeakWorkingSetMegabytes:F1} MB; memoryWarnings={latest.MemoryWarnings.Count}; firstCriticalError={latest.FirstCriticalError}; over1s={latest.CountOverOneSecond}; factoryEvidenceAccepted={factoryAccepted}. Shorter or simulated profiles are pilot stability evidence only.",
            status == "Go" ? "No action required." : "Run the Factory PoC 8-hour profile to completion with real camera source evidence and no critical errors.");
    }

    private static void AddCamera(FactoryReadinessReport report, FactoryReadinessCriteria criteria, CameraAcceptanceRun? run)
    {
        var summary = CameraAcceptanceTestService.ToSummary(run);
        var required = criteria.RequireCameraAcceptance;
        var realRequired = criteria.RequireRealHardwareAcceptance && required;
        var ok = summary.AcceptanceStatus is "PASS" or "WARN" && (!realRequired || summary.IsRealHardware);
        var status = ok ? "Go" : required ? "No-Go" : "Conditional";
        Add(report, "Camera acceptance status", status, $"{PlainLanguageGlossaryService.AcceptanceBoundary("Camera")} Status={summary.Status}; result={summary.AcceptanceStatus}; real hardware={summary.IsRealHardware}; adapter={summary.AdapterName}; frames received={summary.TotalReceivedFrames}/{summary.TotalRequestedFrames}. {string.Join(" ", summary.Messages)}", ok ? "No action required." : "Run camera evidence check with the required factory camera profile.");
    }

    private static void AddProfile3D(FactoryReadinessReport report, FactoryReadinessCriteria criteria, Profile3DAcceptanceRun? run)
    {
        if (run is null)
        {
            Add(report, "3D profile acceptance status", criteria.RequireProfile3DAcceptance ? "No-Go" : "Conditional", PlainLanguageGlossaryService.EvidenceMissing(
                "No 3D height/profile evidence check has been recorded.",
                "Height or coplanarity inspection cannot be claimed without a recorded 3D source check.",
                "Run the 3D profile evidence check when height inspection is in scope."),
                "Run 3D profile evidence check when height/coplanarity inspection is part of the deployment profile.");
            return;
        }

        var realOk = !criteria.RequireRealHardwareAcceptance || !run.IsSimulated;
        var ok = run.Status is "PASS" or "WARN" && realOk;
        Add(
            report,
            "3D profile acceptance status",
            ok ? "Go" : criteria.RequireProfile3DAcceptance ? "No-Go" : "Conditional",
            $"{PlainLanguageGlossaryService.AcceptanceBoundary("3D height")} Status={run.Status}; readiness={run.FactoryReadinessStatus}; source={run.SourceName}; simulated={run.IsSimulated}; frame={run.FrameId}; dimensions={run.Width}x{run.Height}; invalid heights={run.NaNHeightCount + run.MissingHeightCount}. {string.Join(" ", run.Warnings.Concat(run.Failures))}",
            ok ? "No action required." : "Run 3D profile evidence check with the required source, and do not treat sample CSV evidence as real 3D camera validation.");
    }

    private static void AddLighting(FactoryReadinessReport report, FactoryReadinessCriteria criteria, LightingAcceptanceRun? run)
    {
        if (run is null)
        {
            Add(report, "Lighting sync status", criteria.RequireLightingAcceptance ? "No-Go" : "Conditional", "No lighting synchronization acceptance run has been recorded.", "Run lighting sync acceptance when lighting is part of deployment.");
            return;
        }

        var realOk = !criteria.RequireRealHardwareAcceptance || !run.IsSimulated;
        var ok = run.Status is "PASS" or "WARN" && realOk;
        Add(report, "Lighting sync status", ok ? "Go" : criteria.RequireLightingAcceptance ? "No-Go" : "Conditional", $"Status={run.Status}; mode={run.Mode}; simulated={run.IsSimulated}; steps={run.PassedStepCount}/{run.StepCount}. {string.Join(" ", run.Warnings.Concat(run.Failures))}", ok ? "No action required." : "Run lighting sync with the production lighting controller or scope review to simulation only.");
    }

    private static void AddRobot(FactoryReadinessReport report, FactoryReadinessCriteria criteria, RobotAcceptanceRun? run)
    {
        var summary = RobotAcceptanceTestService.ToSummary(run);
        var realOk = !criteria.RequireRealHardwareAcceptance || summary.SourceKind == "Real";
        var eStopOk = !criteria.RequireRobotAcceptance || summary.EmergencyStopBlocked;
        var safetyOk = !criteria.RequireRobotAcceptance || summary.SafetyFaultBlocked || summary.SafetySourceKind == "Real";
        var ok = summary.Status == "PASS" && realOk && eStopOk && safetyOk;
        Add(report, "Robot acceptance status", ok ? "Go" : criteria.RequireRobotAcceptance ? "No-Go" : "Conditional", $"{PlainLanguageGlossaryService.AcceptanceBoundary("Robot cell")} Status={summary.Status}; source={summary.SourceKind}; controller={summary.ControllerName}; safety={summary.SafetyControllerName}/{summary.SafetySourceKind}; full cycle ms={summary.FullCycleMs:F1}; emergency stop blocked motion={summary.EmergencyStopBlocked}; safety fault blocked motion={summary.SafetyFaultBlocked}. {string.Join(" ", summary.Messages)}", ok ? "No action required." : "Run robot cell evidence check with PLC/safety interlock evidence, including emergency-stop and guard/clamp blocking. Simulation is not real machine validation.");
    }

    private static void AddMes(FactoryReadinessReport report, FactoryReadinessCriteria criteria, MesReadinessSummary mes)
    {
        var productionMes = !criteria.Stage1Only && criteria.RequireNoPendingMesQueueForProductionMode;
        var status = mes.Status switch
        {
            "MES REST Ready" => "Go",
            "MES Queue Pending" or "MES Queue Pending FAIL" => productionMes ? "No-Go" : "Conditional",
            "MES REST Error" => "No-Go",
            "MES Mock Only" or "MES Not Configured" => productionMes ? "No-Go" : "Conditional",
            _ => "Conditional",
        };
        Add(report, "MES/spool status", status, $"{PlainLanguageGlossaryService.Explain("MES")} {mes.Status}; mode={mes.Mode}; waiting={mes.PendingCount}; old waiting={mes.OldPendingCount}; failed={mes.FailedCount}; sent={mes.SentCount}; abandoned={mes.AbandonedCount}; traceability={mes.LatestTraceabilityTestStatus}; latest traceability check={mes.LatestTraceabilityTestStatus}. {string.Join(" ", mes.Messages)}", status == "Go" ? "No action required." : "Resolve pending/failed MES queue items, configure accepted factory traceability connection, and run a passing traceability signoff.");
    }

    private static void AddCentralSync(FactoryReadinessReport report, FactoryReadinessCriteria criteria, CentralSyncReadinessSummary summary)
    {
        var status = summary.Status switch
        {
            "Central Sync Ready" => "Go",
            "Central Sync Disabled" when criteria.RequireCentralSyncEvidence => "No-Go",
            "Central Sync Error" when criteria.RequireCentralSyncEvidence => "No-Go",
            "Central Sync Pending" when criteria.RequireCentralSyncEvidence => "No-Go",
            "Central Sync Error" => "Conditional",
            "Central Sync Pending" => "Conditional",
            "Central Sync Disabled" when criteria.WarnWhenCentralSyncDisabled => "Conditional",
            "Central Sync Disabled" => "Go",
            _ => "Conditional",
        };

        Add(
            report,
            "Central sync status",
            status,
            $"Mode={summary.Mode}; pending={summary.PendingCount}; failed={summary.FailedCount}; sent={summary.SentCount}; skipped={summary.SkippedCount}. {string.Join(" ", summary.Messages)}",
            status == "Go" ? "No action required." : "Configure central sync or clear pending/failed queue items before claiming multi-station management aggregation.");
    }

    private static void AddPilotIssues(FactoryReadinessReport report)
    {
        var summary = PilotIssueService.Summarize();
        var status = summary.CriticalOpen > 0 ? "Conditional" : "Go";
        Add(
            report,
            "Pilot issue status",
            status,
            $"Pilot issues total={summary.Total}; open={summary.Open}; criticalOpen={summary.CriticalOpen}.",
            summary.CriticalOpen > 0
                ? "Resolve, waive, or close critical pilot issues before customer/factory Go claims."
                : "No critical open pilot issues recorded.");
    }

    private static void AddAlarmEvents(FactoryReadinessReport report)
    {
        var critical = AlarmEventService.GetActiveCriticalAlarms();
        if (critical.Count > 0)
        {
            Add(
                report,
                "Alarm/event status",
                "No-Go",
                $"{critical.Count} active Critical alarm(s) exist. Latest={critical[0].TimestampUtc:O}; source={critical[0].Source}; message={critical[0].Message}.",
                "Resolve Critical alarms before factory/client readiness can pass.");
            return;
        }

        var unacknowledgedAlarms = AlarmEventService.GetUnacknowledgedAlarmLevelEvents();
        Add(
            report,
            "Alarm/event status",
            unacknowledgedAlarms.Count > 0 ? "Conditional" : "Go",
            unacknowledgedAlarms.Count > 0
                ? $"{unacknowledgedAlarms.Count} unacknowledged Alarm-level event(s) are active."
                : "No active Critical alarms or unacknowledged Alarm-level events are present.",
            unacknowledgedAlarms.Count > 0
                ? "Acknowledge or resolve Alarm-level events before release packaging."
                : "No action required.");
    }

    private static void AddSecurityAudit(FactoryReadinessReport report)
    {
        var audits = AoiDatabase.GetAuditEvents(new LogFilter()).Take(50).ToArray();
        var denied = audits.Count(item => item.ActionCategory.Equals("ACCESS_DENIED", StringComparison.OrdinalIgnoreCase));
        Add(report, "Security/role audit status", audits.Length == 0 ? "Conditional" : "Go", $"Audit rows available={audits.Length}; recent access-denied events={denied}. Admin-only actions remain role-gated in the local audit trail.", audits.Length == 0 ? "Exercise role-gated workflows and export audit evidence." : "No action required.");
    }

    private static void AddAuthenticationMode(FactoryReadinessReport report, OperatingMode operatingMode)
    {
        var mode = AuthenticationSettingsService.CurrentMode;
        var authenticated = mode is AuthenticationMode.LocalUsers or AuthenticationMode.MesAuthenticationBoundary;
        var waiverActive = OperatingModeSettingsService.HasActivePilotAuthenticationWaiver();
        var status = operatingMode switch
        {
            OperatingMode.Production when !authenticated => "No-Go",
            OperatingMode.Pilot when !authenticated && !waiverActive => "No-Go",
            OperatingMode.Pilot when !authenticated && waiverActive => "Conditional",
            _ => mode == AuthenticationMode.LocalUsers ? "Go" : "Conditional",
        };
        var evidence = mode switch
        {
            AuthenticationMode.LocalUsers => "LocalUsers mode active. Local users authenticate with salted password hashes; roles come from the local user store.",
            AuthenticationMode.MesAuthenticationBoundary => "MES authentication boundary selected. This PoC documents the MES identity boundary but does not authenticate against a production MES identity provider.",
            _ => "DemoLocalRoleSelector mode active. The top-bar role selector is for demonstration only and is not production authentication.",
        };
        if (operatingMode == OperatingMode.Pilot && mode == AuthenticationMode.DemoLocalRoleSelector && waiverActive)
            evidence += " An explicit Pilot authentication waiver is active; readiness remains Conditional at best for accountability.";
        var next = status == "Go"
            ? "No action required for local accountability."
            : operatingMode == OperatingMode.Production
                ? "Switch to LocalUsers or integrate the customer MES identity provider before Production readiness claims."
                : "Switch to LocalUsers/MES authentication, or record an explicit Pilot authentication waiver before customer pilot review.";
        Add(report, "Authentication mode", status, evidence, next);
    }

    private static void AddKnownLimitations(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var scope = criteria.Stage1Only
            ? $"Selected profile: {DisplayName(criteria.DeploymentProfile)}. Stage 1 package does not validate production camera, lighting, robot, MES writeback, or production model accuracy."
            : $"Selected profile: {DisplayName(criteria.DeploymentProfile)}. Full factory readiness requires real hardware and production MES evidence; simulated evidence is listed separately.";
        Add(report, "Known limitations", "Conditional", scope, "Keep customer/management wording limited to the evidence actually collected.");
    }

    private static string ScopeFor(DeploymentProfile profile)
        => DisplayName(profile);

    private static void Add(FactoryReadinessReport report, string name, string status, string evidence, string nextAction)
        => report.Categories.Add(new FactoryReadinessCategory
        {
            Name = name,
            Status = status,
            Evidence = evidence,
            NextAction = nextAction,
        });

    private static ValidationPackageManifest? TryReadManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ValidationPackageManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyLatestValidationManifest(string packageFolder)
    {
        var latest = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        if (latest is null || !File.Exists(latest.ManifestPath))
            return;

        File.Copy(latest.ManifestPath, Path.Combine(packageFolder, "latest_validation_manifest.json"), overwrite: true);
    }

    private static void WriteLatestExportVerification(string packageFolder)
    {
        var records = AoiDatabase.GetExportVerifications(25);
        if (records.Count == 0)
            return;

        var path = Path.Combine(packageFolder, "latest_export_verification_report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions), Encoding.UTF8);
    }

    private static void CopyLatestBuildTestEvidence(string packageFolder)
    {
        var latest = AoiDatabase.GetLatestBuildTestEvidence();
        if (latest is null)
            return;

        var evidenceFolder = Path.Combine(packageFolder, "build_test_evidence");
        Directory.CreateDirectory(evidenceFolder);
        File.WriteAllText(
            Path.Combine(evidenceFolder, "latest_build_test_evidence_summary.json"),
            JsonSerializer.Serialize(latest, JsonOptions),
            Encoding.UTF8);
        if (!string.IsNullOrWhiteSpace(latest.EvidencePath) && File.Exists(latest.EvidencePath))
            File.Copy(latest.EvidencePath, Path.Combine(evidenceFolder, "latest_build_test_evidence.json"), overwrite: true);
    }

    private static void WriteLatestAcceptanceReports(string packageFolder)
    {
        var evidence = Path.Combine(packageFolder, "latest_acceptance_reports");
        Directory.CreateDirectory(evidence);

        if (AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false) is { } camera)
            File.WriteAllText(Path.Combine(evidence, "latest_camera_acceptance.json"), JsonSerializer.Serialize(camera, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestLightingAcceptanceRun() is { } lighting)
            File.WriteAllText(Path.Combine(evidence, "latest_lighting_acceptance.json"), JsonSerializer.Serialize(lighting, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestProfile3DAcceptanceRun() is { } profile3D)
            File.WriteAllText(Path.Combine(evidence, "latest_3d_profile_acceptance.json"), JsonSerializer.Serialize(profile3D, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestRobotAcceptanceRun() is { } robot)
            File.WriteAllText(Path.Combine(evidence, "latest_robot_acceptance.json"), JsonSerializer.Serialize(robot, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestSoakTestRun() is { } soak)
            File.WriteAllText(Path.Combine(evidence, "latest_soak_test.json"), JsonSerializer.Serialize(soak, JsonOptions), Encoding.UTF8);
        if (ModelRegistryService.GetActiveModel() is { } activeModel)
            File.WriteAllText(Path.Combine(evidence, "active_model_lifecycle.json"), JsonSerializer.Serialize(activeModel, JsonOptions), Encoding.UTF8);

        var mesReport = MesSpoolService.EvaluateReadiness();
        File.WriteAllText(Path.Combine(evidence, "latest_mes_readiness.json"), JsonSerializer.Serialize(mesReport, JsonOptions), Encoding.UTF8);
    }

    private static void WritePackageManifest(string packageFolder, FactoryReadinessReport report)
    {
        var manifestPath = Path.Combine(packageFolder, "package_manifest.json");
        var files = Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(packageFolder, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = Path.GetRelativePath(packageFolder, path).Replace('\\', '/'),
                FileType = Classify(path),
                Bytes = new FileInfo(path).Length,
                Sha256 = HashUtil.ComputeSha256(path),
            })
            .ToList();

        var manifest = new
        {
            schemaVersion = "factory-readiness-package/v1",
            packageId = $"FACTORY-{report.GeneratedAtUtc:yyyyMMddHHmmss}",
            generatedAtUtc = report.GeneratedAtUtc,
            overallStatus = report.OverallStatus,
            deploymentProfile = report.DeploymentProfile,
            scope = report.Scope,
            unmetCriteria = report.UnmetCriteria,
            includedFiles = files,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
    }

    private static string Classify(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Contains("factory_readiness_summary", StringComparison.OrdinalIgnoreCase))
            return "Factory readiness summary";
        if (fileName.Contains("standards_traceability_matrix", StringComparison.OrdinalIgnoreCase))
            return "Standards traceability matrix";
        if (fileName.Contains("validation_manifest", StringComparison.OrdinalIgnoreCase))
            return "Latest validation manifest";
        if (fileName.Contains("export_verification", StringComparison.OrdinalIgnoreCase))
            return "Latest export verification";
        if (fileName.Contains("build_test_evidence", StringComparison.OrdinalIgnoreCase))
            return "Latest build/test evidence";
        if (fileName.Contains("acceptance", StringComparison.OrdinalIgnoreCase) || fileName.Contains("mes_readiness", StringComparison.OrdinalIgnoreCase))
            return "Latest acceptance/readiness evidence";
        if (fileName.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
            return "README";
        return "Factory readiness evidence";
    }

    private static string BuildReadme(FactoryReadinessReport report)
        => $"""
        AOI Monitor Factory Readiness Go/No-Go Package

        Overall status: {report.OverallStatus}
        Deployment profile: {report.DeploymentProfile}
        Scope: {report.Scope}
        Generated UTC: {report.GeneratedAtUtc:O}

        Contents:
        - factory_readiness_summary.html: management-readable Go/No-Go summary.
        - factory_readiness_summary.pdf: native PDF rendering of the Go/No-Go summary.
        - factory_readiness_summary.json: machine-readable category evidence.
        - standards_traceability_matrix.html/pdf/json: standards-alignment checklist mapping project, HMI, quality, and alarm expectations to evidence.
        - latest_validation_manifest.json: copied when a Stage 1 validation package exists.
        - latest_export_verification_report.json: copied when export verification records exist.
        - build_test_evidence/: latest imported hygiene/build/test/publish validation evidence when available.
        - latest_acceptance_reports/: latest camera, lighting, robot, and MES readiness summaries when available.

        Evidence boundary:
        This package distinguishes Stage 1 customer/demo readiness from full factory readiness. Simulated camera, lighting, robot, or MES evidence must not be described as production equipment validation.
        The standards traceability matrix is standards-aligned project evidence only. It is not formal ISO, IEC, ISA, or third-party certification.

        Blocking issues:
        {FormatLines(report.BlockingIssues)}

        Warnings:
        {FormatLines(report.Warnings)}

        Unmet criteria:
        {FormatLines(report.UnmetCriteria)}
        """;

    private static void AppendList(StringBuilder sb, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;
        sb.AppendLine($"<h2>{Html(title)}</h2><ul>");
        foreach (var item in items)
            sb.AppendLine($"<li>{Html(item)}</li>");
        sb.AppendLine("</ul>");
    }

    private static string FormatLines(IReadOnlyCollection<string> lines)
        => lines.Count == 0 ? "- None recorded." : string.Join(Environment.NewLine, lines.Select(line => $"- {line}"));

    private static string Css(string status)
        => status.Replace(" ", "-", StringComparison.Ordinal);

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EnsureUniqueFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return folder;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{folder}_{i:D2}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{folder}_{Guid.NewGuid():N}";
    }
}
