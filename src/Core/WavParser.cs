using System;

namespace Launcher.Core
{
    internal readonly struct WavInfo
    {
        public readonly int SampleRate;
        public readonly int Channels;
        public readonly int BitsPerSample;
        public readonly int DataOffset;
        public readonly int DataLength;

        public WavInfo(int sampleRate, int channels, int bitsPerSample, int dataOffset, int dataLength)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            DataOffset = dataOffset;
            DataLength = dataLength;
        }
    }

    /// <summary>
    /// Minimal RIFF/WAVE PCM header parser. Pure managed code so it can be unit-tested on the desktop.
    /// </summary>
    internal static class WavParser
    {
        public static bool TryParse(byte[] bytes, out WavInfo info)
        {
            info = default;
            if (bytes == null || bytes.Length < 44) return false;
            if (!MatchTag(bytes, 0, "RIFF") || !MatchTag(bytes, 8, "WAVE")) return false;

            int pos = 12;
            bool hasFmt = false;
            int audioFormat = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;

            while (pos + 8 <= bytes.Length)
            {
                int chunkSize = ReadInt32LE(bytes, pos + 4);
                if (chunkSize < 0) return false;
                int dataStart = pos + 8;
                if (dataStart > bytes.Length) return false;

                if (MatchTag(bytes, pos, "fmt ") && chunkSize >= 16 && dataStart + 16 <= bytes.Length)
                {
                    audioFormat = ReadUInt16LE(bytes, dataStart);
                    channels = ReadUInt16LE(bytes, dataStart + 2);
                    sampleRate = ReadInt32LE(bytes, dataStart + 4);
                    bitsPerSample = ReadUInt16LE(bytes, dataStart + 14);
                    hasFmt = true;
                }
                else if (MatchTag(bytes, pos, "data"))
                {
                    if (!hasFmt || audioFormat != 1 || channels <= 0 || sampleRate <= 0 || bitsPerSample != 16)
                        return false;
                    int available = Math.Min(chunkSize, bytes.Length - dataStart);
                    if (available <= 0) return false;
                    info = new WavInfo(sampleRate, channels, bitsPerSample, dataStart, available);
                    return true;
                }

                // RIFF chunks are word-aligned (padded to even byte boundaries).
                int padded = chunkSize + (chunkSize & 1);
                if (padded < 0 || dataStart + padded < dataStart) return false;
                pos = dataStart + padded;
            }

            return false;
        }

        private static bool MatchTag(byte[] b, int offset, string tag)
        {
            return offset + 4 <= b.Length
                && b[offset] == (byte)tag[0]
                && b[offset + 1] == (byte)tag[1]
                && b[offset + 2] == (byte)tag[2]
                && b[offset + 3] == (byte)tag[3];
        }

        private static int ReadUInt16LE(byte[] b, int offset)
        {
            return b[offset] | (b[offset + 1] << 8);
        }

        private static int ReadInt32LE(byte[] b, int offset)
        {
            return b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);
        }
    }
}
