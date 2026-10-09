param([string]$TradeRunId = '')

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'src\QhApp\QhApp.csproj'
$dataRoot = Join-Path $projectRoot 'artifacts\sr2701-15m\wenhua-aligned-data'
if (-not [string]::IsNullOrWhiteSpace($env:QH_DATA_ROOT)) {
    $dataRoot = [System.IO.Path]::GetFullPath($env:QH_DATA_ROOT)
} elseif (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'index.sqlite'))) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "白糖 15/30/60 分钟行情索引不存在：$dataRoot",
        '期货研究与回测工作台',
        'OK',
        'Error'
    ) | Out-Null
    exit 1
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
$sdkList = if ($dotnetExe) { & $dotnetExe --list-sdks 2>$null }
if (-not $sdkList) {
    $userSdkDotnet = Join-Path $env:LOCALAPPDATA 'Programs\dotnet-sdk-10\dotnet.exe'
    if (Test-Path -LiteralPath $userSdkDotnet) {
        $dotnetExe = $userSdkDotnet
        $sdkList = & $dotnetExe --list-sdks 2>$null
    }
}

if (-not $sdkList) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        '启动开发版需要安装 .NET 10 SDK。安装后此快捷方式会直接运行当前项目源码。',
        '期货研究与回测工作台',
        'OK',
        'Warning'
    ) | Out-Null
    exit 1
}

Set-Location $projectRoot
$appArguments = @('--data-root', $dataRoot)
if ([string]::IsNullOrWhiteSpace($TradeRunId)) {
    $finalManifest = Join-Path $dataRoot 'multiside-research-20261008\final-main-release.json'
    if (Test-Path -LiteralPath $finalManifest) {
        $TradeRunId = (Get-Content -LiteralPath $finalManifest -Raw | ConvertFrom-Json).startup_run_id
    } else {
        $TradeRunId = 'trd-20261008151236152-fab7280c0'
    }
}
if (-not [string]::IsNullOrWhiteSpace($TradeRunId)) {
    if ($TradeRunId -ne [System.IO.Path]::GetFileName($TradeRunId) -or $TradeRunId -in @('.', '..')) {
        throw '回测 run ID 无效。'
    }
    $runRecord = Join-Path $dataRoot "trade-runs\$TradeRunId\run.json"
    if (Test-Path -LiteralPath $runRecord) {
        $appArguments += @('--trade-run', $TradeRunId)
    }
}
$researchBuildRoot = (Join-Path $projectRoot "artifacts\final-main-ui-build-exit-reason-left-20261009") + [System.IO.Path]::DirectorySeparatorChar
& $dotnetExe run --project $projectFile --configuration Release "--property:BaseOutputPath=$researchBuildRoot" -- @appArguments
exit $LASTEXITCODE
