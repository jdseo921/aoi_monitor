param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$FrameworkDependent,
    [switch]$SkipClientDemoGate,
    [switch]$IncludeTemplates,
    [switch]$NoRestore,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$publishScript = Join-Path $scriptRoot "publish.ps1"

if (!(Test-Path $publishScript)) {
    throw "Publish script was not found: $publishScript"
}

$publishArgs = @(
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-IncludeDocs",
    "-IncludeSampleManifestTemplate"
)

if (-not $FrameworkDependent) {
    $publishArgs += "-SelfContained"
}

if (-not $SkipClientDemoGate) {
    $publishArgs += "-ClientDemoGate"
}

if ($IncludeTemplates) {
    $publishArgs += "-IncludeTemplates"
}

if ($NoRestore) {
    $publishArgs += "-NoRestore"
}

if (![string]::IsNullOrWhiteSpace($OutputRoot)) {
    $publishArgs += @("-OutputRoot", $OutputRoot)
}

Write-Host "Preparing AOI Monitor client test package..."
Write-Host "Repository:       $repoRoot"
Write-Host "Runtime:          $Runtime"
Write-Host "Mode:             $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' })"
Write-Host "Client demo gate: $(if ($SkipClientDemoGate) { 'skipped by caller' } else { 'required' })"
Write-Host "Zip:              $(if ($Zip) { 'yes' } else { 'no' })"

& pwsh $publishScript @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "Client test package preparation failed during publish."
}

$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "Release"
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
}

$releaseDir = Get-ChildItem -LiteralPath $releaseRoot -Directory -Filter "AOI_Monitor_PoC_*" -ErrorAction Stop |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $releaseDir) {
    throw "Publish completed but no AOI_Monitor_PoC_* release folder was found under $releaseRoot."
}

$handoffReadmePath = Join-Path $releaseDir.FullName "CLIENT_HANDOFF_README.md"
$handoffText = @'
# AOI Monitor Client Test Handoff

This folder is ready for client software evaluation.

## Quick Start

1. Open `app/`.
2. Run `AOI_Monitor.exe`.
3. Use Demo Mode unless LocalUsers authentication was configured for this evaluation.
4. Use Admin or Engineer role for setup, import, validation, and export tests.
5. Follow the client test kit section in `Docs/VALIDATION.md`.

## Run Stage 1 Demo in 10 Minutes

1. Open PowerShell in this package folder.
2. Generate synthetic demo data:
   `pwsh .\SampleData\demo_dataset_generator.ps1 -OutputRoot "$PWD\SampleData\DemoSet_Quick"`
3. Launch `app\AOI_Monitor.exe`.
4. Open `Readiness & QA`, click `Performance Benchmark` (Readiness and Quality Gates panel), choose Source `Image folder`, and run against `SampleData\DemoSet_Quick\images`. The benchmark runs first on purpose: the validation package embeds the latest recorded benchmark and refuses (with a PROVENANCE_MISMATCH note) one measured on a different dataset.
5. Open `AI / Models`.
6. Select `SampleData\DemoSet_Quick\images`.
7. Select `SampleData\DemoSet_Quick\customer_validation_manifest.csv`.
8. Click `Run Dataset Preflight`.
9. Click `Run Batch Inspection`.
10. Export CSV and annotated images if requested.
11. Click `Export Stage 1 Validation Package`.
12. Open `Readiness & QA > Stage 1 Readiness`, click `Refresh Stage 1 Readiness`, then `Export Stage 1 Readiness Report`.
13. Review `stage1_readiness_report.html`, `stage1_readiness_report.pdf`, `stage1_readiness_report.json`, `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `benchmark_results.csv`, and `limitations.txt`.

## Suggested First Tests

1. Confirm the readiness panel labels simulated/mock/not-connected features.
2. Run the Stage 1 demo route above with synthetic data.
3. Import a small non-confidential PCB image.
4. Compare it with a golden/reference image.
5. Record one disposition.
6. Run a small customer dataset batch with client-supplied non-confidential data if available.
7. Export inspection/review CSV evidence.
8. From `Readiness & QA`, export the evidence reports: `Export Stage 1 Readiness Report` (Stage 1 Readiness tab), `Export Readiness Evidence`, `Client Demo Readiness` + `Export Demo Gate` (Readiness and Quality Gates panel), and `Export HTML/PDF/JSON` on the Standards & Quality Checklist tab.

## Evidence To Return

- Stage 1 readiness report folder.
- Stage 1 validation package folder.
- Factory readiness package.
- Standards traceability matrix export.
- Client demo readiness gate export.
- Screenshots of any warnings, alarms, or layout issues.
- Crash report folder if a crash occurs.

## Boundary

This package is standards-aligned project evidence for client evaluation. It is not formal ISO, IEC, ISA, safety, cybersecurity, or production-equipment certification. Simulated camera/robot/MES evidence must not be treated as real hardware validation.
'@

Set-Content -LiteralPath $handoffReadmePath -Value $handoffText -Encoding UTF8

$zipPath = $null
if ($Zip) {
    $zipPath = Join-Path $releaseRoot "$($releaseDir.Name).zip"
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -LiteralPath $releaseDir.FullName -DestinationPath $zipPath -Force
}

Write-Host "Client test package ready:"
Write-Host $releaseDir.FullName
if ($Zip) {
    Write-Host "Zip package:"
    Write-Host $zipPath
}
