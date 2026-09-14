param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "DemoSet_Quick"),
    [int]$OkCount = 24,
    [int]$PerDefectClassCount = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($OkCount -lt 20) {
    throw "OkCount must be at least 20 to satisfy the default demo preflight balance gate."
}

if ($PerDefectClassCount -lt 5) {
    throw "PerDefectClassCount must be at least 5 to satisfy the default demo defect-class gate."
}

Add-Type -AssemblyName System.Drawing

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$imagesDir = Join-Path $OutputRoot "images"
$goldenDir = Join-Path $OutputRoot "golden"
$cameraRoot = Join-Path $OutputRoot "folder_camera"
$cameraTop = Join-Path $cameraRoot "top"
$cameraSide = Join-Path $cameraRoot "side"
$cameraBottom = Join-Path $cameraRoot "bottom"

foreach ($folder in @($OutputRoot, $imagesDir, $goldenDir, $cameraRoot, $cameraTop, $cameraSide, $cameraBottom)) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

function New-Rect([int]$x, [int]$y, [int]$w, [int]$h) {
    return [System.Drawing.Rectangle]::new($x, $y, $w, $h)
}

function New-Brush([int]$r, [int]$g, [int]$b) {
    return [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($r, $g, $b))
}

function Draw-Component($graphics, [string]$label, [System.Drawing.Rectangle]$rect, [System.Drawing.Font]$font) {
    $componentBrush = New-Brush 31 40 44
    $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(104, 126, 135), 2)
    $textBrush = New-Brush 221 232 238
    $graphics.FillRectangle($componentBrush, $rect)
    $graphics.DrawRectangle($outlinePen, $rect)
    $textSize = $graphics.MeasureString($label, $font)
    $graphics.DrawString(
        $label,
        $font,
        $textBrush,
        [single]($rect.X + ($rect.Width - $textSize.Width) / 2),
        [single]($rect.Y + ($rect.Height - $textSize.Height) / 2))
    $componentBrush.Dispose()
    $outlinePen.Dispose()
    $textBrush.Dispose()
}

