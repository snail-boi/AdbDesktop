/*
 * scrcpy_video -- video-only scrcpy as a DLL.
 *
 * This file is the whole port. Everything else in src/ is upstream scrcpy v4.1,
 * either untouched or with a small, commented "PORT:" change.
 *
 * It replaces upstream's main.c + cli.c + scrcpy.c + screen.c: it owns session
 * lifetime, wires server -> demuxer -> decoder -> sink, and terminates the
 * pipeline in a frame sink that converts to BGRA and hands frames to the host.
 *
 * Pipeline (all upstream except the last box):
 *
 *   sc_server (adb push, tunnel, connect)
 *     -> video_socket
 *       -> sc_demuxer   (meta-header protocol, config packet merging)
 *         -> sc_decoder (avcodec -> AVFrame, YUV420P)
 *           -> scv_video_sink   <- this file
 */

#include "scrcpy_video.h"

#include <assert.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>
#include <libavutil/pixfmt.h>
#include <libswscale/swscale.h>

#include "adb/adb.h"
#include "common.h"
#include "control_msg.h"
#include "controller.h"
#include "decoder.h"
#include "demuxer.h"
#include "options.h"
#include "server.h"
#include "trait/frame_sink.h"
#include "util/log.h"
#include "util/net.h"
#include "util/rand.h"
#include "util/thread.h"
#include "util/tick.h"
#include "video_regulator.h"

/* ------------------------------------------------------------------ */
/* session                                                            */
/* ------------------------------------------------------------------ */

struct scv_video_sink {
    struct sc_frame_sink frame_sink; // frame sink trait
    scv_session *session;
};

struct scv_session {
    struct sc_server server;
    struct sc_demuxer demuxer;
    struct sc_decoder decoder;
    struct scv_video_sink sink;
    struct sc_controller controller;

    /*
     * Optional, and only spliced into the frame chain when video_buffer is
     * non-zero. sc_video_regulator_init() documents the delay as strictly
     * positive, and a zero-delay regulator would be a thread and a queue doing
     * nothing but copying frames.
     */
    struct sc_video_regulator regulator;
    bool regulator_enabled;
    uint32_t video_buffer;

    /*
     * Owned copies of every string in the settings. The caller's buffers may be
     * temporaries (they routinely are when marshalled from C#), and the server
     * thread reads these long after scv_open() has returned.
     */
    char *serial;
    char *adb_path;
    char *server_path;
    char *video_encoder;
    char *video_codec_options;
    char *max_fps;
    char *crop;
    char *angle;
    char *new_display;

    scv_event_cb event_cb;
    void *userdata;

    sc_mutex mutex;

    /*
     * Triple buffering. The decoder writes into `scratch` outside the lock,
     * then swaps it with `ready` under the lock; the host swaps `ready` with
     * `front` when it acquires. With only two buffers the decoder would have to
     * stall (or tear) whenever the host was holding a frame; with three it
     * simply overwrites the frame nobody has taken yet, which is the right
     * behaviour for video -- dropping a stale frame beats delaying a fresh one.
     */
    uint8_t *scratch;
    uint8_t *ready;
    uint8_t *front;
    /*
     * A buffer that was replaced by a resize while the host still held it. Kept
     * alive until scv_release_frame(), because the host is reading from it on
     * another thread.
     */
    uint8_t *retired;
    /*
     * The exact buffer handed out by scv_acquire_frame(). Tracked separately from
     * `front`, because a resize reassigns `front` to a freshly allocated buffer
     * while the host is still reading the old one -- after which `front` no longer
     * says anything about what the host holds.
     */
    uint8_t *held;
    size_t buffer_size;
    bool has_new;
    bool front_held;

    struct SwsContext *sws;
    int src_width;
    int src_height;
    enum AVPixelFormat src_format;

    uint32_t width;
    uint32_t height;
    uint32_t stride;

    bool server_started;
    bool demuxer_started;
    bool controller_started;
    bool control_enabled;
    bool connected;

    char device_name[SC_DEVICE_NAME_FIELD_LENGTH];
};

/* ------------------------------------------------------------------ */
/* logging                                                            */
/* ------------------------------------------------------------------ */

