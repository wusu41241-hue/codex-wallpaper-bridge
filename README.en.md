# Codex Wallpaper Bridge

A native Windows controller that uses locally installed Wallpaper Engine wallpapers as backgrounds in Codex Desktop. Derived from [Fei-Away/Codex-Dream-Skin](https://github.com/Fei-Away/Codex-Dream-Skin). Controller version **3.6.2**. [中文](README.md)

## Features

- Lists downloaded Workshop, user-created and built-in projects across Steam libraries.
- Animated video, scene and web wallpapers; presets work when the original resources are installed. Application wallpapers are listed with an unsupported reason.
- Select any wallpaper or follow the current desktop choice. Search, thumbnails, native WinForms UI and an original app icon.
- Switch skins in an existing connectable Codex session. Recovers across conversation changes, new pages and supported connection changes.
- Automatic connection discovery, session rebinding, health checks and recovery with backoff.
- Wallpaper Engine renders a muted helper window outside all displays. Authorized frames travel through the existing local debugging connection into a Codex canvas, at up to roughly 8 FPS. Large video and scene files remain in their original library.

## Requirements and limits

Windows 10/11 x64, Windows .NET Framework 4.x, official Microsoft Store Codex Desktop, Node.js 22+ on PATH, and Steam Wallpaper Engine running with fully downloaded wallpapers.

The Codex session must provide a supported loopback debugging endpoint. An already open session without that endpoint cannot be guaranteed to accept a skin without being reopened. The controller reports failure and does not force a restart. Launching through the included skin shortcut attempts to establish the connection. Future changes that remove debugging support or substantially alter the UI can still require a compatibility update.

This is a background frame bridge: scene interaction, desktop audio and the original rendering frame rate are not forwarded. Some web or scene projects may have their own rendering limitations.

## Install and use

Extract a Release ZIP or clone the source into a permanent folder, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

Open the desktop skin controller to choose a wallpaper, or the skin-enabled Codex shortcut to select the current Wallpaper Engine wallpaper. The installer deploys the runtime and controller to `%LOCALAPPDATA%\CodexDreamSkin`, records the extraction directory and creates desktop/Start Menu shortcuts. It does not launch, close or restart Codex, change `config.toml`, read credentials or modify WindowsApps/application binaries.

For updates, exit the controller and its background services first. A file-in-use check stops installation safely. Retain the extraction directory. Existing saved themes remain in user state.

Use the controller to enable a selection, restore the full list, search, use the desktop choice, refresh the library, pause the skin or probe the current Codex connection. Your desktop wallpaper selection remains managed by Wallpaper Engine.

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
