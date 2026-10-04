param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$ErrorPath,
    [Parameter(Mandatory = $true)][string]$TimingPath,
    [Parameter(Mandatory = $true)][string]$FilePath,
    [Parameter(Mandatory = $true)][string[]]$ArgumentList,
    [int]$MaxSeconds = 20,
    [long]$MaxPrivateBytes = 1GB,
    [long]$MaxOutputBytes = 1MB,
    [int]$SampleMilliseconds = 250
)

$ErrorActionPreference = 'Stop'

function Get-DescendantIds([int]$RootPid, [object[]]$Processes) {
    $result = [System.Collections.Generic.HashSet[int]]::new()
    $frontier = [System.Collections.Generic.Queue[int]]::new()
    [void]$result.Add($RootPid)
    $frontier.Enqueue($RootPid)
    while ($frontier.Count -gt 0) {
        $parentPid = $frontier.Dequeue()
        foreach ($item in $Processes | Where-Object { [int]$_.ParentProcessId -eq $parentPid }) {
            $childPid = [int]$item.ProcessId
            if ($result.Add($childPid)) { $frontier.Enqueue($childPid) }
        }
    }
    return @($result)
}

function Get-ProcessIdentity([int]$ProcessId, [object[]]$Processes) {
    $cim = $Processes | Where-Object { [int]$_.ProcessId -eq $ProcessId } | Select-Object -First 1
    $nativeCreationUtc = $null
    $nativeCreationTicks = $null
    $image = $null
    try {
        $item = Get-Process -Id $ProcessId -ErrorAction Stop
        $start = $item.StartTime.ToUniversalTime()
        $nativeCreationUtc = $start.ToString('O')
        $nativeCreationTicks = [int64]$start.Ticks
        $image = $item.Path
    } catch { }

    $cimCreationUtc = $null
    if ($null -ne $cim -and $null -ne $cim.CreationDate) {
        try { $cimCreationUtc = ([DateTime]$cim.CreationDate).ToUniversalTime().ToString('O') } catch { }
    }
    if ($null -eq $image -and $null -ne $cim) { $image = $cim.ExecutablePath }

    [ordered]@{
        pid = $ProcessId
        identity_status = if ($null -ne $nativeCreationTicks) { 'known' } else { 'unknown' }
        creation_ticks_utc = $nativeCreationTicks
        creation_utc = $nativeCreationUtc
        cim_creation_utc = $cimCreationUtc
        image_path = $image
        command_line = if ($null -ne $cim) { [string]$cim.CommandLine } else { $null }
    }
}

function Get-IdentityState([int]$ProcessId, [System.Collections.IDictionary]$Identity) {
    $item = $null
    try {
        $item = Get-Process -Id $ProcessId -ErrorAction Stop
    } catch {
        return 'not_running'
    }
    if ($Identity.identity_status -ne 'known' -or $null -eq $Identity.creation_ticks_utc) { return 'unknown' }
    try {
        $currentTicks = [int64]$item.StartTime.ToUniversalTime().Ticks
        if ($currentTicks -eq [int64]$Identity.creation_ticks_utc) { return 'match' }
        return 'different'
    } catch {
        return 'unknown'
    }
}

function Get-ProcessRole([System.Collections.IDictionary]$Identity) {
    $text = "{0} {1}" -f [string]$Identity.image_path, [string]$Identity.command_line
    if ($text -match '(?i)Qh\.Cli(?:\.exe)?') { return 'core' }
    if ($text -match '(?i)qh_signal_worker\.py') { return 'worker' }
    return 'other'
}

function Capture-OwnedIdentities([int]$RootPid, [object[]]$Processes, [hashtable]$IdentityMap) {
    $currentTree = @(Get-DescendantIds $RootPid $Processes)
    foreach ($processId in $currentTree) {
        $identity = Get-ProcessIdentity ([int]$processId) $Processes
        if (-not $IdentityMap.ContainsKey([int]$processId)) {
            $IdentityMap[[int]$processId] = $identity
        } elseif ($IdentityMap[[int]$processId].identity_status -eq 'unknown' -and $identity.identity_status -eq 'known') {
            $IdentityMap[[int]$processId] = $identity
        }
    }
    return $currentTree
}

