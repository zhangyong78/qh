$projectRoot = Split-Path $PSScriptRoot -Parent
$app = Join-Path $projectRoot 'artifacts/sr2701-backtest-20261003/app/QhApp.exe'
$data = Join-Path $projectRoot 'artifacts/sr2701-15m/wenhua-aligned-data'
if (-not (Test-Path -LiteralPath $app)) { throw "未找到回测应用：$app" }
if (-not (Test-Path -LiteralPath $data)) { throw "未找到真实行情及回测数据：$data" }
Start-Process -FilePath $app -ArgumentList @('--data-root', $data, '--trade-run', 'trd-20261004000756277-b95fb9a00')
