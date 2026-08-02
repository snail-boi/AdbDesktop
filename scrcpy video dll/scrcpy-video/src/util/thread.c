// PORT: Win32 replacement for upstream's SDL-backed threading.
//
// Upstream scrcpy uses SDL for threads, mutexes and condition variables. This
// build renders in the host process, so SDL had no other job here -- and
// pulling in a 23 MB SDL3.dll purely for CreateThread and a log formatter is a
// bad trade.
//
// Mapping:
//   SDL_Thread     -> _beginthreadex (CRT-aware; plain CreateThread would skip
//                     per-thread CRT init/teardown)
//   SDL_Mutex      -> SRWLOCK. SDL's mutex is recursive, but scrcpy asserts
//                     non-recursive use anyway (see sc_mutex_lock), so the
//                     cheaper, stricter primitive is the right fit.
//   SDL_Condition  -> CONDITION_VARIABLE (pairs with SRWLOCK)
//
// Every sc_* signature matches upstream exactly, so no caller changed.

#include "thread.h"

#include <assert.h>
#include <process.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

#include "util/log.h"

sc_thread_id SC_MAIN_THREAD_ID;

struct sc_thread_impl {
    HANDLE handle;
    sc_thread_fn *fn;
    void *userdata;
    int result;
};

struct sc_mutex_impl {
    SRWLOCK lock;
};

struct sc_cond_impl {
    CONDITION_VARIABLE cond;
};

// _beginthreadex wants unsigned __stdcall(void *) but sc_thread_fn is
// int(void *). The trampoline stashes the return value so sc_thread_join can
// hand it back, rather than going through the thread exit code.
static unsigned __stdcall
sc_thread_entry(void *arg) {
    struct sc_thread_impl *impl = arg;
    impl->result = impl->fn(impl->userdata);
    return 0;
}

bool
sc_thread_create(sc_thread *thread, sc_thread_fn fn, const char *name,
                 void *userdata) {
    // Kept from upstream: some platforms cap thread names at 16 bytes.
    assert(strlen(name) <= 15);
    (void) name; // naming a Win32 thread needs SetThreadDescription; no value here

    struct sc_thread_impl *impl = malloc(sizeof(*impl));
    if (!impl) {
        LOG_OOM();
        return false;
    }

    impl->fn = fn;
    impl->userdata = userdata;
    impl->result = 0;

    uintptr_t h = _beginthreadex(NULL, 0, sc_thread_entry, impl, 0, NULL);
    if (!h) {
        LOGE("Could not create thread '%s'", name);
        free(impl);
        return false;
    }

    impl->handle = (HANDLE) h;
    thread->thread = impl;
    return true;
}

static int
to_win32_thread_priority(enum sc_thread_priority priority) {
    switch (priority) {
        case SC_THREAD_PRIORITY_TIME_CRITICAL:
            return THREAD_PRIORITY_TIME_CRITICAL;
        case SC_THREAD_PRIORITY_HIGH:
            return THREAD_PRIORITY_ABOVE_NORMAL;
        case SC_THREAD_PRIORITY_NORMAL:
            return THREAD_PRIORITY_NORMAL;
        case SC_THREAD_PRIORITY_LOW:
            return THREAD_PRIORITY_BELOW_NORMAL;
        default:
            assert(!"Unknown thread priority");
            return THREAD_PRIORITY_NORMAL;
    }
}

bool
sc_thread_set_priority(enum sc_thread_priority priority) {
    int win_priority = to_win32_thread_priority(priority);
    if (!SetThreadPriority(GetCurrentThread(), win_priority)) {
        LOGD("Could not set thread priority");
        return false;
    }

    return true;
}

void
sc_thread_join(sc_thread *thread, int *status) {
    struct sc_thread_impl *impl = thread->thread;
    if (!impl) {
        if (status) {
            *status = 0;
        }
        return;
    }

    WaitForSingleObject(impl->handle, INFINITE);

    if (status) {
        *status = impl->result;
    }

    CloseHandle(impl->handle);
    free(impl);
    thread->thread = NULL;
}

