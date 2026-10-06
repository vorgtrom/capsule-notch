using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Capsule
{
    // Capsule's end of the pipe (act-from-notch spec §4): accepts the hooks' connections on a background thread, reads
    // each one's request, and hands it on, still on a worker thread, with the link to answer it through. Only the current
    // Windows user can open the pipe, and the first instance is made with FILE_FLAG_FIRST_PIPE_INSTANCE, so a pipe of that
    // name someone else made first is never served. Requests are held in memory only, and never logged.
    public sealed class PromptServer : IDisposable
    {
        public const int RequestMs = 5000, WriteMs = 5000, DrainMs = 5000;
        const int BufferBytes = 65536;

        readonly string name;
        readonly Action<PromptRequest, PromptLink> arrived;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;
        bool failureLogged;
        readonly List<SafePipeHandle> handles = new List<SafePipeHandle>();   // our instances, waiting or serving, until closed

        // arrived is called on a worker thread, once per well-formed request.
        public PromptServer(string pipeName, Action<PromptRequest, PromptLink> arrived)
        {
            name = pipeName;
            this.arrived = arrived;
        }

        // Makes the pipe and starts accepting. False when the pipe couldn't be made (logged with the Win32 error number),
        // among other reasons because the name is already taken: nothing is held then, and every request goes to Claude
        // as before.
        public bool Start()
        {
            NamedPipeServerStream first = Make(true);
            if (first == null) return false;
            thread = new Thread(delegate() { Accept(first); });
            thread.IsBackground = true;
            thread.Name = "Capsule prompts";
            thread.Start();
            return true;
        }

        // The current user, and no one else, may open it.
        public static PipeSecurity Security()
        {
            var security = new PipeSecurity();
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        // kernel32's CreateNamedPipe: .NET Framework's NamedPipeServerStream has no way to ask for the first-instance flag.
        static class Win32
        {
            public const uint DuplexAccess = 0x3, Overlapped = 0x40000000, FirstInstance = 0x00080000;
            public const uint ByteType = 0x0, ByteRead = 0x0, Wait = 0x0, RejectRemote = 0x8;

            [StructLayout(LayoutKind.Sequential)]
            public struct SecurityAttributes
            {
                public int Length;
                public IntPtr Descriptor;
                public int InheritHandle;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
                uint outBufferSize, uint inBufferSize, uint defaultTimeOut, IntPtr securityAttributes);
        }

        // One server instance of the pipe, for the current user only, remote clients refused. first = true asks for the
        // first instance: it fails, with ERROR_ACCESS_DENIED (5) or ERROR_PIPE_BUSY (231), when the name already exists.
        // Null when it couldn't be made; error is then the Win32 error number (-1 for a failure that wasn't one).
        internal static NamedPipeServerStream CreatePipe(string name, bool first, out int error)
        {
            SafePipeHandle handle;
            return CreatePipe(name, first, out error, out handle);
        }

        // handle is the instance's handle, which reads as closed once the instance is disposed.
        static NamedPipeServerStream CreatePipe(string name, bool first, out int error, out SafePipeHandle made)
        {
            error = 0;
            made = null;
            IntPtr descriptor = IntPtr.Zero, attributes = IntPtr.Zero;
            try
            {
                byte[] binary = Security().GetSecurityDescriptorBinaryForm();
                descriptor = Marshal.AllocHGlobal(binary.Length);
                Marshal.Copy(binary, 0, descriptor, binary.Length);
                var sa = new Win32.SecurityAttributes();
                sa.Length = Marshal.SizeOf(typeof(Win32.SecurityAttributes));
                sa.Descriptor = descriptor;
                sa.InheritHandle = 0;
                attributes = Marshal.AllocHGlobal(sa.Length);
                Marshal.StructureToPtr(sa, attributes, false);
                uint open = Win32.DuplexAccess | Win32.Overlapped | (first ? Win32.FirstInstance : 0);
                uint mode = Win32.ByteType | Win32.ByteRead | Win32.Wait | Win32.RejectRemote;
                SafePipeHandle handle = Win32.CreateNamedPipe(@"\\.\pipe\" + name, open, mode, 255, BufferBytes, BufferBytes, 0, attributes);
                if (handle.IsInvalid)
                {
                    error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    return null;
                }
                try
                {
                    var pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
                    made = handle;
                    return pipe;
                }
                catch (Exception)
                {
                    handle.Dispose();
                    error = -1;
                    return null;
                }
            }
            catch (Exception)
            {
                error = -1;
                return null;
            }
            finally
            {
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (descriptor != IntPtr.Zero) Marshal.FreeHGlobal(descriptor);
            }
        }

        // The next instance. While any instance is alive the name is ours and a plain instance joins it; when none is (the
        // name could go free between two instances), it is made as a first instance again, so a pipe someone else took in
        // the gap is never joined or served: that fails and is retried, and nothing is served meanwhile.
        internal NamedPipeServerStream Next() { return Make(!AnyAlive()); }

        bool AnyAlive()
        {
            lock (handles)
            {
                handles.RemoveAll(delegate(SafePipeHandle h) { return h.IsClosed; });
                return handles.Count > 0;
            }
        }

        // Null when it couldn't be made: logged once, with the error number only.
        NamedPipeServerStream Make(bool first)
        {
            int error;
            SafePipeHandle handle;
            NamedPipeServerStream pipe = CreatePipe(name, first, out error, out handle);
            if (pipe != null) lock (handles) handles.Add(handle);
            if (pipe == null && !failureLogged)
            {
                Log.Error("prompts: pipe couldn't be made (error " + error + ")", null);
                failureLogged = true;
            }
            return pipe;
        }

        // One pipe instance waits at a time. As soon as a hook connects, the next one is made, and only then is the
        // connected one served on a worker thread: several hooks are served at once, and the name is never free. Nothing
        // that goes wrong here may end the thread (an unhandled exception on it would end Capsule).
        void Accept(NamedPipeServerStream pipe)
        {
            while (true)
            {
                try
                {
                    if (pipe == null)
                    {
                        if (stop.WaitOne(1000)) return;   // couldn't be made just now: try again shortly
                        pipe = Next();
                        continue;
                    }
                    bool connected = false;
                    try
                    {
                        IAsyncResult waiting = pipe.BeginWaitForConnection(null, null);
                        if (WaitHandle.WaitAny(new[] { waiting.AsyncWaitHandle, stop }) == 0)
                        {
                            pipe.EndWaitForConnection(waiting);
                            connected = true;
                        }
                    }
                    catch (Exception) { }
                    if (!connected)
                    {
                        if (stop.WaitOne(0))
                        {
                            pipe.Dispose();
                            return;
                        }
                        NamedPipeServerStream replacement = Next();   // before this one goes, so the name is never free
                        pipe.Dispose();
                        pipe = replacement;
                        if (stop.WaitOne(50)) { if (pipe != null) pipe.Dispose(); return; }
                        continue;
                    }
                    NamedPipeServerStream current = pipe;
                    pipe = Next();   // the next one waits before this one is handed off
                    Task.Run(() => Serve(current));
                }
                catch (Exception e)
                {
                    Log.Error("prompts: accepting failed (" + e.GetType().Name + ")", null);
                    if (stop.WaitOne(200)) { if (pipe != null) pipe.Dispose(); return; }
                }
            }
        }

        // A request that isn't one is closed, unanswered; any other failure is answered pass (or closed): nothing here
        // throws.
        void Serve(NamedPipeServerStream pipe)
        {
            PromptLink link = null;
            try
            {
                PromptRequest request = PromptRequest.Parse(PromptPipe.Read(pipe, RequestMs));
                if (request == null)
                {
                    pipe.Dispose();
                    return;
                }
                link = new PromptLink(pipe);
                link.Watch();
                if (stop.WaitOne(0))
                {
                    link.Send(PromptReply.Passing());   // Capsule has stopped: Claude asks as usual, not into silence
                    return;
                }
                arrived(request, link);
            }
            catch (Exception e)
            {
                Log.Error("prompts: a request couldn't be handed on (" + e.GetType().Name + ")", null);
                try
                {
                    if (link != null) link.Send(PromptReply.Passing());
                    else pipe.Dispose();
                }
                catch (Exception) { }
            }
        }

        // Stops accepting. Requests already handed on are released by their holder (PromptBroker.ReleaseAll).
        public void Dispose()
        {
            stop.Set();
            if (thread != null) thread.Join(2000);
        }
    }

    // One hook's connection, from its request to Capsule's reply. Send answers it once; the hook going away first is
    // reported once, through WhenGone.
    public sealed class PromptLink
    {
        readonly NamedPipeServerStream pipe;
        readonly object gate = new object();
        readonly TaskCompletionSource<int> ended = new TaskCompletionSource<int>();   // the hook closed its end of the pipe
        readonly byte[] one = new byte[1];
        bool replied, gone;
        Action onGone;

        internal PromptLink(NamedPipeServerStream pipe) { this.pipe = pipe; }

        public bool IsGone { get { lock (gate) return gone; } }

        // Waits, without blocking, for the hook's end to close: before a reply, that means it has gone. A byte from the
        // hook is not that, and is ignored.
        internal void Watch() { ReadNext(); }

        void ReadNext()
        {
            Task<int> read;
            try { read = pipe.ReadAsync(one, 0, 1); }
            catch (Exception)
            {
                Over();
                return;
            }
            read.ContinueWith(delegate(Task<int> t)
            {
                if (t.Exception != null || t.IsCanceled || t.Result <= 0) Over();   // closed, broken or disposed
                else ReadNext();                                                       // a stray byte: keep watching
            });
        }

        void Over()
        {
            ended.TrySetResult(0);
            Ended();
        }

        void Ended()
        {
            Action call;
            lock (gate)
            {
                if (replied || gone) return;
                gone = true;
                call = onGone;
                onGone = null;
            }
            Close();
            if (call != null) call();
        }

        // action runs once, on a worker thread, if the hook goes away before a reply; at once if it already has.
        public void WhenGone(Action action)
        {
            bool already;
            lock (gate)
            {
                already = gone;
                if (!already) onGone = action;
            }
            if (already && action != null) action();
        }

        // Sends the reply, then closes once the hook has read it, on a worker thread. False when there already was a reply
        // or the hook has gone.
        public bool Send(PromptReply reply)
        {
            lock (gate)
            {
                if (replied || gone) return false;
                replied = true;
            }
            string json = (reply ?? PromptReply.Passing()).ToJson();
            Task.Run(() => Finish(json));
            return true;
        }

        // Writes the reply and waits, for a limit, until the hook has read all of it: closing earlier could cut it short.
        void Finish(string json)
        {
            PromptPipe.Write(pipe, json, PromptServer.WriteMs);
            Task drain = Task.Run(delegate()
            {
                try { pipe.WaitForPipeDrain(); }
                catch (Exception) { }   // the hook has gone, or the pipe was closed
            });
            try
            {
                drain.Wait(PromptServer.DrainMs);
                ended.Task.Wait(100);   // the hook usually closes its end right after reading
            }
            catch (Exception) { }
            Close();
        }

        void Close()
        {
            try { pipe.Dispose(); }
            catch (Exception) { }
        }
    }
}
