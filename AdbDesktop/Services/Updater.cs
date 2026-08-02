using System;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace AdbDesktop
{
    public enum UpdateStatus
    {
        UpdateAvailable,
        UpToDate,
        DebugBuild
    }

    /// <summary>
    /// very important reminder as to how the updater works
    /// it first checks numerically eg 1.2.0 or 1.3.0 (this takes priority)
    /// if it finds multiple matching version eg 1.2-beta17 or 1.2-beta16 (-betanumber get's cuttoff) it will switch checking lexographically with beta
    /// </summary>
    public static class Updater
    {
        private const string RepoOwner = "snail-boi";
        private const string RepoName = "adbDesktop";
        private const string InstallerPrefix = "AdbDesktop_Setup";
        private const string ReleasesPageUrl = "https://github.com/snail-boi/adbDesktop/releases";
        private static readonly SemaphoreSlim PromptSemaphore = new(1, 1);

        public static event Action<UpdateStatus, string?, string?>? UpdateStatusChanged;

        public static UpdateStatus Status { get; private set; } = UpdateStatus.UpToDate;
        public static bool IsUpdateAvailable => Status == UpdateStatus.UpdateAvailable;
        public static string? LatestVersion { get; private set; }
        public static string? LatestPatchNotes { get; private set; }

        /// <summary>
        /// Call this at app startup (Option 1: fire-and-forget) or in Loaded event.
        /// </summary>
        /// <param name="currentVersion">Your current app version string, e.g., "v1.2-beta 10"</param>
        public static async Task CheckForUpdateAsync(string currentVersion, bool showPrompt = true, bool allowRemindLater = false, string? ignoredVersion = null, Action<string>? onDismissed = null)
        {
            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("adbDesktopUpdater/1.0");

                string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases";
                string json = await client.GetStringAsync(apiUrl);

                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return;

                JsonElement? latestRelease = null;
                Version? latestNumericVersion = null;

                foreach (JsonElement release in root.EnumerateArray())
                {
                    if (!release.TryGetProperty("tag_name", out JsonElement tagElem))
                        continue;

                    string? tagName = tagElem.GetString();
                    if (string.IsNullOrEmpty(tagName))
                        continue;

                    var match = Regex.Match(tagName, @"\d+(\.\d+)*");
                    if (!match.Success)
                        continue;

                    if (!Version.TryParse(match.Value.Replace("-", "."), out Version? releaseVersion))
                        continue;

                    if (latestNumericVersion == null || releaseVersion > latestNumericVersion)
                    {
                        latestNumericVersion = releaseVersion;
                        latestRelease = release;
                    }
                }

                if (latestRelease == null)
                    return;

                string latestVersion = latestRelease.Value.GetProperty("tag_name").GetString() ?? string.Empty;
                LatestVersion = latestVersion;
                LatestPatchNotes = GetReleaseNotes(latestRelease.Value);

                if (!IsNewerVersion(latestVersion, currentVersion))
                {
                    Status = IsNewerVersion(currentVersion, latestVersion)
                        ? UpdateStatus.DebugBuild
                        : UpdateStatus.UpToDate;
                    UpdateStatusChanged?.Invoke(Status, latestVersion, LatestPatchNotes);
                    return;
                }

                Status = UpdateStatus.UpdateAvailable;
                UpdateStatusChanged?.Invoke(Status, latestVersion, LatestPatchNotes);

                var patchNotes = LatestPatchNotes ?? GetReleaseNotes(latestRelease.Value);

                if (!showPrompt)
                {
                    UpdateStatusChanged?.Invoke(Status, latestVersion, LatestPatchNotes);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(ignoredVersion) && string.Equals(ignoredVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!await PromptSemaphore.WaitAsync(0))
                    return;

                try
                {
                    var prompt = new UpdatePromptWindow(latestVersion, patchNotes, allowRemindLater);
                    var owner = GetPromptOwner();
                    if (owner != null)
                    {
                        prompt.Owner = owner;
                    }

                    bool? result = prompt.ShowDialog();

                    if (prompt.Choice == UpdatePromptChoice.Ignore)
                    {
                        onDismissed?.Invoke(latestVersion);
                        return;
                    }

                    if (prompt.Choice != UpdatePromptChoice.Install || result != true)
                        return;

                    if (AppPaths.IsPortable)
                    {
                        Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
                        return;
                    }

                    if (!latestRelease.Value.TryGetProperty("assets", out JsonElement assetsElem) || assetsElem.ValueKind != JsonValueKind.Array)
                        return;

                    JsonElement? installerAsset = null;
                    foreach (JsonElement asset in assetsElem.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out JsonElement nameElem))
                            continue;

                        string? name = nameElem.GetString();
                        if (string.IsNullOrEmpty(name))
                            continue;

                        if (name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            installerAsset = asset;
                            break;
                        }
                    }

                    if (installerAsset == null)
                        return;

                    string installerName = installerAsset.Value.GetProperty("name").GetString() ?? "";
                    string downloadUrl = installerAsset.Value.GetProperty("browser_download_url").GetString() ?? "";

                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), installerName);
                    byte[] data = await client.GetByteArrayAsync(downloadUrl);
                    await System.IO.File.WriteAllBytesAsync(tempPath, data);

                    Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                    Application.Current.Shutdown();
                }
                finally
                {
                    PromptSemaphore.Release();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // Network unavailable or timeout — not worth bothering the user.
                Debugger.show($"[UPDATER] Update check failed (network): {ex.Message}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update check failed:\n{ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Status = UpdateStatus.UpToDate;
                UpdateStatusChanged?.Invoke(Status, LatestVersion, LatestPatchNotes);
            }
        }

        private static string GetReleaseNotes(JsonElement release)
        {
            if (release.TryGetProperty("body", out JsonElement bodyElem))
            {
                string notes = bodyElem.GetString() ?? "";
                return notes.Trim();
            }
            return "No patch notes available.";
        }

        private static Window? GetPromptOwner()
        {
            var app = Application.Current;
            if (app == null)
                return null;

            return app.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsVisible && w.IsActive)
                ?? app.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsVisible);
        }

        /// <summary>
        /// Compare two version strings like "v1.2-beta 10" or "v1.2.10.0"
        /// </summary>
        private static bool IsNewerVersion(string latest, string current)
        {
            string cleanLatest = latest.TrimStart('v', 'V').Trim();
            string cleanCurrent = current.TrimStart('v', 'V').Trim();

            // Strip build number (4th part) before comparing — GitHub tags use x.x.x only.
            cleanCurrent = Regex.Replace(cleanCurrent, @"^(\d+\.\d+\.\d+)\.\d+", "$1");
            cleanLatest = Regex.Replace(cleanLatest, @"^(\d+\.\d+\.\d+)\.\d+", "$1");

            var rx = new Regex(@"\d+");
            var latestNumbers = rx.Matches(cleanLatest);
            var currentNumbers = rx.Matches(cleanCurrent);

            int len = Math.Min(latestNumbers.Count, currentNumbers.Count);
            for (int i = 0; i < len; i++)
            {
                int lv = int.Parse(latestNumbers[i].Value);
                int cv = int.Parse(currentNumbers[i].Value);
                if (lv > cv) return true;
                if (lv < cv) return false;
            }

            return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }
}