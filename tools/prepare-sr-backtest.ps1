param([string]$Directory = 'artifacts/sr2701-15m')
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$source = Join-Path $Directory 'tqsdk-history.json'
$market = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
if (-not $market.ok) { throw '行情下载未成功。' }
$bars = @($market.bars | Where-Object is_complete | Sort-Object { [DateTimeOffset]$_.timestamp })
if ($bars.Count -le 60) { throw '数据不足以预热60周期均线。' }
$csv = Join-Path $Directory 'SR2701-15m.csv'
$bars | Select-Object timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding utf8
$summary = [ordered]@{
    symbol='CZCE.SR701'; display_symbol='SR2701'; period='15m'; provider='TqSdk 3.8.7 / get_kline_serial'
    source_sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    fetched_rows=$market.bars.Count; accepted_rows=$bars.Count; omitted_unconfirmed_rows=$market.bars.Count-$bars.Count
    first=$bars[0].timestamp; last=$bars[-1].timestamp; warmup=60
    backtest_start=$bars[60].timestamp; backtest_end=([DateTimeOffset]$bars[-1].timestamp).AddMinutes(15).ToString('o')
    fast_period=20; slow_period=60; average='sma'
    coverage_note=if ($market.bars.Count -eq $bars.Count) {
        '实际返回的历史区间；各根K线均有供应商标记、后继K线或已超过收盘时间作为完成证据；不宣称覆盖上市以来全部行情。'
    } else {
        '实际返回的历史区间；未完成K线已排除；不宣称覆盖上市以来全部行情。'
    }
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'data-provenance.json') -Encoding utf8
$summary | ConvertTo-Json -Compress
