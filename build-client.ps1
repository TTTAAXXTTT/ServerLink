param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'client-fix-build'),[switch]$IncludeRegressionTests,[switch]$IncludeLiveCheck)
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$common=@('/nologo','/platform:x64','/codepage:65001','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll')
$sources=@((Join-Path $PSScriptRoot 'src\ApplicationInfo.cs'),(Join-Path $PSScriptRoot 'src\Protocol.cs'),(Join-Path $PSScriptRoot 'src\Client.cs'),(Join-Path $PSScriptRoot 'src\ClientNavigation.cs'),(Join-Path $PSScriptRoot 'src\MessageProof.cs'))
& $compiler @common /target:winexe "/win32manifest:$PSScriptRoot\app.manifest" "/out:$OutputDirectory\ServerLink.Client.exe" /main:ServerLink.ClientProgram @sources
if($LASTEXITCODE -ne 0){throw 'Client build failed.'}
if($IncludeRegressionTests){
    & $compiler @common /target:exe "/out:$OutputDirectory\ServerLink.Client.Tests.exe" /main:ServerLink.ClientRegressionProgram @sources (Join-Path $PSScriptRoot 'tests\ClientRegression.cs')
    if($LASTEXITCODE -ne 0){throw 'Client regression tests build failed.'}
}
if($IncludeLiveCheck){
    & $compiler @common /target:exe "/out:$OutputDirectory\ServerLink.LiveClientFormCheck.exe" /main:ServerLink.LiveClientFormProgram @sources (Join-Path $PSScriptRoot 'tests\ClientRegression.cs') (Join-Path $PSScriptRoot 'tests\LiveClientFormCheck.cs')
    if($LASTEXITCODE -ne 0){throw 'Live client form check build failed.'}
    & $compiler @common /target:exe "/out:$OutputDirectory\ServerLink.LiveClientCheck.exe" /main:ServerLink.LiveClientCheck (Join-Path $PSScriptRoot 'src\Protocol.cs') (Join-Path $PSScriptRoot 'tests\LiveClientCheck.cs')
    if($LASTEXITCODE -ne 0){throw 'Live protocol check build failed.'}
}
Write-Output 'Client build completed.'
