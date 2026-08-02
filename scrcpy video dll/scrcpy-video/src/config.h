#ifndef SC_CONFIG_H
#define SC_CONFIG_H

// Hand-written replacement for the meson-generated config.h.
// Video-only DLL build for Windows (mingw-w64 / w64devkit).
//
// Mirrors the conf.set() calls in scrcpy's app/meson.build.

// Must match the version of the scrcpy-server file pushed to the device: the
// server performs a version handshake and refuses a mismatch.
#define SCRCPY_VERSION "4.1"

// Unix install prefix. Unused on Windows but referenced by server.c.
#define PREFIX "."

// Locate scrcpy-server next to the executable when SCRCPY_SERVER_PATH is unset.
// The host overrides this outright via sc_server_set_server_path().
#define PORTABLE 1

#define DEFAULT_LOCAL_PORT_RANGE_FIRST 27183
#define DEFAULT_LOCAL_PORT_RANGE_LAST 27199

// mingw-w64 provides strdup; the other functions meson probes for (asprintf,
// vasprintf, nrand48, jrand48, reallocarray) are supplied by compat.c.
#define HAVE_STRDUP 1

#endif
