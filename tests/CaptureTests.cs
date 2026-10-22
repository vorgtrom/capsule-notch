using System;

namespace Capsule
{
    public static class CaptureTests
    {
        public static void Run()
        {
            KnownRows();
            RefusedJoins();
            FixedBands();
            Validation();
            Ownership();
            BoundedStorage();
            Limits();
        }

        static CaptureFrame Page(int width, int start, int rows)
        {
            byte[] pixels = new byte[width * rows * 4];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < width; x++)
                {
                    uint value = unchecked((uint)((start + y + 1) * 2654435761L) ^ (uint)((x + 1) * 2246822519L));
                    int p = (y * width + x) * 4;
                    pixels[p] = (byte)value;
                    pixels[p + 1] = (byte)(value >> 8);
                    pixels[p + 2] = (byte)(value >> 16);
                    pixels[p + 3] = (byte)(value >> 24);
                }
            return new CaptureFrame(width, rows, pixels);
        }

        static void Same(CaptureFrame expected, CaptureFrame actual, string name)
        {
            TestRunner.Eq(expected.Width, actual.Width, name + " width");
            TestRunner.Eq(expected.Height, actual.Height, name + " rows");
            bool same = expected.Pixels.Length == actual.Pixels.Length;
            for (int i = 0; same && i < expected.Pixels.Length; i++) same = expected.Pixels[i] == actual.Pixels[i];
            TestRunner.Check(same, name + " preserves every BGRA byte");
            expected.Dispose();
            actual.Dispose();
        }

        static void KnownRows()
        {
            using (var stitcher = new CaptureStitcher(Page(30, 0, 120)))
            {
                StitchResult next = stitcher.Append(Page(30, 60, 120));
                TestRunner.Check(next.Matched && !next.Unchanged, "distinct overlap matches");
                TestRunner.Eq(60, next.AddedRows, "60 rows are new");
                Same(Page(30, 0, 180), stitcher.Build(), "known overlap");
            }
            using (var stitcher = new CaptureStitcher(Page(30, 0, 120)))
            {
                stitcher.Append(Page(30, 60, 120));
                TestRunner.Eq(7, stitcher.Append(Page(30, 67, 120)).AddedRows, "partial final scroll appends seven rows");
                Same(Page(30, 0, 187), stitcher.Build(), "partial final scroll");
            }
            using (var stitcher = new CaptureStitcher(Page(30, 0, 120)))
            using (var next = Page(30, 0, 120))
            {
                StitchResult result = stitcher.Append(next);
                TestRunner.Check(result.Unchanged && result.AddedRows == 0, "identical frame signals no movement");
                Same(Page(30, 0, 120), stitcher.Build(), "unchanged frame");
            }
            var first = Page(30, 0, 120);
            var second = Page(30, 60, 120);
            var expected = Page(30, 0, 180);
            RepeatLeft(first, 0);
            RepeatLeft(second, 60);
            RepeatLeft(expected, 0);
            using (var stitcher = new CaptureStitcher(first))
            {
                TestRunner.Eq(60, stitcher.Append(second).AddedRows, "two distinct regions establish a join when the third repeats");
                Same(expected, stitcher.Build(), "two independent regions");
            }
        }

        static void RepeatLeft(CaptureFrame frame, int start)
        {
            using (var pattern = Page(30, 0, 10))
                for (int y = 0; y < frame.Height; y++)
                    Buffer.BlockCopy(pattern.Pixels, ((start + y) % 10) * 120, frame.Pixels, y * 120, 40);
        }

        static void Refused(CaptureFrame first, CaptureFrame next, string name)
        {
            var expected = new CaptureFrame(first.Width, first.Height, (byte[])first.Pixels.Clone());
            using (next)
            using (var stitcher = new CaptureStitcher(first))
            {
                StitchResult result = stitcher.Append(next);
                TestRunner.Check(!result.Matched && !result.Unchanged && result.AddedRows == 0, name + " refuses the seam");
                TestRunner.Check(!String.IsNullOrEmpty(result.Notice), name + " explains the refusal");
                Same(expected, stitcher.Build(), name + " leaves verified output unchanged");
                TestRunner.Check(next.Pixels != null, name + " leaves the rejected frame with its caller");
            }
        }