function Stop-OwnedTree([int]$RootPid, [hashtable]$IdentityMap) {
    if ($IdentityMap.ContainsKey($RootPid) -and (Get-IdentityState $RootPid $IdentityMap[$RootPid]) -eq 'match') {
        $latestProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
        [void](Capture-OwnedIdentities $RootPid $latestProcesses $IdentityMap)
    }
    $actions = @()
    foreach ($processId in @($IdentityMap.Keys | Sort-Object -Descending)) {
        $currentPid = [int]$processId
        $identity = $IdentityMap[$currentPid]
        $state = Get-IdentityState $currentPid $identity
        if ($state -eq 'match') {
            try {
                Stop-Process -Id $currentPid -Force -ErrorAction Stop
                $action = 'stopped'
            } catch {
                $action = 'stop_failed'
            }
        } else {
            $action = 'skipped'
        }
        $actions += [ordered]@{
            pid = $currentPid
            identity_status = $identity.identity_status
            identity_state = $state
            action = $action
        }
    }
    [ordered]@{ root_pid = $RootPid; actions = @($actions) }
}

$resolvedFilePath = [System.IO.Path]::GetFullPath($FilePath)
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedErrorPath = [System.IO.Path]::GetFullPath($ErrorPath)
$resolvedTimingPath = [System.IO.Path]::GetFullPath($TimingPath)
$workingDirectory = (Get-Location).Path
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $resolvedFilePath
$startInfo.WorkingDirectory = $workingDirectory
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in $ArgumentList) { [void]$startInfo.ArgumentList.Add($argument) }

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
[void]$process.Start()
$stdoutStream = [System.IO.FileStream]::new($resolvedOutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read, 65536, [IO.FileOptions]::Asynchronous)
$stderrStream = [System.IO.FileStream]::new($resolvedErrorPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read, 65536, [IO.FileOptions]::Asynchronous)
$stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
$stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrStream)
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$identityMap = @{}
$stopEvents = @()
$initialProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
$identityMap[[int]$process.Id] = Get-ProcessIdentity $process.Id $initialProcesses
$peakPrivateBytes = 0L
$peakParentPrivateBytes = 0L
$sampleCount = 0
$limitKind = $null
$cleanupTimedOut = $false
$stdoutDrainCompleted = $false
$stderrDrainCompleted = $false

