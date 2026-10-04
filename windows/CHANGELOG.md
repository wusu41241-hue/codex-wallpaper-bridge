# Windows Changelog

## 1.5.4 / Controller 3.6.3 - 2026-10-04

- Preserve the decoded wallpaper canvas and frame during route changes and same-source hot injection. Mark new shells before painting and ignore streaming-message mutations.
- Pace capture and relay toward 30 FPS, reuse drawing and JPEG buffers, and skip unchanged frames with authenticated frame cursors.
- Broadcast with one pending frame per receiver so a slow window cannot hold up other windows. Pause established background receivers and resume through lightweight checks.
- Keep the live source and capture window when only theme appearance changes. Expose authenticated numeric capture metrics without wallpaper paths or credentials.
- Retain sidebar liquid glass and remove the decorative white fade above Dots.
