[CmdletBinding()]
param([string]$ScratchRoot=[System.IO.Path]::GetTempPath())
$ErrorActionPreference='Stop'
$automation=Split-Path -Parent $PSScriptRoot
$scriptTokens=$null
$scriptErrors=$null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $automation 'controller\apply-saved-theme.ps1'),[ref]$scriptTokens,[ref]$scriptErrors)
if($scriptErrors.Count -gt 0){throw ('Saved-theme script cannot be parsed by this PowerShell version: '+$scriptErrors[0].Message)}
$testRoot=Join-Path $ScratchRoot ('skin-controller-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$exe=Join-Path $testRoot 'ControllerBehaviorTests.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
if(-not (Test-Path -LiteralPath $compiler)){throw 'The .NET Framework C# compiler is required on Windows.'}
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:ControllerBehaviorTests "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $automation 'controller\CodexDreamSkinController.cs') (Join-Path $automation 'controller\WallpaperCatalog.cs') (Join-Path $automation 'controller\MotionHost.cs') (Join-Path $automation 'controller\RuntimeConnection.cs') (Join-Path $automation 'controller\RecoveryAgent.cs') (Join-Path $PSScriptRoot 'ControllerBehaviorTests.cs')
if($LASTEXITCODE -ne 0){throw "Controller behavior tests could not be compiled (exit $LASTEXITCODE)."}
& $exe (Join-Path $testRoot 'state')
if($LASTEXITCODE -ne 0){throw "Controller behavior tests failed (exit $LASTEXITCODE)."}
Write-Host "Test artifacts: $testRoot"
