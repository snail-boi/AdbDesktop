using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdbDesktop
{
    /// <summary>
    /// Pulls the icon-bearing APKs for one package off the device.
    ///
    /// The important difference from ASM: `pm path` returns *many* lines for a
    /// split-installed app, and ASM's Regex.Match (singular) only ever sees base.apk.
    /// Real output from a Play-installed app:
    ///
    ///   package:/data/app/~~hash==/com.discord-hash==/base.apk
    ///   package:/data/app/~~hash==/com.discord-hash==/split_config.arm64_v8a.apk
    ///   package:/data/app/~~hash==/com.discord-hash==/split_config.en.apk
    ///   package:/data/app/~~hash==/com.discord-hash==/split_config.xxhdpi.apk
    ///
    /// Density splits carry launcher rasters, so they must be pulled. ABI and language
    /// splits never do, and the ABI split is routinely 40-80 MB, so they are skipped.
    /// </summary>
    internal static class ApkPuller
    {
        private static readonly Regex PathRegex = new(
            @"^package:(?<path>\S+\.apk)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex DensitySplitRegex = new(
            @"split_config\.(?:l|m|h|xh|xxh|xxxh|tv|no|any)dpi\.apk$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public sealed class PullResult
        {
            public List<string> LocalApks { get; } = new();
            public string WorkingDirectory { get; init; } = string.Empty;
            public string? Error { get; set; }
            public bool Success => Error == null && LocalApks.Count > 0;
        }

        /// <summary>
        /// Pulls base.apk plus any density config splits into a per-package scratch dir.
        /// The caller owns cleanup via <see cref="Cleanup"/>.
        /// </summary>
        public static async Task<PullResult> PullAsync(
            string serial,
            string package,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var workDir = Path.Combine(AppPaths.PullTempDir, Sanitize(package));
            var result = new PullResult { WorkingDirectory = workDir };

            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(package))
            {
                result.Error = "No device selected.";
                return result;
            }

            progress?.Report("Locating APK...");

            var pathOutput = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell pm path {package}")
                .ConfigureAwait(false);

            var remotePaths = PathRegex.Matches(pathOutput)
                .Select(m => m.Groups["path"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (remotePaths.Count == 0)
            {
                result.Error = $"Could not locate an APK for {package}. It may be uninstalled or restricted.";
                Debugger.show($"[PULL] pm path returned nothing usable for {package}: {pathOutput.Trim()}");
                return result;
            }

            var wanted = remotePaths.Where(IsIconBearing).ToList();
            if (wanted.Count == 0)
                wanted = remotePaths.Take(1).ToList();   // odd layout: fall back to the first path

            Debugger.show($"[PULL] {package}: {remotePaths.Count} split(s) on device, pulling {wanted.Count}.");

            // Start from a clean directory so a previous failed attempt can't leave a
            // stale APK behind that the extractor would then scan.
            Cleanup(workDir);
            Directory.CreateDirectory(workDir);

            var index = 0;
            foreach (var remote in wanted)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                var localName = Path.GetFileName(remote);
                if (string.IsNullOrWhiteSpace(localName))
                    localName = $"part{index}.apk";

                var local = Path.Combine(workDir, localName);

                progress?.Report($"Pulling {localName} ({index}/{wanted.Count})...");

                await AdbHelper.RunAdbCaptureAsync($"-s {serial} pull \"{remote}\" \"{local}\"")
                    .ConfigureAwait(false);

                if (File.Exists(local) && new FileInfo(local).Length > 0)
                    result.LocalApks.Add(local);
                else
                    Debugger.show($"[PULL] Failed to pull {remote}.");
            }

            if (result.LocalApks.Count == 0)
            {
                result.Error = $"Could not pull the APK for {package}. The app may be protected.";
                Cleanup(workDir);
            }

            return result;
        }

        /// <summary>
        /// base.apk and density splits carry drawables. ABI splits (arm64_v8a, x86_64)
        /// hold only native libraries, language splits only strings -- both are dead
        /// weight, and the ABI split is usually the largest file in the package.
        /// </summary>
        private static bool IsIconBearing(string remotePath)
        {
            var name = Path.GetFileName(remotePath);
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.Equals("base.apk", StringComparison.OrdinalIgnoreCase))
                return true;

            return DensitySplitRegex.IsMatch(name);
        }

        public static void Cleanup(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                return;

            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex)
            {
                // Non-fatal -- the extractor has already released its handles by now, but
                // an AV scanner can still hold one briefly.
                Debugger.show($"[PULL] Could not clean {workDir}: {ex.Message}");
            }
        }

        /// <summary>Drops anything that isn't safe in a directory name.</summary>
        private static string Sanitize(string package)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(package.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
