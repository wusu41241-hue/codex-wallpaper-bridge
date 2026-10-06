# Codex Wallpaper Bridge

A native Windows controller that uses locally installed Wallpaper Engine wallpapers as backgrounds in Codex Desktop. Derived from [Fei-Away/Codex-Dream-Skin](https://github.com/Fei-Away/Codex-Dream-Skin). Controller version **3.6.7**. [中文](README.md)

## Features

- Lists downloaded Workshop, user-created and built-in projects across Steam libraries.
- Animated video, scene and web wallpapers; presets work when the original resources are installed. Application wallpapers are listed with an unsupported reason.
- Select any wallpaper or follow the current desktop choice. Search, thumbnails, native WinForms UI and an original app icon.
- Reopening the controller restores its existing hidden, minimized or offscreen window. Opening the ordinary controller entry does not switch the wallpaper or restart Codex; silent commands remain in the background.
- Switch skins in an existing connectable Codex session. Conversation, Home and Dots transitions retain the last frame and mark the next shell before painting. Hot updates of the same wallpaper reuse the playing canvas.
- Ordinary **Codex** desktop and user Start Menu shortcuts prepare the local interface on cold start with the skin initially off. Enable and disable the skin at any time in that session. Toggle, theme and background recovery operations run in order; a briefly unavailable endpoint is checked again.
- Capture and relay target 30 FPS, reuse drawing and encoding resources, and skip duplicate frames. Slow receivers do not block other windows. Established hidden pages pause frame delivery and resume when visible.
- Automatic connection discovery, session rebinding, health checks and recovery with backoff.
- Wallpaper Engine renders a muted helper window outside all displays. Authorized frames travel through the existing local debugging connection into a Codex canvas. Large video and scene files remain in their original library.
- Videos use the same controlled frame pipeline as scenes and web projects. This fixes the first-frame wait caused by a transport/host mismatch and avoids local media URLs rejected by the app's URL safety policy, without disabling that policy or buffering an entire video in the page.

## Requirements and limits

Native wallpaper commands now enforce bounded execution and exit checks, matching the already running engine architecture. A failed wallpaper is durably quarantined and the skin is paused; switching away or restarting the controller does not reset its quarantine. Other validated wallpapers remain usable. An ambiguous window identity or failed targeted close pauses all later window operations. Settings are checked against the source schema and sent in batches of at most 1536 UTF-8 bytes after the window is stable and a frame is available. See [SECURITY.md](SECURITY.md) for the audit scope and limits. The bridge cannot repair native engine crashes or sandbox Wallpaper Engine project scripts.

Windows 10/11 x64, Windows .NET Framework 4.x, official Microsoft Store Codex Desktop, Node.js 22+ on PATH, and Steam Wallpaper Engine running with fully downloaded wallpapers.

The Codex session must provide a supported loopback debugging endpoint. Launch through the installed ordinary **Codex** shortcut or the skin-enabled Codex shortcut. Turning the skin off keeps the interface open so it can be enabled again in the same session.

The official AppsFolder entry, direct application launches and other paths that bypass these shortcuts do not guarantee an interface on cold start. An external controller cannot enable a missing interface in an already running session; it reports the cause and leaves Codex running. Configuring shortcuts does not restart the current session. Future changes that remove debugging support or substantially alter the UI can still require a compatibility update.

Capture and relay pacing target **30 FPS**. Actual playback depends on wallpaper rendering, capture and encoding, and page decoding; complex scenes can run below this target. This is a background frame bridge: scene interaction, desktop audio and the original rendering frame rate are not forwarded. Some web or scene projects may have their own rendering limitations.

## Install and use

Extract a Release ZIP or clone the source into a permanent folder, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

Open Codex from the ordinary desktop or user Start Menu **Codex** shortcut; a cold start keeps the native appearance. Open the desktop skin controller to choose and enable a wallpaper, then toggle it live in the current session. The skin-enabled Codex shortcut also selects the current Wallpaper Engine wallpaper. The installer deploys the runtime and controller to `%LOCALAPPDATA%\CodexDreamSkin`, records the extraction directory and creates desktop/Start Menu shortcuts. It does not launch, close or restart Codex, change `config.toml`, read credentials or modify WindowsApps/application binaries.

Launcher configuration only handles current-user shortcuts. It identifies ordinary Codex links by their registered application target or identity, creates desktop and user Start Menu `Codex.lnk` entries, and configures matching taskbar shortcuts. Links with custom arguments, unrelated same-name entries and dedicated skin launchers are preserved. Original shortcut bytes and properties are backed up under `control\launcher-backups`. To restore:

```powershell
& "$env:LOCALAPPDATA\CodexDreamSkin\control\configure-launchers.ps1" -Mode Restore
```

Restore only touches managed shortcuts whose contents have not since been edited. Use `-Mode DryRun` to inspect the plan or `-ShortcutPath` to select an exact `.lnk` path. The official registered AppsFolder entry is unchanged.

For updates, exit the controller and its background services first. A file-in-use check stops installation safely. Retain the extraction directory. Existing saved themes remain in user state.

Use the controller to enable a selection, restore the full list, search, use the desktop choice, refresh the library, pause the skin or probe the current Codex connection. Pausing stops the background and frame delivery while keeping Codex and its local interface running for later hot enable. Your desktop wallpaper selection remains managed by Wallpaper Engine.

The debugging endpoint is loopback only and sensitive. The media host also listens only on loopback and requires a random local token. Never expose these ports or publish local state, tokens, logs or personal wallpapers.

## Build and test

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -OutputDirectory ..\artifacts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-SmokeTests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\tests\run-tests.ps1
```

No real Codex window or wallpaper is changed by the portable tests. The source package excludes personal AI generation workflows, user images, logs, runtime state and third-party wallpaper resources. No Node.js, Codex or Wallpaper Engine binary is bundled.

## License

[MIT](LICENSE). Upstream copyright notices are retained. See [NOTICE.md](NOTICE.md) for attribution and asset boundaries. The abstract default wallpaper and controller icon are original project assets. Wallpaper authors retain their own rights; this project does not grant redistribution rights to their artwork. This is an independent project, not affiliated with OpenAI, Valve or Wallpaper Engine.
