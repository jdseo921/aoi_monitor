<#
.SYNOPSIS
    Runs the complete headless Stage 1 testing sequence and reports the readiness verdict.

.DESCRIPTION
    Stage 1 evidence is a chain: each step persists evidence the next one reads, and the
    readiness gate is only meaningful once all of them have run. This script runs that chain in
    the required order against the generated demo dataset (or a dataset you supply) and stops at
    the first failure, so a tester gets one command instead of four ordered invocations.

    Scope: Stage 1 uploaded-image validation only. Nothing here validates real cameras, lighting,
    robots, PLC safety, production MES/ERP, or factory automation.

.PARAMETER Operator
    Operator id recorded on every piece of evidence. Required for traceability.

.PARAMETER DatasetRoot
    Dataset root containing images/, golden/ and customer_validation_manifest.csv.
    Defaults to SampleData/DemoSet_Quick.

.PARAMETER ResultsDirectory
    Where evidence is written. Defaults to TestResults/stage1.

.PARAMETER Priority
    Detection policy. Defaults to maximize-defect-recall, which is the policy the demo dataset
    expectations in Docs/VALIDATION.md §4.0 are measured against.

.PARAMETER BuildStatus / TestStatus / HygieneStatus / PublishValidationStatus
    The real outcomes of your build/test/quality-gate run. They are recorded verbatim as build
    evidence. Do not pass PASS unless you observed PASS.

.PARAMETER SkipGenerate
    Reuse the existing dataset instead of regenerating it.

.PARAMETER SkipBuild
    Skip the Release build (use when you have just built).

.EXAMPLE
    pwsh Scripts/run-stage1-testing.ps1 -Operator qa01
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Operator,

    [string]$DatasetRoot = (Join-Path $PSScriptRoot "..\SampleData\DemoSet_Quick"),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot "..\TestResults\stage1"),
    [ValidateSet("balanced", "minimize-false-positives", "maximize-defect-recall")]
    [string]$Priority = "maximize-defect-recall",

    [ValidateSet("PASS", "FAIL", "UNKNOWN")]
    [string]$BuildStatus = "PASS",
    [ValidateSet("PASS", "FAIL", "UNKNOWN")]
    [string]$TestStatus = "PASS",
    [ValidateSet("PASS", "FAIL", "UNKNOWN")]
    [string]$HygieneStatus = "PASS",
    [ValidateSet("PASS", "FAIL", "UNKNOWN")]
    [string]$PublishValidationStatus = "PASS",

    [switch]$SkipGenerate,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$datasetRoot = [System.IO.Path]::GetFullPath($DatasetRoot)
$results = [System.IO.Path]::GetFullPath($ResultsDirectory)
$solution = Join-Path $repoRoot "AOI_PCB_Database.slnx"
$toolsProject = Join-Path $repoRoot "AOI_Monitor.Tools\AOI_Monitor.Tools.csproj"
$generator = Join-Path $repoRoot "SampleData\demo_dataset_generator.ps1"

$images = Join-Path $datasetRoot "images"
$manifest = Join-Path $datasetRoot "customer_validation_manifest.csv"
$golden = Join-Path $datasetRoot "golden\tbox_ref_top.png"

# The tool's own output is the evidence a tester reads, so it must reach the console verbatim.
# The exit code is handed back through a script-scoped variable rather than the pipeline: emitting
# it as output would interleave it with the tool's stdout and make the caller's capture an array.
$script:LastStepExitCode = 0

function Invoke-Step {
    param(
        [string]$Title,
        [string[]]$ToolArguments,
        [int[]]$AcceptableExitCodes = @(0)
    )

    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
    & dotnet run --project $toolsProject -c Release --no-build -- @ToolArguments
    $script:LastStepExitCode = $LASTEXITCODE
    if ($AcceptableExitCodes -notcontains $script:LastStepExitCode) {
        throw "$Title failed with exit code $($script:LastStepExitCode)."
    }
}

New-Item -ItemType Directory -Force -Path $results | Out-Null

if (-not $SkipGenerate) {
    Write-Host "=== Generating Stage 1 demo dataset ===" -ForegroundColor Cyan
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $generator -OutputRoot $datasetRoot
    if ($LASTEXITCODE -ne 0) { throw "Demo dataset generation failed with exit code $LASTEXITCODE." }
}

foreach ($required in @($images, $manifest, $golden)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required dataset path was not found: $required"
    }
}

if (-not $SkipBuild) {
    Write-Host "=== Release build ===" -ForegroundColor Cyan
    & dotnet build $solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}

# The benchmark runs BEFORE the exit step: the stage1-exit validation package embeds the
# latest recorded benchmark, so running it afterwards would stamp a previous (possibly
# different-dataset) benchmark into the package.
Invoke-Step -Title "1/4 Inspection performance benchmark" -ToolArguments @(
    "benchmark",
    "--images", $images,
    "--golden", $golden,
    "--output", (Join-Path $results "bench"),
    "--priority", $Priority
)

Invoke-Step -Title "2/4 Stage 1 exit evidence" -ToolArguments @(
    "stage1-exit",
    "--dataset", $images,
    "--manifest", $manifest,
    "--output", (Join-Path $results "exit"),
    "--operator", $Operator,
    "--priority", $Priority,
    "--allow-simulation"
)

Invoke-Step -Title "3/4 Record build/test evidence" -ToolArguments @(
    "record-build-evidence",
    "--operator", $Operator,
    "--configuration", "Release",
    "--hygiene", $HygieneStatus,
    "--build", $BuildStatus,
    "--test", $TestStatus,
    "--publish-validation", $PublishValidationStatus,
    "--test-results", (Join-Path $repoRoot "TestResults")
)

# The readiness gate reports CONDITIONAL (1) as a real outcome, not a crash: accept it here and
# let the caller decide, but never accept FAIL (2) or a usage error (3).
Invoke-Step -Title "4/4 Stage 1 readiness gate" -AcceptableExitCodes @(0, 1) -ToolArguments @(
    "stage1-readiness",
    "--dataset", $images,
    "--manifest", $manifest,
    "--output", (Join-Path $results "readiness")
)
$readinessCode = $script:LastStepExitCode

Write-Host ""
if ($readinessCode -eq 0) {
    Write-Host "Stage 1 readiness: PASS" -ForegroundColor Green
} else {
    Write-Host "Stage 1 readiness: CONDITIONAL - review the per-check 'Next:' lines above." -ForegroundColor Yellow
}

Write-Host "Evidence root: $results"
Write-Host ""
Write-Host "Scope reminder: Stage 1 uploaded-image validation only. No real camera, lighting, robot," -ForegroundColor DarkYellow
Write-Host "PLC safety, production MES/ERP, or factory automation readiness is claimed by this run." -ForegroundColor DarkYellow

exit $readinessCode
