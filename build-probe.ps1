$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkWpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$outputDirectory=Join-Path $PSScriptRoot 'probe'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$outputDirectory\ServerLink.Probe.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/r:$frameworkWpf\UIAutomationClient.dll" "/r:$frameworkWpf\UIAutomationTypes.dll" "/r:$frameworkWpf\WindowsBase.dll" (Join-Path $PSScriptRoot 'src\DesktopProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Server compatibility probe compilation failed.'}
Write-Output 'Server compatibility probe built.'
