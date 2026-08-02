/*
 * Native smoke test for scrcpy_video.dll.
 *
 * Proves the whole pipeline (adb push -> tunnel -> connect -> demux -> decode
 * -> BGRA conversion) without involving C# at all, so that when something
 * misbehaves later it is obvious which side is at fault.
 *
 * Build:  make test
 * Run:    build\test_host.exe <adb.exe> <scrcpy-server> [serial]
 *
 * On success it writes build/frame.bmp and prints the frame rate it observed.
 */

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

#include "scrcpy_video.h"

static const char *const LEVEL_NAMES[] = {
    "VERBOSE", "DEBUG", "INFO", "WARN", "ERROR"
};

static void
on_log(int32_t level, const char *message, void *userdata) {
    (void) userdata;
    const char *name = (level >= 0 && level <= 4) ? LEVEL_NAMES[level] : "?";
    printf("[%-7s] %s\n", name, message);
    fflush(stdout);
}

static volatile long g_connected;
static volatile long g_failed;

// Launched from the CONNECTED event, exactly as AdbDesktop does. It cannot wait for
// the first frame: with vd_system_decorations=0 an empty virtual display renders
// nothing at all, so no frame ever arrives until something is running on it.
static const char *g_app;

static void
on_event(scv_session *session, int32_t event, void *userdata) {
    (void) session;
    (void) userdata;

    static const char *const NAMES[] = {
        "CONNECTED", "CONNECTION_FAILED", "STREAM_STARTED", "STREAM_STOPPED",
        "DISCONNECTED", "SIZE_CHANGED", "ERROR"
    };

    const char *name = (event >= 0 && event <= 6) ? NAMES[event] : "?";
    printf(">>> event: %s\n", name);
    fflush(stdout);

    if (event == SCV_EVENT_CONNECTED) {
        InterlockedExchange(&g_connected, 1);
        if (g_app) {
            printf(">>> scv_start_app(\"%s\") -> %d\n", g_app,
                   (int) scv_start_app(session, g_app));
        }
    } else if (event == SCV_EVENT_CONNECTION_FAILED
            || event == SCV_EVENT_ERROR) {
        InterlockedExchange(&g_failed, 1);
    }
}

/* Write BGRA32 top-down as a 32-bit BMP (negative height = top-down). */
static bool
write_bmp(const char *path, const uint8_t *data, uint32_t width,
          uint32_t height, uint32_t stride) {
    FILE *f = fopen(path, "wb");
    if (!f) {
        return false;
    }

    uint32_t image_size = width * height * 4;
    uint32_t file_size = 14 + 40 + image_size;

    uint8_t header[14 + 40] = {0};
    header[0] = 'B'; header[1] = 'M';
    memcpy(&header[2], &file_size, 4);
    uint32_t offset = 54;
    memcpy(&header[10], &offset, 4);

    uint32_t dib = 40;
    memcpy(&header[14], &dib, 4);
    int32_t w = (int32_t) width;
    int32_t h = -(int32_t) height; // top-down
    memcpy(&header[18], &w, 4);
    memcpy(&header[22], &h, 4);
    uint16_t planes = 1, bpp = 32;
    memcpy(&header[26], &planes, 2);
    memcpy(&header[28], &bpp, 2);
    memcpy(&header[34], &image_size, 4);

    fwrite(header, 1, sizeof(header), f);
    for (uint32_t y = 0; y < height; y++) {
        fwrite(data + (size_t) y * stride, 1, (size_t) width * 4, f);
    }

    fclose(f);
    return true;
}

