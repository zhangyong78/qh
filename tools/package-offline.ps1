param(
    [string]$OutputRoot = "artifacts\g6-offline-package-20261002",
    [string]$PythonVersion = "3.12.10"
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$output = [System.IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts\g6-offline-build'))
$downloadRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts\g6-offline-downloads'))
$tqSdkRequirements = Join-Path $repo 'requirements-tqsdk.txt'

function Assert-SafePackageTarget([string]$path, [string]$label, [string]$leafPattern) {
    $full = [System.IO.Path]::GetFullPath($path)
    if ($full -eq $artifactsRoot -or (Split-Path -Parent $full) -ne $artifactsRoot -or (Split-Path -Leaf $full) -notmatch $leafPattern) {
        throw "$label must be a direct, explicitly named child of $artifactsRoot; refusing destructive operation: $full"
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$label must not be a reparse point: $full"
        }
    }
}

Assert-SafePackageTarget $output 'OutputRoot' '^g6-offline-package-[A-Za-z0-9._-]+$'
Assert-SafePackageTarget $buildRoot 'buildRoot' '^g6-offline-build$'
Assert-SafePackageTarget $downloadRoot 'downloadRoot' '^g6-offline-downloads$'
$dotnet = Join-Path $repo 'tools\dotnet\dotnet.exe'
$pythonZip = Join-Path $downloadRoot ("python-$PythonVersion-embed-amd64.zip")
$pythonUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $output, $buildRoot, $downloadRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $pythonZip)) {
    Invoke-WebRequest -Uri $pythonUrl -OutFile $pythonZip
}

$appPublish = Join-Path $buildRoot 'app'
$cliPublish = Join-Path $buildRoot 'cli'
& $dotnet publish (Join-Path $repo 'src\QhApp\QhApp.csproj') --configuration Release --runtime win-x64 --self-contained true --output $appPublish -p:PublishTrimmed=false --nologo
if ($LASTEXITCODE -ne 0) { throw "QhApp publish failed: $LASTEXITCODE" }
& $dotnet publish (Join-Path $repo 'src\Qh.Cli\Qh.Cli.csproj') --configuration Release --runtime win-x64 --self-contained true --output $cliPublish -p:PublishTrimmed=false --nologo
if ($LASTEXITCODE -ne 0) { throw "Qh.Cli publish failed: $LASTEXITCODE" }

function Install-PythonRuntime([string]$target) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $target -Force
    $pth = Get-ChildItem -LiteralPath $target -Filter '*._pth' | Select-Object -First 1
    if ($null -eq $pth) { throw "Embedded Python _pth file not found in $target" }
    Add-Content -LiteralPath $pth.FullName -Value 'import site' -Encoding ASCII
    & (Join-Path $target 'python.exe') -c 'import importlib.util,json,sys; print(sys.version.split()[0])'
    if ($LASTEXITCODE -ne 0) { throw "Embedded Python validation failed: $target" }
}

function Install-AppPayload([string]$target) {
    New-Item -ItemType Directory -Path (Join-Path $target 'worker'), (Join-Path $target 'strategies'), (Join-Path $target 'fixtures'), (Join-Path $target 'python') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'worker\qh_worker.py') -Destination (Join-Path $target 'worker\qh_worker.py')
    Copy-Item -LiteralPath (Join-Path $repo 'worker\qh_signal_worker.py') -Destination (Join-Path $target 'worker\qh_signal_worker.py')
    Copy-Item -LiteralPath (Join-Path $repo 'worker\qh_tqsdk_bridge.py') -Destination (Join-Path $target 'worker\qh_tqsdk_bridge.py')
    Get-ChildItem -LiteralPath (Join-Path $repo 'strategies') -Filter '*.py' -File | Copy-Item -Destination (Join-Path $target 'strategies')
    Get-ChildItem -LiteralPath (Join-Path $repo 'fixtures') -Filter '*.csv' -File | Copy-Item -Destination (Join-Path $target 'fixtures')
    Install-PythonRuntime (Join-Path $target 'python')
}

