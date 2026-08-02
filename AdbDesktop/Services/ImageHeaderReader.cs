using System.Buffers.Binary;

namespace AdbDesktop
{
    /// <summary>
    /// Reads image dimensions straight out of the file header, without decoding pixels.
    ///
    /// This is what makes the icon scan affordable: a real APK pair holds ~1600 raster
    /// entries, and fully decoding all of them to find out which are icon-shaped would
    /// take many seconds. Parsing headers lets the extractor discard ~97% of them first
    /// and decode only the ~60 that survive.
    ///
    /// Deliberately managed-only, including the WebP case, so the gate behaves the same
    /// whether or not libwebp loaded.
    /// </summary>
    internal static class ImageHeaderReader
    {
        /// <summary>Bytes needed at the front of a file for any of the parsers below.</summary>
        public const int HeaderBytes = 1024;

        public static bool TryGetSize(ReadOnlySpan<byte> data, out int width, out int height)
        {
            width = height = 0;

            return TryPng(data, out width, out height)
                || TryWebp(data, out width, out height)
                || TryJpeg(data, out width, out height);
        }

        private static bool TryPng(ReadOnlySpan<byte> d, out int w, out int h)
        {
            w = h = 0;
            if (d.Length < 24) return false;

            // 8-byte signature, then the IHDR chunk whose width/height are at 16 and 20.
            if (d[0] != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G' ||
                d[4] != 0x0D || d[5] != 0x0A || d[6] != 0x1A || d[7] != 0x0A)
                return false;

            w = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
            h = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
            return w > 0 && h > 0;
        }

        private static bool TryWebp(ReadOnlySpan<byte> d, out int w, out int h)
        {
            w = h = 0;
            if (d.Length < 30) return false;

            if (d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return false;
            if (d[8] != 'W' || d[9] != 'E' || d[10] != 'B' || d[11] != 'P') return false;

            // Chunk FourCC at 12 selects the sub-format.
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X')
            {
                // Extended: 24-bit little-endian canvas size minus one, at 24 and 27.
                w = (d[24] | (d[25] << 8) | (d[26] << 16)) + 1;
                h = (d[27] | (d[28] << 8) | (d[29] << 16)) + 1;
                return w > 0 && h > 0;
            }

            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == ' ')
            {
                // Lossy: the VP8 keyframe start code 9D 01 2A precedes 14-bit w/h.
                for (var i = 16; i + 6 < d.Length && i < 64; i++)
                {
                    if (d[i] != 0x9D || d[i + 1] != 0x01 || d[i + 2] != 0x2A) continue;

                    w = (d[i + 3] | (d[i + 4] << 8)) & 0x3FFF;
                    h = (d[i + 5] | (d[i + 6] << 8)) & 0x3FFF;
                    return w > 0 && h > 0;
                }
                return false;
            }

            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'L')
            {
                // Lossless: signature byte 0x2F at 20, then packed 14-bit w-1 / h-1.
                if (d[20] != 0x2F) return false;

                var bits = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
                w = (int)(bits & 0x3FFF) + 1;
                h = (int)((bits >> 14) & 0x3FFF) + 1;
                return w > 0 && h > 0;
            }

            return false;
        }

        private static bool TryJpeg(ReadOnlySpan<byte> d, out int w, out int h)
        {
            w = h = 0;
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;

            var pos = 2;
            while (pos + 9 < d.Length)
            {
                if (d[pos] != 0xFF) { pos++; continue; }

                var marker = d[pos + 1];
                pos += 2;

                // Standalone markers carry no length field.
                if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 ||
                    (marker >= 0xD0 && marker <= 0xD7))
                    continue;

                if (pos + 1 >= d.Length) return false;
                var length = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos, 2));

                // SOF0..SOF15, excluding DHT (C4), JPG (C8) and DAC (CC).
                var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF &&
                                     marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                if (isStartOfFrame && pos + 7 < d.Length)
                {
                    h = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos + 3, 2));
                    w = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos + 5, 2));
                    return w > 0 && h > 0;
                }

                if (length < 2) return false;
                pos += length;
            }

            return false;
        }
    }
}
