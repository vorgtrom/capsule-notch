using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class PromptPipeTests
    {
        public static void Run()
        {
            AMessageGoesThereAndBack();
            TheSizeIsCapped();
            MalformedMessagesReadAsNothing();
            ALateMessageReadsAsNothing();
            TheNameIsTheUsersOrTheTests();
            ABodyThatNeverComesReadsAsNothing();
            AWriteThatCantGoGivesUp();
            AGivenUpTaskIsNeverLeftUnobserved();
            ANegativeLimitIsNoTime();
            APeersBomIsLeftOut();
            TheNameSurvivesAUserWithoutASid();
        }

        // A stream whose reads and writes can be made to never finish: what a stuck peer looks like.
        sealed class Stalling : Stream
        {
            public byte[] Incoming = new byte[0];   // what reads give first
            public readonly TaskCompletionSource<int> ReadHangs = new TaskCompletionSource<int>();
            public readonly TaskCompletionSource<int> WriteHangs = new TaskCompletionSource<int>();
            int position;

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (position >= Incoming.Length) return ReadHangs.Task;
                int n = Math.Min(count, Incoming.Length - position);
                Buffer.BlockCopy(Incoming, position, buffer, offset, n);
                position += n;
                return Task.FromResult(n);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { return WriteHangs.Task; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        // The length arrives and the body never does: nothing, once the time is up. (Not through a real pipe's abandoned
        // read, which would swallow the header.)
        static void ABodyThatNeverComesReadsAsNothing()
        {
            var stuck = new Stalling { Incoming = new byte[] { 50, 0, 0, 0 } };
            var clock = Stopwatch.StartNew();
            string nothing = PromptPipe.Read(stuck, 300);
            long took = clock.ElapsedMilliseconds;
            TestRunner.Check(nothing == null && took >= 250 && took < 2000, "a body that never comes: nothing, after the time limit (" + took + " ms)");
            string name = "capsule-tests-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                Task waiting = server.WaitForConnectionAsync();
                client.Connect(2000);
                waiting.Wait(2000);
                server.Write(new byte[] { 50, 0, 0, 0 }, 0, 4);   // the length is there before the read starts
                server.Flush();
                clock.Restart();
                nothing = PromptPipe.Read(client, 300);
                took = clock.ElapsedMilliseconds;
                TestRunner.Check(nothing == null && took >= 250 && took < 2000, "the same on a real pipe (" + took + " ms)");
            }
        }

        static void AWriteThatCantGoGivesUp()
        {
            var clock = Stopwatch.StartNew();
            bool sent = PromptPipe.Write(new Stalling(), "{\"a\":1}", 300);
            long took = clock.ElapsedMilliseconds;
            TestRunner.Check(!sent && took >= 250 && took < 2000, "a write that never finishes gives up at its time limit (" + took + " ms)");
            string name = "capsule-tests-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                Task waiting = server.WaitForConnectionAsync();
                client.Connect(2000);
                waiting.Wait(2000);
                clock.Restart();
                sent = PromptPipe.Write(server, "\"" + new string('a', 300000) + "\"", 300);   // the client never reads
                took = clock.ElapsedMilliseconds;
                TestRunner.Check(!sent && took >= 250 && took < 3000, "so does one into a pipe nobody reads, more than its buffer holds (" + took + " ms)");
            }
        }

        // A read given up on fails once its pipe is closed; nobody is left holding that failure unseen.
        static void AGivenUpTaskIsNeverLeftUnobserved()
        {
            int unseen = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> count = delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                if (e.Exception.ToString().Contains("sample-abandoned")) Interlocked.Increment(ref unseen);
            };
            TaskScheduler.UnobservedTaskException += count;
            try
            {
                GiveUpAndFail();
                Thread.Sleep(300);
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                TestRunner.Eq(0, unseen, "a read or write given up on, then failing, is never an unobserved task exception");
            }
            finally { TaskScheduler.UnobservedTaskException -= count; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void GiveUpAndFail()
        {
            var stuck = new Stalling();
            PromptPipe.Read(stuck, 100);
            PromptPipe.Write(stuck, "{}", 100);
            stuck.ReadHangs.SetException(new IOException("sample-abandoned read"));
            stuck.WriteHangs.SetException(new IOException("sample-abandoned write"));
        }

        static void ANegativeLimitIsNoTime()
        {
            TestRunner.Check(!PromptPipe.Write(new MemoryStream(), "{}", -1), "a negative write limit never means wait for ever: nothing is written");
            var whole = new MemoryStream();
            PromptPipe.Write(whole, "{}", 1000);
            TestRunner.Check(PromptPipe.Read(new MemoryStream(whole.ToArray()), -1) == null, "nor a negative read limit");
        }

        static void APeersBomIsLeftOut()
        {
            byte[] json = Encoding.UTF8.GetBytes("{}");
            var body = new byte[3 + json.Length];
            body[0] = 0xEF;
            body[1] = 0xBB;
            body[2] = 0xBF;
            Buffer.BlockCopy(json, 0, body, 3, json.Length);
            TestRunner.Eq("{}", ReadBack(Frame(body.Length, body)), "a byte order mark at the start of a peer's message isn't part of it");
        }

        static void TheNameSurvivesAUserWithoutASid()
        {
            string name = null;
            try { name = PromptPipe.NameFor(null); }
            catch (Exception) { }
            TestRunner.Check(name != null && name.StartsWith("capsule-", StringComparison.Ordinal) && name.Length > 8 && name.IndexOf('\\') < 0, "a user with no SID still has a pipe name: " + name);
        }

        static byte[] Frame(int length, byte[] body)
        {
            var frame = new byte[4 + body.Length];
            frame[0] = (byte)length;
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)(length >> 16);
            frame[3] = (byte)(length >> 24);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        static string ReadBack(byte[] bytes) { return PromptPipe.Read(new MemoryStream(bytes), 1000); }

        static void AMessageGoesThereAndBack()
        {
            var stream = new MemoryStream();
            string json = "{\"answer\":\"Café …\"}";
            TestRunner.Check(PromptPipe.Write(stream, json, 1000), "a message is written");
            byte[] bytes = stream.ToArray();
            int length = Encoding.UTF8.GetByteCount(json);
            TestRunner.Eq(4 + length, bytes.Length, "as its length and its UTF-8 bytes");
            TestRunner.Eq(length, bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24), "the length comes first, little-endian");
            TestRunner.Eq(json, ReadBack(bytes), "and reads back as it was, accents and all");
            TestRunner.Check(!PromptPipe.Write(new MemoryStream(), "", 1000), "an empty message isn't written");
        }

        static void TheSizeIsCapped()
        {
            string biggest = "\"" + new string('a', PromptPipe.MaxBytes - 2) + "\"";
            var stream = new MemoryStream();
            TestRunner.Check(PromptPipe.Write(stream, biggest, 5000), "a message of exactly 1 MB is written");
            TestRunner.Eq(biggest, ReadBack(stream.ToArray()), "and read");
            var refused = new MemoryStream();
            TestRunner.Check(!PromptPipe.Write(refused, biggest + " ", 5000), "one byte more isn't written");
            TestRunner.Eq(0L, refused.Length, "not even in part");
            TestRunner.Check(ReadBack(Frame(PromptPipe.MaxBytes + 1, new byte[16])) == null, "a length over 1 MB reads as nothing, whatever follows");
            TestRunner.Check(ReadBack(Frame(-1, new byte[16])) == null, "so does a negative length");
            TestRunner.Check(ReadBack(Frame(0, new byte[0])) == null, "and a length of 0");
        }

        static void MalformedMessagesReadAsNothing()
        {
            TestRunner.Check(ReadBack(new byte[0]) == null, "an empty pipe reads as nothing");
            TestRunner.Check(ReadBack(new byte[] { 5, 0 }) == null, "half a length reads as nothing");
            TestRunner.Check(ReadBack(Frame(10, Encoding.UTF8.GetBytes("{\"a\":"))) == null, "a message cut short reads as nothing");
            TestRunner.Check(ReadBack(Frame(3, new byte[] { 0x7B, 0xFF, 0x7D })) == null, "bytes that aren't UTF-8 read as nothing");
            TestRunner.Eq("{}", ReadBack(Frame(2, Encoding.UTF8.GetBytes("{}x"))), "bytes after the message are left alone");
        }

        // A real pipe whose other end never writes: the read gives up at its time limit. (A body that never comes is
        // ABodyThatNeverComesReadsAsNothing.)
        static void ALateMessageReadsAsNothing()
        {
            string name = "capsule-tests-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                Task waiting = server.WaitForConnectionAsync();
                client.Connect(2000);
                waiting.Wait(2000);
                var clock = Stopwatch.StartNew();
                string nothing = PromptPipe.Read(client, 300);
                long took = clock.ElapsedMilliseconds;
                TestRunner.Check(nothing == null, "nothing arrives: the read reads nothing");
                TestRunner.Check(took >= 250 && took < 2000, "after its time limit (" + took + " ms)");
            }
        }

        static void TheNameIsTheUsersOrTheTests()
        {
            string tests = Environment.GetEnvironmentVariable("CAPSULE_PIPE");
            TestRunner.Check(!string.IsNullOrEmpty(tests) && tests.StartsWith("capsule-tests-", StringComparison.Ordinal), "the tests have a pipe of their own, never the running Capsule's");
            TestRunner.Eq(tests, PromptPipe.Name, "and the pipe's name is theirs");
            try
            {
                Environment.SetEnvironmentVariable("CAPSULE_PIPE", null);
                string mine = PromptPipe.Name;
                TestRunner.Check(mine.StartsWith("capsule-S-1-5-", StringComparison.Ordinal), "otherwise it is capsule- and the user's SID: " + mine);
            }
            finally { Environment.SetEnvironmentVariable("CAPSULE_PIPE", tests); }
        }
    }
}
