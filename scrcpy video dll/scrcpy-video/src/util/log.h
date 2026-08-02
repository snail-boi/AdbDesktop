#ifndef SC_LOG_H
#define SC_LOG_H

#include "common.h"

#include <stdarg.h>
#include <stdbool.h>

#include "options.h"

/*
 * PORT: upstream routes every log through SDL_Log*. This build has no SDL, so
 * the LOGx macros go straight to sc_log() and the formatted line is handed to a
 * sink installed by the host (see sc_log_set_sink). With no sink installed the
 * line is dropped, which is what a DLL inside a GUI process wants -- there is
 * no console to print to.
 */

#define LOG_STR_IMPL_(x) # x
#define LOG_STR(x) LOG_STR_IMPL_(x)

#define LOGV(...) sc_log(SC_LOG_LEVEL_VERBOSE, __VA_ARGS__)
#define LOGD(...) sc_log(SC_LOG_LEVEL_DEBUG, __VA_ARGS__)
#define LOGI(...) sc_log(SC_LOG_LEVEL_INFO, __VA_ARGS__)
#define LOGW(...) sc_log(SC_LOG_LEVEL_WARN, __VA_ARGS__)
#define LOGE(...) sc_log(SC_LOG_LEVEL_ERROR, __VA_ARGS__)

#define LOG_OOM() \
    LOGE("OOM: %s:%d %s()", __FILE__, __LINE__, __func__)

void
sc_set_log_level(enum sc_log_level level);

enum sc_log_level
sc_get_log_level(void);

void
sc_log(enum sc_log_level level, const char *fmt, ...);
#define LOG(LEVEL, ...) sc_log((LEVEL), __VA_ARGS__)

/* Receives one fully formatted line, without a trailing newline. */
typedef void (*sc_log_sink)(enum sc_log_level level, const char *message,
                            void *userdata);

/*
 * Install the destination for log lines. Intended to be called once, before any
 * session starts; there is no locking around the sink pointer.
 */
void
sc_log_set_sink(sc_log_sink sink, void *userdata);

#ifdef _WIN32
// Log system error (typically returned by GetLastError() or similar)
bool
sc_log_windows_error(const char *prefix, int error);
#endif

void
sc_log_configure(void);

#endif