        static void RefusedJoins()
        {
            Refused(Page(30, 0, 120), Page(31, 60, 120), "changed width");
            Refused(Page(30, 0, 120), Page(30, 60, 121), "changed height");
            Refused(Page(30, 0, 120), Page(30, 500, 120), "missing overlap");
            var repeated = Page(30, 0, 120);
            var repeatedNext = Page(30, 0, 120);
            for (int y = 0; y < 120; y++)
            {
                Buffer.BlockCopy(repeated.Pixels, (y % 10) * 120, repeated.Pixels, y * 120, 120);
                Buffer.BlockCopy(repeated.Pixels, ((y + 3) % 10) * 120, repeatedNext.Pixels, y * 120, 120);
            }
            Refused(repeated, repeatedNext, "repeated rows");
            repeated = Page(30, 0, 120);
            repeatedNext = Page(30, 0, 120);
            using (var pattern = Page(30, 0, 10))
                for (int y = 0; y < 120; y++)
                {
                    Buffer.BlockCopy(pattern.Pixels, (y % 10) * 120, repeated.Pixels, y * 120, 120);
                    Buffer.BlockCopy(pattern.Pixels, ((y + 63) % 10) * 120, repeatedNext.Pixels, y * 120, 120);
                }
            repeated.Pixels[(115 * 30 + 15) * 4] ^= 127;
            repeatedNext.Pixels[(52 * 30 + 15) * 4] ^= 127;
            Refused(repeated, repeatedNext, "one marker in otherwise repeated horizontal regions");
            var shifted = Page(30, 60, 120);
            for (int y = 0; y < 120; y++)
                Buffer.BlockCopy(shifted.Pixels, y * 120 + 4, shifted.Pixels, y * 120, 116);
            Refused(Page(30, 0, 120), shifted, "horizontal displacement");
            var animated = Page(30, 60, 120);
            for (int y = 15; y < 35; y++) animated.Pixels[(y * 30 + 15) * 4] ^= 127;
            Refused(Page(30, 0, 120), animated, "animated overlap patch");

            var blank = new CaptureFrame(30, 120, new byte[14400]);
            var mostlyBlank = new CaptureFrame(30, 120, new byte[14400]);
            mostlyBlank.Pixels[80 * 120] = 1;
            Refused(blank, mostlyBlank, "mostly blank content");
        }

        static CaptureFrame Bands(int start, int header, int footer)
        {
            var frame = Page(30, start, 120);
            using (var top = Page(30, 50000, header + 1))
                Buffer.BlockCopy(top.Pixels, 0, frame.Pixels, 0, header * 120);
            using (var bottom = Page(30, 60000, footer + 1))
                Buffer.BlockCopy(bottom.Pixels, 0, frame.Pixels, (120 - footer) * 120, footer * 120);
            return frame;
        }

        static void FixedBands()
        {
            foreach (int header in new int[] { 0, 9 })
                foreach (int footer in new int[] { 0, 11 })
                {
                    using (var stitcher = new CaptureStitcher(Bands(0, header, footer)))
                    {
                        TestRunner.Eq(60, stitcher.Append(Bands(60, header, footer)).AddedRows, "fixed bands preserve scroll distance");
                        TestRunner.Eq(7, stitcher.Append(Bands(67, header, footer)).AddedRows, "fixed bands preserve final short scroll");
                        var expected = Page(30, 0, 187);
                        using (var first = Bands(0, header, footer))
                            Buffer.BlockCopy(first.Pixels, 0, expected.Pixels, 0, header * 120);
                        using (var last = Bands(67, header, footer))
                            Buffer.BlockCopy(last.Pixels, (120 - footer) * 120, expected.Pixels, (187 - footer) * 120, footer * 120);
                        Same(expected, stitcher.Build(), "fixed bands appear once");
                    }
                }
            using (var stitcher = new CaptureStitcher(Bands(0, 9, 11)))
            using (var changed = Bands(67, 9, 11))
            {
                stitcher.Append(Bands(60, 9, 11));
                changed.Pixels[changed.Pixels.Length - 4] ^= 127;
                TestRunner.Check(!stitcher.Append(changed).Matched, "changed fixed footer is refused");
                var expected = Page(30, 0, 180);
                using (var first = Bands(0, 9, 11))
                    Buffer.BlockCopy(first.Pixels, 0, expected.Pixels, 0, 9 * 120);
                using (var last = Bands(60, 9, 11))
                    Buffer.BlockCopy(last.Pixels, 109 * 120, expected.Pixels, 169 * 120, 11 * 120);
                Same(expected, stitcher.Build(), "refusal after a verified join");
                TestRunner.Check(changed.Pixels != null, "changed footer frame stays with caller");
            }
        }

        static void Throws<T>(Action action, string name) where T : Exception
        {
            bool threw = false;
            try { action(); }
            catch (T) { threw = true; }
            TestRunner.Check(threw, name);
        }

