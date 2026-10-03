[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'automation\controller\build-controller.ps1') -OutputDirectory $OutputDirectory
