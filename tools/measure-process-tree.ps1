param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$ErrorPath,
    [Parameter(Mandatory = $true)][string]$TimingPath,
    [Parameter(Mandatory = $true)][string]$FilePath,
    [Parameter(Mandatory = $true)][string[]]$ArgumentList
)

$ErrorActionPreference = 'Stop'
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $FilePath
$startInfo.WorkingDirectory = (Get-Location).Path
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in $ArgumentList) { [void]$startInfo.ArgumentList.Add($argument) }
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
[void]$process.Start()
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$peakParent = 0L
$peakChildren = 0L
$sampleCount = 0
$childPids = [System.Collections.Generic.List[int]]::new()
while (-not $process.HasExited) {
    $process.Refresh()
    try {
        $parent = Get-Process -Id $process.Id -ErrorAction Stop
        $peakParent = [Math]::Max($peakParent, [int64]$parent.WorkingSet64)
    } catch { }
    $children = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $process.Id })
    foreach ($child in $children) {
        if (-not $childPids.Contains([int]$child.ProcessId)) { [void]$childPids.Add([int]$child.ProcessId) }
        try {
            $childProcess = Get-Process -Id ([int]$child.ProcessId) -ErrorAction Stop
            $peakChildren = [Math]::Max($peakChildren, [int64]$childProcess.WorkingSet64)
        } catch { }
    }
    $sampleCount++
    Start-Sleep -Milliseconds 250
}
$process.WaitForExit()
$stdoutTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$stderrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $ErrorPath -Encoding UTF8
$watch.Stop()
$childPidText = ($childPids | Sort-Object) -join ','
$line = "wall_ms=$($watch.ElapsedMilliseconds);exit_code=$($process.ExitCode);parent_pid=$($process.Id);parent_peak_working_set_bytes=$peakParent;direct_child_peak_working_set_bytes=$peakChildren;sample_count=$sampleCount;child_pids=$childPidText"
Set-Content -LiteralPath $TimingPath -Value $line -Encoding UTF8
Write-Output $line
Get-Content -LiteralPath $OutputPath -Tail 5