static scv_log_cb g_log_cb;
static void *g_log_userdata;
static bool g_log_configured;

static void
scv_log_sink(enum sc_log_level level, const char *message, void *userdata) {
    (void) userdata;

    scv_log_cb cb = g_log_cb;
    if (cb) {
        cb((int32_t) level, message, g_log_userdata);
    }
}

void
scv_set_log_callback(scv_log_cb cb, void *userdata) {
    g_log_cb = cb;
    g_log_userdata = userdata;
    sc_log_set_sink(scv_log_sink, NULL);

    if (!g_log_configured) {
        // Also routes FFmpeg's av_log into the same sink
        sc_log_configure();
        g_log_configured = true;
    }
}

static void
scv_notify(scv_session *s, enum scv_event event) {
    if (s->event_cb) {
        s->event_cb(s, (int32_t) event, s->userdata);
    }
}

/* ------------------------------------------------------------------ */
/* frame sink                                                         */
/* ------------------------------------------------------------------ */

// Caller must hold s->mutex.
static bool
scv_realloc_buffers_locked(scv_session *s, uint32_t width, uint32_t height) {
    if (!width || !height || width > 8192 || height > 8192) {
        LOGE("Refusing absurd frame size %ux%u", width, height);
        return false;
    }

    /*
     * Align the stride, and allocate through av_malloc so the buffer start is
     * aligned too. swscale writes in SIMD-width blocks and will happily run past
     * the end of a tightly-sized, unaligned destination -- which is a silent
     * heap corruption that only shows up under heavy resizing.
     */
    size_t stride = ((size_t) width * 4 + 63) & ~(size_t) 63;
    size_t size = stride * height;

    // Extra slack for the same reason: swscale may touch a little past the last
    // row when the height is not a multiple of its block size.
    uint8_t *a = av_malloc(size + 64);
    uint8_t *b = av_malloc(size + 64);
    uint8_t *c = av_malloc(size + 64);
    if (!a || !b || !c) {
        av_free(a);
        av_free(b);
        av_free(c);
        LOG_OOM();
        return false;
    }

    // Zeroed rather than left uninitialised. No alpha fill: nothing is ever shown
    // before the first real frame (the host only paints once has_new is set), and
    // touching every fourth byte of three buffers on every resize was pure cost.
    memset(a, 0, size);
    memset(b, 0, size);
    memset(c, 0, size);

    /*
     * The host may be reading its buffer right now: it keeps the pointer between
     * scv_acquire_frame() and scv_release_frame(), and blits from it on another
     * thread. Freeing that is what corrupts the heap during a fast resize, so it
     * is retired instead and freed on release.
     *
     * One hold can outlive several resizes, which is why the held buffer is
     * matched by pointer rather than assumed to still be `front`. By the second
     * resize it is not: `front` was reassigned by the first one, and freeing the
     * previous retiree would free the buffer the host is reading.
     */
    uint8_t *held = s->front_held ? s->held : NULL;

    if (s->scratch != held) {
        av_free(s->scratch);
    }
    if (s->ready != held) {
        av_free(s->ready);
    }
    if (s->front != held) {
        av_free(s->front);
    }
    if (s->retired != held) {
        av_free(s->retired);
    }

    s->retired = held;

    s->scratch = a;
    s->ready = b;
    s->front = c;
    s->buffer_size = size;
    s->width = width;
    s->height = height;
    s->stride = (uint32_t) stride;
    s->has_new = false;

    // front_held is deliberately preserved: the host still owns the retired
    // buffer until it calls scv_release_frame().

    return true;
}

static bool
scv_ensure_sws(scv_session *s, const AVFrame *frame) {
    if (s->sws && s->src_width == frame->width
            && s->src_height == frame->height
            && s->src_format == (enum AVPixelFormat) frame->format) {
        return true;
    }

    sws_freeContext(s->sws);
    s->sws = sws_getContext(frame->width, frame->height,
                            (enum AVPixelFormat) frame->format,
                            frame->width, frame->height, AV_PIX_FMT_BGRA,
                            SWS_BILINEAR, NULL, NULL, NULL);
    if (!s->sws) {
        LOGE("Could not create swscale context (%dx%d fmt=%d)",
             frame->width, frame->height, frame->format);
        return false;
    }

    s->src_width = frame->width;
    s->src_height = frame->height;
    s->src_format = (enum AVPixelFormat) frame->format;
    return true;
}

