param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'test-build'))
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$common=@('/nologo','/target:exe','/platform:x64','/codepage:65001','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll')
$clientSources=@('ApplicationInfo.cs','Protocol.cs','Client.cs','ClientNavigation.cs','MessageProof.cs') | ForEach-Object {Join-Path $PSScriptRoot ('src\'+$_)}
& $compiler @common "/out:$OutputDirectory\ServerLink.Client.Tests.exe" /main:ServerLink.ClientRegressionProgram @clientSources (Join-Path $PSScriptRoot 'tests\ClientRegression.cs')
if($LASTEXITCODE -ne 0){throw 'Client regression build failed.'}
$serverSources=@('ApplicationInfo.cs','Protocol.cs','DesktopProbe.cs','DesktopAdapter.cs','ServerRuntime.cs','Cdp.cs','Operations.cs','MessageProof.cs','Navigation.cs') | ForEach-Object {Join-Path $PSScriptRoot ('src\'+$_)}
& $compiler @common "/out:$OutputDirectory\ParserRegression.exe" /main:ServerLink.ParserRegression "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" @serverSources (Join-Path $PSScriptRoot 'tests\ParserRegression.cs')
if($LASTEXITCODE -ne 0){throw 'Parser regression build failed.'}
& (Join-Path $OutputDirectory 'ServerLink.Client.Tests.exe') (Join-Path $OutputDirectory 'client-regression.txt')
if($LASTEXITCODE -ne 0){throw 'Client regression checks failed.'}
& (Join-Path $OutputDirectory 'ParserRegression.exe') (Join-Path $OutputDirectory 'parser-regression.txt')
if($LASTEXITCODE -ne 0){throw 'Parser regression checks failed.'}
& $compiler @common "/out:$OutputDirectory\OperationRegression.exe" /main:ServerLink.OperationRegression (Join-Path $PSScriptRoot 'src\Protocol.cs') (Join-Path $PSScriptRoot 'src\Operations.cs') (Join-Path $PSScriptRoot 'src\MessageProof.cs') (Join-Path $PSScriptRoot 'tests\OperationRegression.cs')
if($LASTEXITCODE -ne 0){throw 'Operation regression build failed.'}
& (Join-Path $OutputDirectory 'OperationRegression.exe') (Join-Path $OutputDirectory 'operation-regression.txt')
if($LASTEXITCODE -ne 0){throw 'Operation regression checks failed.'}
Write-Output 'Independent regression checks passed; no live server contacted.'
