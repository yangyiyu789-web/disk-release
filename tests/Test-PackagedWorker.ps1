[CmdletBinding()]
param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\DiskRelease.exe'),
    [string]$ProbeDrive = '',
    [switch]$InvalidOnly,
    [string]$ResultPath = (Join-Path $PSScriptRoot 'packaged-worker-results.json')
)

$ErrorActionPreference = 'Stop'

# The shipped binary targets .NET Framework. Load it in that runtime, including
# when the caller normally uses PowerShell 7. This child never opens a window.
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    $legacyPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $legacyInfo = New-Object Diagnostics.ProcessStartInfo
    $legacyInfo.FileName = $legacyPowerShell
    $legacyInfo.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -AppPath "' + $AppPath + '" -ResultPath "' + $ResultPath + '"'
    if ($ProbeDrive) { $legacyInfo.Arguments += ' -ProbeDrive "' + $ProbeDrive.TrimEnd('\') + '"' }
    if ($InvalidOnly) { $legacyInfo.Arguments += ' -InvalidOnly' }
    $legacyInfo.UseShellExecute = $false
    $legacyInfo.CreateNoWindow = $true
    $legacyInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $legacyInfo.RedirectStandardOutput = $true
    $legacyInfo.RedirectStandardError = $true
    $legacyInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $legacyInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $legacyProcess = [Diagnostics.Process]::Start($legacyInfo)
    try {
        $legacyOutput = $legacyProcess.StandardOutput.ReadToEndAsync()
        $legacyError = $legacyProcess.StandardError.ReadToEndAsync()
        $legacyProcess.WaitForExit()
        $normalText = $legacyOutput.GetAwaiter().GetResult()
        $errorText = $legacyError.GetAwaiter().GetResult()
        if ($normalText) { Write-Output $normalText.Trim() }
        if ($errorText) { [Console]::Error.Write($errorText) }
        exit $legacyProcess.ExitCode
    } finally { $legacyProcess.Dispose() }
}

[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$utf8 = New-Object Text.UTF8Encoding($false)
$checks = New-Object 'Collections.Generic.List[string]'
$appFullPath = [IO.Path]::GetFullPath($AppPath)
$fixturePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'BlockerFixture.exe'))
$testDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('packaged-worker-' + [Guid]::NewGuid().ToString('N'))))
if (!(Test-Path -LiteralPath $appFullPath -PathType Leaf)) { throw 'Packaged WinExe not found.' }
if (!(Test-Path -LiteralPath $fixturePath -PathType Leaf)) { throw 'Compile tests\BlockerFixture.cs before this test.' }
if (!$testDirectory.StartsWith([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test path is outside tests directory.' }
$null = New-Item -Path $testDirectory -ItemType Directory
$heldFile = Join-Path $testDirectory 'occupied-file.bin'
$readyFile = Join-Path $testDirectory 'ready.txt'
$stopFile = Join-Path $testDirectory 'stop.txt'
$fixtureProcess = $null
$fixtureStart = 0L
$ownedWorkers = New-Object 'Collections.Generic.List[object]'

function Assert-Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw $Description }
    $checks.Add($Description)
}

function Stop-OwnedProcess($Owned) {
    if ($null -eq $Owned -or $null -eq $Owned.Process) { return }
    $ownedProcess = $Owned.Process
    try {
        $ownedProcess.Refresh()
        if (!$ownedProcess.HasExited -and $ownedProcess.StartTime.ToUniversalTime().Ticks -eq $Owned.StartTicks -and
            [String]::Equals($ownedProcess.MainModule.FileName, $Owned.Executable, [StringComparison]::OrdinalIgnoreCase)) {
            $ownedProcess.Kill()
            $null = $ownedProcess.WaitForExit(5000)
        }
    } catch { }
}

function New-TargetData($Disk) {
    # Only public data travels over the real worker protocol. Its Revalidate
    # call obtains internal volume mappings from Windows again.
    return [ordered]@{
        Root = $Disk.Root; Label = $Disk.Label; Model = $Disk.Model
        Identity = $Disk.Identity; PnpId = $Disk.PnpId; DiskNumber = $Disk.DiskNumber
        CanEject = $Disk.CanEject; SafetyNote = $Disk.SafetyNote
        RelatedRoots = @($Disk.RelatedRoots)
    }
}

function Invoke-PackagedWorker($Target) {
    $request = [ordered]@{ Target = $Target; ParentPid = $PID; TimeoutSeconds = 38 }
    $json = ConvertTo-Json -InputObject $request -Depth 8 -Compress
    $encoded = [Convert]::ToBase64String($utf8.GetBytes($json))
    $workerInfo = New-Object Diagnostics.ProcessStartInfo
    $workerInfo.FileName = $appFullPath
    $workerInfo.Arguments = '--scan-worker ' + $encoded
    $workerInfo.UseShellExecute = $false
    $workerInfo.CreateNoWindow = $true
    $workerInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $workerInfo.WorkingDirectory = $env:WINDIR
    $workerInfo.RedirectStandardOutput = $true
    $workerInfo.RedirectStandardError = $true
    $workerInfo.StandardOutputEncoding = $utf8
    $workerInfo.StandardErrorEncoding = $utf8
    $workerProcess = [Diagnostics.Process]::Start($workerInfo)
    $ownership = [PSCustomObject]@{ Process = $workerProcess; StartTicks = $workerProcess.StartTime.ToUniversalTime().Ticks; Executable = $appFullPath }
    $ownedWorkers.Add($ownership)
    try {
        $stdout = $workerProcess.StandardOutput.ReadToEndAsync()
        $stderr = $workerProcess.StandardError.ReadToEndAsync()
        if (!$workerProcess.WaitForExit(40000)) {
            Stop-OwnedProcess $ownership
            throw 'Packaged worker exceeded the 40-second test deadline.'
        }
        $raw = $stdout.GetAwaiter().GetResult()
        $rawError = $stderr.GetAwaiter().GetResult()
        Assert-Check ($raw.IndexOf([char]0xFFFD) -lt 0 -and $rawError.IndexOf([char]0xFFFD) -lt 0) 'Worker UTF-8 streams contain no replacement characters'
        $events = @()
        foreach ($line in ($raw -split '[\r\n]+')) {
            if ([String]::IsNullOrWhiteSpace($line)) { continue }
            try { $events += (ConvertFrom-Json -InputObject $line) }
            catch { throw 'Packaged worker emitted malformed JSON (contents suppressed).' }
        }
        foreach ($event in $events) {
            if ($event.Message) { Assert-Check ($event.Message.IndexOf([char]0xFFFD) -lt 0) 'Worker message decodes without replacement characters' }
        }
        return [PSCustomObject]@{ ExitCode = $workerProcess.ExitCode; Events = $events; StderrEmpty = [String]::IsNullOrWhiteSpace($rawError) }
    } finally {
        Stop-OwnedProcess $ownership
        $null = $ownedWorkers.Remove($ownership)
        $workerProcess.Dispose()
    }
}

try {
    $image = [IO.File]::ReadAllBytes($appFullPath)
    $peOffset = [BitConverter]::ToInt32($image, 0x3c)
    Assert-Check ([BitConverter]::ToUInt16($image, $peOffset + 24 + 68) -eq 2) 'Test binary uses the Windows GUI subsystem, not a console harness'
    $assembly = [Reflection.Assembly]::LoadFrom($appFullPath)
    Assert-Check ($assembly.EntryPoint.DeclaringType.FullName -eq 'DiskRelease.Program') 'Test uses the shipped application entry point'
    $engine = $assembly.GetType('DiskRelease.DiskEngine', $true)
    $diskList = @($engine.GetMethod('ListDisks').Invoke($null, @()))
    $targetDisk = $diskList | Where-Object {
        $mapped = $false
        foreach ($root in $_.RelatedRoots) { if ($heldFile.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $mapped = $true } }
        $mapped
    } | Select-Object -First 1
    Assert-Check ($null -ne $targetDisk) 'Temporary fixture maps to a real local volume'

    if ($InvalidOnly) {
        $invalidTarget = New-TargetData $targetDisk
        $invalidTarget.Identity = 'QA-INVALID-DEVICE-' + [Guid]::NewGuid().ToString('N')
        $invalid = Invoke-PackagedWorker $invalidTarget
        $errors = @($invalid.Events | Where-Object { $_.Kind -eq 'error' })
        Assert-Check ($invalid.ExitCode -ne 0 -and $errors.Count -eq 1) 'Shipped WinExe worker rejects a stale device identity'
        Assert-Check ($errors[0].Message -match '磁盘身份') 'Failure message retains the expected Chinese identity explanation'
        Assert-Check (@($invalid.Events | Where-Object { $_.Kind -eq 'item' -or $_.Kind -eq 'end' }).Count -eq 0) 'Invalid identity never produces scan evidence or success'
        $result = [ordered]@{ passed = $checks.Count; checks = @($checks); testedBinary = $appFullPath; mode = 'invalid-identity-only'; chineseDecoded = $true; invalidIdentityRejected = $true; visibleWindowsOpened = 0 }
        $summary = ConvertTo-Json -InputObject $result -Depth 6
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath), $summary, $utf8)
        Write-Output $summary
        return
    }

    $fixtureInfo = New-Object Diagnostics.ProcessStartInfo
    $fixtureInfo.FileName = $fixturePath
    $fixtureInfo.Arguments = '"' + $heldFile + '" "' + $readyFile + '" "' + $stopFile + '"'
    $fixtureInfo.UseShellExecute = $false
    $fixtureInfo.CreateNoWindow = $true
    $fixtureInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $fixtureInfo.RedirectStandardOutput = $true
    $fixtureInfo.RedirectStandardError = $true
    $fixtureProcess = [Diagnostics.Process]::Start($fixtureInfo)
    $fixtureStart = $fixtureProcess.StartTime.ToUniversalTime().Ticks
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(8)
    while (!(Test-Path -LiteralPath $readyFile) -and [DateTime]::UtcNow -lt $readyDeadline) { Start-Sleep -Milliseconds 100 }
    Assert-Check (Test-Path -LiteralPath $readyFile) 'Owned fixture holds its test file'
    $identity = ([IO.File]::ReadAllText($readyFile)).Split('|')
    Assert-Check ([int]$identity[0] -eq $fixtureProcess.Id -and [long]$identity[1] -eq $fixtureStart) 'Owned fixture identity matches its process object'

    $valid = Invoke-PackagedWorker (New-TargetData $targetDisk)
    Assert-Check ($valid.ExitCode -eq 0) 'Shipped WinExe worker exits successfully'
    $blockerEvents = @($valid.Events | Where-Object { $_.Kind -eq 'item' -and $_.Item.Pid -eq $fixtureProcess.Id })
    Assert-Check ($blockerEvents.Count -gt 0) 'Shipped WinExe worker detects the owned blocker PID'
    $hasPath = $false
    foreach ($event in $blockerEvents) {
        foreach ($path in $event.Item.Paths) { if ($path.EndsWith('occupied-file.bin', [StringComparison]::OrdinalIgnoreCase)) { $hasPath = $true } }
    }
    Assert-Check $hasPath 'Shipped WinExe worker reports the fixture file path'
    $endEvents = @($valid.Events | Where-Object { $_.Kind -eq 'end' })
    Assert-Check ($endEvents.Count -eq 1 -and $endEvents[0].Complete -eq $true) 'Shipped WinExe worker completes the native handle scan'
    Assert-Check ($endEvents[0].Message -match '扫描|占用') 'Successful warning retains expected Chinese text'
    Assert-Check (@($valid.Events | Where-Object { $_.Kind -eq 'error' }).Count -eq 0) 'Successful worker emits no error event'

    $invalidTarget = New-TargetData $targetDisk
    $invalidTarget.Identity = 'QA-INVALID-DEVICE-' + [Guid]::NewGuid().ToString('N')
    $invalid = Invoke-PackagedWorker $invalidTarget
    $errors = @($invalid.Events | Where-Object { $_.Kind -eq 'error' })
    Assert-Check ($invalid.ExitCode -ne 0 -and $errors.Count -eq 1) 'Shipped WinExe worker rejects a stale device identity'
    Assert-Check ($errors[0].Message -match '磁盘身份') 'Failure message retains the expected Chinese identity explanation'
    Assert-Check (@($invalid.Events | Where-Object { $_.Kind -eq 'item' -or $_.Kind -eq 'end' }).Count -eq 0) 'Invalid identity never produces scan evidence or success'

    $probeSummary = 'not-requested'
    if ($ProbeDrive) {
        $requestedRoot = $ProbeDrive.TrimEnd('\') + '\'
        $probeDisk = $diskList | Where-Object { @($_.RelatedRoots) -contains $requestedRoot } | Select-Object -First 1
        if ($probeDisk) {
            $probe = Invoke-PackagedWorker (New-TargetData $probeDisk)
            $probeEnd = @($probe.Events | Where-Object { $_.Kind -eq 'end' })
            Assert-Check ($probe.ExitCode -eq 0 -and $probeEnd.Count -eq 1 -and $probeEnd[0].Complete) 'Optional external-drive worker probe completes read-only'
            $probeItems = @($probe.Events | Where-Object { $_.Kind -eq 'item' })
            $probeSummary = [PSCustomObject]@{ root = $requestedRoot; readOnly = $true; itemEvents = $probeItems.Count; processes = @($probeItems | ForEach-Object { $_.Item.Pid } | Sort-Object -Unique).Count; names = @($probeItems | ForEach-Object { $_.Item.Name } | Sort-Object -Unique) }
        } else { $probeSummary = 'requested-drive-not-connected' }
    }

    $result = [ordered]@{
        passed = $checks.Count; checks = @($checks); testedBinary = $appFullPath
        fixturePid = $fixtureProcess.Id; validItemEvents = @($valid.Events | Where-Object { $_.Kind -eq 'item' }).Count
        completed = $true; invalidIdentityRejected = $true; chineseDecoded = $true
        externalProbe = $probeSummary; visibleWindowsOpened = 0
    }
    $summary = ConvertTo-Json -InputObject $result -Depth 6
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath), $summary, $utf8)
    Write-Output $summary
} finally {
    foreach ($ownership in $ownedWorkers.ToArray()) { Stop-OwnedProcess $ownership }
    if ($null -ne $fixtureProcess) {
        [IO.File]::WriteAllText($stopFile, 'stop')
        if (!$fixtureProcess.WaitForExit(5000)) {
            Stop-OwnedProcess ([PSCustomObject]@{ Process = $fixtureProcess; StartTicks = $fixtureStart; Executable = $fixturePath })
        }
        $fixtureProcess.Dispose()
    }
    foreach ($filePath in @($heldFile, $readyFile, $stopFile)) { if (Test-Path -LiteralPath $filePath) { Remove-Item -LiteralPath $filePath -Force } }
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory }
}