static bool
scv_sink_open(struct sc_frame_sink *sink, const AVCodecContext *ctx,
              const struct sc_stream_session *session) {
    struct scv_video_sink *vs =
        container_of(sink, struct scv_video_sink, frame_sink);
    scv_session *s = vs->session;

    (void) session;

    LOGI("Video stream: %dx%d", ctx->width, ctx->height);

    sc_mutex_lock(&s->mutex);
    bool ok = scv_realloc_buffers_locked(s, (uint32_t) ctx->width,
                                         (uint32_t) ctx->height);
    sc_mutex_unlock(&s->mutex);

    if (!ok) {
        return false;
    }

    scv_notify(s, SCV_EVENT_STREAM_STARTED);
    return true;
}

static void
scv_sink_close(struct sc_frame_sink *sink) {
    struct scv_video_sink *vs =
        container_of(sink, struct scv_video_sink, frame_sink);

    scv_notify(vs->session, SCV_EVENT_STREAM_STOPPED);
}

static bool
scv_sink_push(struct sc_frame_sink *sink, const AVFrame *frame) {
    struct scv_video_sink *vs =
        container_of(sink, struct scv_video_sink, frame_sink);
    scv_session *s = vs->session;

    if (!scv_ensure_sws(s, frame)) {
        return false;
    }

    // The device can change resolution mid-stream (rotation, or a resized
    // virtual display) without a new session packet, so the buffers are
    // validated per frame rather than only on open.
    sc_mutex_lock(&s->mutex);
    if ((uint32_t) frame->width != s->width
            || (uint32_t) frame->height != s->height) {
        if (!scv_realloc_buffers_locked(s, (uint32_t) frame->width,
                                        (uint32_t) frame->height)) {
            sc_mutex_unlock(&s->mutex);
            return false;
        }
        sc_mutex_unlock(&s->mutex);
        scv_notify(s, SCV_EVENT_SIZE_CHANGED);
        sc_mutex_lock(&s->mutex);
    }

    uint8_t *dst = s->scratch;
    int dst_stride = (int) s->stride;
    sc_mutex_unlock(&s->mutex);

    // Convert outside the lock: this is the expensive part, and holding the
    // mutex here would block the host's acquire for the whole conversion.
    uint8_t *dst_planes[4] = { dst, NULL, NULL, NULL };
    int dst_strides[4] = { dst_stride, 0, 0, 0 };

    int ret = sws_scale(s->sws, (const uint8_t *const *) frame->data,
                        frame->linesize, 0, frame->height,
                        dst_planes, dst_strides);
    if (ret <= 0) {
        LOGE("swscale conversion failed");
        return false;
    }

    sc_mutex_lock(&s->mutex);
    // The scratch buffer may have been reallocated by a concurrent resize while
    // the lock was released; if so, this frame is stale -- drop it.
    if (dst == s->scratch) {
        uint8_t *tmp = s->ready;
        s->ready = s->scratch;
        s->scratch = tmp;
        s->has_new = true;
    }
    sc_mutex_unlock(&s->mutex);

    return true;
}

static bool
scv_sink_push_session(struct sc_frame_sink *sink,
                      const struct sc_stream_session *session) {
    struct scv_video_sink *vs =
        container_of(sink, struct scv_video_sink, frame_sink);
    scv_session *s = vs->session;

    uint32_t width = session->video.width;
    uint32_t height = session->video.height;
    if (!width || !height) {
        return true;
    }

    LOGI("Video stream session: %ux%u", width, height);

    sc_mutex_lock(&s->mutex);
    bool changed = width != s->width || height != s->height;
    bool ok = true;
    if (changed) {
        ok = scv_realloc_buffers_locked(s, width, height);
    }
    sc_mutex_unlock(&s->mutex);

    if (ok && changed) {
        scv_notify(s, SCV_EVENT_SIZE_CHANGED);
    }

    return ok;
}