$appRoot = Join-Path $output 'app'
$cliRoot = Join-Path $output 'cli'
Copy-Item -LiteralPath $appPublish -Destination $appRoot -Recurse
Copy-Item -LiteralPath $cliPublish -Destination $cliRoot -Recurse
Install-AppPayload $appRoot
Install-AppPayload $cliRoot
$tqSdkSite = Join-Path $output 'tqsdk-site'
$systemPythonCommand = Get-Command python.exe -ErrorAction Stop
New-Item -ItemType Directory -Path $tqSdkSite -Force | Out-Null
& $systemPythonCommand.Source -m pip install --disable-pip-version-check --no-input --upgrade --target $tqSdkSite -r $tqSdkRequirements
if ($LASTEXITCODE -ne 0) { throw "TqSdk project-isolated install failed: $LASTEXITCODE" }
& (Join-Path $appRoot 'python\python.exe') -c "import sys; sys.path.insert(0, r'$tqSdkSite'); import tqsdk; print(tqsdk.__version__)"
if ($LASTEXITCODE -ne 0) { throw "TqSdk project-isolated runtime validation failed" }
New-Item -ItemType Directory -Path (Join-Path $output 'fixtures'), (Join-Path $output 'strategies') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'fixtures') -Filter '*.csv' -File | Copy-Item -Destination (Join-Path $output 'fixtures')
Get-ChildItem -LiteralPath (Join-Path $repo 'strategies') -Filter '*.py' -File | Copy-Item -Destination (Join-Path $output 'strategies')

