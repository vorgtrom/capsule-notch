using System;
using System.Collections.Generic;
using System.IO.Pipes;

namespace Capsule
{
    // The hook's end of the pipe (act-from-notch spec §4): one request, one reply.
    public static class PromptClient
    {
        public const int ConnectMs = 500;          // how long the hook looks for Capsule's pipe before going on without it
        public const int SendMs = 5000;
        public const int ReplyMs = 90 * 1000;      // the 60 s countdown, its restarts within reason, and a margin

        // What the hook does with Capsule for one event: for a PermissionRequest, it asks, and turns the reply into what to
        // print. "" (print nothing, so Claude Code asks as usual) for any other event, with no Capsule, or on any failure.
        // now: the time the hook stamped on the session's status for this event.
        public static string Handle(string input, long now, string pipeName, int connectMs, int replyMs)
        {
            Dictionary<string, object> root = Json.Obj(Json.TryParse(input));
            PromptRequest request = PromptRequest.FromHookInput(root, now);
            if (request == null) return "";
            return HookReply.Output(PromptReply.Parse(Ask(pipeName, request.ToJson(), connectMs, replyMs)), root);
        }

        // Sends the request and waits for the reply: its text, or null when there is no pipe, the pipe's server isn't the
        // real Capsule (nothing is sent then), or anything fails or is late.
        public static string Ask(string pipeName, string request, int connectMs, int replyMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(connectMs);
                    // Before anything is sent: whoever made this pipe instance must be the real Capsule (PipePeer). The
                    // request carries tool inputs, and the reply decides what Claude Code does.
                    if (!PipePeer.IsCapsule(pipe.SafePipeHandle)) return null;
                    if (!PromptPipe.Write(pipe, request, SendMs)) return null;
                    return PromptPipe.Read(pipe, replyMs);
                }
            }
            catch (Exception) { return null; }
        }
    }
}
