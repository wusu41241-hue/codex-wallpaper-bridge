# Notices and attribution

Codex Wallpaper Bridge is an independent Windows extension of
[Fei-Away/Codex-Dream-Skin](https://github.com/Fei-Away/Codex-Dream-Skin),
based on upstream commit `f7bf17efb46c3351d4ef8925b3594c9876185a08`.
The original runtime, renderer, theme store, safety checks and tests retain
the upstream MIT license and copyright notice in [LICENSE](LICENSE).

The added Windows controller, Wallpaper Engine catalog, offscreen media host,
frame relay, recovery changes, installation scripts and portable tests are
distributed under the same MIT license. Original contributor notices remain
intact. This distribution is not affiliated with, endorsed by or sponsored by
OpenAI, Valve or Wallpaper Engine.

The MIT license applies to the software source and the original abstract
`windows/assets/default-background.png`, generated reproducibly by
`tools/generate-default-background.py`. The controller icon files are original
assets redesigned for this project; they are included under the MIT license.
No third-party Wallpaper Engine wallpaper, user screenshot, character artwork,
celebrity photo or upstream reference photo is included in this distribution.

The software license does not grant rights to OpenAI/Codex, Valve/Steam or
Wallpaper Engine names, trademarks, logos, application binaries or assets.
Wallpapers remain in each user's own local library. Their respective authors
and licenses govern use and redistribution. Wallpaper Engine and Codex are
separate prerequisites and are not bundled. Node.js is also not bundled.

The runtime uses a Chromium debugging connection on loopback only. Treat that
local debugging endpoint as sensitive. The separate media host requires a
random local token and serves authorized wallpaper frames from memory.
