# Native controller

Version 3.6.2. The WinForms controller, Wallpaper Engine catalog, offscreen media
host and recovery supervisor share a single Windows .NET Framework executable.

Build using `build-controller.ps1 -OutputDirectory <outside-repository-path>`.
See the root README for installation and requirements.

The publication build supports saved themes and local Wallpaper Engine projects.
It has no dependency on the original machine's AI generation workflow or disks.
Installation writes the real extraction path to private user configuration.

Only loopback connections to verified Codex targets are accepted. Hot application
requires an available endpoint. The controller does not force a running Codex
process to restart. The background supervisor detects stale sessions, periodically
validates the rendered background and attempts recovery with backoff while enabled.
Pausing the skin disables that recovery flow.
