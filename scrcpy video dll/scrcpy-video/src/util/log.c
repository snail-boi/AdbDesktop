// PORT: SDL-free logging. Upstream funnels everything through SDL_Log*; here a
// line is formatted locally and handed to a sink the host installs. FFmpeg logs
// are folded into the same path, exactly as upstream folds them into SDL's.

#include "log.h"

#if _WIN32
# include <windows.h>
#endif
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libavutil/log.h>

// Matches upstream's default (SDL defaults the application category to INFO).
static enum sc_log_level g_log_level = SC_LOG_LEVEL_INFO;

static sc_log_sink g_log_sink;
static void *g_log_sink_userdata;

void
sc_log_set_sink(sc_log_sink sink, void *userdata) {
    g_log_sink = sink;
    g_log_sink_userdata = userdata;
}

void
sc_set_log_level(enum sc_log_level level) {
    g_log_level = level;
}

enum sc_log_level
sc_get_log_level(void) {
    return g_log_level;
}

static void
sc_log_dispatch(enum sc_log_level level, const char *prefix, const char *fmt,
                va_list ap) {
    if (level < g_log_level) {
        return;
    }

    sc_log_sink sink = g_log_sink;
    if (!sink) {
        // No host attached: drop it. A DLL in a GUI process has no console, so
        // printing would go nowhere useful anyway.
        return;
    }

    char stack_buf[1024];
    char *buf = stack_buf;
    size_t prefix_len = prefix ? strlen(prefix) : 0;

    if (prefix_len) {
        memcpy(buf, prefix, prefix_len);
    }

    va_list ap_copy;
    va_copy(ap_copy, ap);
    int written = vsnprintf(buf + prefix_len, sizeof(stack_buf) - prefix_len,
                            fmt, ap_copy);
    va_end(ap_copy);

    if (written < 0) {
        return;
    }

    // Long lines (an adb command line, a codec dump) would otherwise be
    // silently truncated, so retry once on the heap.
    if ((size_t) written >= sizeof(stack_buf) - prefix_len) {
        size_t needed = prefix_len + (size_t) written + 1;
        buf = malloc(needed);
        if (!buf) {
            // Report the truncated version rather than nothing at all.
            sink(level, stack_buf, g_log_sink_userdata);
            return;
        }

        if (prefix_len) {
            memcpy(buf, prefix, prefix_len);
        }
        vsnprintf(buf + prefix_len, needed - prefix_len, fmt, ap);
    }

    sink(level, buf, g_log_sink_userdata);

    if (buf != stack_buf) {
        free(buf);
    }
}

void
sc_log(enum sc_log_level level, const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    sc_log_dispatch(level, NULL, fmt, ap);
    va_end(ap);
}

#ifdef _WIN32
bool
sc_log_windows_error(const char *prefix, int error) {
    assert(prefix);

    char *message;
    DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM;
    DWORD lang_id = MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US);
    int ret =
        FormatMessage(flags, NULL, error, lang_id, (char *) &message, 0, NULL);
    if (ret <= 0) {
        return false;
    }

    // Note: message already contains a trailing '\n'
    LOGE("%s: [%d] %s", prefix, error, message);
    LocalFree(message);
    return true;
}
#endif

static enum sc_log_level
sc_level_from_av_level(int level) {
    switch (level) {
        case AV_LOG_PANIC:
        case AV_LOG_FATAL:
        case AV_LOG_ERROR:
            return SC_LOG_LEVEL_ERROR;
        case AV_LOG_WARNING:
            return SC_LOG_LEVEL_WARN;
        case AV_LOG_INFO:
            return SC_LOG_LEVEL_INFO;
        default:
            // do not forward others, which are too verbose
            return SC_LOG_LEVEL_VERBOSE;
    }
}

static void
sc_av_log_callback(void *avcl, int level, const char *fmt, va_list vl) {
    (void) avcl;

    if (level > AV_LOG_INFO) {
        return; // too verbose
    }

    sc_log_dispatch(sc_level_from_av_level(level), "[FFmpeg] ", fmt, vl);
}

void
sc_log_configure(void) {
#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif

    // Redirect FFmpeg logs into the same sink
    av_log_set_callback(sc_av_log_callback);
}
