param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$common=@('/nologo','/target:winexe','/platform:x64','/codepage:65001','/optimize+',"/win32manifest:$PSScriptRoot\app.manifest",'/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll')
& $compiler @common "/out:$OutputDirectory\ServerLink.Client.exe" /main:ServerLink.ClientProgram (Join-Path $PSScriptRoot 'src\ApplicationInfo.cs') (Join-Path $PSScriptRoot 'src\Protocol.cs') (Join-Path $PSScriptRoot 'src\Client.cs') (Join-Path $PSScriptRoot 'src\ClientNavigation.cs') (Join-Path $PSScriptRoot 'src\MessageProof.cs')
if($LASTEXITCODE -ne 0){throw 'Local client build failed.'}
$serverSources=@('ApplicationInfo.cs','Protocol.cs','DesktopProbe.cs','DesktopAdapter.cs','ServerRuntime.cs','Cdp.cs','Operations.cs','MessageProof.cs','Navigation.cs','DesktopWorker.cs') | ForEach-Object {Join-Path $PSScriptRoot ('src\'+$_)}
& $compiler @common "/out:$OutputDirectory\ServerLink.Agent.exe" /main:ServerLink.AgentProgram "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" @serverSources (Join-Path $PSScriptRoot 'src\Agent.cs') (Join-Path $PSScriptRoot 'src\Validation.cs')
if($LASTEXITCODE -ne 0){throw 'Server bridge build failed.'}
& $compiler @common "/out:$OutputDirectory\ServerLink.Host.exe" /main:ServerLink.HostProgram "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" @serverSources (Join-Path $PSScriptRoot 'src\Host.cs') (Join-Path $PSScriptRoot 'src\WindowsAppActivation.cs')
if($LASTEXITCODE -ne 0){throw 'Server host build failed.'}
Write-Output 'Built local client and server bridge.'
