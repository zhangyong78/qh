param(
    [ValidateSet('probe','history')][string]$Operation = 'probe',
    [string]$Symbol = 'CZCE.SR701',
    [string]$Start = '2025-01-01T00:00:00+08:00',
    [string]$End = '2026-10-03T00:00:00+08:00',
    [switch]$Professional,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runtimeRoot = Join-Path $projectRoot 'artifacts/g6-offline-package-20261003-tqsdk-diagnostics-r2'
$vaultPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'QhResearch/tqsdk-credential.dpapi'
if (-not (Test-Path -LiteralPath $vaultPath)) { throw '请先在应用中保存天勤账号密码。' }
$protectedBytes = [IO.File]::ReadAllBytes($vaultPath)
$plainBytes = [Security.Cryptography.ProtectedData]::Unprotect($protectedBytes, [Text.Encoding]::UTF8.GetBytes('QhResearch/TqSdk/CurrentUser/v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
try {
    $credential = [Text.Encoding]::UTF8.GetString($plainBytes) | ConvertFrom-Json
    $request = @{ op=$Operation; account=$credential.Account; password=$credential.Password }
    if ($Operation -eq 'history') {
        $request.symbol=$Symbol; $request.duration_seconds=900
        $request.start=$Start; $request.end=$End; $request.professional=[bool]$Professional
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $runtimeRoot 'app/python/python.exe'
    $info.ArgumentList.Add((Join-Path $projectRoot 'worker/qh_tqsdk_bridge.py'))
    $info.Environment['QH_TQSDK_PATH'] = Join-Path $runtimeRoot 'tqsdk-site'
    $info.WorkingDirectory=$projectRoot
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process = [Diagnostics.Process]::new(); $process.StartInfo=$info
    [void]$process.Start()
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress))
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw '天勤请求超时。' }
    $raw=$stdout.GetAwaiter().GetResult()
    [void]$stderr.GetAwaiter().GetResult()
    $result=$raw | ConvertFrom-Json
    if ($OutputPath -and $result.ok) {
        $destination=[IO.Path]::GetFullPath($OutputPath)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::WriteAllText($destination,$raw,[Text.UTF8Encoding]::new($false))
    }
    if ($Operation -eq 'history' -and $result.ok) {
        [pscustomobject]@{ ok=$result.ok; rows=$result.bars.Count; first=$result.bars[0].timestamp; last=$result.bars[-1].timestamp; complete=$result.complete; professional=$result.used_professional_downloader; output=$OutputPath } | ConvertTo-Json -Compress
    } else { $result | ConvertTo-Json -Compress }
} finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($plainBytes)
    $credential=$null; $request=$null
    if ($process) { $process.Dispose() }
}
