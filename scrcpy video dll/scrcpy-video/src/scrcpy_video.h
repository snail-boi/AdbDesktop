#ifndef SCRCPY_VIDEO_H
#define SCRCPY_VIDEO_H

/*
 * scrcpy_video -- video-only scrcpy as a Windows DLL.
 *
 * Carved down from upstream scrcpy v4.1. The device-side server, the adb
 * push/tunnel/connect handshake, the demuxer and the decoder are upstream code,
 * unmodified. Removed: all SDL window/render/input handling, the audio path,
 * the recorder, v4l2, OTG/HID/USB, and the CLI.
 *
 * Threading model
 * ---------------
 * - scv_open() / scv_close() are not reentrant for a given session, but
 *   different sessions are fully independent -- unlike the audio-only port,
 *   there is no static global session here. AdbDesktop runs one session per
 *   window (each on its own Android virtual display), so concurrency is the
 *   whole point.
 * - scv_acquire_frame() / scv_release_frame() may be called from any single
 *   thread (in practice the host's UI/render thread).
 * - Callbacks fire on internal background threads. Do not call scv_close()
 *   from inside one.
 *
 * Frame delivery
 * --------------
 * Pull, not push. The host asks for the newest frame when it is ready to
 * paint, which lets it run at its own refresh rate and drop frames it would
 * never display. Frames are converted to BGRA32 (what WPF's WriteableBitmap
 * wants) so the host does no pixel work.
 *
 * Note there is no video equivalent of the audio port's "always return data,
 * pad with silence" contract: audio is clocked by the sound card, video is not,
 * so scv_acquire_frame() explicitly reports whether a new frame exists.
 */

#include <stdint.h>

#ifdef SCV_BUILDING
# define SCV_API __declspec(dllexport)
#else
# define SCV_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Opaque session handle. */
typedef struct scv_session scv_session;

enum scv_log_level {
    SCV_LOG_VERBOSE = 0,
    SCV_LOG_DEBUG = 1,
    SCV_LOG_INFO = 2,
    SCV_LOG_WARN = 3,
    SCV_LOG_ERROR = 4,
};

enum scv_event {
    SCV_EVENT_CONNECTED = 0,
    SCV_EVENT_CONNECTION_FAILED = 1,
    SCV_EVENT_STREAM_STARTED = 2,   /* first frame decoded; size is known */
    SCV_EVENT_STREAM_STOPPED = 3,
    SCV_EVENT_DISCONNECTED = 4,
    SCV_EVENT_SIZE_CHANGED = 5,     /* device rotated or display resized */
    SCV_EVENT_ERROR = 6,
};

/* scv_open() error codes (also reported via the out-param). */
enum scv_error {
    SCV_OK = 0,
    SCV_ERR_ABI = -1,               /* struct_size mismatch */
    SCV_ERR_BAD_ARGUMENT = -2,
    SCV_ERR_INIT = -3,              /* adb/net/server init failed */
    SCV_ERR_THREAD = -4,
    SCV_ERR_OOM = -5,
};

typedef void (*scv_event_cb)(scv_session *session, int32_t event,
                             void *userdata);
typedef void (*scv_log_cb)(int32_t level, const char *message, void *userdata);

struct scv_settings {
    /*
     * Must be set to sizeof(struct scv_settings). Guards against a host built
     * against a different version of this header.
     */
    uint32_t struct_size;

    const char *serial;         /* NULL = the only connected device */
    const char *adb_path;       /* NULL = "adb" from PATH / ADB env */
    const char *server_path;    /* NULL = scrcpy-server next to the host exe */

    const char *video_codec;    /* "h264" (default), "h265", "av1" */
    const char *video_encoder;  /* NULL = device default */
    const char *video_codec_options;

    uint32_t video_bit_rate;    /* 0 = 8000000 */
    const char *max_fps;        /* float parsed by the server, e.g. "60" */
    uint16_t max_size;          /* 0 = unlimited */
    const char *crop;
    const char *angle;

    /*
     * "<width>x<height>[/<dpi>]" creates a new Android virtual display rather
     * than mirroring the phone screen. This is what makes several independent
     * app windows possible; NULL mirrors display_id instead.
     */
    const char *new_display;
    uint32_t display_id;            /* used only when new_display is NULL */
    uint8_t vd_destroy_content;     /* kill the apps when the display goes away */
    uint8_t vd_system_decorations;  /* default 1 */

    /*
     * Opens the control channel, which is what makes scv_resize_display() and
     * scv_start_app() work. Required for flex_display.
     */
    uint8_t control;

    /*
     * Lets the virtual display be resized live via scv_resize_display(), so a
     * window resize re-lays-out the Android side instead of scaling a fixed
     * image. Requires both `control` and `new_display`.
     */
    uint8_t flex_display;