        static void Validation()
        {
            Throws<ArgumentException>(delegate { new CaptureFrame(0, 10, new byte[0]); }, "zero width refused");
            Throws<ArgumentException>(delegate { new CaptureFrame(10, -1, new byte[0]); }, "negative rows refused");
            Throws<ArgumentException>(delegate { new CaptureFrame(Int32.MaxValue, Int32.MaxValue, new byte[0]); }, "dimension overflow refused");
            Throws<ArgumentException>(delegate { new CaptureFrame(2, 2, new byte[15]); }, "short pixel buffer refused");
            Throws<ArgumentException>(delegate { new CaptureFrame(2, 2, new byte[17]); }, "padded pixel buffer refused");
            Throws<ArgumentNullException>(delegate { new CaptureFrame(2, 2, null); }, "null buffer refused");
            Throws<ArgumentException>(delegate { new CaptureFrame(1, 32001, new byte[0]); }, "source row limit checked before buffer");
            Throws<ArgumentException>(delegate { new CaptureFrame(2000, 16001, new byte[0]); }, "source pixel limit checked before buffer");
            Throws<ArgumentNullException>(delegate { new CaptureStitcher(null); }, "null first frame refused");
            using (var stitcher = new CaptureStitcher(Page(3, 0, 20)))
            using (var disposedNext = Page(3, 10, 20))
            {
                Throws<ArgumentNullException>(delegate { stitcher.Append(null); }, "null append refused");
                disposedNext.Dispose();
                Throws<ObjectDisposedException>(delegate { stitcher.Append(disposedNext); }, "disposed append refused");
            }
        }

        static void Ownership()
        {
            var first = Page(3, 0, 20);
            var next = Page(3, 10, 20);
            var stitcher = new CaptureStitcher(first);
            stitcher.Append(next);
            CaptureFrame result = stitcher.Build();
            TestRunner.Check(first.Pixels == null && next.Pixels == null, "Build releases accepted source buffers");
            stitcher.Dispose();
            Same(Page(3, 0, 30), result, "Build transfers an independent result to its caller");
            Throws<ObjectDisposedException>(delegate { stitcher.Build(); }, "finished stitcher cannot build again");
            first = Page(3, 0, 20);
            stitcher = new CaptureStitcher(first);
            stitcher.Dispose();
            stitcher.Dispose();
            TestRunner.Check(first.Pixels == null, "Dispose releases owned source buffers and is idempotent");
            Throws<ObjectDisposedException>(delegate { stitcher.Append(null); }, "disposed stitcher rejects appends");
            var disposed = Page(3, 0, 20);
            disposed.Dispose();
            Throws<ObjectDisposedException>(delegate { new CaptureStitcher(disposed); }, "disposed first source refused");
        }

        static void Limits()
        {
            using (var stitcher = new CaptureStitcher(Page(3, 0, 20)))
            {
                for (int i = 1; i < 120; i++)
                    TestRunner.Eq(1, stitcher.Append(Page(3, i, 20)).AddedRows, "accepted frame " + (i + 1));
                using (var extra = Page(3, 120, 20))
                {
                    StitchResult refused = stitcher.Append(extra);
                    TestRunner.Check(!refused.Matched && refused.Notice.Length > 0, "frame 121 is refused");
                }
                Same(Page(3, 0, 139), stitcher.Build(), "frame limit preserves 120 frames");
            }
            using (var stitcher = new CaptureStitcher(Page(1, 0, 20000)))
            {
                TestRunner.Eq(12000, stitcher.Append(Page(1, 12000, 20000)).AddedRows, "32000 output rows accepted");
                using (var extra = Page(1, 12001, 20000))
                    TestRunner.Check(!stitcher.Append(extra).Matched, "row 32001 refused");
                Same(Page(1, 0, 32000), stitcher.Build(), "row limit preserves output");
            }
            using (var stitcher = new CaptureStitcher(Page(2000, 0, 10000)))
            {
                TestRunner.Eq(6000, stitcher.Append(Page(2000, 6000, 10000)).AddedRows, "32 million output pixels accepted");
                using (var extra = Page(2000, 6001, 10000))
                    TestRunner.Check(!stitcher.Append(extra).Matched, "output beyond 32 million pixels refused");
                using (var result = stitcher.Build())
                    TestRunner.Eq(16000, result.Height, "pixel limit preserves accepted rows");
            }
        }

        static void BoundedStorage()
        {
            var previous = Page(30, 0, 1000);
            bool released = true, bounded = true;
            using (var stitcher = new CaptureStitcher(previous))
            {
                for (int i = 1; i < 120; i++)
                {
                    var next = Page(30, i, 1000);
                    stitcher.Append(next);
                    released &= previous.Pixels == null;
                    bounded &= RetainedBytes(stitcher) <= (long)(1000 + i + 1000) * 30 * 4;
                    previous = next;
                }
                TestRunner.Check(released, "one-row scrolls release older full source buffers");
                TestRunner.Check(bounded, "retained pixels stay within output plus one viewport");
                Same(Page(30, 0, 1119), stitcher.Build(), "bounded storage keeps exact output");
            }
        }

        static long RetainedBytes(CaptureStitcher stitcher)
        {
            // Inspect owned buffers without adding test-only memory counters to production code.
            var field = typeof(CaptureStitcher).GetField("parts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var parts = (System.Collections.IEnumerable)field.GetValue(stitcher);
            long bytes = 0;
            foreach (object part in parts)
            {
                var frame = (CaptureFrame)part.GetType().GetField("Frame").GetValue(part);
                bytes += frame.Pixels.LongLength;
            }
            return bytes;
        }
    }
}
