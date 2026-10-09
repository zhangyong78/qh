param([string]$ProjectRoot = 'D:\mycode\qh')
$ErrorActionPreference = 'Stop'
$taskData = Join-Path $ProjectRoot 'artifacts\sr2701-15m\wenhua-aligned-data'
$taskOut = Join-Path $taskData 'multiside-research-20261008'
$taskManifest = Get-Content -LiteralPath (Join-Path $taskOut 'final-main-release.json') -Raw | ConvertFrom-Json
$taskExpectedApp = Join-Path $ProjectRoot 'artifacts\final-main-ui-build-stopfix-20261009\Release\net10.0-windows\win-x64\QhApp.exe'
$taskDesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) '期货研究与回测工作台.lnk'
$taskShortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($taskDesktopLink)
if ($taskShortcut.Arguments -notmatch [regex]::Escape((Join-Path $ProjectRoot 'tools\start-dev.ps1'))) { throw '桌面快捷方式不是当前主目录入口' }
$taskResults = @()
for ($taskAttempt = 1; $taskAttempt -le 2; $taskAttempt++) {
    $taskStarted = Get-Date
    $taskShell = Start-Process -FilePath $taskShortcut.TargetPath -ArgumentList $taskShortcut.Arguments -WorkingDirectory $ProjectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut "stopfix-launcher-$taskAttempt.stdout.log") -RedirectStandardError (Join-Path $taskOut "stopfix-launcher-$taskAttempt.stderr.log")
    $taskApp = $null
    $taskLine = $null
    try {
        for ($taskPoll = 0; $taskPoll -lt 30; $taskPoll++) {
            $taskProcesses = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='QhApp.exe'")
            $taskDotnetIds = @($taskProcesses | Where-Object { $_.ParentProcessId -eq $taskShell.Id } | ForEach-Object ProcessId)
            $taskCandidate = $taskProcesses | Where-Object { $_.ExecutablePath -eq $taskExpectedApp -and $_.ParentProcessId -in $taskDotnetIds -and $_.CreationDate -ge $taskStarted } | Select-Object -First 1
            if ($taskCandidate) {
                $taskApp = Get-Process -Id $taskCandidate.ProcessId
                $taskLine = Get-Content -LiteralPath (Join-Path $taskData 'startup-load.log') -Tail 30 | Where-Object { $_ -match "pid=$($taskApp.Id) " -and $_ -like '*trade-run-opened:*' } | Select-Object -Last 1
                if ($taskLine) { break }
            }
            Start-Sleep -Seconds 1
        }
        if (-not $taskApp -or -not $taskLine) { throw '桌面入口没有在30秒内加载保存结果' }
        # Snapshot ID is the durable period check.
        $taskPassed = $taskLine -like "*run=$($taskManifest.startup_run_id),*" -and $taskLine -like '*snapshot=snapshot-37c8f4717a61ae49,*' -and $taskLine -like '*bars=3893,*' -and $taskLine -like '*CZCE.SR701*' -and $taskLine -like "*net=$($taskManifest.expected.long.net_pnl)*" -and $taskLine -like "*drawdown=$($taskManifest.expected.long.max_drawdown)*" -and $taskLine -like '*ema=EMA21*'
        if (-not $taskPassed) { throw "启动数据/结果不符合定稿：$taskLine" }
        $taskResults += [pscustomobject]@{attempt=$taskAttempt;passed=$taskPassed;process_id=$taskApp.Id;app=$taskExpectedApp;startup_line=$taskLine}
    } finally {
        # Close only the application process launched and ancestry-verified by this check.
        if ($taskApp -and -not $taskApp.HasExited) {
            $null = $taskApp.CloseMainWindow()
            if (-not $taskApp.WaitForExit(5000)) { Stop-Process -Id $taskApp.Id }
        }
        if (-not $taskShell.WaitForExit(5000)) { Stop-Process -Id $taskShell.Id }
    }
}
[pscustomobject]@{passed=$true;timestamp=(Get-Date).ToString('o');checks=$taskResults} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'stopfix-launcher-verification-20261009.json') -Encoding utf8
Write-Output 'PASS desktop launcher: two actual cold starts; final build, SR701 15m, 3893 bars, default main saved run, EMA and net/DD verified.'
