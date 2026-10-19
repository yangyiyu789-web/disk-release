[CmdletBinding()]
param([switch]$IncludeWorkerProtocol)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$projectDir = Split-Path $PSScriptRoot -Parent
$outputDir = Join-Path $PSScriptRoot 'results\run-tests'
$null = New-Item -ItemType Directory -Path $outputDir -Force
$appPath = Join-Path $projectDir 'bin\DiskRelease.exe'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$powershellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$manifestPath = Join-Path $projectDir 'src\app.manifest'
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw '需要 Windows x64 与 .NET Framework 4.8。' }

function ConvertTo-NativeArgument([string]$Argument) {
    return '"' + [regex]::Replace([regex]::Replace($Argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-HiddenTool([string]$Name, [string]$FilePath, [string[]]$Arguments) {
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
        $stdoutPath = Join-Path $outputDir ($Name + '.stdout.log')
        $stderrPath = Join-Path $outputDir ($Name + '.stderr.log')
        [IO.File]::WriteAllText($stdoutPath, $outputText, $utf8)
        [IO.File]::WriteAllText($stderrPath, $errorText, $utf8)
        if ($process.ExitCode -ne 0) {
            throw ($Name + ' 失败，退出码 ' + $process.ExitCode + '。详见：' + $stdoutPath + ' 和 ' + $stderrPath)
        }
        return $outputText.Trim()
    } finally { $process.Dispose() }
}

function Read-TestSummary([string]$Name, [string]$Text) {
    if ($Text.IndexOf([char]0xFFFD) -ge 0) { throw ($Name + ' 的 UTF-8 输出包含无效字符。') }
    try { $summary = ConvertFrom-Json -InputObject $Text }
    catch { throw ($Name + ' 未返回有效的 JSON 结果。') }
    if ($null -eq $summary.passed -or [int]$summary.passed -le 0 -or $summary.visibleWindowsOpened -ne 0) {
        throw ($Name + ' 的测试结果无效。')
    }
    return $summary
}

function Get-AppSha256 {
    $stream = [IO.File]::OpenRead($appPath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

# Always rebuild the application, then reference that exact shipped WinExe.
$null = Invoke-HiddenTool 'build' $powershellPath @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectDir 'build.ps1'))
$appHash = Get-AppSha256
$commonCompilerArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/utf8output', ('/win32manifest:' + $manifestPath), '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.Web.Extensions.dll', ('/r:' + $appPath))
$testCases = @(
    @{ Name = 'UiV110SafetyTests'; Main = 'UiV110Bootstrap' },
    @{ Name = 'PrimaryCloseActionTests'; Main = 'PrimaryCloseActionBootstrap' },
    @{ Name = 'ExplorerProjectionTests'; Main = 'ExplorerProjectionBootstrap' }
)
$results = New-Object 'Collections.Generic.List[object]'
$totalChecks = 0
foreach ($testCase in $testCases) {
    $testPath = Join-Path $outputDir ($testCase.Name + '.exe')
    $sourcePath = Join-Path $PSScriptRoot ($testCase.Name + '.cs')
    $compileArgs = $commonCompilerArgs + @(('/main:' + $testCase.Main), ('/out:' + $testPath), $sourcePath)
    $null = Invoke-HiddenTool ($testCase.Name + '.compile') $compilerPath $compileArgs
    $rawResult = Invoke-HiddenTool $testCase.Name $testPath @($appPath)
    $summary = Read-TestSummary $testCase.Name $rawResult
    $results.Add([ordered]@{ name = $testCase.Name; result = $summary })
    $totalChecks += [int]$summary.passed
    Write-Host ($testCase.Name + '：' + $summary.passed + ' 项通过')
}

if ($IncludeWorkerProtocol) {
    # The protocol helper requires this ignored path even in InvalidOnly mode.
    # The fixture is compiled but never launched; the worker rejects the fake
    # device identity before scanning handles or attempting any close operation.
    $fixturePath = Join-Path $PSScriptRoot 'BlockerFixture.exe'
    $fixtureArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/utf8output', ('/win32manifest:' + $manifestPath), ('/out:' + $fixturePath), '/r:System.dll', (Join-Path $PSScriptRoot 'BlockerFixture.cs'))
    $null = Invoke-HiddenTool 'BlockerFixture.compile' $compilerPath $fixtureArgs
    $workerResultPath = Join-Path $outputDir 'packaged-worker-results.json'
    $workerArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Test-PackagedWorker.ps1'), '-AppPath', $appPath, '-InvalidOnly', '-ResultPath', $workerResultPath)
    $rawResult = Invoke-HiddenTool 'PackagedWorkerInvalidIdentity' $powershellPath $workerArgs
    $summary = Read-TestSummary 'PackagedWorkerInvalidIdentity' $rawResult
    if ($summary.mode -ne 'invalid-identity-only' -or -not $summary.invalidIdentityRejected) {
        throw 'worker 协议测试未以无效磁盘身份模式完成。'
    }
    $results.Add([ordered]@{ name = 'PackagedWorkerInvalidIdentity'; result = $summary })
    $totalChecks += [int]$summary.passed
    Write-Host ('PackagedWorkerInvalidIdentity：' + $summary.passed + ' 项通过')
}

if ((Get-AppSha256) -ne $appHash) { throw '测试期间应用二进制发生变化，请重新运行。' }
$result = [ordered]@{
    passed = $totalChecks
    testedBinary = $appPath
    testedBinarySha256 = $appHash
    includeWorkerProtocol = [bool]$IncludeWorkerProtocol
    visibleWindowsOpened = 0
    tests = @($results.ToArray())
}
$resultJson = ConvertTo-Json -InputObject $result -Depth 12
$resultPath = Join-Path $outputDir 'test-results.json'
[IO.File]::WriteAllText($resultPath, $resultJson, $utf8)
Write-Output $resultJson
