[CmdletBinding()]
param(
    [switch]$Installer,
    [switch]$IconOnly,
    [string]$InnoSetupCompiler = ''
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$projectDir = $PSScriptRoot

function ConvertTo-NativeArgument([string]$Argument) {
    # Quote according to the Windows command-line rules, including trailing slashes.
    return '"' + [regex]::Replace([regex]::Replace($Argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-BuildTool([string]$FilePath, [string[]]$Arguments) {
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $startInfo.WorkingDirectory = $projectDir
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = $utf8
    $startInfo.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $outputText = $outputTask.GetAwaiter().GetResult()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($outputText) { Write-Host $outputText.TrimEnd() }
        if ($errorText) { [Console]::Error.Write($errorText) }
        if ($process.ExitCode -ne 0) { throw ('构建工具失败，退出码 ' + $process.ExitCode + '：' + $FilePath) }
    } finally { $process.Dispose() }
}

function Find-InnoSetupCompiler {
    if ($InnoSetupCompiler) {
        if (-not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf)) {
            throw ('指定的 Inno Setup 编译器不存在：' + $InnoSetupCompiler)
        }
        return (Resolve-Path -LiteralPath $InnoSetupCompiler).ProviderPath
    }
    $localCompiler = Join-Path $projectDir 'tools\InnoSetup\ISCC.exe'
    if (Test-Path -LiteralPath $localCompiler -PathType Leaf) { return $localCompiler }
    $pathCompiler = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pathCompiler) { return $pathCompiler.Source }
    $programDirectories = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ } | Select-Object -Unique
    foreach ($version in @(7, 6)) {
        foreach ($programDirectory in $programDirectories) {
            $candidate = Join-Path $programDirectory ('Inno Setup ' + $version + '\ISCC.exe')
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw '未找到 Inno Setup 编译器。请从官方网站 https://jrsoftware.org/isinfo.php 安装 Inno Setup，然后使用 .\build.ps1 -Installer -InnoSetupCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" 指定编译器。本脚本不会下载或安装编译器。'
}

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows x64 与 .NET Framework 4.8。' }
$binDir = Join-Path $projectDir 'bin'
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $projectDir 'src\app.ico'
function New-DiskIconFrame([int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size,$Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphite = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(41,41,41))
    $white = [Drawing.Pen]::new([Drawing.Color]::White,[single][Math]::Max(6,128.0/$Size))
    $whiteBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $png = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.ScaleTransform([single]($Size/128.0),[single]($Size/128.0))
        $shape.AddArc(4,4,32,32,180,90)
        $shape.AddArc(92,4,32,32,270,90)
        $shape.AddArc(92,92,32,32,0,90)
        $shape.AddArc(4,92,32,32,90,90)
        $shape.CloseFigure()
        $graphics.FillPath($graphite,$shape)
        $white.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $driveBody = [Drawing.Point[]]@([Drawing.Point]::new(35,36),[Drawing.Point]::new(93,36),[Drawing.Point]::new(102,72),[Drawing.Point]::new(102,91),[Drawing.Point]::new(26,91),[Drawing.Point]::new(26,72))
        $graphics.DrawPolygon($white,$driveBody)
        $graphics.DrawLine($white,26,72,102,72)
        $graphics.FillEllipse($whiteBrush,84,79,5,5)
        $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
        return ,$png.ToArray()
    } finally {
        $png.Dispose(); $shape.Dispose(); $whiteBrush.Dispose(); $white.Dispose(); $graphite.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

# Windows 10/11 ICO: individually rendered PNG frames remain sharp at every shell size.
$iconSizes = @(16,24,32,48,64,128,256)
$iconFrames = [Collections.Generic.List[byte[]]]::new()
foreach ($iconSize in $iconSizes) { $iconFrames.Add((New-DiskIconFrame $iconSize)) }
$iconStream = [IO.File]::Create($iconPath)
$iconWriter = [IO.BinaryWriter]::new($iconStream)
try {
    $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]$iconSizes.Count)
    $frameOffset = 6 + 16 * $iconSizes.Count
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $dimensionByte = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
        $iconWriter.Write([byte]$dimensionByte); $iconWriter.Write([byte]$dimensionByte)
        $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconFrames[$index].Length); $iconWriter.Write([uint32]$frameOffset)
        $frameOffset += $iconFrames[$index].Length
    }
    foreach ($iconFrame in $iconFrames) { $iconWriter.Write([byte[]]$iconFrame) }
} finally {
    $iconWriter.Dispose(); $iconStream.Dispose()
}
if ($IconOnly) { Write-Output ('已生成多尺寸图标：' + $iconPath); return }
$executable = Join-Path $binDir 'DiskRelease.exe'
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName)
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + $executable),('/win32manifest:' + (Join-Path $projectDir 'src\app.manifest')),('/win32icon:' + $iconPath),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll') + $sourceFiles
Invoke-BuildTool $compilerPath $compilerArgs
Write-Output ('已编译：' + $executable)
if ($Installer) {
    $setupCompiler = Find-InnoSetupCompiler
    Invoke-BuildTool $setupCompiler @('/Qp', (Join-Path $projectDir 'installer.iss'))
}
