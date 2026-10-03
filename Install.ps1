[CmdletBinding()]
param(
  [switch]$NoShortcuts,
  [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'CodexDreamSkin')
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$runtimeSource = Join-Path $repoRoot 'windows'
$fullStateRoot = [System.IO.Path]::GetFullPath($StateRoot)
$defaultStateRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'))
if (-not $NoShortcuts -and $fullStateRoot -ine $defaultStateRoot) {
  throw 'Custom StateRoot is for isolated installation tests only; specify -NoShortcuts.'
}
. (Join-Path $runtimeSource 'scripts\common-windows.ps1')
. (Join-Path $runtimeSource 'scripts\theme-windows.ps1')

$operationLock = Enter-DreamSkinOperationLock
try {
  # These are read-only probes. Installation never launches or closes Codex and
  # never changes its config.toml, credentials, app.asar or WindowsApps package.
  $null = Get-DreamSkinNodeRuntime
  if (@(Get-DreamSkinRegisteredCodexInstalls).Count -eq 0) {
    throw 'Install the official Microsoft Store Codex application first.'
  }
  Ensure-DreamSkinManagedDirectory -Path $fullStateRoot -Root $fullStateRoot
  $controlRoot = Join-Path $fullStateRoot 'control'
  Ensure-DreamSkinManagedDirectory -Path $controlRoot -Root $fullStateRoot
  $installedExecutable = Join-Path $controlRoot 'CodexDreamSkinController.exe'
  Assert-DreamSkinNoReparseComponents -Path $installedExecutable
  if (Test-Path -LiteralPath $installedExecutable) {
    try {
      $probe = [System.IO.File]::Open($installedExecutable, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
      $probe.Dispose()
    } catch {
      throw 'The existing skin controller or its background services are still running. Close them before installing. Codex has not been restarted.'
    }
  }

  $bundledExecutable = Join-Path $repoRoot 'CodexDreamSkinController.exe'
  if (-not (Test-Path -LiteralPath $bundledExecutable -PathType Leaf)) {
    $buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('codex-wallpaper-bridge-build-' + [guid]::NewGuid().ToString('N'))
    $bundledExecutable = & (Join-Path $repoRoot 'Build.ps1') -OutputDirectory $buildRoot
    if (-not (Test-Path -LiteralPath $bundledExecutable -PathType Leaf)) { throw 'The controller build did not produce an executable.' }
  }
  $engine = Install-DreamSkinRuntimeEngine -SkillRoot $runtimeSource -StateRoot $fullStateRoot
  $null = Initialize-DreamSkinThemeStore -SkillRoot $engine.Root -StateRoot $fullStateRoot

  # Keep a previous controller for recovery; engine installation has its own
  # checked transaction. Saved themes and the active wallpaper remain in place.
  if (Test-Path -LiteralPath $installedExecutable) {
    $backup = Join-Path $controlRoot 'CodexDreamSkinController.previous.exe'
    Assert-DreamSkinNoReparseComponents -Path $backup
    Copy-Item -LiteralPath $installedExecutable -Destination $backup -Force
  }
  Copy-Item -LiteralPath $bundledExecutable -Destination $installedExecutable -Force
  foreach ($file in @('apply-saved-theme.ps1', 'app.ico')) {
    $destination = Join-Path $controlRoot $file
    Assert-DreamSkinNoReparseComponents -Path $destination
    Copy-Item -LiteralPath (Join-Path $repoRoot ('automation\controller\' + $file)) -Destination $destination -Force
  }
  $configuration = [ordered]@{ schema_version = 1; project_root = $repoRoot; port = 9335 }
  Write-DreamSkinUtf8FileAtomically -Path (Join-Path $controlRoot 'control-config.json') `
    -Content (($configuration | ConvertTo-Json) + "`r`n")
  $record = [ordered]@{ project = 'codex-wallpaper-bridge'; controller_version = '3.6.2'; source_root = $repoRoot; installed_at = (Get-Date).ToUniversalTime().ToString('o') }
  Write-DreamSkinUtf8FileAtomically -Path (Join-Path $controlRoot 'bridge-install.json') `
    -Content (($record | ConvertTo-Json) + "`r`n")

  if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    foreach ($folder in @($desktop, $startMenu)) {
      foreach ($entry in @(
        @{ Name = 'Codex 皮肤控制器'; Arguments = ''; Description = 'Choose an installed Wallpaper Engine wallpaper for Codex' },
        @{ Name = 'Codex（可随时换肤）'; Arguments = '--start --wallpaper-current'; Description = 'Start or connect Codex with the current Wallpaper Engine wallpaper' }
      )) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder ($entry.Name + '.lnk')))
        $shortcut.TargetPath = $installedExecutable
        $shortcut.Arguments = $entry.Arguments
        $shortcut.WorkingDirectory = $repoRoot
        $shortcut.IconLocation = $installedExecutable + ',0'
        $shortcut.Description = $entry.Description
        $shortcut.Save()
      }
    }
  }
  Write-Host "Installed Codex Wallpaper Bridge 3.6.2 at $fullStateRoot."
  Write-Host 'Installation finished. Codex has not been started, closed or restarted.'
  Write-Host 'Open the skin controller when ready. Wallpaper Engine must be running for animated backgrounds.'
} finally {
  Exit-DreamSkinOperationLock -Mutex $operationLock
}
