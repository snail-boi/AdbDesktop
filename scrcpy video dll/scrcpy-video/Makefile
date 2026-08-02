# Build scrcpy_video.dll (video-only scrcpy) with w64devkit (mingw-w64 gcc).
#
# Usage, from this directory:
#   PATH="../w64devkit/bin:$PATH" make -j
#
# Runtime dependencies of the produced DLL:
#   avcodec-62.dll, avutil-60.dll, swscale-9.dll
#
# Note there is NO SDL3 dependency: upstream only used SDL for threads and
# logging, and both are reimplemented on Win32 in util/thread.c and util/log.c.
# That removes a 22.9 MB runtime DLL.

CC ?= gcc

FFMPEG := deps/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1

TARGET := build/scrcpy_video.dll

# Release by default. `make DEBUG=1` keeps scrcpy's assertions live and adds
# symbols -- worth doing after any change to session setup or teardown, because
# scrcpy asserts on a lot of its internal contracts and NDEBUG turns those
# checks into silent undefined behaviour.
ifeq ($(DEBUG),1)
  OPT := -O0 -g
else
  OPT := -O2 -DNDEBUG
endif

# _WIN32_WINNT >= 0x0600 is required for SRWLOCK / CONDITION_VARIABLE.
CFLAGS := -std=c11 $(OPT) -Wall -Wextra -Wno-unused-parameter \
	-DSCV_BUILDING \
	-D_GNU_SOURCE -D_POSIX_C_SOURCE=200809L -D_XOPEN_SOURCE=700 \
	-D_WIN32_WINNT=0x0601 -DWINVER=0x0601 \
	-Isrc -I$(FFMPEG)/include

LDFLAGS := -shared -static-libgcc \
	-L$(FFMPEG)/lib \
	-lavcodec -lavutil -lswscale -lws2_32

SRC := \
	src/scrcpy_video.c \
	src/clock.c \
	src/compat.c \
	src/decoder.c \
	src/demuxer.c \
	src/frame_buffer.c \
	src/options.c \
	src/packet_merger.c \
	src/server.c \
	src/video_regulator.c \
	src/controller.c \
	src/control_msg.c \
	src/device_msg.c \
	src/receiver.c \
	src/adb/adb.c \
	src/adb/adb_device.c \
	src/adb/adb_parser.c \
	src/adb/adb_tunnel.c \
	src/trait/frame_source.c \
	src/trait/packet_source.c \
	src/util/command.c \
	src/util/env.c \
	src/util/file.c \
	src/util/intr.c \
	src/util/log.c \
	src/util/memory.c \
	src/util/net.c \
	src/util/net_intr.c \
	src/util/process.c \
	src/util/process_intr.c \
	src/util/rand.c \
	src/util/str.c \
	src/util/strbuf.c \
	src/util/thread.c \
	src/util/tick.c \
	src/sys/win/file.c \
	src/sys/win/process.c

OBJ := $(SRC:src/%.c=build/obj/%.o)

.PHONY: all clean test

all: $(TARGET)

$(TARGET): $(OBJ)
	@mkdir -p $(dir $@)
	$(CC) $(OBJ) -o $@ $(LDFLAGS)

build/obj/%.o: src/%.c
	@mkdir -p $(dir $@)
	$(CC) $(CFLAGS) -c $< -o $@

# Native smoke test: connects to a device and dumps a frame to BMP.
test: $(TARGET)
	$(CC) -std=c11 -O2 -Isrc test/test_host.c -o build/test_host.exe $(TARGET)

clean:
	rm -rf build
