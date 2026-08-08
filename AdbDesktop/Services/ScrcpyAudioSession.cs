using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AdbDesktop
{
    /// <summary>
    /// ONE device's audio link: pulls PCM out of a privately-loaded scrcpy_audio module
    /// and plays it through its own WASAPI stream.
    ///
    /// Several of these run at once, one per device. That works because each holds its
    /// own <see cref="ScrcpyAudioModule"/> -- a separate copy of the DLL, and so a
    /// separate copy of the port's single static session.
    ///
    /// Volume is per stream, not per process, so the sliders are genuinely independent.
    /// </summary>
    internal sealed class ScrcpyAudioSession : IDisposable
    {
        // Held in fields for the session's life: native stores the pointers, so letting
        // the delegates be collected would crash on the next callback.
        private readonly ScrcpyAudioNative.EventCallback _eventCb;
        private readonly ScrcpyAudioNative.LogCallback _logCb;

        private ScrcpyAudioModule? _module;
        private PullProvider? _provider;
        private WasapiOut? _output;
        private bool _started;
        private float _volume = 1f;

        public event Action<int>? SessionEvent;

        public bool IsRunning => _started;

        public ScrcpyAudioSession()
        {
            _eventCb = OnEvent;
            _logCb = OnLog;
        }

        /// <summary>
        /// 0..1, applied by scaling this session's own samples.
        ///
        /// Deliberately not ISimpleAudioVolume (which is per PROCESS, so it would move
        /// every device's stream at once) and not WasapiOut.Volume (which routes to the
        /// shared session volume for the same reason). Scaling the float PCM as it is
        /// pulled is the only level that is unambiguously this stream's alone.
        /// </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);

                if (_provider != null)
                    _provider.Volume = _volume;
            }
        }

        public bool Start(string serial)
        {
            // Still probed through the shared resolver: it validates the ABI once and
            // reports a missing or mismatched DLL clearly.
            ScrcpyAudioNative.Initialize();

            if (!ScrcpyAudioNative.IsAvailable)
            {
                Debugger.show("[AUDIO] Cannot start: native library unavailable.");
                return false;
            }

            if (_started)
                return true;

            _module = ScrcpyAudioModule.Create();
            if (_module == null)
                return false;

            var settings = new ScrcpyAudioNative.Settings();
            _module.SettingsInit(ref settings);

            settings.Serial = string.IsNullOrWhiteSpace(serial) ? null : serial;
            settings.AdbPath = AppPaths.AdbPath;
            settings.ServerPath = AppPaths.GetResourcePath("scrcpy-server-v4.1");
            settings.AudioCodec = "opus";
            // "playback" captures what the device is actually outputting.
            settings.AudioSource = "playback";
            settings.AudioBufferMs = 50;
            settings.OutputBufferMs = 60;
            settings.EventCb = _eventCb;
            settings.LogCb = _logCb;

            var rc = _module.Start(ref settings);
            if (rc != 0)
            {
                Debugger.show($"[AUDIO] sca_start failed for {serial}: {rc}");
                _module.Dispose();
                _module = null;
                return false;
            }

            try
            {
                // The native side resamples to 48 kHz stereo float and pads with silence
                // when the stream is not up, so the render thread can always be served.
                _provider = new PullProvider(_module) { Volume = _volume };
                _output = new WasapiOut(AudioClientShareMode.Shared, true, 60);
                _output.Init(_provider);
                _output.Play();
            }
            catch (Exception ex)
            {
                Debugger.show("[AUDIO] WASAPI init failed: " + ex.Message);
                _module.Stop();
                _module.Dispose();
                _module = null;
                return false;
            }

            _started = true;

            Debugger.show($"[AUDIO] Audio link started for {serial}.");
            return true;
        }

        /// <summary>The teardown in flight, if any. Only waited on at shutdown.</summary>
        private Task? _teardown;

        /// <summary>
        /// Detaches the session immediately and finishes tearing it down in the
        /// background.
        ///
        /// sca_stop() blocks: it kills the device-side server and joins the port's
        /// threads, which takes long enough to be seen as a stall if it runs on the UI
        /// thread -- and this is called straight from a click. ScrcpyVideoSession.Dispose
        /// does the same thing with scv_close for the same reason.
        ///
        /// Everything the session owns is handed to the task and the fields cleared
        /// first, so a Start() right afterwards (RestartFor does exactly that) builds a
        /// fresh module while the old one is still winding down. They are separate
        /// private copies of the DLL, so the two do not interfere.
        /// </summary>
        public void Stop()
        {
            if (!_started)
                return;

            _started = false;

            var output = _output;
            var module = _module;

            _output = null;
            _module = null;
            _provider = null;

            _teardown = Task.Run(() =>
            {
                try
                {
                    // First, so the render thread is gone before the module is retired.
                    output?.Stop();
                    output?.Dispose();
                }
                catch (Exception ex)
                {
                    Debugger.show("[AUDIO] Stopping WASAPI failed: " + ex.Message);
                }

                try
                {
                    module?.Stop();
                }
                catch (Exception ex)
                {
                    Debugger.show("[AUDIO] sca_stop failed: " + ex.Message);
                }
                finally
                {
                    // The module goes with the session: its statics are the session.
                    module?.Dispose();
                }

                Debugger.show("[AUDIO] Audio link stopped.");
            });
        }

        private void OnEvent(int evt, IntPtr userdata) => SessionEvent?.Invoke(evt);

        private void OnLog(int level, IntPtr message, IntPtr userdata)
        {
            var text = Marshal.PtrToStringAnsi(message);
            if (!string.IsNullOrEmpty(text))
                Debugger.show("[scrcpy-audio] " + text);
        }

        /// <summary>
        /// Pulls straight from this session's own native ring buffer on the WASAPI render
        /// thread. sca_read always fills the request (padding with silence when the stream
        /// is down), so the output never underruns.
        /// </summary>
        private sealed class PullProvider : IWaveProvider
        {
            private readonly ScrcpyAudioModule _module;

            public PullProvider(ScrcpyAudioModule module) => _module = module;

            /// <summary>0..1. Read on the render thread; a torn float is harmless here.</summary>
            public float Volume { get; set; } = 1f;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

            public int Read(byte[] buffer, int offset, int count)
            {
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                bool filled;
                try
                {
                    var ptr = IntPtr.Add(handle.AddrOfPinnedObject(), offset);

                    // False once the session has been stopped. This call runs on the
                    // render thread, which can outlive the stop by a buffer or two.
                    filled = _module.Read(ptr, count);
                }
                catch
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }
                finally
                {
                    handle.Free();
                }

                if (!filled)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                Scale(buffer, offset, count, Volume);
                return count;
            }

            /// <summary>
            /// Applies this stream's level in place. The buffer is 32-bit float PCM, so
            /// silence is a straight zero-fill and anything else is a multiply.
            /// </summary>
            private static void Scale(byte[] buffer, int offset, int count, float volume)
            {
                if (volume >= 0.999f)
                    return;

                if (volume <= 0f)
                {
                    Array.Clear(buffer, offset, count);
                    return;
                }

                var samples = MemoryMarshal.Cast<byte, float>(
                    buffer.AsSpan(offset, count - count % sizeof(float)));

                for (var i = 0; i < samples.Length; i++)
                    samples[i] *= volume;
            }
        }

        /// <summary>
        /// Never waits: this is what turning audio off from the panel calls, and blocking
        /// here is the stall that backgrounding the teardown exists to avoid.
        /// </summary>
        public void Dispose() => Stop();

        /// <summary>
        /// Waits for a backgrounded teardown, for shutdown only.
        ///
        /// The process is about to exit there, and without this sca_stop can be cut off
        /// before it kills the device-side server -- leaving the phone capturing audio
        /// for nobody. Bounded, so a wedged teardown cannot stop AdbDesktop from closing.
        /// </summary>
        public void WaitForTeardown(TimeSpan timeout)
        {
            try
            {
                _teardown?.Wait(timeout);
            }
            catch (Exception ex)
            {
                Debugger.show("[AUDIO] Waiting for audio teardown failed: " + ex.Message);
            }
        }
    }
}
