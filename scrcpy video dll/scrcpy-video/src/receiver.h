#ifndef SC_RECEIVER_H
#define SC_RECEIVER_H

#include "common.h"

#include <stdbool.h>

#include "device_msg.h"
#include "util/net.h"
#include "util/thread.h"

/*
 * PORT: heavily reduced.
 *
 * Upstream's receiver exists to do three things with device->client messages:
 * copy the device clipboard into the PC clipboard, resolve clipboard acks, and
 * dispatch UHID output reports. This build wants none of them -- it only sends
 * control messages (RESIZE_DISPLAY, START_APP) and never needs a reply.
 *
 * It cannot simply be deleted, because sc_controller embeds one and the inbound
 * half of the control socket still has to be drained: if nothing reads it, the
 * device can eventually block writing to it. So the read loop is kept and the
 * parsed messages are discarded.
 *
 * Dropping the handling also drops what it dragged in: SDL_clipboard, events.c
 * (the SDL event loop), uhid/, hid/keyboard and acksync.
 */

// receive events from the device
// managed by the controller
struct sc_receiver {
    sc_socket control_socket;
    sc_thread thread;
    sc_mutex mutex;

    /*
     * PORT: per receiver, not a static inside the read loop.
     *
     * Upstream is one session per process, so one shared buffer was fine there.
     * This build runs a session per window, each with its own receiver thread --
     * they would all recv() into the same array and then parse it against their
     * own offsets, reading bytes another session had just written over.
     */
    uint8_t buf[DEVICE_MSG_MAX_SIZE];

    /*
     * Retained only so sc_controller_configure() still compiles unchanged. Both
     * are always NULL in this build and nothing dereferences them.
     */
    void *acksync;
    void *uhid_devices;

    const struct sc_receiver_callbacks *cbs;
    void *cbs_userdata;
};

struct sc_receiver_callbacks {
    void (*on_ended)(struct sc_receiver *receiver, bool error, void *userdata);
};

bool
sc_receiver_init(struct sc_receiver *receiver, sc_socket control_socket,
                 const struct sc_receiver_callbacks *cbs, void *cbs_userdata);

void
sc_receiver_destroy(struct sc_receiver *receiver);

bool
sc_receiver_start(struct sc_receiver *receiver);

// no sc_receiver_stop(), it will automatically stop on control_socket shutdown

void
sc_receiver_join(struct sc_receiver *receiver);

#endif