New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
function Copy-NugetLicense([string]$packagePath, [string]$sourceName, [string]$destinationName) {
    $source = Join-Path $nugetRoot $packagePath
    if (-not (Test-Path -LiteralPath $source)) { throw "NuGet package license source is missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output "licenses\$destinationName")
}
Copy-NugetLicense 'duckdb.net.data.full\1.5.6\LICENSE.md' 'DuckDB.NET.Data.Full' 'DuckDB.NET.Data.Full-1.5.6-LICENSE.md'
Copy-NugetLicense 'duckdb.net.data.full\1.5.6\LICENSE-DuckDB.txt' 'DuckDB native' 'DuckDB-native-1.5.6-LICENSE.txt'
Copy-NugetLicense 'apache.arrow\23.0.0\LICENSE.txt' 'Apache Arrow' 'Apache.Arrow-23.0.0-LICENSE.txt'
Copy-NugetLicense 'apache.arrow.scalars\23.0.0\LICENSE.txt' 'Apache Arrow Scalars' 'Apache.Arrow.Scalars-23.0.0-LICENSE.txt'

$apacheLicense = @'
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

   "License" shall mean the terms and conditions for use, reproduction, and
   distribution as defined by Sections 1 through 9 of this document.

   "Licensor" shall mean the copyright owner or entity authorized by
   the copyright owner that is granting the License.

   "Legal Entity" shall mean the union of the acting entity and all
   other entities that control, are controlled by, or are under common
   control with that entity. For the purposes of this definition,
   "control" means (i) the power, direct or indirect, to cause the
   direction or management of such entity, whether by contract or
   otherwise, or (ii) ownership of fifty percent (50%) or more of the
   outstanding shares, or (iii) beneficial ownership of such entity.

   "You" (or "Your") shall mean an individual or Legal Entity
   exercising permissions granted by this License.

   "Source" form shall mean the preferred form for making modifications,
   including but not limited to software source code, documentation
   source, and configuration files.

   "Object" form shall mean any form resulting from mechanical
   transformation or translation of a Source form, including but
   not limited to compiled object code, generated documentation,
   and conversions to other media types.

   "Work" shall mean the work of authorship, whether in Source or Object
   form, made available under the License, as indicated by a copyright
   notice that is included in or attached to the work (an example is
   provided in the Appendix below).

   "Derivative Works" shall mean any work, whether in Source or Object
   form, that is based on (or derived from) the Work and for which the
   editorial revisions, annotations, elaborations, or other modifications
   represent, as a whole, an original work of authorship. For the purposes
   of this License, Derivative Works shall not include works that remain
   separable from, or merely link (or bind by name) to the interfaces of,
   the Work and Derivative Works thereof.

   "Contribution" shall mean any work of authorship, including
   the original version of the Work and any modifications or additions
   to that Work or Derivative Works thereof, that is intentionally
   submitted to Licensor for inclusion in the Work by the copyright owner
   or by an individual or Legal Entity authorized to submit on behalf of
   the copyright owner. For the purposes of this definition, "submitted"
   means any form of electronic, verbal, or written communication sent
   to the Licensor or its representatives, including but not limited to
   communication on electronic mailing lists, source code control systems,
   and issue tracking systems that are managed by, or on behalf of, the
   Licensor for the purpose of discussing and improving the Work, but
   excluding communication that is conspicuously marked or otherwise
   designated in writing by the copyright owner as "Not a Contribution."

   "Contributor" shall mean Licensor and any individual or Legal Entity
   on behalf of whom a Contribution has been received by Licensor and
   subsequently incorporated within the Work.

2. Grant of Copyright License. Subject to the terms and conditions of
   this License, each Contributor hereby grants to You a perpetual,
   worldwide, non-exclusive, no-charge, royalty-free, irrevocable
   copyright license to reproduce, prepare Derivative Works of,
   publicly display, publicly perform, sublicense, and distribute the
   Work and such Derivative Works in Source or Object form.

3. Grant of Patent License. Subject to the terms and conditions of
   this License, each Contributor hereby grants to You a perpetual,
   worldwide, non-exclusive, no-charge, royalty-free, irrevocable
   (except as stated in this section) patent license to make, have made,
   use, offer to sell, sell, import, and otherwise transfer the Work,
   where such license applies only to those patent claims licensable
   by such Contributor that are necessarily infringed by their
   Contribution(s) alone or by combination of their Contribution(s)
   with the Work to which such Contribution(s) was submitted. If You
   institute patent litigation against any entity (including a
   cross-claim or counterclaim in a lawsuit) alleging that the Work
   or a Contribution incorporated within the Work constitutes direct
   or contributory patent infringement, then any patent licenses
   granted to You under this License for that Work shall terminate
   as of the date such litigation is filed.

4. Redistribution. You may reproduce and distribute copies of the Work
   or Derivative Works thereof in any medium, with or without
   modifications, and in Source or Object form, provided that You meet
   the following conditions:

   (a) You must give any other recipients of the Work or Derivative Works
       a copy of this License; and

   (b) You must cause any modified files to carry prominent notices
       stating that You changed the files; and

   (c) You must retain, in the Source form of any Derivative Works that
       You distribute, all copyright, patent, trademark, and attribution
       notices from the Source form of the Work, excluding those notices
       that do not pertain to any part of the Derivative Works; and

   (d) If the Work includes a "NOTICE" text file as part of its
       distribution, then any Derivative Works that You distribute must
       include a readable copy of the attribution notices contained
       within such NOTICE file, excluding those notices that do not
       pertain to any part of the Derivative Works, in at least one
       of the following places: within a NOTICE text file distributed
       as part of the Derivative Works; within the Source form or
       documentation, if provided along with the Derivative Works; or,
       within a display generated by the Derivative Works, if and
       wherever such third-party notices normally appear. The contents
       of the NOTICE file are for informational purposes only and
       do not modify the License. You may add Your own attribution
       notices within Derivative Works that You distribute, alongside
       or as an addendum to the NOTICE text from the Work, provided
       that such additional attribution notices cannot be construed
       as modifying the License.

   (e) You may add Your own copyright statement to Your modifications
       and may provide additional or different license terms and
       conditions for use, reproduction, or distribution of Your
       modifications, or for any such Derivative Works as a whole,
       provided Your use, reproduction, and distribution of the Work
       otherwise complies with the conditions stated in this License.

5. Submission of Contributions. Unless You explicitly state otherwise,
   any Contribution intentionally submitted for inclusion in the Work
   by You to the Licensor shall be under the terms and conditions of
   this License, without any additional terms or conditions.

6. Trademarks. This License does not grant permission to use the trade
   names, trademarks, service marks, or product names of the Licensor,
   except as required for reasonable and customary use in describing
   the origin of the Work and reproducing the content of the NOTICE file.

7. Disclaimer of Warranty. Unless required by applicable law or
   agreed to in writing, Licensor provides the Work (and each
   Contributor provides its Contributions) on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
   implied, including, without limitation, any warranties or conditions
   of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
   PARTICULAR PURPOSE. You are solely responsible for determining the
   appropriateness of using or redistributing the Work and assume any
   risks associated with Your exercise of permissions under this License.

8. Limitation of Liability. In no event and under no legal theory,
   whether in tort (including negligence), contract, or otherwise,
   unless required by applicable law (such as deliberate and grossly
   negligent acts) or agreed to in writing, shall any Contributor be
   liable to You for damages, including any direct, indirect, special,
   incidental, or consequential damages of any character arising as a
   result of this License or out of the use or inability to use the
   Work (including but not limited to damages for loss of goodwill,
   work stoppage, computer failure or malfunction, or any and all
   other commercial damages or losses), even if such Contributor
   has been advised of the possibility of such damages.

9. Accepting Warranty or Additional Liability. While redistributing
   the Work or Derivative Works thereof, You may choose to offer,
   and charge a fee for, acceptance of support, warranty, indemnity,
   or other liability obligations and/or rights consistent with this
   License. However, in accepting such obligations, You may act only
   on Your own behalf and on Your sole responsibility, not on behalf
   of any other Contributor, and only if You agree to indemnify,
   defend, and hold each Contributor harmless for any liability
   incurred by, or claims asserted against, such Contributor by reason
   of your accepting any such warranty or additional liability.
'@
Set-Content -LiteralPath (Join-Path $output 'licenses\Apache-2.0.txt') -Value $apacheLicense -Encoding UTF8
$mitLicense = @'
MIT License

Copyright (c) the respective copyright holders listed in the dependency
packages and source notices distributed with this package.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
Set-Content -LiteralPath (Join-Path $output 'licenses\MIT.txt') -Value $mitLicense -Encoding UTF8

$thirdParty = @'
QH G6 offline package third-party notices and exact dependency versions

Runtime and framework:
- Microsoft .NET 10 self-contained win-x64 runtime: MIT. The runtime is
  redistributed by Microsoft; see https://github.com/dotnet/runtime/blob/main/LICENSE.TXT.
- Python 3.12.10 embeddable amd64: Python Software Foundation License;
  see https://docs.python.org/3/license.html and licenses/Python-3.12.10-LICENSE.txt.

Application dependencies (versions are from src/Qh.Core/Qh.Core.csproj and
the resolved project.assets.json):
- DuckDB.NET.Data.Full 1.5.6: MIT; licenses/DuckDB.NET.Data.Full-1.5.6-LICENSE.md and licenses/MIT.txt.
- DuckDB.NET.Bindings.Full 1.5.6 and native DuckDB 1.5.6: MIT/embedded
  DuckDB terms; licenses/DuckDB-native-1.5.6-LICENSE.txt.
- Apache.Arrow 23.0.0 and Apache.Arrow.Scalars 23.0.0: Apache-2.0;
  licenses/Apache.Arrow-23.0.0-LICENSE.txt and licenses/Apache-2.0.txt.
- Microsoft.Data.Sqlite 10.0.12 and Microsoft.Data.Sqlite.Core 10.0.12: MIT;
  licenses/MIT.txt; source notice https://github.com/dotnet/efcore/tree/v10.0.12/src/Microsoft.Data.Sqlite.
- SQLitePCLRaw.core, .bundle_e_sqlite3, .lib.e_sqlite3, and
  .provider.e_sqlite3 2.1.12: Apache-2.0; licenses/Apache-2.0.txt.

This package includes the project-isolated TqSdk runtime (target version 3.8.7)
under `tqsdk-site` for bridge import and runtime capability checks. It contains
no market-data account, notification service, CTP connector, or real-order
component; no account login is performed by packaging or `g4-gateway-check`.
Included fixtures and fee settings are synthetic acceptance data only.
'@
Set-Content -LiteralPath (Join-Path $output 'licenses\Python-3.12.10-LICENSE.txt') -Value (Invoke-WebRequest -Uri 'https://docs.python.org/3/license.html').Content -Encoding UTF8
Set-Content -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Value $thirdParty -Encoding UTF8

$readmeTemplate = @'
# QH G6 离线包（Windows x64）

## 1. 启动与第一次验证

1. 将整个包解压到一个新的目录，不要从压缩包内运行。
2. 双击 `app\QhApp.exe` 启动 WPF 离线研究应用；包内 Python 位于
   `app\python\python.exe`，不要求系统 Python 或修改 PATH。
3. 在 WPF 中导入 `fixtures\g3-bars-f01.csv`，确认数据质量为可发布；选择
   `strategies\period_signal.py`，先做信号回放，再选择
   `strategies\trade_sequence.py` 做交易回放。
4. 在 PowerShell 中运行下面的自检命令，确认控件、导入、图表、信号、成交、
   运行记录重开和取消链路都完成：

```powershell
& .\app\QhApp.exe --self-check .\self-check.json --self-check-data-root .\user-data
Get-Content .\self-check.json
```

自检是程序内部调用同一套 WPF 导入、回放和绑定路径，不等同于人工鼠标点击；
仍需在目标 Windows 机器上按上面的步骤做一次人工验收。省略
`--self-check-data-root` 时，程序会自动创建唯一临时隔离目录，不写入正常用户数据目录。

## 2. CLI 离线流程

```powershell
New-Item -ItemType Directory .\user-data -Force | Out-Null
& .\cli\Qh.Cli.exe import .\fixtures\g3-bars-f01.csv .\user-data
$snapshot = '<把 import 输出中的 snapshot-id 填在这里>'
& .\cli\Qh.Cli.exe quality-check .\user-data $snapshot
& .\cli\Qh.Cli.exe replay .\user-data $snapshot .\strategies\period_signal.py 2026-10-01T09:01:00+08:00 2026-10-01T09:03:00+08:00 period=1
& .\cli\Qh.Cli.exe trade-replay .\user-data $snapshot .\strategies\trade_sequence.py 2026-10-01T09:00:00+08:00 2026-10-01T09:03:00+08:00 scenario=f01 --slippage-ticks=1
```

CLI 输出中区分 signal intent、order intent 和 fill；只有有效 K 线完成后产生的
意图，才会在下一根有效 K 线开盘尝试成交。交易回放只接受具体合约；KQ 主连/指数、
888/999 连续合约和明确的 main/index 标识会被阻断，TEST/合成验收夹具例外，未知真实
合约属性不会被猜测。

G4 bridge 的无账号运行时检查和模拟夹具检查：

```powershell
& .\cli\Qh.Cli.exe g4-gateway-check
$fixtureDataRoot = Join-Path $env:TEMP ("qh-g4-fixture-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDataRoot -Force | Out-Null
& .\cli\Qh.Cli.exe g4-fixture-check $fixtureDataRoot (Join-Path $fixtureDataRoot 'g4-report.json')
```

前者只验证包内项目隔离 TqSdk 版本及 `DataDownloader` 模块可导入，不登录账号；
后者只验证明确标记的模拟夹具。真实账号、权限、行情覆盖和实时恢复仍需现场小范围验证。
运行时检查属于有界的依赖准备阶段；失败时 CLI 返回非零退出码，不代表真实连接成功。
WPF 的连接测试和实时入口也先执行同一依赖准备阶段，再进入网络握手/实时握手；取消和窗口关闭沿用同一个可取消令牌。

WPF 历史和实时入口还要求适用于具体合约和请求范围的本机 `calendar.json`，或通过
`QH_TQSDK_CALENDAR_PATH` 指定配置。配置至少包含 `source`、`valid_from`、`valid_to`、
`trading_days` 和带 `trading_day/start/end/symbol_scope` 的 `session_ranges`；跨午夜时段
使用实际自然日的 `start/end`，不能把交易日直接当自然日。缺失、合约不匹配、有效期或范围
不覆盖时会阻断；实时连续性也使用这些显式时段，未知时段不会被默认交易时段放行。

## 3. 策略合同

策略文件必须声明如下冻结元数据：`STRATEGY = {name, version, kind, warmup_bars, parameters}`。
`kind` 只能是 `signal` 或 `trade`；`warmup_bars` 是非负整数；每个参数必须声明
`type` 和 `default`，可选 `min/max/choices`。文件还必须提供
`create_strategy(parameters)`，返回实现 `on_bar(context)` 的对象。`period_signal.py`、
`sma_ema_cross.py` 和 `trade_sequence.py` 是随包示例。

context 属性包括：`bar`（当前已完成 K 线字典）、`bar_end`（bar 起点加 period 后的
ISO-8601 时间）、`is_formal`（是否已经越过 warmup）、`parameters`、`bar_index` 和
只读 `simulation`；指标方法为 `sma(period)`、`ema(period)`、`atr(period)`，不足预热
时返回 null。策略不能读取未来 K 线、访问网络/账号/下单接口；这是可信本地 Python，
不是安全沙箱。

signal 策略的 `on_bar` 返回 null、一个 signal 对象或对象列表；每个 signal 必须含
`category,direction,description`，可选 `reference_price`。trade 策略返回 null、一个
对象或列表；对象可含上述 `signal`，以及 `intent`，其 `action` 为 `open/close/reverse`、
`direction` 为 `long/short`、`quantity` 为正整数。只有正式完成 K 线产生的意图进入交易
账本，下一根有效 K 线开盘只尝试一次；异常、非法返回、超时或取消会结束本次 run 并保留
部分状态，不能把失败当作成功。每根 bar 从发送请求时建立一个绝对策略 deadline，读取
同一 bar 的多条 signal/intent/bar_done 会扣减剩余预算；超时诊断包含 bar_start、bar_end、
策略副本路径和阶段，父进程会终止隔离 Worker。初始化、完成和取消仍使用各自的协议/收尾期限。

CSV 必须包含 `timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete`。
`timestamp` 是 bar 开始时间，必须是带时区偏移的 ISO-8601；`period` 决定 `bar_end`，
`trading_day` 是供应商字段优先的交易所口径 `yyyy-MM-dd`；缺字段时仅使用 SDK 18:00/周末规则的有限 fallback，未验证节假日日历；`is_complete=true`
才可进入回放。导入会执行排序、重复、缺失值、OHLC 和时间连续性检查。严格模式
阻断质量失败，探索模式只允许显式的研究用途，不能把失败数据当成交易结果。

回放假设：手续费支持 fixed/rate 两种模式；交易回放使用显式 tick size、tick value、
margin rate、slippage、stop loss 和 take profit 参数；未知品种不会自动套用费用、保证金
或合约乘数。意图由完成 K 线产生，下一根有效 K 线开盘成交；结束时不强制平仓，未能
执行的末根意图记录 `no_next_valid_bar`。结果页可以查看 equity、orders、fills、signals
和 run metadata，复制交易配置，并从现有按钮导出 CSV/JSON；PNG 仅是 self-check 证据，
不是承诺的用户导出入口。这些都是本地模拟结果。

## 4. 自定义策略、备份与升级

不要修改包内 `strategies`。把自定义策略放在用户数据目录之外的独立目录，并通过 WPF
选择或 CLI 的显式路径加载。备份时复制整个 `%LOCALAPPDATA%\QhResearch\data` 或导出的
run 目录。升级时解压到新的目录，先复制旧用户数据到备份，再用新包指向同一数据目录；
不要覆盖旧包，出现问题可回退到旧目录。

## 5. 已知边界与人工验收清单

- 包内虽含项目隔离 TqSdk 运行时，但没有真实行情账号、通知、CTP 或实盘下单链路；
  `g4-gateway-check` 不登录账号。
- 冷启动依赖导入时间可能明显长于热启动；运行时探针只验证依赖准备和模块导入，不宣称真实网络连接、权限或行情覆盖。
- WPF 连接/实时阶段会分别显示依赖准备和握手状态；依赖准备失败或取消不会伪称已连接。
- P04 固定百万根性能目标已测失败：已有固定记录为 `128443.1954ms/completed/exit0`，超过 30 秒，目标口径不降低。buf64 的 `42460.8ms`、Worker `40278ms` 仅保留为开发报告值；对应原始外层/资源文件缺失，不能冒充独立原始证据，也不因本 README 重跑百万。性能数据不代表人工交互延迟。
- 在目标机器上检查：启动无系统 Python 依赖；导入/质量结果；信号与成交的下一根
  开盘语义；stop/take 与手续费；run 重开后日期、策略、参数保持；复制配置和导出文件；
  取消后进程退出且不残留 python worker。
- `fixtures` 和费用设置是合成验收数据，不能当作市场事实。
'@
$readme = $readmeTemplate.Replace('3.12.10', $PythonVersion)
Set-Content -LiteralPath (Join-Path $output 'README-offline.md') -Value $readme -Encoding UTF8

$manifestPath = Join-Path $output 'manifest.json'
$manifestFiles = Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($output.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    package = 'qh-g6-offline-win-x64'
    created_at = (Get-Date).ToUniversalTime().ToString('O')
    dotnet_publish = 'self-contained win-x64, untrimmed'
    python_runtime = "Python $PythonVersion embeddable amd64"
    files = $manifestFiles
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding UTF8
$zipPath = "$output.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$outputParent = Split-Path -Parent $output
$outputName = Split-Path -Leaf $output
& tar.exe -a -c -f $zipPath -C $outputParent $outputName
if ($LASTEXITCODE -ne 0) { throw "Package archive failed: $LASTEXITCODE" }
Write-Output "package=$output"
Write-Output "archive=$zipPath"
Write-Output "file_count=$($manifestFiles.Count)"
Write-Output "package_bytes=$((Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object -Property Length -Sum).Sum)"