static const struct sc_frame_sink_ops scv_sink_ops = {
    .open = scv_sink_open,
    .close = scv_sink_close,
    .push = scv_sink_push,
    .push_session = scv_sink_push_session,
};

/* ------------------------------------------------------------------ */
/* server / demuxer callbacks                                         */
/* ------------------------------------------------------------------ */

static void
scv_on_connection_failed(struct sc_server *server, void *userdata) {
    (void) server;
    scv_notify((scv_session *) userdata, SCV_EVENT_CONNECTION_FAILED);
}

/*
 * Not optional: sc_demuxer_init() asserts on it, and the demuxer thread calls
 * it unconditionally when the stream ends. Leaving it NULL only looks harmless
 * because NDEBUG compiles the assert away -- the release build then jumps
 * through a NULL pointer at teardown.
 */
static void
scv_demuxer_on_ended(struct sc_demuxer *demuxer, enum sc_demuxer_status status,
                     void *userdata) {
    (void) demuxer;
    scv_session *s = userdata;

    switch (status) {
        case SC_DEMUXER_STATUS_ERROR:
            LOGE("Video stream ended with an error");
            scv_notify(s, SCV_EVENT_ERROR);
            break;
        case SC_DEMUXER_STATUS_DISABLED:
            LOGW("Video stream was disabled by the device");
            scv_notify(s, SCV_EVENT_ERROR);
            break;
        case SC_DEMUXER_STATUS_EOS:
            // Normal: the device closed the stream, or we are shutting down.
            LOGD("Video stream ended");
            break;
    }
}

static void
scv_controller_on_ended(struct sc_controller *controller, bool error,
                        void *userdata) {
    (void) controller;
    scv_session *s = userdata;

    if (error) {
        LOGE("Control channel ended with an error");
        scv_notify(s, SCV_EVENT_ERROR);
    }
}

static void
scv_on_connected(struct sc_server *server, void *userdata) {
    scv_session *s = userdata;

    memcpy(s->device_name, server->info.device_name, sizeof(s->device_name));
    s->device_name[sizeof(s->device_name) - 1] = '\0';
    s->connected = true;

    if (s->control_enabled) {
        static const struct sc_controller_callbacks controller_cbs = {
            .on_ended = scv_controller_on_ended,
        };

        if (sc_controller_init(&s->controller, server->control_socket,
                               &controller_cbs, s)) {
            // No acksync and no UHID devices in this build; the receiver only
            // drains the socket.
            sc_controller_configure(&s->controller, NULL, NULL);

            if (sc_controller_start(&s->controller)) {
                s->controller_started = true;
            } else {
                LOGE("Could not start the controller");
                sc_controller_destroy(&s->controller);
            }
        } else {
            LOGE("Could not init the controller");
        }
    }

    // The video socket only exists once connected, so the demuxer is wired up
    // and started here rather than in scv_open().
    static const struct sc_demuxer_callbacks demuxer_cbs = {
        .on_ended = scv_demuxer_on_ended,
    };

    sc_demuxer_init(&s->demuxer, "video", server->video_socket, &demuxer_cbs,
                    s);
    sc_decoder_init(&s->decoder, "video");

    sc_packet_source_add_sink(&s->demuxer.packet_source,
                              &s->decoder.packet_sink);

    /*
     * decoder -> [regulator] -> sink.
     *
     * The regulator needs no explicit start/stop here: its thread is created by
     * its frame_sink open op and joined by the close op, both of which the
     * decoder drives through the frame source chain. Tearing down the demuxer
     * in scv_close() therefore joins the buffering thread too.
     */
    if (s->regulator_enabled) {
        sc_video_regulator_init(&s->regulator,
                                SC_TICK_FROM_MS(s->video_buffer),
                                // Show something immediately rather than making
                                // the window sit blank for the buffer duration.
                                true);
        sc_frame_source_add_sink(&s->decoder.frame_source,
                                 &s->regulator.frame_sink);
        sc_frame_source_add_sink(&s->regulator.frame_source,
                                 &s->sink.frame_sink);
    } else {
        sc_frame_source_add_sink(&s->decoder.frame_source, &s->sink.frame_sink);
    }

    if (!sc_demuxer_start(&s->demuxer)) {
        LOGE("Could not start video demuxer");
        scv_notify(s, SCV_EVENT_ERROR);
        return;
    }

    s->demuxer_started = true;
    scv_notify(s, SCV_EVENT_CONNECTED);
}

