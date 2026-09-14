param(
    [string]$DatasetRoot = (Join-Path $PSScriptRoot "..\SampleData\DemoSet_Quick"),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot "..\TestResults"),
    [switch]$Restore,
    [switch]$SkipGenerate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$datasetRootFull = [System.IO.Path]::GetFullPath($DatasetRoot)
$resultsRoot = [System.IO.Path]::GetFullPath($ResultsDirectory)
$generator = Join-Path $repoRoot "SampleData\demo_dataset_generator.ps1"
$templatePath = Join-Path $repoRoot "SampleData\customer_validation_manifest_template.csv"
$manifestPath = Join-Path $datasetRootFull "customer_validation_manifest.csv"
$imagesDir = Join-Path $datasetRootFull "images"
$goldenDir = Join-Path $datasetRootFull "golden"
$testProject = Join-Path $repoRoot "AOI_Monitor.Tests\AOI_Monitor.Tests.csproj"
$reportTextPath = Join-Path $resultsRoot "stage1_readiness_smoke_report.txt"
$reportJsonPath = Join-Path $resultsRoot "stage1_readiness_smoke_report.json"

$requiredColumns = @(
    "image",
    "ground_truth",
    "golden_image",
    "defect_type",
    "side",
    "refdes",
    "roi_id",
    "roi_type",
    "lot_id",
    "board_model",
    "notes"
)

function Assert-PathExists([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label was not found: $Path"
    }

    Write-Host "[OK] ${Label}: $Path"
}

function Assert-ManifestColumns([string]$Path, [string[]]$Columns, [string]$Label) {
    Assert-PathExists $Path $Label
    $headerLine = Get-Content -LiteralPath $Path -TotalCount 1
    if ([string]::IsNullOrWhiteSpace($headerLine)) {
        throw "$Label has no header row: $Path"
    }

    $headers = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($header in $headerLine.Split(",")) {
        [void]$headers.Add($header.Trim().Trim([char]0xFEFF).Trim('"'))
    }

    foreach ($column in $Columns) {
        if (-not $headers.Contains($column)) {
            throw "$Label is missing required column '$column'."
        }
    }
}

function Assert-ManifestPathsResolve([string]$Manifest, [string]$Root) {
    $rows = @(Import-Csv -LiteralPath $Manifest)
    if ($rows.Count -eq 0) {
        throw "Manifest has no data rows: $Manifest"
    }

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $rows) {
        foreach ($field in @("image", "golden_image")) {
            $value = [string]$row.$field
            if ([string]::IsNullOrWhiteSpace($value)) {
                $missing.Add("$field is blank for manifest row $($row.image)") | Out-Null
                continue
            }

            $candidate = if ([System.IO.Path]::IsPathRooted($value)) {
                $value
            } else {
                Join-Path $Root $value
            }

            if (-not (Test-Path -LiteralPath $candidate)) {
                $missing.Add("$field path does not resolve: $value") | Out-Null
            }
        }
    }

    if ($missing.Count -gt 0) {
        throw "Manifest path resolution failed:`n$($missing | Select-Object -First 10 | Out-String)"
    }

    $okCount = @($rows | Where-Object { $_.ground_truth -eq "OK" }).Count
    $ngRows = @($rows | Where-Object { $_.ground_truth -eq "NG" })
    $classes = @($ngRows | Group-Object defect_type)
    Write-Host "[OK] Manifest rows: $($rows.Count), OK: $okCount, NG: $($ngRows.Count), NG defect classes: $($classes.Count)"
}

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

if (-not $SkipGenerate) {
    Assert-PathExists $generator "Demo dataset generator"
    Write-Host "Generating synthetic Stage 1 demo dataset..."
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $generator -OutputRoot $datasetRootFull
    if ($LASTEXITCODE -ne 0) {
        throw "Demo dataset generation failed with exit code $LASTEXITCODE."
    }
}

Assert-ManifestColumns $templatePath $requiredColumns "Manifest template"
Assert-PathExists $imagesDir "Demo images folder"
Assert-PathExists $goldenDir "Golden reference folder"
Assert-ManifestColumns $manifestPath $requiredColumns "Customer validation manifest"
Assert-ManifestPathsResolve $manifestPath $datasetRootFull

$previousDatasetRoot = $env:AOI_STAGE1_DEMO_DATASET_ROOT
$filter = "FullyQualifiedName~Stage1SmokeGeneratedDatasetCompletesPreflightAndBenchmarkWhenProvided"
$testArgs = @(
    $testProject,
    "--configuration",
    "Release"
)
if (-not $Restore) {
    $testArgs += "--no-restore"
}
$testArgs += @(
    "--filter",
    $filter,
    "--logger",
    "trx;LogFileName=stage1_readiness_smoke.trx",
    "--results-directory",
    $resultsRoot
)
try {
    $env:AOI_STAGE1_DEMO_DATASET_ROOT = $datasetRootFull
    Write-Host "Running focused Stage 1 service smoke test..."
    & dotnet test @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Stage 1 readiness smoke test failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:AOI_STAGE1_DEMO_DATASET_ROOT = $previousDatasetRoot
}

$manualSteps = @(
    "Open AOI Monitor.",
    "AI / Models: select $imagesDir.",
    "AI / Models: select $manifestPath.",
    "Click Run Dataset Preflight.",
    "Click Run Batch Inspection.",
    "Review rows, OK/NG/REVIEW counts, false calls, possible escapes, and selected-row preview.",
    "Export CSV and Export Annotated Images.",
    "Click Export Stage 1 Validation Package.",
    "Readiness & QA > Performance Benchmark: run against $imagesDir.",
    "Readiness & QA > Stage 1 Readiness: click Refresh, then Export Report.",
    "Open validation_summary.html, customer_validation_report.html, benchmark_report.html, stage1_readiness_report.html, stage1_readiness_report.pdf, stage1_readiness_report.json, benchmark_results.csv, and limitations.txt.",
    "Confirm every report keeps the evidence scoped to Stage 1 uploaded-image validation and does not claim real camera, lighting, robot, MES, or full factory readiness."
)

$report = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repositoryRoot = $repoRoot
    datasetRoot = $datasetRootFull
    manifestPath = $manifestPath
    resultsDirectory = $resultsRoot
    smokeStatus = "PASS"
    manualGuiSteps = $manualSteps
    limitations = @(
        "This smoke check is service-level and synthetic-data based.",
        "It does not launch WPF or validate real camera, lighting, robot, PLC safety, production MES, ERP, or factory automation.",
        "Use the UI Stage 1 Readiness report for client-facing evidence after the GUI workflow is exercised."
    )
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportJsonPath -Encoding UTF8
@(
    "AOI Monitor Stage 1 Readiness Smoke",
    "Status: PASS",
    "Dataset: $datasetRootFull",
    "Manifest: $manifestPath",
    "Results: $resultsRoot",
    "",
    "Manual GUI steps:",
    ($manualSteps | ForEach-Object -Begin { $i = 0 } -Process { $i++; "$i. $_" }),
    "",
    "Limitations:",
    ($report.limitations | ForEach-Object { "- $_" })
) | Set-Content -LiteralPath $reportTextPath -Encoding UTF8

Write-Host ""
Write-Host "Stage 1 readiness smoke PASS."
Write-Host "Report: $reportTextPath"
Write-Host "JSON:   $reportJsonPath"
Write-Host ""
Write-Host "Manual GUI steps:"
for ($i = 0; $i -lt $manualSteps.Count; $i++) {
    Write-Host "$($i + 1). $($manualSteps[$i])"
}
