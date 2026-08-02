using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdbDesktop
{
    /// <summary>
    /// Copied verbatim from AMPL (AndroidMusicPresence\Services\AdbHelper.cs).
    ///
    /// The load-bearing idea: callers always write plain adb argument strings, and any
    /// argument matching "[-s serial] shell &lt;cmd&gt;" is transparently rerouted into a
    /// long-lived `adb shell` process instead of spawning a new adb every time. That
    /// removes ~100-300ms of process-spawn cost per command, which matters because
    /// AdbDesktop iterates over hundreds of packages.
    /// </summary>
    public static class AdbHelper
    {
        private const string DefaultSessionKey = "__default__";
        public static TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(20);
        private static readonly ConcurrentDictionary<string, AdbShellSession> ShellSessions = new();
        private static readonly object SessionSync = new();
        private static readonly Regex ShellArgsRegex = new(@"^\s*(?:-s\s+(?<serial>\S+)\s+)?shell\s+(?<command>[\s\S]+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string _adbPath = string.Empty;
        public static string AdbPath
        {
            get => _adbPath;
            set
            {
                var newPath = value ?? string.Empty;
                if (string.Equals(_adbPath, newPath, StringComparison.Ordinal))
                    return;

                _adbPath = newPath;
                DisposeAllShellSessions();
            }
        }

        public static async Task RunAdbAsync(string args)
        {
            if (!IsAdbConfigured())
                return;

            if (TryParseShellCommand(args, out var serial, out var shellCommand))
            {
                await ExecuteShellCommandAsync(serial, shellCommand, captureOutput: false).ConfigureAwait(false);
                return;
            }

            await RunAdbProcessAsync(args, captureOutput: false).ConfigureAwait(false);
        }

        public static void StopServer()
        {
            if (!IsAdbConfigured(showError: false))
                return;
            try
            {
                DisposeAllShellSessions();
                RunAdbProcessAsync("kill-server", captureOutput: false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debugger.show("ADB stop failed: " + ex.Message);
            }
        }

        public static async Task<string> RunAdbCaptureAsync(string args)
        {
            if (!IsAdbConfigured())
                return string.Empty;

            if (TryParseShellCommand(args, out var serial, out var shellCommand))
            {
                return await ExecuteShellCommandAsync(serial, shellCommand, captureOutput: true).ConfigureAwait(false);
            }

            return await RunAdbProcessAsync(args, captureOutput: true).ConfigureAwait(false);
        }

        private static bool IsAdbConfigured(bool showError = true)
        {
            var ok = !string.IsNullOrWhiteSpace(AdbPath) && File.Exists(AdbPath);
            if (!ok && showError)
            {
                Debugger.show("ADB path not set or missing: " + AdbPath);
            }

            return ok;
        }

        private static bool TryParseShellCommand(string args, out string serial, out string shellCommand)
        {
            serial = string.Empty;
            shellCommand = string.Empty;

            if (string.IsNullOrWhiteSpace(args))
                return false;

            var match = ShellArgsRegex.Match(args);
            if (!match.Success)
                return false;

            serial = match.Groups["serial"].Value.Trim();
            shellCommand = match.Groups["command"].Value.Trim();
            return !string.IsNullOrWhiteSpace(shellCommand);
        }

        private static async Task<string> ExecuteShellCommandAsync(string serial, string shellCommand, bool captureOutput)
        {
            var key = string.IsNullOrWhiteSpace(serial) ? DefaultSessionKey : serial.Trim();

            try
            {
                CleanupIdleShellSessions();
                var session = ShellSessions.GetOrAdd(key, _ => new AdbShellSession(AdbPath, serial));
                if (!session.IsCompatibleWith(AdbPath, serial))
                {
                    if (ShellSessions.TryRemove(key, out var removed))
                    {
                        removed.Dispose();
                    }

                    session = ShellSessions.GetOrAdd(key, _ => new AdbShellSession(AdbPath, serial));
                }

                return await session.ExecuteAsync(shellCommand, captureOutput).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("ADB shell error: " + ex.Message);
                return string.Empty;
            }
        }

        private static async Task<string> RunAdbProcessAsync(string args, bool captureOutput)
        {
            bool trace = Debugger.AdvancedEnabled;
            Debugger.advanced($"[ADB EXEC] adb {args}");

            var psi = new ProcessStartInfo(AdbPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return string.Empty;

                // Advanced tracing needs the output even for fire-and-forget calls,
                // so it forces the capturing path.
                if (captureOutput || trace)
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync().ConfigureAwait(false);

                    var output = await outputTask.ConfigureAwait(false);
                    var error = await errorTask.ConfigureAwait(false);

                    Debugger.advanced($"[ADB EXEC] exit={process.ExitCode}, output: {(string.IsNullOrWhiteSpace(output) ? "<empty>" : output.Trim())}");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Debugger.show("ADB error: " + error.Trim());
                    }

                    return captureOutput ? output : string.Empty;
                }

                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                return string.Empty;
            }
            catch (Exception ex)
            {
                Debugger.show("ADB error: " + ex.Message);
                return string.Empty;
            }
        }

        private static void CleanupIdleShellSessions()
        {
            foreach (var pair in ShellSessions)
            {
                var session = pair.Value;
                if (!session.TryDisposeIfIdle(SessionIdleTimeout))
                    continue;

                ShellSessions.TryRemove(pair.Key, out _);
            }
        }

        private static void DisposeAllShellSessions()
        {
            lock (SessionSync)
            {
                foreach (var pair in ShellSessions)
                {
                    try
                    {
                        pair.Value.Dispose();
                    }
                    catch
                    {
                    }
                }

                ShellSessions.Clear();
            }
        }

        private sealed class AdbShellSession : IDisposable
        {
            private readonly string _adbPath;
            private readonly string _serial;
            private readonly SemaphoreSlim _sessionLock = new(1, 1);

            private Process? _process;
            private StreamWriter? _stdin;
            private StreamReader? _stdout;
            private bool _disposed;
            private int _commandCounter;
            private DateTime _lastUsedUtc = DateTime.UtcNow;

            public AdbShellSession(string adbPath, string serial)
            {
                _adbPath = adbPath;
                _serial = serial?.Trim() ?? string.Empty;
            }

            public bool IsCompatibleWith(string adbPath, string serial)
            {
                return string.Equals(_adbPath, adbPath, StringComparison.Ordinal)
                    && string.Equals(_serial, serial?.Trim() ?? string.Empty, StringComparison.Ordinal);
            }

            public async Task<string> ExecuteAsync(string shellCommand, bool captureOutput)
            {
                await _sessionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed)
                        return string.Empty;

                    _lastUsedUtc = DateTime.UtcNow;
                    EnsureStarted();
                    if (_process == null || _stdin == null || _stdout == null)
                        return string.Empty;

                    var commandNumber = ++_commandCounter;

                    bool trace = Debugger.AdvancedEnabled;
                    Debugger.advanced($"[ADB SHELL] ({(string.IsNullOrWhiteSpace(_serial) ? "default" : _serial)}, cmd#{commandNumber}) $ {shellCommand}");

                    var marker = "__ADB_HELPER_DONE__" + Guid.NewGuid().ToString("N");
                    var lineToSend = shellCommand + "; echo " + marker + ":$?";

                    await _stdin.WriteLineAsync(lineToSend).ConfigureAwait(false);
                    await _stdin.FlushAsync().ConfigureAwait(false);

                    // Tracing collects output even when the caller doesn't want it; the
                    // lines are read off the pipe either way to find the marker.
                    StringBuilder? output = captureOutput || trace ? new StringBuilder() : null;
                    string exitCode = "?";

                    while (true)
                    {
                        var line = await _stdout.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            Debugger.advanced($"[ADB SHELL] (cmd#{commandNumber}) stream closed before marker; restarting session");
                            Restart();
                            return captureOutput ? output?.ToString() ?? string.Empty : string.Empty;
                        }

                        if (line.StartsWith(marker + ":", StringComparison.Ordinal))
                        {
                            exitCode = line.Substring(marker.Length + 1);
                            break;
                        }

                        output?.AppendLine(line);
                    }

                    if (trace && output != null)
                    {
                        Debugger.advanced($"[ADB SHELL] (cmd#{commandNumber}) exit={exitCode}, output: {(output.Length == 0 ? "<empty>" : output.ToString().TrimEnd())}");
                    }

                    return captureOutput ? output?.ToString() ?? string.Empty : string.Empty;
                }
                catch (Exception ex)
                {
                    Debugger.show("ADB shell session failed: " + ex.Message);
                    Restart();
                    return string.Empty;
                }
                finally
                {
                    _lastUsedUtc = DateTime.UtcNow;
                    _sessionLock.Release();
                }
            }

            public bool TryDisposeIfIdle(TimeSpan idleTimeout)
            {
                if (_disposed)
                    return true;

                if (_process == null || _process.HasExited)
                    return true;

                if (DateTime.UtcNow - _lastUsedUtc < idleTimeout)
                    return false;

                if (!_sessionLock.Wait(0))
                    return false;

                try
                {
                    if (DateTime.UtcNow - _lastUsedUtc < idleTimeout)
                        return false;

                    Dispose();
                    return true;
                }
                finally
                {
                    if (!_disposed)
                    {
                        _sessionLock.Release();
                    }
                }
            }

            private void EnsureStarted()
            {
                if (_process != null && !_process.HasExited)
                    return;

                Restart();

                var args = string.IsNullOrWhiteSpace(_serial)
                    ? "shell"
                    : $"-s {_serial} shell";

                var psi = new ProcessStartInfo(_adbPath, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    // stdout/stderr are already UTF-8, but the default stdin encoding on
                    // Windows is the legacy console codepage (typically CP1252), which
                    // mangles non-ASCII chars in commands. Force UTF-8 here so commands
                    // round-trip correctly.
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _process = Process.Start(psi);
                if (_process == null)
                {
                    return;
                }

                _commandCounter = 0;
                Debugger.show($"[ADB HELPER] ADB shell session started pid={_process.Id}, device={(string.IsNullOrWhiteSpace(_serial) ? "default" : _serial)}");

                _process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Debugger.show("ADB shell stderr: " + e.Data);
                    }
                };
                _process.BeginErrorReadLine();

                _stdin = _process.StandardInput;
                _stdout = _process.StandardOutput;
            }

            private void Restart()
            {
                try
                {
                    if (_process != null)
                    {
                        if (!_process.HasExited)
                        {
                            try
                            {
                                _stdin?.WriteLine("exit");
                                _stdin?.Flush();
                            }
                            catch
                            {
                            }

                            if (!_process.WaitForExit(500))
                            {
                                _process.Kill(true);
                            }
                        }

                        Debugger.show($"[ADB HELPER] ADB shell session ended pid={_process.Id}, device={(string.IsNullOrWhiteSpace(_serial) ? "default" : _serial)}");
                        _process.Dispose();
                    }
                }
                catch
                {
                }
                finally
                {
                    _process = null;
                    _stdin = null;
                    _stdout = null;
                }
            }

            public void Dispose()
            {
                _disposed = true;
                Restart();
                _sessionLock.Dispose();
            }
        }
    }
}