static void
scv_on_disconnected(struct sc_server *server, void *userdata) {
    (void) server;
    scv_session *s = userdata;

    s->connected = false;
    scv_notify(s, SCV_EVENT_DISCONNECTED);
}

/* ------------------------------------------------------------------ */
/* settings                                                           */
/* ------------------------------------------------------------------ */

void
scv_settings_init(struct scv_settings *settings) {
    memset(settings, 0, sizeof(*settings));
    settings->struct_size = sizeof(*settings);
    settings->log_level = SCV_LOG_INFO;
    settings->vd_system_decorations = 1;
}

static bool
scv_dup(char **dst, const char *src) {
    if (!src) {
        *dst = NULL;
        return true;
    }

    *dst = strdup(src);
    if (!*dst) {
        LOG_OOM();
        return false;
    }
    return true;
}

static enum sc_codec
scv_parse_codec(const char *name) {
    if (!name) {
        return SC_CODEC_H264;
    }
    if (!strcmp(name, "h265")) {
        return SC_CODEC_H265;
    }
    if (!strcmp(name, "av1")) {
        return SC_CODEC_AV1;
    }
    return SC_CODEC_H264;
}

// Same as upstream's scrcpy_generate_scid(). The scid namespaces the device-side
// socket name, so every concurrent session needs a distinct one.
static uint32_t
scv_generate_scid(void) {
    struct sc_rand rand;
    sc_rand_init(&rand);
    return sc_rand_u32(&rand) & 0x7FFFFFFF;
}

static void
scv_free_strings(scv_session *s) {
    free(s->serial);
    free(s->adb_path);
    free(s->server_path);
    free(s->video_encoder);
    free(s->video_codec_options);
    free(s->max_fps);
    free(s->crop);
    free(s->angle);
    free(s->new_display);
}

/* ------------------------------------------------------------------ */
/* public API                                                         */
/* ------------------------------------------------------------------ */

