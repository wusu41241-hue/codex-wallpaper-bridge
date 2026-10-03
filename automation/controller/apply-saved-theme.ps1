[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ThemeDirectory
)

$ErrorActionPreference = 'Stop'
$EngineScripts = Join-Path $env:LOCALAPPDATA 'CodexDreamSkin\engine\scripts'
$CommonScript = Join-Path $EngineScripts 'common-windows.ps1'
$ThemeScript = Join-Path $EngineScripts 'theme-windows.ps1'
if (-not (Test-Path -LiteralPath $CommonScript -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ThemeScript -PathType Leaf)) {
  throw "Dream Skin 运行时文件缺失，无法应用已保存主题。"
}

. $CommonScript
. $ThemeScript

$ThemeDirectory = [System.IO.Path]::GetFullPath($ThemeDirectory)
$applied = Use-DreamSkinSavedTheme -ThemeDirectory $ThemeDirectory
$id = if ($applied.Theme.id) { "$($applied.Theme.id)" } else { 'saved-theme' }
Write-Output "applied:$id"
