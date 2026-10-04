param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path $PackageRoot).Path
$python = Join-Path $root 'python\python.exe'
$site = Join-Path $root 'python\site-packages'
$requirements = Join-Path $repo 'requirements-tqsdk.txt'
if (-not (Test-Path -LiteralPath $python)) { throw "项目隔离 Python 不存在：$python" }
if (-not (Test-Path -LiteralPath $requirements)) { throw "依赖清单不存在：$requirements" }
$systemPythonCommand = Get-Command python.exe -ErrorAction Stop
New-Item -ItemType Directory -Path $site -Force | Out-Null
& $systemPythonCommand.Source -m pip install --disable-pip-version-check --no-input --upgrade --target $site -r $requirements
if ($LASTEXITCODE -ne 0) { throw "TqSdk 项目隔离安装失败：$LASTEXITCODE" }
& $python -c "import sys; sys.path.insert(0, r'$site'); import tqsdk; print(tqsdk.__version__)"
if ($LASTEXITCODE -ne 0) { throw "项目隔离 Python 无法加载 TqSdk" }
Write-Output "tqsdk_site=$site"