scv_session *
scv_open(const struct scv_settings *settings, int32_t *error) {
    int32_t err = SCV_OK;

    if (!settings || settings->struct_size != sizeof(*settings)) {
        // The host was built against a different version of this header.
        err = SCV_ERR_ABI;
        goto fail;
    }

    if (settings->log_level > SCV_LOG_ERROR) {
        err = SCV_ERR_BAD_ARGUMENT;
        goto fail;
    }

    scv_session *s = calloc(1, sizeof(*s));
    if (!s) {
        err = SCV_ERR_OOM;
        goto fail;
    }

    s->event_cb = settings->event_cb;
    s->userdata = settings->userdata;
    s->src_format = AV_PIX_FMT_NONE;
    s->control_enabled = settings->control != 0;

    // Capped rather than rejected: a buffer this long is already well past
    // useful, and failing to open a session over it would be worse than
    // quietly clamping.
    s->video_buffer = settings->video_buffer > 5000 ? 5000
                                                    : settings->video_buffer;
    s->regulator_enabled = s->video_buffer > 0;

    if (!scv_dup(&s->serial, settings->serial)
            || !scv_dup(&s->adb_path, settings->adb_path)
            || !scv_dup(&s->server_path, settings->server_path)
            || !scv_dup(&s->video_encoder, settings->video_encoder)
            || !scv_dup(&s->video_codec_options, settings->video_codec_options)
            || !scv_dup(&s->max_fps, settings->max_fps)
            || !scv_dup(&s->crop, settings->crop)
            || !scv_dup(&s->angle, settings->angle)
            || !scv_dup(&s->new_display, settings->new_display)) {
        err = SCV_ERR_OOM;
        goto fail_free;
    }

    if (!sc_mutex_init(&s->mutex)) {
        err = SCV_ERR_INIT;
        goto fail_free;
    }

    sc_set_log_level((enum sc_log_level) settings->log_level);

    // Winsock refcounts, so calling this per session is safe.
    if (!net_init()) {
        err = SCV_ERR_INIT;
        goto fail_mutex;
    }

    // Must happen before sc_server_start(): the server thread calls
    // sc_adb_init() itself.
    if (s->adb_path) {
        sc_adb_set_executable(s->adb_path);
    }
    if (s->server_path) {
        sc_server_set_server_path(s->server_path);
    }

    s->sink.frame_sink.ops = &scv_sink_ops;
    s->sink.session = s;

    struct sc_server_params params = {
        // A distinct scid per session is what lets several sessions coexist:
        // it namespaces the device-side socket and the local port.
        .scid = scv_generate_scid(),
        .req_serial = s->serial,
        .log_level = (enum sc_log_level) settings->log_level,

        .video_codec = scv_parse_codec(settings->video_codec),
        .video_source = SC_VIDEO_SOURCE_DISPLAY,
        .video_encoder = s->video_encoder,
        .video_codec_options = s->video_codec_options,
        .video_bit_rate = settings->video_bit_rate ? settings->video_bit_rate
                                                   : 8000000,
        .max_fps = s->max_fps,
        .max_size = settings->max_size,
        .crop = s->crop,
        .angle = s->angle,

        .display_id = settings->display_id,
        .new_display = s->new_display,
        .vd_destroy_content = settings->vd_destroy_content != 0,
        .vd_system_decorations = settings->vd_system_decorations != 0,

        .port_range = {
            .first = settings->port_first ? settings->port_first
                                          : DEFAULT_LOCAL_PORT_RANGE_FIRST,
            .last = settings->port_last ? settings->port_last
                                        : DEFAULT_LOCAL_PORT_RANGE_LAST,
        },

        .video = true,
        // Audio is deliberately off: it is device-wide on Android, so it does
        // not belong to a per-window session. It ships as its own DLL.
        .audio = false,
        /*
         * Deliberately not exposed. The device server sets it through
         * Settings.System, which drives touch feedback on the phone's own
         * screen; nothing is drawn on a virtual display, so the option was all
         * cost and no effect for the way AdbDesktop uses scrcpy.
         */
        .show_touches = false,
        .control = settings->control != 0,
        // Upstream refuses flex without new_display (cli.c), so mirror that
        // rather than letting the device reject the connection.
        .flex_display = settings->flex_display != 0
                        && settings->control != 0
                        && s->new_display != NULL,
        /*
         * Must still be OPUS even with audio disabled. server.c emits
         * "audio_codec=<name>" whenever this differs from SC_CODEC_OPUS and
         * does NOT gate that on params->audio -- so leaving it zero-initialised
         * sends "audio_codec=h264" (H264 is 0 in the shared sc_codec enum) and
         * the device server rejects the whole connection.
         */
        .audio_codec = SC_CODEC_OPUS,
        .audio_source = SC_AUDIO_SOURCE_OUTPUT,
        // "@0": pinned to the natural orientation. server.c only emits this at
        // all when the lock differs from UNLOCKED or the orientation from 0.
        .capture_orientation = SC_ORIENTATION_0,
        .capture_orientation_lock = settings->lock_orientation
                                    ? SC_ORIENTATION_LOCKED_VALUE
                                    : SC_ORIENTATION_UNLOCKED,
        .display_ime_policy = SC_DISPLAY_IME_POLICY_UNDEFINED,

        /*
         * These three have non-zero defaults upstream, and server.c serialises
         * each one by comparing against that default rather than checking a
         * feature flag -- so a zero-initialised struct silently sends bad
         * values. Notably screen_off_timeout=0 would blank the phone's screen
         * immediately; -1 means "leave it alone".
         */
        .min_size_alignment = 1,
        .camera_facing = SC_CAMERA_FACING_ANY,
        .screen_off_timeout = -1,

        .cleanup = true,
        .power_on = true,
        .clipboard_autosync = false,
        .downsize_on_error = true,
    };

    static const struct sc_server_callbacks server_cbs = {
        .on_connection_failed = scv_on_connection_failed,
        .on_connected = scv_on_connected,
        .on_disconnected = scv_on_disconnected,
    };

    LOGI("Session: new_display=%s control=%d flex=%d orientation_lock=%d "
         "vd_decorations=%d bit_rate=%u max_fps=%s codec=%s buffer=%ums",
         s->new_display ? s->new_display : "(mirror)",
         (int) params.control, (int) params.flex_display,
         (int) params.capture_orientation_lock,
         (int) params.vd_system_decorations,
         params.video_bit_rate, s->max_fps ? s->max_fps : "(default)",
         settings->video_codec ? settings->video_codec : "h264",
         s->video_buffer);

    if (!sc_server_init(&s->server, &params, &server_cbs, s)) {
        err = SCV_ERR_INIT;
        goto fail_net;
    }

    if (!sc_server_start(&s->server)) {
        err = SCV_ERR_THREAD;
        goto fail_server;
    }

    s->server_started = true;

    if (error) {
        *error = SCV_OK;
    }
    return s;

fail_server:
    sc_server_destroy(&s->server);
fail_net:
    net_cleanup();
fail_mutex:
    sc_mutex_destroy(&s->mutex);
fail_free:
    scv_free_strings(s);
    free(s);
fail:
    if (error) {
        *error = err;
    }
    return NULL;
}