function Draw-Board($path, [string]$side, [string]$variant, [int]$serial) {
    $width = 900
    $height = 600
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $background = New-Brush 8 14 16
    $boardBrush = New-Brush 16 78 37
    $boardAltBrush = New-Brush 18 86 43
    $gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(34, 117, 55), 1)
    $boardPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(84, 150, 89), 3)
    $font = [System.Drawing.Font]::new("Segoe UI", 18, [System.Drawing.FontStyle]::Regular)
    $smallFont = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular)

    try {
        $graphics.FillRectangle($background, 0, 0, $width, $height)
        $boardRect = New-Rect 95 85 710 430
        $graphics.FillRectangle($boardBrush, $boardRect)
        $graphics.DrawRectangle($boardPen, $boardRect)

        for ($x = $boardRect.X; $x -le ($boardRect.X + $boardRect.Width); $x += 55) {
            $graphics.DrawLine($gridPen, $x, $boardRect.Y, $x, $boardRect.Y + $boardRect.Height)
        }

        for ($y = $boardRect.Y; $y -le ($boardRect.Y + $boardRect.Height); $y += 48) {
            $graphics.DrawLine($gridPen, $boardRect.X, $y, $boardRect.X + $boardRect.Width, $y)
        }

        if ($side -eq "bottom") {
            Draw-Component $graphics "J1" (New-Rect 150 150 210 78) $font
            Draw-Component $graphics "R12" (New-Rect 185 335 115 62) $font
            Draw-Component $graphics "U9" (New-Rect 360 245 250 110) $font
            Draw-Component $graphics "CN3" (New-Rect 620 160 120 70) $font
        } elseif ($side -eq "side") {
            Draw-Component $graphics "SIDE-U9" (New-Rect 185 210 505 120) $font
            Draw-Component $graphics "EDGE" (New-Rect 150 350 600 42) $font
        } else {
            Draw-Component $graphics "U101" (New-Rect 160 150 210 90) $font
            Draw-Component $graphics "D12" (New-Rect 180 340 115 62) $font
            Draw-Component $graphics "U107" (New-Rect 335 260 245 92) $font
            Draw-Component $graphics "CN8" (New-Rect 635 150 120 70) $font
            Draw-Component $graphics "SH1" (New-Rect 500 335 210 78) $font
        }

        $serialBrush = New-Brush 170 188 198
        $graphics.DrawString(("S{0:D4} {1}" -f $serial, $side.ToUpperInvariant()), $smallFont, $serialBrush, 105, 530)
        $serialBrush.Dispose()

        switch ($variant) {
            "solder_bridge" {
                $defectBrush = New-Brush 255 46 62
                $hotBrush = New-Brush 255 192 59
                $graphics.FillRectangle($hotBrush, (New-Rect 420 257 120 32))
                $graphics.FillRectangle($defectBrush, (New-Rect 430 282 95 78))
                $graphics.DrawString("BRIDGE", $smallFont, [System.Drawing.Brushes]::White, 438, 265)
                $defectBrush.Dispose()
                $hotBrush.Dispose()
            }
            "missing_component" {
                $graphics.FillRectangle($boardAltBrush, (New-Rect 178 338 122 68))
                $warnPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 185, 42), 4)
                $graphics.DrawRectangle($warnPen, (New-Rect 174 334 130 76))
                $graphics.DrawString("MISSING", $smallFont, [System.Drawing.Brushes]::White, 182, 356)
                $warnPen.Dispose()
            }
            "polarity_error" {
                $defectBrush = New-Brush 255 46 62
                $graphics.FillRectangle($defectBrush, (New-Rect 610 145 38 120))
                $graphics.DrawString("REV", $smallFont, [System.Drawing.Brushes]::White, 616, 188)
                $defectBrush.Dispose()
            }
            "height_anomaly" {
                $defectBrush = New-Brush 255 46 62
                $graphics.FillEllipse($defectBrush, (New-Rect 430 176 160 160))
                $graphics.DrawString("HEIGHT", $smallFont, [System.Drawing.Brushes]::White, 474, 243)
                $defectBrush.Dispose()
            }
        }

        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $font.Dispose()
        $smallFont.Dispose()
        $background.Dispose()
        $boardBrush.Dispose()
        $boardAltBrush.Dispose()
        $gridPen.Dispose()
        $boardPen.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Add-Row($rows, [string]$image, [string]$groundTruth, [string]$golden, [string]$defect, [string]$side, [string]$refdes, [string]$roiId, [string]$roiType, [string]$notes) {
    $rows.Add([pscustomobject]@{
        image = $image
        ground_truth = $groundTruth
        golden_image = $golden
        defect_type = $defect
        side = $side
        refdes = $refdes
        roi_id = $roiId
        roi_type = $roiType
        lot_id = "LOT-DEMO-07"
        board_model = "TBOX-DEMO"
        notes = $notes
    }) | Out-Null
}

$goldenBySide = @{
    top = "golden/tbox_ref_top.png"
    bottom = "golden/tbox_ref_bottom.png"
    side = "golden/tbox_ref_side.png"
}

Draw-Board (Join-Path $goldenDir "tbox_ref_top.png") "top" "ok" 0
Draw-Board (Join-Path $goldenDir "tbox_ref_bottom.png") "bottom" "ok" 0
Draw-Board (Join-Path $goldenDir "tbox_ref_side.png") "side" "ok" 0

$rows = [System.Collections.Generic.List[object]]::new()
$serial = 1
$sides = @("top", "bottom", "side")
for ($i = 0; $i -lt $OkCount; $i++) {
    $side = $sides[$i % $sides.Count]
    $fileName = "tbox_lot07_{0:D4}_{1}_ok.png" -f $serial, $side
    Draw-Board (Join-Path $imagesDir $fileName) $side "ok" $serial
    Add-Row $rows "images/$fileName" "OK" $goldenBySide[$side] "OK" $side "BOARD" ("ROI-OK-{0:D4}" -f $serial) "board" "generated known-good $side view"
    $serial++
}