bool
sc_mutex_init(sc_mutex *mutex) {
    struct sc_mutex_impl *impl = malloc(sizeof(*impl));
    if (!impl) {
        LOG_OOM();
        return false;
    }

    InitializeSRWLock(&impl->lock);

    mutex->mutex = impl;
#ifndef NDEBUG
    atomic_init(&mutex->locker, 0);
#endif
    return true;
}

void
sc_mutex_destroy(sc_mutex *mutex) {
    // An SRWLOCK needs no explicit destruction.
    free(mutex->mutex);
    mutex->mutex = NULL;
}

void
sc_mutex_lock(sc_mutex *mutex) {
    // Upstream notes SDL mutexes are recursive but that recursion is unwanted.
    // SRWLOCK is non-recursive, so this assert now guards a real self-deadlock
    // rather than just a style violation.
    assert(!sc_mutex_held(mutex));
    AcquireSRWLockExclusive(&mutex->mutex->lock);
#ifndef NDEBUG
    atomic_store_explicit(&mutex->locker, sc_thread_get_id(),
                          memory_order_relaxed);
#endif
}

void
sc_mutex_unlock(sc_mutex *mutex) {
    assert(sc_mutex_held(mutex));
#ifndef NDEBUG
    atomic_store_explicit(&mutex->locker, 0, memory_order_relaxed);
#endif
    ReleaseSRWLockExclusive(&mutex->mutex->lock);
}

sc_thread_id
sc_thread_get_id(void) {
    return (sc_thread_id) GetCurrentThreadId();
}

bool
sc_thread_is_main(void) {
    return sc_thread_get_id() == SC_MAIN_THREAD_ID;
}

#ifndef NDEBUG
bool
sc_mutex_held(struct sc_mutex *mutex) {
    sc_thread_id locker_id =
        atomic_load_explicit(&mutex->locker, memory_order_relaxed);
    return locker_id == sc_thread_get_id();
}
#endif

bool
sc_cond_init(sc_cond *cond) {
    struct sc_cond_impl *impl = malloc(sizeof(*impl));
    if (!impl) {
        LOG_OOM();
        return false;
    }

    InitializeConditionVariable(&impl->cond);

    cond->cond = impl;
    return true;
}

void
sc_cond_destroy(sc_cond *cond) {
    // A CONDITION_VARIABLE needs no explicit destruction.
    free(cond->cond);
    cond->cond = NULL;
}

void
sc_cond_wait(sc_cond *cond, sc_mutex *mutex) {
    SleepConditionVariableSRW(&cond->cond->cond, &mutex->mutex->lock, INFINITE,
                              0);
#ifndef NDEBUG
    atomic_store_explicit(&mutex->locker, sc_thread_get_id(),
                          memory_order_relaxed);
#endif
}

bool
sc_cond_timedwait(sc_cond *cond, sc_mutex *mutex, sc_tick deadline) {
    sc_tick now = sc_tick_now();
    if (deadline <= now) {
        return false; // timeout
    }

    // Round up to the next millisecond to guarantee that the deadline is
    // reached when returning due to timeout (upstream behaviour).
    DWORD ms = (DWORD) SC_TICK_TO_MS(deadline - now + SC_TICK_FROM_MS(1) - 1);
    BOOL signaled =
        SleepConditionVariableSRW(&cond->cond->cond, &mutex->mutex->lock, ms, 0);
#ifndef NDEBUG
    atomic_store_explicit(&mutex->locker, sc_thread_get_id(),
                          memory_order_relaxed);
#endif

    // The deadline is reached on timeout
    assert(signaled || sc_tick_now() >= deadline);
    return signaled;
}

void
sc_cond_signal(sc_cond *cond) {
    WakeConditionVariable(&cond->cond->cond);
}

void
sc_cond_broadcast(sc_cond *cond) {
    WakeAllConditionVariable(&cond->cond->cond);
}