int
main(int argc, char *argv[]) {
    if (argc < 3) {
        fprintf(stderr,
                "usage: %s <adb.exe> <scrcpy-server> [serial]\n", argv[0]);
        return 2;
    }

    scv_set_log_callback(on_log, NULL);

    struct scv_settings settings;
    scv_settings_init(&settings);
    settings.adb_path = argv[1];
    settings.server_path = argv[2];
    settings.serial = (argc > 3 && argv[3][0]) ? argv[3] : NULL;
    // 0 = native resolution. Defaults to 800 just to keep the test cheap.
    settings.max_size = argc > 4 ? (uint16_t) atoi(argv[4]) : 800;
    settings.max_fps = "30";
    settings.event_cb = on_event;

    // argv[5]: "<w>x<h>[/<dpi>]" -> a virtual display instead of mirroring.
    const char *new_display = (argc > 5 && argv[5][0]) ? argv[5] : NULL;
    const char *app = (argc > 6 && argv[6][0]) ? argv[6] : NULL;
    g_app = app;

    if (new_display) {
        settings.new_display = new_display;
        settings.control = 1;       // required for flex + start_app
        settings.flex_display = 1;
        settings.vd_destroy_content = 1;
        settings.vd_system_decorations = 0;
        settings.lock_orientation = 1;   // match what AdbDesktop asks for
        settings.max_size = 0;      // the display size already is the size
        printf("virtual display: %s%s%s\n", new_display,
               app ? ", app: " : "", app ? app : "");
    }

    printf("Opening session...\n");

    int32_t error = 0;
    scv_session *session = scv_open(&settings, &error);
    if (!session) {
        fprintf(stderr, "scv_open failed: %d\n", (int) error);
        return 1;
    }

    // Wait for the first frame (server push + connect can take a few seconds).
    const uint8_t *data;
    uint32_t stride, width, height;
    bool got = false;

    bool resized = false;
    int frames = 0;
    int stress_count = 0;
    uint32_t last_w = 0, last_h = 0;

    // argv[7] = "stress" hammers scv_resize_display while pulling frames.
    bool stress = argc > 7 && !strcmp(argv[7], "stress");
    DWORD start = GetTickCount();
    DWORD first_frame_at = 0;

    while (GetTickCount() - start < 20000) {
        if (g_failed) {
            fprintf(stderr, "connection failed\n");
            break;
        }

        int32_t r = scv_acquire_frame(session, &data, &stride, &width, &height);
        if (r == 1) {
            if (got && (width != last_w || height != last_h)) {
                printf("*** frame size changed: %ux%u -> %ux%u\n",
                       last_w, last_h, width, height);
            }
            last_w = width;
            last_h = height;

            if (!got) {
                got = true;
                first_frame_at = GetTickCount();
                printf("first frame: %ux%u stride=%u after %lu ms\n",
                       width, height, stride,
                       (unsigned long) (first_frame_at - start));
                // Relative to the working directory, which is where the DLL
                // and its FFmpeg dependencies live.
                if (write_bmp("frame.bmp", data, width, height, stride)) {
                    printf("wrote frame.bmp\n");
                } else {
                    fprintf(stderr, "could not write frame.bmp\n");
                }
            }
            frames++;
            scv_release_frame(session);

            DWORD since = GetTickCount() - first_frame_at;

            // Then exercise the flex display: ask Android to genuinely re-lay-out
            // at a new size, and confirm the stream follows.
            if (new_display && !resized && since > 3000) {
                resized = true;
                printf(">>> scv_resize_display(640, 480) -> %d\n",
                       (int) scv_resize_display(session, 640, 480));
            }

            /*
             * Stress the resize path: this is what crashed the host with
             * STATUS_HEAP_CORRUPTION. Hammering resize while frames are being
             * acquired is exactly the race between the decoder thread
             * reallocating buffers and this thread reading the one it holds.
             */
            if (new_display && stress && since > 4000 && stress_count < 60) {
                static const struct { uint16_t w, h; } sizes[] = {
                    {320, 240}, {900, 600}, {200, 400}, {1280, 720},
                    {160, 120}, {640, 480}, {480, 900}, {1024, 600},
                };
                const int n = (int) (sizeof(sizes) / sizeof(sizes[0]));
                scv_resize_display(session, sizes[stress_count % n].w,
                                   sizes[stress_count % n].h);
                stress_count++;
                if (stress_count % 10 == 0) {
                    printf("    stress: %d resizes, current frame %ux%u\n",
                           stress_count, width, height);
                }
            }

            // Once streaming is proven, measure and stop.
            if (since > (new_display ? (stress ? 25000u : 8000u) : 5000u)) {
                break;
            }
        } else {
            Sleep(1);
        }
    }

    if (got) {
        DWORD elapsed = GetTickCount() - first_frame_at;
        printf("received %d frames in %lu ms (%.1f fps)\n", frames,
               (unsigned long) elapsed,
               elapsed ? frames * 1000.0 / elapsed : 0.0);
        printf("device: %s\n", scv_get_device_name(session));
    } else {
        fprintf(stderr, "no frame received\n");
    }

    printf("Closing...\n");
    scv_close(session);
    printf("Done.\n");

    return got ? 0 : 1;
}
