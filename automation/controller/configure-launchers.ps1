[CmdletBinding()]
param(
  [ValidateSet('DryRun', 'Apply', 'Restore')][string]$Mode = 'DryRun',
  [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'),
  [string]$ControllerPath,
  [string[]]$ShortcutPath = @(),
  [string]$IsolatedTestRoot
)

# This script configures user-owned launchers only. It never launches, closes,
# restarts, injects into, or modifies the registered Codex application.
$ErrorActionPreference = 'Stop'
$defaultStateRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'))
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
if ($StateRoot -ine $defaultStateRoot -and -not $IsolatedTestRoot) {
  throw 'Custom StateRoot requires IsolatedTestRoot so real user shortcuts remain untouched.'
}
$common = Join-Path $StateRoot 'engine\scripts\common-windows.ps1'
if (-not (Test-Path -LiteralPath $common -PathType Leaf)) {
  $common = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\windows\scripts\common-windows.ps1'))
}
if (-not (Test-Path -LiteralPath $common -PathType Leaf)) { throw 'The Dream Skin runtime helpers are missing.' }
. $common
. (Join-Path ([IO.Path]::GetDirectoryName($common)) 'theme-windows.ps1')

$userDesktop = [Environment]::GetFolderPath('Desktop')
$userPrograms = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$userTaskbar = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$roots = @($userDesktop, $userPrograms, $userTaskbar)
if ($IsolatedTestRoot) {
  $IsolatedTestRoot = [IO.Path]::GetFullPath($IsolatedTestRoot)
  foreach ($actualRoot in $roots) {
    if ($IsolatedTestRoot -ieq $actualRoot -or $IsolatedTestRoot.StartsWith($actualRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
      throw 'IsolatedTestRoot must not be inside a real user shortcut folder.'
    }
  }
  Assert-DreamSkinNoReparseComponents -Path $IsolatedTestRoot
  if (-not $StateRoot.StartsWith($IsolatedTestRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Isolated launcher tests must keep StateRoot inside IsolatedTestRoot.'
  }
  $roots = @('Desktop', 'Programs', 'TaskBar') | ForEach-Object { Join-Path $IsolatedTestRoot $_ }
}
$roots = @($roots | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') })
foreach ($root in $roots) { Assert-DreamSkinNoReparseComponents -Path $root }

function Assert-UserShortcut([string]$Path) {
  $full = [IO.Path]::GetFullPath($Path)
  if ([IO.Path]::GetExtension($full) -ine '.lnk') { throw 'Only .lnk files can be configured.' }
  $allowed = $false
  foreach ($root in $roots) {
    if ($full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
      $parent = [IO.Path]::GetDirectoryName($full)
      # Start Menu subfolders are allowed; Desktop and Taskbar remain flat.
      if ($root -ieq $roots[1] -or $parent -ieq $root) { $allowed = $true }
    }
  }
  if (-not $allowed) { throw "Shortcut is outside the permitted user folders: $full" }
  Assert-DreamSkinNoReparseComponents -Path $full
  return $full
}
function Get-BytesHash([byte[]]$Bytes) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
function Get-FileHashValue([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
  return Get-BytesHash ([IO.File]::ReadAllBytes($Path))
}
function Write-LauncherJournal {
  Write-DreamSkinUtf8FileAtomically -Path $journalPath -Content (($journal | ConvertTo-Json -Depth 8) + "`r`n")
}
function Get-LinkProperties([string]$Path) {
  $link = $wscript.CreateShortcut($Path)
  $folder = $shell.Namespace([IO.Path]::GetDirectoryName($Path))
  $item = if ($folder) { $folder.ParseName([IO.Path]::GetFileName($Path)) } else { $null }
  $properties = [ordered]@{
    TargetPath = "$($link.TargetPath)"; Arguments = "$($link.Arguments)"
    WorkingDirectory = "$($link.WorkingDirectory)"; IconLocation = "$($link.IconLocation)"
    Description = "$($link.Description)"; Hotkey = "$($link.Hotkey)"
    WindowStyle = [int]$link.WindowStyle; AppUserModelId = ''
    RelaunchCommand = ''; RelaunchIconResource = ''; RelaunchDisplayNameResource = ''
  }
  if ($item) {
    foreach ($property in @(
      @('AppUserModelId','System.AppUserModel.ID'),
      @('RelaunchCommand','System.AppUserModel.RelaunchCommand'),
      @('RelaunchIconResource','System.AppUserModel.RelaunchIconResource'),
      @('RelaunchDisplayNameResource','System.AppUserModel.RelaunchDisplayNameResource')
    )) { $properties[$property[0]] = "$($item.ExtendedProperty($property[1]))" }
  }
  return [pscustomobject]$properties
}
function Get-OfficialLinkReason([object]$Properties) {
  foreach ($install in $installs) {
    if ($Properties.TargetPath -ieq $install.Executable) {
      if ($Properties.Arguments.Trim()) { return 'custom-arguments' }
      return 'registered-executable'
    }
    $appArgument = 'shell:AppsFolder\' + $install.AppUserModelId
    if ($Properties.TargetPath -ieq (Join-Path $env:WINDIR 'explorer.exe') -and
        $Properties.Arguments.Trim().Trim('"') -ieq $appArgument) { return 'registered-app-activation' }
    if ($Properties.AppUserModelId -ieq $install.AppUserModelId -and
        (-not $Properties.TargetPath -or $Properties.TargetPath -ieq $appArgument)) {
      return 'registered-app-identity'
    }
  }
  return $null
}
function Initialize-LauncherPropertyWriter {
  if ('CodexDreamSkinLaunchers.PropertyWriter' -as [type]) { return }
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace CodexDreamSkinLaunchers {
  [StructLayout(LayoutKind.Sequential, Pack=4)]
  public struct PropertyKey { public Guid Format; public uint Id; public PropertyKey(uint id) { Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); Id=id; } }
  [StructLayout(LayoutKind.Explicit)]
  public struct PropVariant {
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(16)] public IntPtr Padding;
  }
  [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IPropertyStore {
    uint GetCount();
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
  }
  public static class PropertyWriter {
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    static void Set(IPropertyStore store, uint id, string text) {
      var key=new PropertyKey(id); var value=new PropVariant();
      value.Type=31; value.Pointer=Marshal.StringToCoTaskMemUni(text);
      try { store.SetValue(ref key, ref value); } finally { PropVariantClear(ref value); }
    }
    public static void Configure(string path, string appId, string command, string icon) {
      object link=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
      try {
        var file=(IPersistFile)link; file.Load(path, 2); var store=(IPropertyStore)link;
        Set(store,5,appId); Set(store,2,command); Set(store,3,icon); store.Commit(); file.Save(path,true);
      } finally { if(Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
    }
  }
}
'@
}

$journalRoot = Join-Path $StateRoot 'control\launcher-backups'
$journalPath = Join-Path $journalRoot 'launchers.json'
Assert-DreamSkinNoReparseComponents -Path $journalPath
$journal = [pscustomobject]@{ schema_version = 1; entries = @() }
if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
  $journal = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($journal.schema_version -ne 1 -or $null -eq $journal.entries) { throw 'The launcher backup journal is invalid.' }
}
$wscript = New-Object -ComObject WScript.Shell
$shell = New-Object -ComObject Shell.Application
$results = @()
$mutex = New-Object Threading.Mutex($false, 'Local\CodexDreamSkin.UserLauncherConfiguration')
$locked = $false
try {
  $locked = $mutex.WaitOne(0)
  if (-not $locked) { throw 'Another launcher configuration is running.' }
  if ($Mode -eq 'Restore') {
    $selected = @($ShortcutPath | ForEach-Object { Assert-UserShortcut $_ })
    foreach ($entry in @($journal.entries)) {
      $path = Assert-UserShortcut "$($entry.path)"
      if ($selected.Count -and $selected -inotcontains $path) { continue }
      if ($entry.status -eq 'restored') { $results += [pscustomobject]@{path=$path;action='already-restored'}; continue }
      $currentHash = Get-FileHashValue $path
      if ($currentHash -cne "$($entry.managed_sha256)") {
        $results += [pscustomobject]@{path=$path;action='skipped-user-change'}
        continue
      }
      if ($entry.original_exists) {
        $backup = [IO.Path]::GetFullPath("$($entry.backup)")
        if (-not $backup.StartsWith([IO.Path]::GetFullPath($journalRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
          throw 'A backup path escapes the managed launcher backup folder.'
        }
        Assert-DreamSkinNoReparseComponents -Path $backup
        if ((Get-FileHashValue $backup) -cne "$($entry.original_sha256)") { throw "Launcher backup hash mismatch: $backup" }
        Write-DreamSkinBytesAtomically -Path $path -Bytes ([IO.File]::ReadAllBytes($backup)) -ExpectedBytes ([IO.File]::ReadAllBytes($path))
      } else {
        # The path and exact current content were checked above. No recursive deletion.
        Remove-Item -LiteralPath $path -Force
      }
      $entry.status = 'restored'
      Write-LauncherJournal
      $results += [pscustomobject]@{path=$path;action='restored'}
    }
  } else {
    if (-not $ControllerPath) { $ControllerPath = Join-Path $StateRoot 'control\CodexDreamSkinController.exe' }
    $ControllerPath = [IO.Path]::GetFullPath($ControllerPath)
    Assert-DreamSkinNoReparseComponents -Path $ControllerPath
    if ([IO.Path]::GetFileName($ControllerPath) -ine 'CodexDreamSkinController.exe' -or
        -not (Test-Path -LiteralPath $ControllerPath -PathType Leaf)) { throw 'The installed controller executable is missing.' }
    $installs = @(Get-DreamSkinRegisteredCodexInstalls)
    if (-not $installs.Count) { throw 'No trusted registered OpenAI.Codex Store package was found.' }
    $appId = $installs[0].AppUserModelId
    $arguments = '--native --silent'
    $candidates = @()
    foreach ($root in $roots) {
      if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
      $files = if ($root -ieq $roots[1]) {
        Get-ChildItem -LiteralPath $root -Filter '*.lnk' -File -Recurse
      } else { Get-ChildItem -LiteralPath $root -Filter '*.lnk' -File }
      foreach ($file in @($files)) {
        $path = Assert-UserShortcut $file.FullName
        $properties = Get-LinkProperties $path
        $reason = Get-OfficialLinkReason $properties
        $known = @($journal.entries | Where-Object { $_.path -ieq $path })
        if ($known.Count) { $reason = 'managed-entry' }
        if ($reason -eq 'custom-arguments') {
          $results += [pscustomobject]@{path=$path;action='skipped-custom-arguments'}
        } elseif ($reason) { $candidates += [pscustomobject]@{path=$path;properties=$properties;reason=$reason} }
      }
    }
    foreach ($root in @($roots[0], $roots[1])) {
      $path = Assert-UserShortcut (Join-Path $root 'Codex.lnk')
      if (@($candidates | Where-Object { $_.path -ieq $path }).Count) { continue }
      if (Test-Path -LiteralPath $path) {
        $results += [pscustomobject]@{path=$path;action='skipped-name-collision'}
      } else { $candidates += [pscustomobject]@{path=$path;properties=$null;reason='create-user-entry'} }
    }
    $selected = @($ShortcutPath | ForEach-Object { Assert-UserShortcut $_ })
    foreach ($candidate in $candidates) {
      $path = $candidate.path
      if ($selected.Count -and $selected -inotcontains $path) { continue }
      $entry = @($journal.entries | Where-Object { $_.path -ieq $path } | Select-Object -First 1)
      if ($entry.Count) { $entry = $entry[0] } else { $entry = $null }
      $currentHash = Get-FileHashValue $path
      $expectedBytes = if ($currentHash) { [IO.File]::ReadAllBytes($path) } else { $null }
      if ($entry) {
        if ($currentHash -and $currentHash -ceq "$($entry.managed_sha256)") {
          $results += [pscustomobject]@{path=$path;action='already-configured'}
          continue
        }
        $originalStillPresent = if ($entry.original_exists) {
          $currentHash -ceq "$($entry.original_sha256)"
        } else { -not $currentHash }
        if (-not $originalStillPresent -or $entry.status -eq 'managed') {
          $results += [pscustomobject]@{path=$path;action='skipped-user-change'}
          continue
        }
      }
      if ($Mode -eq 'DryRun') {
        $results += [pscustomobject]@{path=$path;action='would-configure';reason=$candidate.reason;target=$ControllerPath;arguments=$arguments}
        continue
      }
      if (-not (Test-Path -LiteralPath $journalRoot)) { $null = New-Item -ItemType Directory -Path $journalRoot -Force }
      if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($path)))) {
        if (-not $IsolatedTestRoot) { throw "User shortcut folder is missing: $path" }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force
      }
      if (-not $entry) {
        $id = Get-BytesHash ([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant()))
        $backup = if ($currentHash) { Join-Path $journalRoot ($id + '.lnk') } else { $null }
        if ($backup) {
          Assert-DreamSkinNoReparseComponents -Path $backup
          if (Test-Path -LiteralPath $backup) { throw "Untracked launcher backup already exists: $backup" }
          [IO.File]::WriteAllBytes($backup, [IO.File]::ReadAllBytes($path))
        }
        $entry = [pscustomobject]@{
          path=$path;original_exists=[bool]$currentHash;backup=$backup;original_sha256=$currentHash
          before_properties=$candidate.properties;managed_sha256=$null;status='pending'
          created_at=[DateTimeOffset]::Now.ToString('o')
        }
        $journal.entries = @($journal.entries) + $entry
        Write-LauncherJournal
      }
      $temp = Join-Path ([IO.Path]::GetDirectoryName($path)) ('.codex-launcher-' + [guid]::NewGuid().ToString('N') + '.lnk')
      Assert-DreamSkinNoReparseComponents -Path $temp
      try {
        $link = $wscript.CreateShortcut($temp)
        $link.TargetPath = $ControllerPath; $link.Arguments = $arguments
        $link.WorkingDirectory = [IO.Path]::GetDirectoryName($ControllerPath)
        $link.IconLocation = $ControllerPath + ',0'
        $link.Description = 'Open Codex with the local skin connection prepared; skins can be enabled or disabled later.'
        $link.WindowStyle = 1
        if ($candidate.properties) {
          $link.Hotkey = $candidate.properties.Hotkey
          $link.WindowStyle = $candidate.properties.WindowStyle
        }
        $link.Save()
        Initialize-LauncherPropertyWriter
        $command = '"' + $ControllerPath + '" ' + $arguments
        [CodexDreamSkinLaunchers.PropertyWriter]::Configure($temp, $appId, $command, $ControllerPath + ',0')
        $newHash = Get-FileHashValue $temp
        # Record exact replacement content before the filesystem transaction.
        $entry.managed_sha256 = $newHash; $entry.status = 'pending'
        Write-LauncherJournal
        Write-DreamSkinBytesAtomically -Path $path -Bytes ([IO.File]::ReadAllBytes($temp)) -ExpectedBytes $expectedBytes
        $entry.status = 'managed'
        Write-LauncherJournal
        $results += [pscustomobject]@{path=$path;action='configured';reason=$candidate.reason;target=$ControllerPath;arguments=$arguments}
      } finally {
        if (Test-Path -LiteralPath $temp -PathType Leaf) { Remove-Item -LiteralPath $temp -Force }
      }
    }
  }
  [pscustomobject]@{
    mode=$Mode;codex_restarted=$false;user_shortcuts_only=(-not [bool]$IsolatedTestRoot)
    journal_path=$journalPath;results=@($results)
  }
} finally {
  if ($locked) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
  if ([Runtime.InteropServices.Marshal]::IsComObject($wscript)) { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($wscript) }
  if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
