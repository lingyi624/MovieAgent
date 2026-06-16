$sourcePath = $PSScriptRoot
$outputPath = Join-Path $sourcePath "Resources\appicon.ico"

if (Test-Path $outputPath) {
    Write-Host "Icon already exists, skipping generation."
    exit 0
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$drawingGroup = New-Object System.Windows.Media.DrawingGroup
$context = $drawingGroup.Open()

$backgroundBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(26, 26, 46))
$context.DrawEllipse($backgroundBrush, $null, (New-Object System.Windows.Point(100, 100)), 100, 100)

$whiteBrush = [System.Windows.Media.Brushes]::White
$whitePen = New-Object System.Windows.Media.Pen($whiteBrush, 2)

$rectGeometry = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(25, 40, 150, 120)), 8, 8)
$context.DrawGeometry($null, $whitePen, $rectGeometry)

$trianglePoints = New-Object System.Windows.Media.PointCollection
$trianglePoints.Add((New-Object System.Windows.Point(55, 60)))
$trianglePoints.Add((New-Object System.Windows.Point(55, 140)))
$trianglePoints.Add((New-Object System.Windows.Point(115, 100)))

$pathFigure = New-Object System.Windows.Media.PathFigure
$pathFigure.StartPoint = $trianglePoints[0]
$pathFigure.IsClosed = $true
for ($i = 1; $i -lt $trianglePoints.Count; $i++) {
    $pathFigure.Segments.Add((New-Object System.Windows.Media.LineSegment($trianglePoints[$i], $true)))
}
$triangleGeometry = New-Object System.Windows.Media.PathGeometry
$triangleGeometry.Figures.Add($pathFigure)
$context.DrawGeometry($whiteBrush, $null, $triangleGeometry)

$linePen = New-Object System.Windows.Media.Pen($whiteBrush, 3)
$linePen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
$linePen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
$context.DrawLine($linePen, (New-Object System.Windows.Point(130, 65)), (New-Object System.Windows.Point(160, 65)))
$context.DrawLine($linePen, (New-Object System.Windows.Point(130, 100)), (New-Object System.Windows.Point(155, 100)))
$context.DrawLine($linePen, (New-Object System.Windows.Point(130, 135)), (New-Object System.Windows.Point(145, 135)))

$context.Close()

function CreatePngBytes {
    param(
        [System.Windows.Media.DrawingGroup]$drawing,
        [int]$size
    )
    
    $renderBitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $visual = New-Object System.Windows.Media.DrawingVisual
    $visualContext = $visual.RenderOpen()
    $scale = $size / 200.0
    $visualContext.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    $visualContext.DrawDrawing($drawing)
    $visualContext.Close()
    $renderBitmap.Render($visual)
    
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($renderBitmap))
    
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return $stream.ToArray()
}

$sizes = @(256, 48, 32, 16)
$pngDataList = @()

foreach ($size in $sizes) {
    $pngDataList += ,(CreatePngBytes $drawingGroup $size)
}

$stream = New-Object System.IO.FileStream($outputPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([short]0)
$writer.Write([short]1)
$writer.Write([short]$pngDataList.Count)

$dataOffset = 6 + $pngDataList.Count * 16

for ($i = 0; $i -lt $pngDataList.Count; $i++) {
    $pngData = $pngDataList[$i]
    $size = $sizes[$i]
    
    $writer.Write([byte](if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([byte](if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([short]1)
    $writer.Write([short]32)
    $writer.Write($pngData.Length)
    $writer.Write($dataOffset)
    
    $dataOffset += $pngData.Length
}

foreach ($pngData in $pngDataList) {
    $writer.Write($pngData)
}

$writer.Close()
$stream.Close()

Write-Host "Icon generated successfully at $outputPath"