void
scv_close(scv_session *s) {
    if (!s) {
        return;
    }

    // Stop the controller first: it must not try to write to a control socket
    // the server is about to close.
    if (s->controller_started) {
        sc_controller_stop(&s->controller);
    }

    if (s->server_started) {
        sc_server_stop(&s->server);
    }

    if (s->controller_started) {
        sc_controller_join(&s->controller);
        sc_controller_destroy(&s->controller);
    }

    // Joining the demuxer also tears the decoder and this sink down, via the
    // packet/frame source chain.
    if (s->demuxer_started) {
        sc_demuxer_join(&s->demuxer);
    }

    if (s->server_started) {
        sc_server_join(&s->server);
        sc_server_destroy(&s->server);
    }

    sws_freeContext(s->sws);

    av_free(s->scratch);
    av_free(s->ready);
    av_free(s->front);
    av_free(s->retired);

    sc_mutex_destroy(&s->mutex);
    scv_free_strings(s);

    net_cleanup();

    free(s);
}

int32_t
scv_resize_display(scv_session *s, uint16_t width, uint16_t height) {
    if (!s) {
        return SCV_ERR_BAD_ARGUMENT;
    }
    if (!s->controller_started) {
        return SCV_ERR_BAD_ARGUMENT;
    }
    if (!width || !height) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    // Not queued upstream: a new request replaces any pending one, so calling
    // this on every mouse-move of a resize is fine.
    sc_controller_resize_display(&s->controller, width, height);
    return SCV_OK;
}

int32_t
scv_start_app(scv_session *s, const char *name) {
    if (!s || !name || !*name) {
        return SCV_ERR_BAD_ARGUMENT;
    }
    if (!s->controller_started) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    char *copy = strdup(name);
    if (!copy) {
        LOG_OOM();
        return SCV_ERR_OOM;
    }

    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_START_APP,
        .start_app.name = copy, // ownership passes to the controller
    };

    if (!sc_controller_push_msg(&s->controller, &msg)) {
        free(copy);
        LOGE("Could not request start of app '%s'", name);
        return SCV_ERR_INIT;
    }

    LOGI("Requested start of app '%s'", name);
    return SCV_OK;
}

/*
 * Input injection.
 *
 * The message-building machinery (control_msg.c / controller.c) is upstream and
 * unmodified. What upstream has that this build does not is input_manager.c and
 * the SDL mouse/keyboard handlers -- those read SDL_Events out of scrcpy's own
 * window, which does not exist here. The host feeds WPF input in through these
 * entry points instead.
 */
static int32_t
scv_push(scv_session *s, const struct sc_control_msg *msg) {
    if (!s || !s->controller_started) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    return sc_controller_push_msg(&s->controller, msg) ? SCV_OK : SCV_ERR_INIT;
}