$defects = @(
    @{ name = "solder_bridge"; side = "top"; refdes = "U107"; roi = "ROI-U107-PINS"; type = "pad"; note = "generated solder bridge defect" },
    @{ name = "missing_component"; side = "bottom"; refdes = "R12"; roi = "ROI-R12"; type = "component"; note = "generated missing component defect" },
    @{ name = "polarity_error"; side = "top"; refdes = "D5"; roi = "ROI-D5"; type = "marking"; note = "generated reversed polarity marking" },
    @{ name = "height_anomaly"; side = "side"; refdes = "U9"; roi = "ROI-U9-BODY"; type = "body"; note = "generated height/anomaly sample for supported engines" }
)

foreach ($defect in $defects) {
    for ($i = 0; $i -lt $PerDefectClassCount; $i++) {
        $side = [string]$defect.side
        $name = [string]$defect.name
        $fileName = "tbox_lot07_{0:D4}_{1}_{2}.png" -f $serial, $side, $name
        Draw-Board (Join-Path $imagesDir $fileName) $side $name $serial
        Add-Row $rows "images/$fileName" "NG" $goldenBySide[$side] $name $side ([string]$defect.refdes) ([string]$defect.roi) ([string]$defect.type) ([string]$defect.note)
        $serial++
    }
}

$manifestPath = Join-Path $OutputRoot "customer_validation_manifest.csv"
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $manifestPath

Copy-Item (Join-Path $imagesDir "tbox_lot07_0001_top_ok.png") (Join-Path $cameraTop "001_top_ok.png") -Force
Copy-Item (Join-Path $imagesDir "tbox_lot07_0002_bottom_ok.png") (Join-Path $cameraBottom "001_bottom_ok.png") -Force
Copy-Item (Join-Path $imagesDir "tbox_lot07_0003_side_ok.png") (Join-Path $cameraSide "001_side_ok.png") -Force
Copy-Item (Join-Path $imagesDir "tbox_lot07_0025_top_solder_bridge.png") (Join-Path $cameraTop "002_top_solder_bridge.png") -Force
Copy-Item (Join-Path $imagesDir "tbox_lot07_0035_bottom_missing_component.png") (Join-Path $cameraBottom "002_bottom_missing_component.png") -Force
Copy-Item (Join-Path $imagesDir "tbox_lot07_0045_top_polarity_error.png") (Join-Path $cameraTop "003_top_polarity_error.png") -Force

$readme = @"
AOI Monitor generated Stage 1 demo dataset

This folder is generated sample data for software workflow demonstration only.
It is not customer production evidence and it is not live camera evidence.

Use AI / Models:
1. Test Image Folder: $imagesDir
2. Ground Truth CSV: $manifestPath
3. Run Dataset Preflight
4. Run Batch Inspection
5. Export CSV, annotated images, or Export Stage 1 Validation Package

Use Readiness & QA:
1. Click Performance Benchmark (Readiness and Quality Gates panel)
2. Source: Image folder
3. Image folder: $imagesDir
4. Stage 1 Readiness tab: click Refresh Stage 1 Readiness, then Export Stage 1 Readiness Report after benchmark/package evidence exists

Use Run Inspection folder-camera simulation:
- Top Folder: $cameraTop
- Side Folder: $cameraSide
- Bottom Folder: $cameraBottom

The folder_camera paths are simulated camera inputs only. They do not validate real GigE, USB3, lighting, robot, PLC, or MES readiness.
"@

Set-Content -Path (Join-Path $OutputRoot "README.txt") -Value $readme -Encoding UTF8

Write-Host "Generated AOI Stage 1 demo dataset:"
Write-Host "  Dataset root: $OutputRoot"
Write-Host "  Images:       $imagesDir"
Write-Host "  Manifest:     $manifestPath"
Write-Host "  Folder camera simulation: $cameraRoot"
