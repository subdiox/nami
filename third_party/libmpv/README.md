# libmpv

Prebuilt libmpv from shinchiro/mpv-winbuild-cmake (`mpv-dev-x86_64-<date>-git-<hash>.7z`).
Only `libmpv-2.dll` and the public headers under `include/mpv` are kept.

Nami requires mpv >= 0.41 because it relies on `--d3d11-output-mode=composition`
and the `display-swapchain` / `d3d11-composition-size` properties.

Current build: 20260921-git-e76a35ec95 (client API 2.5).
