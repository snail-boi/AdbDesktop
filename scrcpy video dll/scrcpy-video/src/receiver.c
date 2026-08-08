// PORT: drain-only receiver. See the comment in receiver.h for why it still
// exists at all. The framing and read loop follow upstream; the difference is
// that a parsed message is freed instead of acted on.

#include "receiver.h"

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "device_msg.h"
#include "util/log.h"
#include "util/net.h"
#include "util/thread.h"

bool
sc_receiver_init(struct sc_receiver *receiver, sc_socket control_socket,
                 const struct sc_receiver_callbacks *cbs, void *cbs_userdata) {
    bool ok = sc_mutex_init(&receiver->mutex);
    if (!ok) {
        return false;
    }

    receiver->control_socket = control_socket;
    receiver->acksync = NULL;
    receiver->uhid_devices = NULL;
    receiver->cbs = cbs;
    receiver->cbs_userdata = cbs_userdata;

    assert(cbs && cbs->on_ended);

    return true;
}

void
sc_receiver_destroy(struct sc_receiver *receiver) {
    sc_mutex_destroy(&receiver->mutex);
}

static void
process_msgs(const uint8_t *buf, size_t len) {
    size_t head = 0;

    for (;;) {
        struct sc_device_msg msg;
        ssize_t r = sc_device_msg_deserialize(&buf[head], len - head, &msg);
        if (r == -1 || r == 0) {
            // error, or not enough data for a complete message yet
            return;
        }

        head += r;

        // Nothing in this build consumes clipboard, acks or UHID output.
        sc_device_msg_destroy(&msg);

        if (head == len) {
            return;
        }
    }
}

static int
run_receiver(void *data) {
    struct sc_receiver *receiver = data;

    uint8_t *buf = receiver->buf;   // this receiver's own; see receiver.h
    size_t head = 0;
    bool error = false;

    for (;;) {
        assert(head < DEVICE_MSG_MAX_SIZE);
        ssize_t r = net_recv(receiver->control_socket, buf + head,
                             DEVICE_MSG_MAX_SIZE - head);
        if (r <= 0) {
            LOGD("Receiver stopped");
            break;
        }

        head += r;

        // Upstream tracks consumed bytes to handle partial messages; since
        // nothing here needs the content, the buffer is simply reset once a
        // batch has been parsed.
        process_msgs(buf, head);
        head = 0;
    }

    receiver->cbs->on_ended(receiver, error, receiver->cbs_userdata);

    return 0;
}

bool
sc_receiver_start(struct sc_receiver *receiver) {
    LOGD("Starting receiver thread");

    bool ok = sc_thread_create(&receiver->thread, run_receiver, "scrcpy-receiver",
                               receiver);
    if (!ok) {
        LOGE("Could not start receiver thread");
        return false;
    }

    return true;
}

void
sc_receiver_join(struct sc_receiver *receiver) {
    sc_thread_join(&receiver->thread, NULL);
}
