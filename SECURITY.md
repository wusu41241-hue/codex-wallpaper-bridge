# Security design and review — 3.6.5

This is a local wallpaper bridge. The review covers the controller's file, native command, local HTTP and failure recovery boundaries. It is a source review and behavioral regression exercise, not an independent certification or a review of Wallpaper Engine internals.

## Findings addressed

| Finding | Protection |
| --- | --- |
| An unsuccessful open or isolation attempt could repeat every few seconds | Native exit and timeout checks; immediate capture stop and durable pause/quarantine |
| Returning to a failed source or restarting the host could erase the failure | Per-source failure history, atomic writes, malformed records fail closed |
| A window can exist while its renderer is initializing | Warmup before capture, settings only after a successful frame, periodic engine/owner checks |
| Scene settings were passed as a large unchecked message | Project-defined typed properties, range/option/resource validation, bounded JSON batches |
| Executable architecture and current working directory could differ from the running engine | Select the active 32/64 bit image and use its install directory |
| A shared helper name could address the wrong window | Random per-host name, exact window handle, owner PID and image checks; no global wallpaper controls |
| Request length was checked after an unbounded read | Bound lines while reading, 8 KiB header budget, eight concurrent connections |

## Input boundaries

- Canonical catalog source and supported kind are checked before a theme is committed and again at host load.
- Files must be rooted, local, regular and inside known wallpaper libraries. UNC paths, alternate data streams, reparse points and command control characters are rejected.
- Project metadata is limited to 1 MiB and parser depth 32. Preview files are limited to 16 MiB. Properties are limited to 256; strings, numeric values and schema ranges are bounded.
- Editor labels and unsupported or unknown properties never reach the native command. Text cannot close the RAW JSON envelope. Each settings batch contains window-specific volume zero.
- Child commands use no shell, hidden windows and bounded deadlines. Timeout cancellation targets only the temporary command PID. The bridge does not terminate the running desktop renderer as a recovery method.
- Media listens on IPv4 loopback. Media and metrics require a random 256-bit token; health exposes only a fixed protocol identifier. Metrics expose counters, not paths or credentials.

## Failure handling

`paused` and `control/motion-safety.json` are local state, never release assets. A replacement host reads the failure history before loading the same source. Other validated sources may resume through an explicit controller selection; their selection retains prior quarantines. Loss of ownership or inability to safely close a previous window pauses further window operations globally. A missing file or unreadable safety record does not trigger native retries.

The controller and recovery agent preserve a running Codex session. They cannot add an absent debugging endpoint to an already running app. No official application binary is modified, and no conversations are inspected.

## Validation and limitations

Regression cases use fake native commands to verify nonzero exits, bounded timeouts, durable quarantine, malformed state, multiple failures, property validation, envelope escaping and bounded reads. A separate local check opened, captured changing frames from and closed a stock scene while keeping the desktop engine and Codex process alive. An individual custom preset still triggered a native engine crash in a single bounded diagnostic attempt and was quarantined; it was not repeatedly retried.

This bridge cannot guarantee that a third-party wallpaper or the native engine never crashes. Scene/web content executes in the user's existing Wallpaper Engine runtime without a new sandbox. A local same-user process may access the user's state and token. CDP is a privileged local debugging capability and must remain on loopback without port forwarding.

Do not include tokens, personal project assets, active themes, logs, screenshots or crash dumps in public reports. Report a reproducible issue to this repository's owner with the controller/engine version, wallpaper type and a redacted failure code. Validate local sources and resolve the native problem before manually reviewing a quarantine record.
