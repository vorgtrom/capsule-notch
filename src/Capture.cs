using System;
using System.Collections.Generic;

namespace Capsule
{
    public static class CaptureLimits
    {
        public const int MaxPixels = 32000000;
        public const int MaxRows = 32000;
        public const int MaxFrames = 120;
        public const int MaxDurationMilliseconds = 90000;
        public const int ClipboardMaxPixels = 16000000;
        public const int ClipboardMaxPngBytes = 8 * 1024 * 1024;
        public const int ClipboardTotalPngBytes = 32 * 1024 * 1024;
        public const int ClipboardMaxImages = 5;
    }

    public sealed class CaptureFrame : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Pixels { get; private set; }

        // Takes ownership of tightly packed BGRA32 pixels. Dispose releases the buffer reference.
        public CaptureFrame(int width, int height, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || height > CaptureLimits.MaxRows ||
                (long)width * height > CaptureLimits.MaxPixels)
                throw new ArgumentException("Capture dimensions exceed the image limits.");
            if (pixels == null) throw new ArgumentNullException("pixels");
            if (pixels.LongLength != (long)width * height * 4)
                throw new ArgumentException("Capture pixels must contain tightly packed BGRA32 rows.", "pixels");
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public void Dispose() { Pixels = null; }
    }

    public sealed class StitchResult
    {
        public readonly int AddedRows;
        public readonly bool Matched;
        public readonly bool Unchanged;
        public readonly string Notice;

        internal StitchResult(int rows, bool matched, bool unchanged, string notice)
        {
            AddedRows = rows;
            Matched = matched;
            Unchanged = unchanged;
            Notice = notice;
        }
    }

    public sealed class CaptureStitcher : IDisposable
    {
        sealed class Part
        {
            public CaptureFrame Frame;
            public int Start, Rows;
        }

        const int MinimumOverlap = 8;
        readonly List<Part> parts = new List<Part>();
        readonly int width, height, regions;
        CaptureFrame previous;
        ulong[] previousHashes;
        int rows, header, footer;
        bool bandsKnown, closed;

        // Takes ownership of first. A successful Append transfers its input and releases older full buffers.
        public CaptureStitcher(CaptureFrame first)
        {
            ValidateFrame(first, "first");
            width = first.Width;
            height = first.Height;
            regions = Math.Min(3, width);
            previousHashes = HashRows(first);
            previous = first;
            rows = height;
            parts.Add(new Part { Frame = first, Start = 0, Rows = height });
        }

        public StitchResult Append(CaptureFrame next)
        {
            EnsureOpen();
            ValidateFrame(next, "next");
            if (next.Width != width || next.Height != height)
                return Refused("The capture dimensions changed.");

            ulong[] hashes = HashRows(next);
            int top = 0, bottom = 0;
            while (top < height && SameRow(next, hashes, top, top)) top++;
            if (top == height) return new StitchResult(0, true, true, "The page did not change.");
            while (bottom < height - top && SameRow(next, hashes, height - bottom - 1, height - bottom - 1)) bottom++;
            if (bandsKnown)
            {
                if (top < header || bottom < footer) return Refused("A fixed page band changed.");
                top = header;
                bottom = footer;
            }

            int contentRows = height - top - bottom;
            var shifts = new int[regions];
            for (int region = 0; region < regions; region++)
                shifts[region] = FindOverlap(previousHashes, hashes, top, contentRows, regions, region);
            int matchedShift = 0, votes = 0;
            for (int region = 0; region < regions; region++)
            {
                if (shifts[region] <= 0) continue;
                if (matchedShift != 0 && matchedShift != shifts[region])
                    return Refused("The page overlap is repeated or ambiguous.");
                matchedShift = shifts[region];
                votes++;
            }
            if (votes < Math.Min(2, regions)) return Refused("The page overlap could not be verified.");
            for (int y = 0; y < contentRows - matchedShift; y++)
                if (!SameRow(next, hashes, top + matchedShift + y, top + y))
                    return Refused("The page overlap changed.");
            if (parts.Count >= CaptureLimits.MaxFrames) return Refused("The capture reached the frame limit.");
            long nextRows = (long)rows + matchedShift;
            if (nextRows > CaptureLimits.MaxRows) return Refused("The capture reached the row limit.");
            if (nextRows * width > CaptureLimits.MaxPixels) return Refused("The capture reached the pixel limit.");

            Part last = parts[parts.Count - 1];
            int keepRows = last.Rows - (bandsKnown ? 0 : bottom);
            byte[] retained = new byte[keepRows * width * 4];
            Buffer.BlockCopy(last.Frame.Pixels, last.Start * width * 4, retained, 0, retained.Length);
            var strip = new CaptureFrame(width, keepRows, retained);
            try { parts.Add(new Part { Frame = next, Start = height - bottom - matchedShift, Rows = matchedShift }); }
            catch { strip.Dispose(); throw; }
            last.Frame.Dispose();
            last.Frame = strip;
            last.Start = 0;
            last.Rows = keepRows;
            header = top;
            footer = bottom;
            bandsKnown = true;
            previous = next;
            previousHashes = hashes;
            rows = (int)nextRows;
            return new StitchResult(matchedShift, true, false, "");
        }

        // Returns an independent buffer owned by the caller, then releases all accepted sources.
        public CaptureFrame Build()
        {
            EnsureOpen();
            byte[] pixels = new byte[checked(width * rows * 4)];
            int offset = 0;
            foreach (Part part in parts)
            {
                int bytes = part.Rows * width * 4;
                Buffer.BlockCopy(part.Frame.Pixels, part.Start * width * 4, pixels, offset, bytes);
                offset += bytes;
            }
            if (footer > 0)
                Buffer.BlockCopy(previous.Pixels, (height - footer) * width * 4, pixels, offset, footer * width * 4);
            var result = new CaptureFrame(width, rows, pixels);
            Dispose();
            return result;
        }

        public void Dispose()
        {
            foreach (Part part in parts) part.Frame.Dispose();
            parts.Clear();
            previous = null;
            previousHashes = null;
            closed = true;
        }

        void EnsureOpen()
        {
            if (closed) throw new ObjectDisposedException("CaptureStitcher");
        }

        static void ValidateFrame(CaptureFrame frame, string name)
        {
            if (frame == null) throw new ArgumentNullException(name);
            if (frame.Pixels == null) throw new ObjectDisposedException(name);
        }

        static StitchResult Refused(string notice) { return new StitchResult(0, false, false, notice); }

        ulong[] HashRows(CaptureFrame frame)
        {
            var hashes = new ulong[height * regions];
            for (int y = 0; y < height; y++)
                for (int region = 0; region < regions; region++)
                {
                    int start = (y * width + width * region / regions) * 4;
                    int end = (y * width + width * (region + 1) / regions) * 4;
                    ulong hash = 14695981039346656037UL;
                    for (int i = start; i < end; i++) hash = unchecked((hash ^ frame.Pixels[i]) * 1099511628211UL);
                    hashes[y * regions + region] = hash;
                }
            return hashes;
        }

        bool SameRow(CaptureFrame next, ulong[] hashes, int oldRow, int newRow)
        {
            for (int region = 0; region < regions; region++)
                if (previousHashes[oldRow * regions + region] != hashes[newRow * regions + region]) return false;
            int oldStart = oldRow * width * 4, newStart = newRow * width * 4;
            for (int i = 0; i < width * 4; i++)
                if (previous.Pixels[oldStart + i] != next.Pixels[newStart + i]) return false;
            return true;
        }

        internal static int FindOverlap(IList<ulong> previousRows, IList<ulong> nextRows, int start, int count, int stride, int region)
        {
            if (count <= MinimumOverlap) return 0;
            // shortcut: sparse overlaps stop; revise only with fixtures that prove a safer confidence rule.
            var distinct = new HashSet<ulong>();
            int detailRows = 0;
            while (detailRows < count && distinct.Count < 4)
                distinct.Add(nextRows[(start + detailRows++) * stride + region]);
            if (distinct.Count < 4) return 0;

            // Prefix links skip already compared rows, including long repeated runs.
            var prefix = new int[count];
            for (int y = 1; y < count; y++)
            {
                int length = prefix[y - 1];
                ulong value = nextRows[(start + y) * stride + region];
                while (length > 0 && value != nextRows[(start + length) * stride + region]) length = prefix[length - 1];
                if (value == nextRows[(start + length) * stride + region]) length++;
                prefix[y] = length;
            }
            int overlap = 0;
            for (int y = 1; y < count; y++)
            {
                ulong value = previousRows[(start + y) * stride + region];
                while (overlap > 0 && value != nextRows[(start + overlap) * stride + region]) overlap = prefix[overlap - 1];
                if (value == nextRows[(start + overlap) * stride + region]) overlap++;
            }
            int minimum = Math.Max(MinimumOverlap, detailRows);
            if (overlap < minimum) return 0;
            return prefix[overlap - 1] >= minimum ? -1 : count - overlap;
        }
    }
}