    /*
     * Pins the capture to the display's natural orientation, so an app that
     * requests landscape/portrait cannot rotate the stream underneath a window
     * that is not shaped for it. Without this the frame dimensions swap
     * mid-session (e.g. 880x588 suddenly becomes 588x880).
     *
     * Sends "capture_orientation=@0".
     */
    uint8_t lock_orientation;

    uint16_t port_first;        /* 0 = 27183 */
    uint16_t port_last;         /* 0 = 27199 */

    uint8_t log_level;          /* enum scv_log_level; default INFO */

    scv_event_cb event_cb;
    void *userdata;
};

/* Fill with defaults. Always call this before setting fields. */
SCV_API void
scv_settings_init(struct scv_settings *settings);

/*
 * Install a process-wide log sink. Logs from scrcpy internals are not tagged
 * per session, so this is global rather than per-session. Call before opening
 * any session; pass NULL to silence.
 */
SCV_API void
scv_set_log_callback(scv_log_cb cb, void *userdata);

/*
 * Start a session. Returns NULL on failure, with *error set (may be NULL).
 * Returns as soon as the server thread is started; watch for
 * SCV_EVENT_CONNECTED / SCV_EVENT_CONNECTION_FAILED.
 */
SCV_API scv_session *
scv_open(const struct scv_settings *settings, int32_t *error);

/* Stop and free. Blocks until every internal thread has joined. */
SCV_API void
scv_close(scv_session *session);

/*
 * Current frame size, i.e. what scv_acquire_frame() will return. Returns 0
 * before the first frame arrives.
 */
SCV_API int32_t
scv_get_size(scv_session *session, uint32_t *width, uint32_t *height);

/*
 * Resize the virtual display, for a flex display.
 *
 * This is how a window resize is propagated: rather than scaling a fixed-size
 * image, the Android side genuinely re-lays-out at the new size. Requires
 * `control` and `flex_display`, and a session created with `new_display`.
 *
 * Cheap to call repeatedly -- upstream never queues these, a new request simply
 * replaces any pending one, so it is safe to call on every resize tick.
 */
SCV_API int32_t
scv_resize_display(scv_session *session, uint16_t width, uint16_t height);

/*
 * Launch an app onto this session's display, by package name or label.
 *
 * Note this cannot be done from the host with "am start --display <id>": the
 * client is never told the virtual display id (sc_server_info carries only the
 * device name). The device server resolves the display itself when it handles
 * this message. Requires `control`.
 *
 * Prefix the name with '+' to force-stop the app first.
 */
SCV_API int32_t
scv_start_app(scv_session *session, const char *name);

/* Motion actions, matching Android's AMOTION_EVENT_ACTION_*. */
enum scv_touch_action {
    SCV_TOUCH_DOWN = 0,
    SCV_TOUCH_UP = 1,
    SCV_TOUCH_MOVE = 2,
};

/*
 * Inject a touch event. Requires `control`.
 *
 * (x, y) are in the coordinate space of a `width` x `height` image -- pass the
 * size of the frame the user actually clicked on and the device scales it, so
 * the host never has to care whether the stream size matches the display size.
 *
 * A mouse is reported as a single pointer; pass pressure 1.0 for down/move and
 * 0.0 for up.
 */
SCV_API int32_t
scv_inject_touch(scv_session *session, int32_t action, int32_t x, int32_t y,
                 uint16_t width, uint16_t height, float pressure,
                 uint32_t buttons);

/* Inject a scroll event, in the same coordinate space as scv_inject_touch. */
SCV_API int32_t
scv_inject_scroll(scv_session *session, int32_t x, int32_t y,
                  uint16_t width, uint16_t height,
                  float hscroll, float vscroll);

/* Inject a key event. `action` is 0 for down, 1 for up; keycode is an Android keycode. */
SCV_API int32_t
scv_inject_keycode(scv_session *session, int32_t action, int32_t keycode,
                   uint32_t repeat, uint32_t metastate);

/* Inject UTF-8 text (as if typed). */
SCV_API int32_t
scv_inject_text(scv_session *session, const char *text);

/* Press the device BACK button (or wake the screen). */
SCV_API int32_t
scv_back(scv_session *session, int32_t action);

/* Device model name reported during the connection handshake, or "". */
SCV_API const char *
scv_get_device_name(scv_session *session);

/*
 * Take the newest frame, if one arrived since the last call.
 *
 * Returns 1 and fills the out-params when a new frame is available, 0 when
 * there is nothing new (the previous frame is still valid on screen), or a
 * negative scv_error on failure.
 *
 * On success the buffer stays valid and locked until scv_release_frame(). Copy
 * it out promptly -- the decoder thread blocks on the next frame while a frame
 * is held.
 *
 * Format is BGRA32, top-down, `stride` bytes per row.
 */
SCV_API int32_t
scv_acquire_frame(scv_session *session, const uint8_t **data, uint32_t *stride,
                  uint32_t *width, uint32_t *height);

/* Release a frame taken by scv_acquire_frame(). */
SCV_API void
scv_release_frame(scv_session *session);

#ifdef __cplusplus
}
#endif

#endif
