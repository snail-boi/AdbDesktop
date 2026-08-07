using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdbDesktop
{
    /// <summary>One notification currently posted on a device.</summary>
    public sealed class DeviceNotification
    {
        /// <summary>
        /// Android's own key ("0|com.whatsapp|1|null|10123"). Identity, so a list that is
        /// re-read every few seconds can be compared without the rows flickering.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        public string Package { get; init; } = string.Empty;

        /// <summary>android.title, or empty when the device would not give it up.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>android.text, same caveat.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>When it was posted, local time. Null when the dump had no android.when.</summary>
        public DateTime? When { get; init; }

        /// <summary>
        /// Package name prettified. The real localized label lives in the APK's
        /// resources.arsc, which is far too expensive to read per notification.
        /// </summary>
        public string AppName => PackageService.Prettify(Package);

        /// <summary>Time only for today, date and time for anything older.</summary>
        public string TimeText
        {
            get
            {
                if (When is not { } when)
                    return string.Empty;

                return when.Date == DateTime.Now.Date
                    ? when.ToString("t", CultureInfo.CurrentCulture)
                    : when.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
            }
        }
    }

    /// <summary>
    /// What one read of a device's shade produced. <see cref="Readable"/> is the
    /// difference between "nothing is posted" and "the device would not tell us" -- an
    /// empty list means nothing on its own, and showing "no notifications" for a failed
    /// read would be a lie.
    /// </summary>
    public sealed class NotificationSnapshot
    {
        public IReadOnlyList<DeviceNotification> Items { get; init; } = Array.Empty<DeviceNotification>();

        public bool Readable { get; init; }

        /// <summary>
        /// The device dumped the list but held the text back. Some builds ignore
        /// --noredact, and then every title comes through as "17 chars".
        /// </summary>
        public bool Redacted { get; init; }

        public static NotificationSnapshot Unreadable => new();
    }

    /// <summary>
    /// Reads the notification shade over adb.
    ///
    /// `dumpsys notification --noredact` is the only shell-reachable source: there is no
    /// adb command that returns posted notifications as data, and a NotificationListener
    /// would mean installing an app on the phone, which adbDesktop deliberately never
    /// does. So this parses a debug dump, and is written to degrade rather than throw when
    /// a vendor build words it differently.
    ///
    /// The dump is large -- hundreds of kilobytes on a busy phone, most of it ranking
    /// tables and the delivery log -- so it is filtered device-side. What crosses the wire
    /// is one line per record, the three extras that are worth showing, and the section
    /// headers, which is what tells the parser where the *posted* list ends and the
    /// historical log begins.
    /// </summary>
    internal static class NotificationService
    {
        /// <summary>
        /// Kept: record headers, the two extras worth showing, the flags line, the post
        /// time, and any bare "Something:" heading. The headings are load-bearing -- see
        /// <see cref="Parse"/>.
        ///
        /// Note the time comes from the record's own "when=" line and NOT from
        /// android.when: real devices do not put android.when in the extras at all.
        /// </summary>
        private const string DumpCommand =
            "dumpsys notification --noredact 2>/dev/null | " +
            "grep -E 'NotificationRecord\\(|android\\.(title|text)=|^ *flags=|^ *when=[0-9]|" +
            "^ *[A-Za-z][A-Za-z ]*:$'";

        private static readonly Regex PackageRegex =
            new(@"\bpkg=(?<pkg>\S+)", RegexOptions.Compiled);

        private static readonly Regex KeyRegex =
            new(@"\bkey=(?<key>[^\s:]+)", RegexOptions.Compiled);

        /// <summary>
        /// "when=1786045564721/1786045564721" -- the record's own line, not an extra. The
        /// second half is the same instant formatted, so only the first is taken.
        /// </summary>
        private static readonly Regex WhenRegex =
            new(@"^\s*when=(?<when>\d+)", RegexOptions.Compiled);

        /// <summary>
        /// "flags=NO_CLEAR|FOREGROUND_SERVICE". Symbolic on every device tested; the hex
        /// form is accepted too because older builds printed it that way.
        /// </summary>
        private static readonly Regex FlagsRegex =
            new(@"^\s*flags=(?<flags>\S+)", RegexOptions.Compiled);

        /// <summary>
        /// "android.title=String (Alice)". The type between the '=' and the '(' varies --
        /// String, SpannableString, CharSequence -- so it is matched loosely, and the value
        /// runs greedily to the last ')' so text containing brackets survives.
        /// </summary>
        private static readonly Regex ExtraRegex =
            new(@"^\s*android\.(?<name>\w+)=\S+ \((?<value>.*)\)\s*$", RegexOptions.Compiled);

        /// <summary>What a redacted dump gives instead of the text: "17 chars".</summary>
        private static readonly Regex RedactedRegex =
            new(@"^\d+ chars$", RegexOptions.Compiled);

        /// <summary>Notification.FLAG_GROUP_SUMMARY.</summary>
        private const int FlagGroupSummary = 0x200;

        public static async Task<NotificationSnapshot> GetAsync(string transport)
        {
            if (string.IsNullOrWhiteSpace(transport))
                return NotificationSnapshot.Unreadable;

            try
            {
                var output = await AdbHelper
                    .RunAdbCaptureAsync($"-s {transport} shell sh -c \"{DumpCommand}\"")
                    .ConfigureAwait(false);

                return Parse(output);
            }
            catch (Exception ex)
            {
                Debugger.show($"[NOTIF] Read failed for {transport}: {ex.Message}");
                return NotificationSnapshot.Unreadable;
            }
        }

        /// <summary>
        /// Opens the shade on the phone itself. The dump is read-only -- there is no adb
        /// route to dismissing or acting on a notification -- so this is the one thing
        /// adbDesktop can offer beyond looking at them.
        /// </summary>
        public static Task ExpandShadeAsync(string transport) =>
            string.IsNullOrWhiteSpace(transport)
                ? Task.CompletedTask
                : AdbHelper.RunAdbAsync($"-s {transport} shell cmd statusbar expand-notifications");

        /// <summary>
        /// Walks the filtered dump.
        ///
        /// Only the "Notification List" section is wanted. The same NotificationRecord
        /// shape also appears further down under the delivery log, which holds everything
        /// that has *been* posted, including what the user swiped away hours ago -- taking
        /// those too would show a shade that never empties. The headings kept by the grep
        /// are how the end of the live section is spotted without depending on indentation,
        /// which vendor builds do not agree on.
        /// </summary>
        private static NotificationSnapshot Parse(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return NotificationSnapshot.Unreadable;

            var items = new List<DeviceNotification>();
            var summaries = new List<string>();   // keys of group summaries, resolved at the end

            var inList = false;
            var sawList = false;
            var redacted = false;

            string key = string.Empty, package = string.Empty, title = string.Empty, text = string.Empty;
            DateTime? when = null;
            var isSummary = false;
            var open = false;

            void Flush()
            {
                if (!open)
                    return;

                open = false;

                if (string.IsNullOrEmpty(package))
                    return;

                var item = new DeviceNotification
                {
                    Key = string.IsNullOrEmpty(key) ? $"{package}|{items.Count}" : key,
                    Package = package,
                    Title = title,
                    Text = text,
                    When = when,
                };

                if (isSummary)
                    summaries.Add(item.Key);

                items.Add(item);
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                if (line.Contains("NotificationRecord(", StringComparison.Ordinal))
                {
                    Flush();

                    // Records outside the live list are the log; skip their extras too.
                    if (!inList)
                        continue;

                    key = KeyRegex.Match(line) is { Success: true } k ? k.Groups["key"].Value : string.Empty;
                    package = PackageRegex.Match(line) is { Success: true } p ? p.Groups["pkg"].Value : string.Empty;

                    title = string.Empty;
                    text = string.Empty;
                    when = null;
                    isSummary = false;
                    open = true;
                    continue;
                }

                if (open && FlagsRegex.Match(line) is { Success: true } flags)
                {
                    isSummary = IsGroupSummary(flags.Groups["flags"].Value);
                    continue;
                }

                if (open && when == null && WhenRegex.Match(line) is { Success: true } posted)
                {
                    when = ParseWhen(posted.Groups["when"].Value);
                    continue;
                }

                var extra = ExtraRegex.Match(line);
                if (extra.Success)
                {
                    if (!open)
                        continue;

                    var value = extra.Groups["value"].Value.Trim();

                    if (RedactedRegex.IsMatch(value))
                    {
                        // The dump gave a length instead of the words. Nothing to show,
                        // but the panel should say why rather than look empty.
                        redacted = true;
                        continue;
                    }

                    switch (extra.Groups["name"].Value)
                    {
                        case "title":
                            title = Clean(value);
                            break;
                        case "text":
                            text = Clean(value);
                            break;
                    }

                    continue;
                }

                // A bare heading. "Notification List:" opens the live section; the next
                // heading of any kind closes it.
                var heading = line.Trim();
                if (!heading.EndsWith(':'))
                    continue;

                Flush();

                // Matched exactly rather than by substring. Samsung prints a second
                // section further down headed "History Notification List:", which holds
                // everything the user has already dealt with -- a substring test lets that
                // one back in and the shade never appears to empty.
                inList = heading.Equals("Notification List:", StringComparison.OrdinalIgnoreCase)
                         || heading.Equals("Current Notification List:", StringComparison.OrdinalIgnoreCase);

                sawList |= inList;
            }

            Flush();

            // The header always prints, even with an empty shade. Never having seen it
            // means the dump did not come through at all -- no root of the tree, so an
            // empty list here is "could not read", not "nothing posted".
            if (!sawList)
                return NotificationSnapshot.Unreadable;

            return new NotificationSnapshot
            {
                Items = Collapse(items, summaries),
                Readable = true,
                Redacted = redacted && items.Count > 0,
            };
        }

        /// <summary>
        /// Drops a group summary when the group's own children are also in the list.
        /// Android posts both -- the summary is the collapsed "3 new messages" row the
        /// shade shows in place of the children -- so keeping both lists every
        /// conversation twice.
        /// </summary>
        private static IReadOnlyList<DeviceNotification> Collapse(
            List<DeviceNotification> items, List<string> summaryKeys)
        {
            if (summaryKeys.Count > 0)
            {
                var summaries = new HashSet<string>(summaryKeys, StringComparer.Ordinal);

                items = items
                    .Where(i => !summaries.Contains(i.Key)
                                || items.Count(o => string.Equals(o.Package, i.Package,
                                                        StringComparison.Ordinal)) == 1)
                    .ToList();
            }

            // Newest first, and anything undated last rather than pinned to 1970.
            return items
                .OrderByDescending(i => i.When ?? DateTime.MinValue)
                .ToList();
        }

        /// <summary>
        /// Handles both shapes of the flags line: the symbolic
        /// "NO_CLEAR|GROUP_SUMMARY" that every current device prints, and the bare hex
        /// older builds used.
        /// </summary>
        private static bool IsGroupSummary(string flags)
        {
            if (flags.Contains("GROUP_SUMMARY", StringComparison.Ordinal))
                return true;

            if (!flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return false;

            return int.TryParse(flags.AsSpan(2), NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture, out var value)
                   && (value & FlagGroupSummary) != 0;
        }

        private static DateTime? ParseWhen(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                || ms <= 0)
                return null;

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// Notification text carries newlines and runs of spaces (bigText especially).
        /// The dump is line-based, so only the first line ever arrives; this tidies what
        /// is left so it sits on one row.
        /// </summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var collapsed = Regex.Replace(value, @"\s+", " ").Trim();
            return collapsed.Length <= 300 ? collapsed : collapsed.Substring(0, 300) + "…";
        }
    }
}
