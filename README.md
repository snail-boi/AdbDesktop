# adbDesktop

A desktop environment on Windows for **any** Android phone, built on `adb` and a
purpose-built scrcpy port.

Samsung DeX gives Samsung phones a desktop when you plug them into a monitor. No other
manufacturer ships an equivalent. adbDesktop is that idea, on the PC side: your phone's
apps open as real, resizable Windows windows on a desktop with icons and a taskbar.

> Not affiliated with or endorsed by Samsung or Google. "DeX" and "Android" belong to
> their respective owners.

---

## What it does

- **Desktop shell** — icons with drag-and-snap, in-place rename, custom icons, a taskbar
  with a search bar, clock and per-device status.
- **Apps in windows** — each window gets its own Android *virtual display*, so an app
  opens as a proper window, resizes freely, and takes mouse, scroll and keyboard input.
  The same app can be opened twice; each copy is an independent instance.
- **Multiple devices** — several phones at once, each with its own desktop, battery
  readout and audio link. A shared unified desktop sits alongside them.
- **Audio** — device audio played through the PC, independently per device with its own
  volume.
- **Wireless** — USB, Wireless Debugging (QR or pairing code) and legacy TCP/IP. A phone
  reachable more than one way is bundled into a single device, and switches transport by
  itself when you pull the cable.

Apps are added by searching the phone's installed apps; adbDesktop pulls the APK, digs
the launcher icons out of it and lets you pick one (or supply your own image).

## Requirements

- Windows 10/11, x64
- .NET 10 desktop runtime
- An Android device with **USB debugging** enabled. Virtual displays need **Android 11+**;
  Wireless Debugging does too.

`adb` ships with the app — nothing to install separately.

## Install

Grab the latest [release](https://github.com/snail-boi/adbDesktop/releases):

- **`AdbDesktop_Setup.exe`** — installs to Program Files, settings under
  `%AppData%\Snail\adbDesktop`.
- **`AdbDesktop_<version>_portable.zip`** — unzip and run. Settings live next to the
  executable, so it leaves nothing behind. The `portable.mode` marker file is what
  selects this; delete it and the copy behaves like an installed one.

## Building

```bash
dotnet build AdbDesktop.slnx -c Release
```

A Release build also packages both distributions into `AdbDesktop installer/output`.
The installer step needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); without it
the build still produces the portable zip and warns. Point it elsewhere with
`-p:InnoSetupExe=<path to ISCC.exe>`.

Releases are cut by pushing a tag (`v0.1.0`), which runs
[`.github/workflows/release.yml`](.github/workflows/release.yml).

### The native port

`scrcpy video dll/scrcpy-video` is a from-scratch port of
[scrcpy](https://github.com/Genymobile/scrcpy) built as a DLL: SDL removed, Win32
threading, frames handed to WPF as pixels rather than an embedded window. It is built
separately with `make` (w64devkit), and the resulting `scrcpy_video.dll` is committed
under `AdbDesktop/Assets`, so building the app does **not** require the C toolchain.

## Layout

```
AdbDesktop/                  the WPF app
  Models/  Services/  ViewModels/  Views/
  Assets/                    adb, the scrcpy ports, FFmpeg, libwebp
AdbDesktop installer/        Inno Setup script; Release output lands in output/
scrcpy video dll/
  scrcpy-video/              our port  (the rest of this folder is not in the repo:
                             scrcpy-master is pristine upstream, w64devkit a toolchain)
```

## Third-party

scrcpy (Apache-2.0), FFmpeg (LGPL-2.1+), libwebp (BSD-3), SDL (Zlib), NAudio (MIT),
QRCoder (MIT), Android platform tools. Full notices in
[THIRD_PARTY_LICENSES.txt](AdbDesktop/THIRD_PARTY_LICENSES.txt).

## Licence

See [LICENSE.txt](LICENSE.txt).
