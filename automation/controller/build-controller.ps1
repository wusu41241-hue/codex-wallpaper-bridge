[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\artifacts'))

$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
  throw 'The Windows .NET Framework x64 compiler is required.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$executable = Join-Path $output 'CodexDreamSkinController.exe'
$arguments = @(
  '/nologo', '/target:winexe', '/optimize+', '/platform:x64', '/utf8output',
  ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
  ('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')),
  ('/out:' + $executable),
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll'
)
foreach ($source in @('CodexDreamSkinController.cs', 'WallpaperCatalog.cs', 'MotionHost.cs', 'RuntimeConnection.cs', 'RecoveryAgent.cs')) {
  $arguments += Join-Path $PSScriptRoot $source
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native controller compilation failed.' }
Write-Output $executable
