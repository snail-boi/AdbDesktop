# scrcpy-video

A **video-only** build of [scrcpy](https://github.com/Genymobile/scrcpy) as a
Windows DLL (`scrcpy_video.dll`), for use by AdbDesktop.

This is **not** an official scrcpy build.

The host application decodes nothing and talks to no device: it calls
`scv_open()`, polls `scv_acquire_frame()` for BGRA frames, and paints them. All
the Android-side work — pushing the server, the adb tunnel, the socket
handshake, demuxing and H.264/H.265/AV1 decoding — is upstream scrcpy code.

## Why a DLL rather than embedding scrcpy.exe

AdbDesktop draws app windows as ordinary WPF content. Embedding scrcpy's own SDL
window (`SetParent` into an `HwndHost`) would make it a child HWND, and a child
HWND always composites *on top of* WPF content in its rectangle — every overlay
in the shell (search panel, icon picker, dialogs) would be painted behind the
video, and rounded corners and shadows would not work on it. Handing frames
across the boundary as pixels avoids that entirely.

## What was kept

Upstream, unmodified:

- `adb/` — device discovery, push, tunnel (reverse + forward fallback)
- `server.c` — server lifecycle, socket handshake, TCP/IP switching
- `demuxer.c` — the meta-header protocol, config-packet merging
- `decoder.c` — avcodec wrapper producing `AVFrame`s
- `packet_merger.c`, `frame_buffer.c`, `video_regulator.c`, `clock.c`
- `trait/`, `util/`, `sys/win/`

## What was removed

All SDL window/rendering/input handling (`screen.c`, `display.c`, `opengl.c`,
`texture.c`, `fps_counter.c`, `sdl_hints.c`, `input_manager.c`, the keyboard,
mouse, gamepad, HID, UHID and USB/OTG layers), the whole audio path
(`audio_player.c`, `audio_regulator.c`, `util/audiobuf.c`, `util/average.c`),
`recorder.c`, `file_pusher.c`, `v4l2_sink.c`, `icon.c`, and the CLI
(`main.c`, `cli.c`, `scrcpy.c`).

Audio is deliberately absent. Android captures audio device-wide rather than
per-app, so it does not belong to a per-window video session; it is handled by a
separate audio-only DLL.

## Modifications to upstream files

Every change is marked with a `PORT:` comment.

| File | Change |
|---|---|
| `util/thread.c`, `util/thread.h` | Reimplemented on Win32 (`_beginthreadex`, `SRWLOCK`, `CONDITION_VARIABLE`) instead of SDL. All `sc_*` signatures unchanged. |
| `util/log.c`, `util/log.h` | Logs are formatted locally and handed to a host-installed sink instead of going through `SDL_Log*`. FFmpeg's `av_log` is folded into the same path. |
| `compat.h` | Dropped the vestigial `<SDL3/SDL_version.h>` include; added `<stdarg.h>`/`<stddef.h>`. |
| `compat.c` | Added `<stdint.h>`. |
| `server.c`, `server.h` | Added `sc_server_set_server_path()`. |
| `adb/adb.c`, `adb/adb.h` | Added `sc_adb_set_executable()`. |
| `sys/win/process.c` | `CREATE_NO_WINDOW` for console children. |

### Note on the SDL removal

Upstream used SDL for exactly two things here: threading and logging. Nothing
in `server.c`, `demuxer.c`, `decoder.c`, `video_regulator.c`, `frame_buffer.c`,
`adb/` or `trait/` referenced it. Replacing those two files removes a 22.9 MB
`SDL3.dll` runtime dependency.

Doing so exposed two latent upstream bugs: `compat.h` declares `vasprintf()` and
`reallocarray()` using `va_list` and `size_t`, and `compat.c` uses `uint64_t`,
but none of `<stdarg.h>`, `<stddef.h>` or `<stdint.h>` was ever included —
all three types arrived by accident through `<SDL3/SDL_version.h>`.

### Note on `CREATE_NO_WINDOW`

This library runs inside a windowed host with no console, so console children
(adb) would each flash up a console window. It is mutually exclusive with
`DETACHED_PROCESS`, so it is only applied on the branch that inherits handles.

## Building

Requires [w64devkit](https://github.com/skeeto/w64devkit) (mingw-w64 gcc).

```sh
PATH="../w64devkit/bin:$PATH" make -j
```

Produces `build/scrcpy_video.dll`. Runtime dependencies: `avcodec-62.dll`,
`avutil-60.dll`, `swscale-9.dll`. There is no SDL dependency.

For a build with scrcpy's internal assertions live — worth doing after any
change to session setup or teardown, since `NDEBUG` turns those contract checks
into silent undefined behaviour:

```sh
make clean && PATH="../w64devkit/bin:$PATH" make -j DEBUG=1
```

### Dependencies (`deps/`, not committed)

- `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1/` — from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). The **LGPL**
  variant, deliberately: the runtime DLLs are redistributed, and a GPL FFmpeg
  build would impose GPL on anything linking it.
- `scrcpy-server-v4.1` — the official prebuilt device server. Its version must
  match `SCRCPY_VERSION` in `src/config.h`; the server refuses a mismatch.

## Smoke test

```sh
make test
cd build && ./test_host.exe <path-to-adb.exe> ../deps/scrcpy-server-v4.1 [serial]
```

Connects, streams for five seconds, writes `frame.bmp` and reports the observed
frame rate. This exercises the whole pipeline without C#, so a later failure can
be attributed to one side or the other.

## Orientation, and what it does not control

`lock_orientation` sends `capture_orientation=@0`, which pins the **capture** to the
display's natural orientation. It prevents a device-driven rotation from swapping
the frame dimensions underneath a window.

It does **not** stop an app changing its own layout. Resizing a window changes the
virtual display's shape (that is what flex display is for), and a well-behaved app
re-lays-out to suit — YouTube, for instance, switches to its portrait player when
the display becomes portrait-shaped. That looks like a rotation but is not one: the
display never rotated, the app just chose a different layout for the aspect ratio it
was given.

This is deliberate. It is what a real external display does, and forcing an aspect
would mean letterboxing apps that are perfectly capable of adapting.

## Current limitations

- **No control channel.** `params.control` is `false`, so there is no input
  (touch/keyboard) and no `START_APP` message.

  This matters for the per-window model: launching a specific app onto a virtual
  display cannot be done with `am start --display <id>`, because the client is
  never told the virtual display id — `sc_server_info` carries only the device
  name. Upstream sends a `START_APP` *control message* and the device server
  resolves the display id itself (`Controller.java`). So per-app windows need
  the control channel, which is also what input will need.

- **One video stream per session.** Sessions are independent (handle-based, no
  global state), each with its own `scid`, so several can run at once — one per
  window, each on its own `--new-display` virtual display.

## Licence

scrcpy is Copyright (C) 2018-2025 Genymobile / Romain Vimont, licensed under the
Apache License 2.0 (see `LICENSE`). The modifications described above and the
new files `src/scrcpy_video.c`, `src/scrcpy_video.h` and `src/config.h` are
released under the same licence.

**Modification notice (Apache 2.0 §4(b)):** `scrcpy_video.dll` is not an
official scrcpy build. The changes are listed in the table above and marked
`PORT:` in the source.
