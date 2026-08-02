using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdbDesktop
{
    /// <summary>One installed, launchable app.</summary>
    public sealed class AppEntry
    {
        public string PackageName { get; init; } = string.Empty;

        /// <summary>Prettified package name. There is no fast device-side way to read the
        /// real localized label -- it lives in the APK's resources.arsc.</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>
        /// Every device that has this package installed. The search list is merged across
        /// devices, so one entry can belong to several -- in which case adding it asks
        /// which one to put on the desktop.
        /// </summary>
        public IReadOnlyList<string> DeviceSerials { get; init; } = Array.Empty<string>();

        /// <summary>Whether more than one device carries it, so a choice is needed.</summary>
        public bool IsShared => DeviceSerials.Count > 1;

        /// <summary>
        /// Which device (or how many) has it, shown under the name. Empty with a single
        /// device attached, where saying so every row would be pure noise.
        /// </summary>
        public string DeviceSummary { get; init; } = string.Empty;

        public bool HasDeviceSummary => !string.IsNullOrEmpty(DeviceSummary);

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Enumerates apps that have a launcher entry. Uses `cmd package query-activities`
    /// rather than ASM's `pm list packages -3`, so system apps that a user genuinely
    /// wants on the desktop (YouTube, Photos, Settings) are included while the ~300
    /// invisible service packages are not.
    /// </summary>
    internal static class PackageService
    {
        /// <summary>
        /// The component line inside a query-activities block, e.g.
        /// "    com.android.chrome/com.google.android.apps.chrome.Main".
        /// Requiring no internal whitespace is what excludes the sibling
        /// "priority=0 preferredOrder=0 ..." lines.
        /// </summary>
        private static readonly Regex ComponentRegex = new(
            @"^\s+(?<pkg>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)/(?<act>[^\s/]+)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Fallback shape: "package:/data/app/~~hash==/pkg-hash==/base.apk=com.foo".
        /// The path is non-greedy and the package is anchored to the end of the line, so
        /// the base64 '=' padding inside modern /data/app paths can't confuse the split
        /// (ASM's greedy "package:(.+)=(.+)" only works here by luck).
        /// </summary>
        private static readonly Regex PmListRegex = new(
            @"^package:.+?=(?<pkg>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Last segment of these is meaningless as a display name, so Prettify steps back
        /// one segment when it lands on one.
        /// </summary>
        private static readonly HashSet<string> GenericSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "android", "app", "apps", "main", "client", "ui", "mobile", "com", "org", "net"
        };

        public static async Task<List<AppEntry>> GetLaunchableAppsAsync(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial))
                return new List<AppEntry>();

            var packages = await QueryLauncherActivitiesAsync(serial).ConfigureAwait(false);

            if (packages.Count == 0)
            {
                Debugger.show("[PKG] query-activities returned nothing; falling back to pm list packages.");
                packages = await QueryAllPackagesAsync(serial).ConfigureAwait(false);
            }

            var apps = packages
                .Select(p => new AppEntry { PackageName = p, DisplayName = Prettify(p) })
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Debugger.show($"[PKG] {apps.Count} launchable apps on {serial}.");
            return apps;
        }

        private static async Task<HashSet<string>> QueryLauncherActivitiesAsync(string serial)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            var output = await AdbHelper.RunAdbCaptureAsync(
                $"-s {serial} shell cmd package query-activities --brief " +
                "-a android.intent.action.MAIN -c android.intent.category.LAUNCHER")
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output) ||
                output.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return result;
            }

            foreach (Match m in ComponentRegex.Matches(output))
                result.Add(m.Groups["pkg"].Value);

            return result;
        }

        private static async Task<HashSet<string>> QueryAllPackagesAsync(string serial)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            var output = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell pm list packages -f")
                .ConfigureAwait(false);

            foreach (Match m in PmListRegex.Matches(output))
                result.Add(m.Groups["pkg"].Value);

            return result;
        }

        /// <summary>
        /// "com.google.android.youtube" -> "Youtube", "com.samsung.android.app.notes" ->
        /// "Notes", "com.discord" -> "Discord". A best-effort stand-in for the real label.
        /// </summary>
        public static string Prettify(string package)
        {
            if (string.IsNullOrWhiteSpace(package))
                return string.Empty;

            var segments = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return package;

            // Walk back from the end past generic segments ("...android.app" -> "android"
            // -> keep walking), but never past the first segment.
            var index = segments.Length - 1;
            while (index > 0 && GenericSegments.Contains(segments[index]))
                index--;

            return Humanize(segments[index]);
        }

        /// <summary>"lostword_client" / "lostWord" -> "Lost Word".</summary>
        private static string Humanize(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return string.Empty;

            var spaced = new StringBuilder(segment.Length + 8);

            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];

                if (c == '_' || c == '-')
                {
                    if (spaced.Length > 0 && spaced[^1] != ' ')
                        spaced.Append(' ');
                    continue;
                }

                // camelCase boundary, and the tail of an ACRONYMWord run.
                if (i > 0 && char.IsUpper(c) &&
                    (char.IsLower(segment[i - 1]) ||
                     (i + 1 < segment.Length && char.IsLower(segment[i + 1]) && char.IsUpper(segment[i - 1]))))
                {
                    if (spaced.Length > 0 && spaced[^1] != ' ')
                        spaced.Append(' ');
                }

                spaced.Append(c);
            }

            var words = spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                // Leave all-caps words (VLC, NFC) alone; title-case everything else.
                words[i] = w.All(char.IsUpper) && w.Length > 1
                    ? w
                    : char.ToUpperInvariant(w[0]) + w.Substring(1);
            }

            return string.Join(' ', words);
        }
    }
}