int32_t
scv_inject_touch(scv_session *s, int32_t action, int32_t x, int32_t y,
                 uint16_t width, uint16_t height, float pressure,
                 uint32_t buttons) {
    if (!width || !height) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT,
        .inject_touch_event = {
            .action = (enum android_motionevent_action) action,
            .action_button = 0,
            .buttons = (enum android_motionevent_buttons) buttons,
            // A mouse is a single pointer. Using the dedicated mouse id (rather
            // than a finger) is what lets the device treat it as a real pointer
            // device, including hover.
            .pointer_id = SC_POINTER_ID_MOUSE,
            .position = {
                // The device scales from this size, so the host can pass the
                // size of whatever image the user actually clicked on.
                .screen_size = { .width = width, .height = height },
                .point = { .x = x, .y = y },
            },
            .pressure = pressure,
        },
    };

    return scv_push(s, &msg);
}

int32_t
scv_inject_scroll(scv_session *s, int32_t x, int32_t y,
                  uint16_t width, uint16_t height,
                  float hscroll, float vscroll) {
    if (!width || !height) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT,
        .inject_scroll_event = {
            .position = {
                .screen_size = { .width = width, .height = height },
                .point = { .x = x, .y = y },
            },
            .hscroll = hscroll,
            .vscroll = vscroll,
            .buttons = 0,
        },
    };

    return scv_push(s, &msg);
}

int32_t
scv_inject_keycode(scv_session *s, int32_t action, int32_t keycode,
                   uint32_t repeat, uint32_t metastate) {
    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_INJECT_KEYCODE,
        .inject_keycode = {
            .action = (enum android_keyevent_action) action,
            .keycode = (enum android_keycode) keycode,
            .repeat = repeat,
            .metastate = (enum android_metastate) metastate,
        },
    };

    return scv_push(s, &msg);
}

int32_t
scv_inject_text(scv_session *s, const char *text) {
    if (!text || !*text) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    char *copy = strdup(text);
    if (!copy) {
        LOG_OOM();
        return SCV_ERR_OOM;
    }

    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_INJECT_TEXT,
        .inject_text.text = copy, // ownership passes to the controller
    };

    int32_t r = scv_push(s, &msg);
    if (r != SCV_OK) {
        free(copy);
    }
    return r;
}

int32_t
scv_back(scv_session *s, int32_t action) {
    struct sc_control_msg msg = {
        .type = SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON,
        .back_or_screen_on.action = (enum android_keyevent_action) action,
    };

    return scv_push(s, &msg);
}

int32_t
scv_get_size(scv_session *s, uint32_t *width, uint32_t *height) {
    if (!s) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    sc_mutex_lock(&s->mutex);
    uint32_t w = s->width;
    uint32_t h = s->height;
    sc_mutex_unlock(&s->mutex);

    if (width) {
        *width = w;
    }
    if (height) {
        *height = h;
    }

    return (w && h) ? 1 : 0;
}

const char *
scv_get_device_name(scv_session *s) {
    return s ? s->device_name : "";
}

int32_t
scv_acquire_frame(scv_session *s, const uint8_t **data, uint32_t *stride,
                  uint32_t *width, uint32_t *height) {
    if (!s || !data) {
        return SCV_ERR_BAD_ARGUMENT;
    }

    sc_mutex_lock(&s->mutex);

    if (!s->has_new || s->front_held) {
        sc_mutex_unlock(&s->mutex);
        return 0;
    }

    uint8_t *tmp = s->front;
    s->front = s->ready;
    s->ready = tmp;
    s->has_new = false;
    s->front_held = true;
    s->held = s->front;   // what a resize must not free out from under the host

    *data = s->front;
    if (stride) {
        *stride = s->stride;
    }
    if (width) {
        *width = s->width;
    }
    if (height) {
        *height = s->height;
    }

    sc_mutex_unlock(&s->mutex);
    return 1;
}

void
scv_release_frame(scv_session *s) {
    if (!s) {
        return;
    }

    sc_mutex_lock(&s->mutex);
    s->front_held = false;
    s->held = NULL;

    // Safe now: the host has finished reading the buffer a resize replaced. Null
    // unless a resize happened during the hold, in which case this is the only
    // pointer left to it.
    av_free(s->retired);
    s->retired = NULL;

    sc_mutex_unlock(&s->mutex);
}
