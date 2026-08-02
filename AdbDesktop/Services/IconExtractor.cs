using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    public sealed class IconCandidate
    {
        public string SourceApk { get; init; } = string.Empty;
        public string EntryPath { get; init; } = string.Empty;
        public BitmapSource Image { get; init; } = null!;
        public int Width { get; init; }
        public int Height { get; init; }
        public int Score { get; init; }

        /// <summary>Shown under the tile so the user can tell near-identical icons apart.</summary>
        public string Label => Path.GetFileNameWithoutExtension(EntryPath);
        public string Tooltip => $"{EntryPath}\n{Width} x {Height}";
    }

    /// <summary>
    /// Finds plausible launcher icons inside a set of pulled APKs.
    ///
    /// An APK is already a zip, so it is opened directly -- the "convert it to a zip"
    /// step is a no-op and is skipped.
    ///
    /// The pipeline exists because the naive version is unusable. Measured on a real
    /// Discord install (base.apk + split_config.xxhdpi.apk): 3104 res/ entries, 1608
    /// rasters, 823 of them at least 96px. Dumping those into a picker grid is noise.
    /// Gating on shape, then ranking, then collapsing density variants, brings it to
    /// ~60 real icons with the app's actual logo first.
    ///
    /// Order matters: every cheap filter runs before any decode.
    /// </summary>
    internal static class IconExtractor
    {
        private const int MinDimension = 48;      // below this it's a UI glyph, not an icon
        private const int MaxDimension = 1024;    // above this it's artwork or a background
        private const long MinFileSize = 400;
        private const long MaxFileSize = 4 * 1024 * 1024;
        private const double MinAspect = 0.8;     // icons are square-ish; this is what
        private const double MaxAspect = 1.25;    // kills banners and splash backgrounds
        private const int MaxCandidates = 60;

        private static readonly (string Token, int Points)[] DensityRanks =
        {
            ("xxxhdpi", 40),
            ("xxhdpi",  34),
            ("xhdpi",   28),
            ("nodpi",   30),
            ("hdpi",    22),
            ("tvdpi",   18),
            ("mdpi",    14),
            ("ldpi",     8),
            ("anydpi",   4)
        };

        public static Task<List<IconCandidate>> ScanAsync(
            IEnumerable<string> apkPaths,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return Task.Run(() => Scan(apkPaths, progress, ct), ct);
        }

        private static List<IconCandidate> Scan(
            IEnumerable<string> apkPaths,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            // Keyed on the density-agnostic basename: res/mipmap-xxxhdpi-v4/galaxy.webp
            // and res/mipmap-hdpi-v4/galaxy.webp are the same icon, and a content hash
            // would not catch that because the bytes genuinely differ.
            var best = new Dictionary<string, Shortlisted>(StringComparer.OrdinalIgnoreCase);
            var header = new byte[ImageHeaderReader.HeaderBytes];
            var inspected = 0;

            foreach (var apkPath in apkPaths)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {Path.GetFileName(apkPath)}...");

                try
                {
                    using var archive = ZipFile.OpenRead(apkPath);

                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!IsRasterResource(entry.FullName)) continue;
                        if (entry.Length < MinFileSize || entry.Length > MaxFileSize) continue;

                        inspected++;

                        if (!TryReadHeader(entry, header, out var headerLength)) continue;
                        if (!ImageHeaderReader.TryGetSize(
                                header.AsSpan(0, headerLength), out var w, out var h)) continue;

                        if (w < MinDimension || h < MinDimension) continue;
                        if (w > MaxDimension || h > MaxDimension) continue;

                        var aspect = (double)w / h;
                        if (aspect < MinAspect || aspect > MaxAspect) continue;

                        var score = ScoreEntry(entry.FullName);
                        var key = Path.GetFileNameWithoutExtension(entry.FullName);

                        if (best.TryGetValue(key, out var existing) && !Beats(score, w * h, existing))
                            continue;

                        best[key] = new Shortlisted
                        {
                            ApkPath = apkPath,
                            EntryPath = entry.FullName,
                            Score = score,
                            Width = w,
                            Height = h
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show($"[ICON] Could not read {apkPath}: {ex.Message}");
                }
            }

            var shortlist = best.Values
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Area)
                .Take(MaxCandidates)
                .ToList();

            Debugger.show($"[ICON] {inspected} rasters inspected, {best.Count} unique, decoding {shortlist.Count}.");

            progress?.Report("Decoding icons...");

            // Decode grouped by archive so each APK is opened once rather than once per
            // icon, then restore the ranked order.
            var decoded = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);

            foreach (var group in shortlist.GroupBy(s => s.ApkPath, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var archive = ZipFile.OpenRead(group.Key);

                    foreach (var item in group)
                    {
                        ct.ThrowIfCancellationRequested();

                        var image = DecodeEntry(archive, item.EntryPath);
                        if (image != null)
                            decoded[item.EntryPath] = image;
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show($"[ICON] Could not decode from {group.Key}: {ex.Message}");
                }
            }

            return shortlist
                .Where(item => decoded.ContainsKey(item.EntryPath))
                .Select(item => new IconCandidate
                {
                    SourceApk = item.ApkPath,
                    EntryPath = item.EntryPath,
                    Image = decoded[item.EntryPath],
                    Width = item.Width,
                    Height = item.Height,
                    Score = item.Score
                })
                .ToList();
        }

        /// <summary>Rank comparison for the density-variant dedupe: score first, then pixels.</summary>
        private static bool Beats(int score, int area, Shortlisted existing)
        {
            if (score != existing.Score) return score > existing.Score;
            return area > existing.Area;
        }

        private sealed class Shortlisted
        {
            public string ApkPath = string.Empty;
            public string EntryPath = string.Empty;
            public int Score;
            public int Width;
            public int Height;
            public int Area => Width * Height;
        }

        private static bool IsRasterResource(string entryPath)
        {
            if (!entryPath.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
                return false;

            return entryPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || entryPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                || entryPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || entryPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads just the leading bytes of a zip entry. The deflate stream is decoded
        /// incrementally, so this stops well before the whole image is inflated.
        /// </summary>
        private static bool TryReadHeader(ZipArchiveEntry entry, byte[] buffer, out int length)
        {
            length = 0;
            try
            {
                using var stream = entry.Open();

                while (length < buffer.Length)
                {
                    var read = stream.Read(buffer, length, buffer.Length - length);
                    if (read <= 0) break;
                    length += read;
                }

                return length >= 24;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Higher is more likely to be the app's launcher icon. Tuned against real APKs:
        /// mipmap dominates because that is where Android requires launcher icons to
        /// live, and the density bonus makes the sharpest copy of each icon win.
        /// </summary>
        private static int ScoreEntry(string entryPath)
        {
            var lower = entryPath.ToLowerInvariant();
            var fileName = Path.GetFileNameWithoutExtension(lower);
            var score = 0;

            if (lower.Contains("mipmap"))
                score += 100;

            if (fileName.Contains("launcher"))
                score += 60;
            else if (fileName.Contains("icon") || fileName.StartsWith("ic_"))
                score += 30;

            // Adaptive-icon background layers are usually a flat colour or gradient, so
            // they rank below the foreground layer of the same icon.
            if (fileName.Contains("background"))
                score -= 25;

            foreach (var (token, points) in DensityRanks)
            {
                if (lower.Contains("-" + token))
                {
                    score += points;
                    break;
                }
            }

            return score;
        }

        private static BitmapSource? DecodeEntry(ZipArchive archive, string entryPath)
        {
            try
            {
                var entry = archive.GetEntry(entryPath);
                if (entry == null) return null;

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();

                if (entryPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    return WebpDecoder.Decode(bytes);

                return DecodeWithWic(bytes);
            }
            catch (Exception ex)
            {
                Debugger.advanced($"[ICON] decode failed for {entryPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Decode via WPF's WIC pipeline. Handles PNG and JPEG.</summary>
        public static BitmapSource? DecodeWithWic(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
