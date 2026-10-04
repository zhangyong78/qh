param(
    [string]$PackageRoot = 'artifacts\g6-offline-package-20261002-r20',
    [string]$OutputRoot = 'artifacts\G6 P08 r20-final-20261002',
    [int]$SampleIntervalSeconds = 1
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = [System.IO.Path]::GetFullPath((Join-Path $repo $PackageRoot))
$output = [System.IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
if (-not (Test-Path -LiteralPath $package)) { throw "PackageRoot missing: $package" }
if (Test-Path -LiteralPath $output) { throw "OutputRoot already exists; use a new evidence directory: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$appDir = Join-Path $package 'app'
$cliDir = Join-Path $package 'cli'
$localApp = Join-Path $output 'localappdata'
$dataRoot = Join-Path $output 'data'
$samplesPath = Join-Path $output 'samples.jsonl'
$cyclesPath = Join-Path $output 'cycles.jsonl'
$fixture = Join-Path $package 'fixtures\g3-bars-f01.csv'
$millionCsv = Join-Path $repo 'artifacts\supervisor-g3-repair-20261002\perf-1000000-daily.csv'
$perfStrategy = Join-Path $package 'strategies\perf_zero_trade.py'
$app = $null
$activeCli = $null
$sampleClock = [Diagnostics.Stopwatch]::StartNew()
$samples = [System.Collections.Generic.List[object]]::new()
$cycles = [System.Collections.Generic.List[object]]::new()
$seenProcesses = [System.Collections.Generic.Dictionary[int, object]]::new()

function Start-ManagedProcess([string]$fileName, [string[]]$arguments, [hashtable]$environment = @{}) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $fileName
    $psi.WorkingDirectory = Split-Path -Parent $fileName
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    foreach ($pair in $environment.GetEnumerator()) { $psi.Environment[$pair.Key] = [string]$pair.Value }
    foreach ($argument in $arguments) { [void]$psi.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($psi)
}

function Get-OwnedProcessIds {
    $roots = @()
    if ($null -ne $script:app -and -not $script:app.HasExited) { $roots += $script:app.Id }
    if ($null -ne $script:activeCli -and -not $script:activeCli.HasExited) { $roots += $script:activeCli.Id }
    $roots = @($roots | Select-Object -Unique)
    if ($roots.Count -eq 0) { return @() }
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath)
    $owned = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($rootId in $roots) { [void]$owned.Add([int]$rootId) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($item in $all) {
            if ($owned.Contains([int]$item.ParentProcessId) -and $owned.Add([int]$item.ProcessId)) { $changed = $true }
        }
    }
    return @($owned)
}

function Get-ProcessStartTimeText([Diagnostics.Process]$process) {
    try { return $process.StartTime.ToUniversalTime().ToString('O') } catch { return $null }
}

function Register-OwnedProcessIdentities {
    foreach ($id in (Get-OwnedProcessIds)) {
        if ($script:seenProcesses.ContainsKey([int]$id)) { continue }
        try {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction Stop
            $process = Get-Process -Id $id -ErrorAction Stop
            $script:seenProcesses[[int]$id] = [PSCustomObject]@{
                pid = [int]$id
                name = $process.ProcessName
                executable_path = $cim.ExecutablePath
                parent_pid = [int]$cim.ParentProcessId
                start_time = Get-ProcessStartTimeText $process
            }
        } catch { }
    }
}

function Add-Sample([string]$phase) {
    Register-OwnedProcessIdentities
    $items = @()
    foreach ($id in (Get-OwnedProcessIds)) {
        try {
            $process = Get-Process -Id $id -ErrorAction Stop
            $process.Refresh()
            $items += [PSCustomObject]@{
                pid = $process.Id
                parent_pid = (@(Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" | Select-Object -ExpandProperty ParentProcessId -ErrorAction SilentlyContinue) | Select-Object -First 1)
                name = $process.ProcessName
                working_set_mb = [math]::Round($process.WorkingSet64 / 1MB, 3)
                private_mb = [math]::Round($process.PrivateMemorySize64 / 1MB, 3)
                cpu_s = [math]::Round($process.CPU, 3)
            }
        } catch { }
    }
    $sample = [PSCustomObject]@{ timestamp = [DateTimeOffset]::UtcNow; elapsed_s = [math]::Round($script:sampleClock.Elapsed.TotalSeconds, 3); phase = $phase; processes = $items }
    $script:samples.Add($sample)
    Add-Content -LiteralPath $samplesPath -Value ($sample | ConvertTo-Json -Compress -Depth 6) -Encoding UTF8
}

function Wait-ManagedProcess([Diagnostics.Process]$process, [string]$phase) {
    while (-not $process.HasExited) {
        Add-Sample $phase
        Start-Sleep -Seconds ([math]::Max(1, $SampleIntervalSeconds))
    }
    $process.WaitForExit()
    return [PSCustomObject]@{ exit_code = $process.ExitCode }
}

function Invoke-ImportIfNeeded {
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $snapshotDirs = @(Get-ChildItem (Join-Path $dataRoot 'snapshots') -Directory -ErrorAction SilentlyContinue)
    if ($snapshotDirs.Count -gt 0) { return $snapshotDirs[0].Name }
    $import = Start-ManagedProcess (Join-Path $cliDir 'Qh.Cli.exe') @('import', $millionCsv, $dataRoot) @{ LOCALAPPDATA = $localApp }
    $result = Wait-ManagedProcess $import 'import'
    if ($result.exit_code -ne 0) { throw "import failed with exit=$($result.exit_code)" }
    $snapshot = @(Get-ChildItem (Join-Path $dataRoot 'snapshots') -Directory | Sort-Object LastWriteTime | Select-Object -Last 1).Name
    if ([string]::IsNullOrWhiteSpace($snapshot)) { throw 'snapshot id missing after import' }
    return $snapshot
}

function Invoke-Research([string]$label, [string[]]$arguments, [bool]$expectCompleted) {
    $beforeRuns = @(Get-ChildItem (Join-Path $dataRoot 'trade-runs') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    $script:activeCli = Start-ManagedProcess (Join-Path $cliDir 'Qh.Cli.exe') $arguments @{ LOCALAPPDATA = $localApp }
    Register-OwnedProcessIdentities
    $result = Wait-ManagedProcess $script:activeCli $label
    $script:activeCli = $null
    $runId = @(Get-ChildItem (Join-Path $dataRoot 'trade-runs') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $beforeRuns } | Sort-Object LastWriteTime | Select-Object -Last 1).Name
    if ([string]::IsNullOrWhiteSpace($runId)) { $runId = $null }
    $runRecord = if ($runId -and (Test-Path (Join-Path $dataRoot "trade-runs\$runId\run.json"))) { Get-Content (Join-Path $dataRoot "trade-runs\$runId\run.json") -Raw | ConvertFrom-Json } else { $null }
    $cycle = [PSCustomObject]@{ label = $label; exit_code = $result.exit_code; run_id = $runId; status = $runRecord.Status; processed_rows = $runRecord.ProcessedRows; partial = ($null -ne $runRecord -and $runRecord.Status -ne 'completed'); expected_completed = $expectCompleted }
    $script:cycles.Add($cycle)
    Add-Content -LiteralPath $cyclesPath -Value ($cycle | ConvertTo-Json -Compress) -Encoding UTF8
    if ($expectCompleted -and ($result.exit_code -ne 0 -or $runRecord.Status -ne 'completed')) { throw "research cycle failed: $($cycle | ConvertTo-Json -Compress)" }
    return $cycle
}

$snapshot = Invoke-ImportIfNeeded
$packageZip = Join-Path $repo ($PackageRoot + '.zip')
$packageInfo = [PSCustomObject]@{ package_root = $package; package_zip = $packageZip; package_sha256 = (Get-FileHash $packageZip -Algorithm SHA256).Hash; started_at = [DateTimeOffset]::UtcNow; snapshot = $snapshot; sample_interval_seconds = $SampleIntervalSeconds }
$packageInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'package.json') -Encoding UTF8

$app = Start-ManagedProcess (Join-Path $appDir 'QhApp.exe') @() @{ LOCALAPPDATA = $localApp }
Register-OwnedProcessIdentities
$warmup = [Diagnostics.Stopwatch]::StartNew()
while ($warmup.Elapsed.TotalSeconds -lt 15) { if ($app.HasExited) { throw "QhApp exited during warmup: $($app.ExitCode)" }; Add-Sample 'warmup'; Start-Sleep -Seconds ([math]::Max(1, $SampleIntervalSeconds)) }
$warmup.Stop()
Add-Sample 'baseline_before_cycles'
$start = '2026-01-01T09:00:00+08:00'
$end100k = ([DateTimeOffset]::Parse($start)).AddDays(100000).ToString('O')
$common = @('trade-replay', $dataRoot, $snapshot, $perfStrategy, $start, $end100k)
for ($index = 1; $index -le 3; $index++) { Invoke-Research "trade_100k_$index" $common $true | Out-Null }
$timeoutStrategy = Join-Path $package 'strategies\trade_timeout.py'
$timeoutArgs = @('trade-replay', $dataRoot, $snapshot, $timeoutStrategy, $start, (([DateTimeOffset]::Parse($start)).AddDays(3).ToString('O')))
Invoke-Research 'trade_timeout_partial' $timeoutArgs $false | Out-Null
Add-Sample 'baseline_after_cycles'
$baselineClock = [Diagnostics.Stopwatch]::StartNew()
while ($baselineClock.Elapsed.TotalSeconds -lt 20) { Add-Sample 'post_cycle_baseline'; Start-Sleep -Seconds 5 }

$closeRequested = $app.CloseMainWindow()
$normalExit = $app.WaitForExit(10000)
Register-OwnedProcessIdentities
$remaining = @(Get-OwnedProcessIds)
$exitChecks = @($seenProcesses.Values | ForEach-Object {
    $current = $null
    try {
        $currentCim = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.pid)" -ErrorAction Stop
        $currentProcess = Get-Process -Id $_.pid -ErrorAction Stop
        $current = [PSCustomObject]@{ name = $currentProcess.ProcessName; executable_path = $currentCim.ExecutablePath; start_time = Get-ProcessStartTimeText $currentProcess }
    } catch { }
    $sameIdentity = $null -ne $current -and $current.executable_path -eq $_.executable_path -and $current.start_time -eq $_.start_time
    [PSCustomObject]@{ pid = $_.pid; seen = $_; exited = $null -eq $current; pid_reused = $null -ne $current -and -not $sameIdentity; current = $current }
})
$exitInfo = [PSCustomObject]@{ close_requested = $closeRequested; normal_exit = $normalExit; app_pid = $app.Id; app_exit_code = if ($app.HasExited) { $app.ExitCode } else { $null }; remaining_owned_pids = $remaining; seen_processes = @($seenProcesses.Values); exit_checks = $exitChecks; seen_all_exited = @($exitChecks | Where-Object { -not $_.exited -and -not $_.pid_reused }).Count -eq 0; checked_at = [DateTimeOffset]::UtcNow }
$exitInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'exit-pids.json') -Encoding UTF8

$allProcessSamples = @($script:samples | ForEach-Object { $_.processes })
$summary = [PSCustomObject]@{
    package = $packageInfo
    warmup_seconds = [math]::Round($warmup.Elapsed.TotalSeconds, 3)
    cycles = $script:cycles
    sample_count = $script:samples.Count
    sample_file = $samplesPath
    cycle_file = $cyclesPath
    app_working_set_min_mb = (($allProcessSamples | Where-Object name -eq 'QhApp' | Measure-Object working_set_mb -Minimum).Minimum)
    app_working_set_max_mb = (($allProcessSamples | Where-Object name -eq 'QhApp' | Measure-Object working_set_mb -Maximum).Maximum)
    python_working_set_max_mb = (($allProcessSamples | Where-Object name -match '^python' | Measure-Object working_set_mb -Maximum).Maximum)
    python_samples = @($allProcessSamples | Where-Object name -match '^python').Count
    partial_cycle_count = @($script:cycles | Where-Object partial).Count
    seen_pid_count = $seenProcesses.Count
    seen_all_exited = $exitInfo.seen_all_exited
    exit = $exitInfo
    evidence_note = '只监视本脚本启动的 QhApp、Qh.Cli 及其后代 PID；samples.jsonl 为低频研究期间采样，post_cycle_baseline 单独标记，不能外推长期稳定性。'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'summary.json') -Encoding UTF8
$app.Dispose()
'P08_EVIDENCE=' + $output
Get-Content (Join-Path $output 'summary.json') -Raw
