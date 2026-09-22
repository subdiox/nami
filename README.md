# Nami

An IINA-style, Windows-native mpv front end. WinUI 3 (Windows App SDK) + libmpv.

## How it works

- libmpv runs with `--vo=gpu-next --gpu-context=d3d11 --d3d11-output-mode=composition`.
  mpv creates no window of its own, only a DXGI composition swapchain (mpv 0.41+).
- The `IDXGISwapChain*` from the `display-swapchain` property is bound to a XAML
  `SwapChainPanel`. Rendering, presentation, hardware decoding and shaders all stay in mpv;
  the UI is XAML layered on top.
- Resizing is just the `d3d11-composition-size` property in physical pixels.

## Build

Prerequisites: .NET 10 SDK, Windows 10 1809 or later. Visual Studio is not required.

```powershell
scripts\fetch-libmpv.ps1          # downloads third_party/libmpv/libmpv-2.dll (needs 7-Zip)
dotnet build src\Nami -c Debug -p:Platform=x64
dotnet run --project src\Nami -- "C:\path\to\video.mkv"
```

Release build (Native AOT, self-contained; needs the VS Build Tools C++ workload for the linker):

```powershell
scripts\publish.ps1
```

## Features

- IINA layout: transparent title bar, floating OSC (switchable to a bottom or top bar), right sidebar
  (quick settings: video / audio / subtitles; playlist / chapters / history)
- Resume playback, unlimited history, auto-queue of the opened file's folder, Windows media controls
- Multiple player windows (`Ctrl+N`, "Open in new window", `--new-window`)
- Subtitles: track switching, external files, OpenSubtitles.com search, font / color / outline / encoding settings
- Video: aspect ratio, crop, rotation, flip, zoom (pinch), picture adjustments, deinterlace, HDR output mode
- Audio: output device, 10-band equalizer, delay, shuffle / repeat
- Seek-bar thumbnails, A-B loop, frame stepping, media info inspector
- URL playback with yt-dlp (downloaded in-app), `namiplayer://` URL scheme, bookmarklet
- Music mode (automatic for audio files), mini player, always on top
- Key bindings editor (edits input.conf, applied live), mpv.conf editor, `--mpv-<option>=<value>` passthrough
- English and Japanese UI (English is the source language; see `src/Nami/Services/Translations.cs`)

## Controls

| Action | Result |
|---|---|
| Click / double-click | Pause / full screen |
| Drag | Move the window (anywhere on the video) |
| Wheel | Volume (horizontal wheel: seek) |
| Right click | Context menu |
| Keyboard | mpv's default bindings and `input.conf` work as-is (Space, arrows, 9/0, m, f, q, …) |
| Ctrl+O / Ctrl+Alt+O / Ctrl+N | Open / open in new window / new window |
| Ctrl+U / Ctrl+I / Ctrl+Shift+K / Ctrl+, / F11 | Open URL / media info / key bindings / preferences / full screen |
| Ctrl+Shift+S / Ctrl+Shift+P / Ctrl+Shift+M | Quick settings / playlist / mini player |
| `Nami.exe --register` | Register in Explorer's "Open with" and Windows Default apps (HKCU; `--unregister` to remove) |

## Layout

| Path | Role |
|---|---|
| `src/Nami/Mpv/LibMpv.cs` | libmpv P/Invoke (1:1 with client.h / render.h) |
| `src/Nami/Mpv/MpvPlayer.cs` | mpv core wrapper: event thread, property observation, UI-thread dispatch |
| `src/Nami/Player/PlayerViewModel.cs` | One per window: mpv properties mapped to UI state, commands |
| `src/Nami/Controls/VideoView.cs` | SwapChainPanel host: creates the player, binds the swapchain, tracks size / DPI |
| `src/Nami/Controls/Osc.xaml` | IINA-style floating / bar OSC |
| `src/Nami/Controls/Sidebar.xaml` | Quick settings and playlist / chapters / history sidebar |
| `src/Nami/MainPage.xaml` | Video surface: pointer / keyboard input, OSC auto-hide, drag & drop |
| `src/Nami/MainWindow.xaml` | Title bar, full screen, fit-to-video, music mode, mini player |
| `src/Nami/Interop/AspectRatioLock.cs` | WM_SIZING subclass keeping the window at the video aspect |
| `src/Nami/Interop/DisplayInfo.cs` | DXGI query of the monitor's HDR state, luminance, SDR white (used by `Player/HdrController.cs`) |
| `src/Nami/Services/L.cs` | Localization (`L.T`, `L.F`, `{l:Tr}`) |
| `third_party/libmpv/` | libmpv headers and DLL (the DLL is not committed) |

mpv reads its configuration from `%LOCALAPPDATA%\Nami\mpv\` (`mpv.conf`, `input.conf`, `scripts\`, `yt-dlp.exe`).
Logs are written next to it (`mpv.log`, `..\nami.log`).