try {
    while (-not $process.HasExited) {
        $process.Refresh()
        $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
        $ownedPids = @(Capture-OwnedIdentities $process.Id $processes $identityMap)
        $aggregatePrivateBytes = 0L
        foreach ($processId in $ownedPids) {
            $identity = $identityMap[[int]$processId]
            if ($null -eq $identity -or (Get-IdentityState ([int]$processId) $identity) -ne 'match') { continue }
            try {
                $item = Get-Process -Id ([int]$processId) -ErrorAction Stop
                $privateBytes = [int64]$item.PrivateMemorySize64
                $aggregatePrivateBytes += $privateBytes
                if ($processId -eq $process.Id) { $peakParentPrivateBytes = [Math]::Max($peakParentPrivateBytes, $privateBytes) }
            } catch { }
        }
        $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $aggregatePrivateBytes)
        $sampleCount++
        $stdoutBytes = if (Test-Path -LiteralPath $resolvedOutputPath) { (Get-Item -LiteralPath $resolvedOutputPath).Length } else { 0L }
        $stderrBytes = if (Test-Path -LiteralPath $resolvedErrorPath) { (Get-Item -LiteralPath $resolvedErrorPath).Length } else { 0L }
        if ($watch.Elapsed.TotalSeconds -ge $MaxSeconds) { $limitKind = 'global_timeout'; $stopEvents += Stop-OwnedTree $process.Id $identityMap; break }
        if ($aggregatePrivateBytes -ge $MaxPrivateBytes) { $limitKind = 'private_memory_limit'; $stopEvents += Stop-OwnedTree $process.Id $identityMap; break }
        if ($stdoutBytes -ge $MaxOutputBytes -or $stderrBytes -ge $MaxOutputBytes) { $limitKind = 'output_limit'; $stopEvents += Stop-OwnedTree $process.Id $identityMap; break }
        Start-Sleep -Milliseconds $SampleMilliseconds
    }
    if (-not $process.WaitForExit(2000)) { $cleanupTimedOut = $true; $stopEvents += Stop-OwnedTree $process.Id $identityMap; [void]$process.WaitForExit(2000) }
}
finally {
    if (-not $process.HasExited) { $stopEvents += Stop-OwnedTree $process.Id $identityMap; if (-not $process.WaitForExit(2000)) { $cleanupTimedOut = $true } }
    $watch.Stop()
    try { $stdoutDrainCompleted = $stdoutTask.Wait(2000) } catch { $stdoutDrainCompleted = $false }
    try { $stderrDrainCompleted = $stderrTask.Wait(2000) } catch { $stderrDrainCompleted = $false }
    try { $stdoutStream.Flush() } catch { }
    try { $stderrStream.Flush() } catch { }
    $stdoutStream.Dispose()
    $stderrStream.Dispose()

    $pidExit = @($identityMap.Keys | Sort-Object | ForEach-Object {
        $currentPid = [int]$_
        $identity = $identityMap[$currentPid]
        $state = Get-IdentityState $currentPid $identity
        $aliveAfter = if ($state -eq 'match') { $true } elseif ($state -eq 'not_running') { $false } else { $null }
        [ordered]@{
            pid = $currentPid
            role = Get-ProcessRole $identity
            identity_status = $identity.identity_status
            identity_state = $state
            creation_ticks_utc = $identity.creation_ticks_utc
            creation_utc = $identity.creation_utc
            cim_creation_utc = $identity.cim_creation_utc
            image_path = $identity.image_path
            command_line = $identity.command_line
            alive_after = $aliveAfter
        }
    })
    $finalStdoutBytes = if (Test-Path -LiteralPath $resolvedOutputPath) { (Get-Item -LiteralPath $resolvedOutputPath).Length } else { 0L }
    $finalStderrBytes = if (Test-Path -LiteralPath $resolvedErrorPath) { (Get-Item -LiteralPath $resolvedErrorPath).Length } else { 0L }
    $parentIdentity = $identityMap[[int]$process.Id]
    $coreSummary = @($pidExit | Where-Object { $_.role -eq 'core' })
    $workerSummary = @($pidExit | Where-Object { $_.role -eq 'worker' })
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }
    $result = [ordered]@{
        wall_ms = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
        exit_code = $exitCode
        cli_exit_code = $exitCode
        parent_pid = $process.Id
        parent_creation_ticks_utc = $parentIdentity.creation_ticks_utc
        parent_creation_utc = $parentIdentity.creation_utc
        file_path = $resolvedFilePath
        working_directory = $workingDirectory
        argument_list = @($ArgumentList)
        max_private_bytes = $peakPrivateBytes
        parent_peak_private_bytes = $peakParentPrivateBytes
        sample_count = $sampleCount
        max_seconds = $MaxSeconds
        max_private_limit_bytes = $MaxPrivateBytes
        max_output_limit_bytes = $MaxOutputBytes
        sample_milliseconds = $SampleMilliseconds
        stdout_bytes = $finalStdoutBytes
        stderr_bytes = $finalStderrBytes
        limit_kind = $limitKind
        cleanup_timed_out = $cleanupTimedOut
        stdout_drain_completed = $stdoutDrainCompleted
        stderr_drain_completed = $stderrDrainCompleted
        monitor_output = [ordered]@{ stdout_path = $resolvedOutputPath; stderr_path = $resolvedErrorPath; timing_path = $resolvedTimingPath }
        core_summary = @($coreSummary)
        worker_summary = @($workerSummary)
        stop_events = @($stopEvents)
        pids = @($pidExit)
    }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resolvedTimingPath -Encoding UTF8
}
