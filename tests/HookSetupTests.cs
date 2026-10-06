using System;
using System.IO;

namespace Capsule
{
    public static class HookSetupTests
    {
        const string Exe = "C:\\Tools\\capsule\\bin\\capsule-hook.exe";
        static readonly DateTime When = new DateTime(2026, 9, 29, 17, 30, 0);
        const string Existing = "{\n  \"model\": \"opus\",\n  \"hooks\": {\n    \"Stop\": [\n      { \"hooks\": [ { \"type\": \"command\", \"command\": \"node other.js\" } ] }\n    ]\n  },\n  \"env\": { \"A\": \"1\" }\n}";

        public static void Run()
        {
            ConnectsIntoAMissingFile();
            KeepsOtherSettingsAndHooks();
            ConnectingTwiceAddsNothing();
            ConnectReplacesTheOldHookName();
            DisconnectRemovesOnlyOurs();
            ProbeWiringUsesTheProbeArgument();
            ShellFormQuotesThePath();
            InvalidJsonIsLeftAlone();
            UnreadableSettingsAreUnknown();
            ThePermissionHookMayWait();
            TheMenuOffersToReconnect();
            NeedsUpdateOnlyLooksAtOurHandlers();
            NeedsUpdateChangesNothing();
        }

        // Someone else's PermissionRequest hook with a short timeout is theirs to set, not a reason to reconnect.
        static void NeedsUpdateOnlyLooksAtOurHandlers()
        {
            string foreign = NewSettings("{\"hooks\":{\"PermissionRequest\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":\"node other.js\",\"timeout\":5}]}]}}");
            TestRunner.Check(!HookSetup.NeedsUpdate(foreign), "a foreign PermissionRequest handler with a timeout of 5: nothing to update");
            string mixed = NewSettings("{\"hooks\":{\"PermissionRequest\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":\"node other.js\",\"timeout\":5}," +
                "{\"type\":\"command\",\"command\":\"" + Exe.Replace("\\", "\\\\") + "\",\"timeout\":5}]}]}}");
            TestRunner.Check(HookSetup.NeedsUpdate(mixed), "but ours beside it, with 5, is");
            TestRunner.Check(!HookSetup.NeedsUpdate(NewSettings("{ \"hooks\": ")), "settings that aren't valid JSON: nothing to update");
            TestRunner.Check(!HookSetup.NeedsUpdate(NewSettings("not json at all")), "nor text that isn't JSON");
            TestRunner.Check(!HookSetup.NeedsUpdate(NewSettings("{\"hooks\":{\"PermissionRequest\":\"oops\"}}")), "nor hooks of the wrong shape");
        }

        static void NeedsUpdateChangesNothing()
        {
            string path = NewSettings("{\"hooks\":{\"PermissionRequest\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":\"" + Exe.Replace("\\", "\\\\") + "\",\"timeout\":5}]}]}}");
            byte[] before = File.ReadAllBytes(path);
            TestRunner.Check(HookSetup.NeedsUpdate(path), "an old connection needs an update");
            TestRunner.Check(before.Length == File.ReadAllBytes(path).Length && Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(path)), "asking leaves settings.json exactly as it was");
            TestRunner.Eq(0, Directory.GetFiles(Path.GetDirectoryName(path), "*.capsule-bak-*").Length, "and makes no backup");
            TestRunner.Eq(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length, "nor any other file");
        }

        static void TheMenuOffersToReconnect()
        {
            TestRunner.Eq("Connect to Claude Code", HookSetup.MenuText(false, false), "not connected: Connect");
            TestRunner.Eq("Disconnect from Claude Code", HookSetup.MenuText(true, false), "connected: Disconnect");
            TestRunner.Eq("Reconnect to Claude Code", HookSetup.MenuText(true, true), "connected by an earlier Capsule: Reconnect");
        }

        // A permission prompt's hook waits for the answer from the capsule (act-from-notch spec §4), up to 90 s, so Claude
        // Code must give it longer than the 5 s every other event gets. An earlier Capsule gave it 5 s too.
        static void ThePermissionHookMayWait()
        {
            string path = NewSettings(Existing);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Near(HookSetup.PromptTimeoutSeconds, Json.Num(Json.Get(FirstHandler(path, "PermissionRequest", 0), "timeout")).Value, "the permission hook may wait 120 s");
            TestRunner.Check(HookSetup.PromptTimeoutSeconds * 1000 >= PromptClient.ReplyMs + 30000, "well beyond the 90 s it waits for Capsule");
            TestRunner.Near(5, Json.Num(Json.Get(FirstHandler(path, "Stop", 1), "timeout")).Value, "every other event still gets 5 s");
            TestRunner.Check(!HookSetup.NeedsUpdate(path), "connected now: nothing to update");
            string exe = Exe.Replace("\\", "\\\\");
            string old = NewSettings("{\"hooks\":{\"PermissionRequest\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":\"" + exe + "\",\"args\":[],\"timeout\":5}]}]}}");
            TestRunner.Check(HookSetup.NeedsUpdate(old), "connected by an earlier Capsule: it needs connecting again");
            HookSetup.Connect(old, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Check(!HookSetup.NeedsUpdate(old), "and connecting again is the update");
            string unlimited = NewSettings("{\"hooks\":{\"PermissionRequest\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":\"" + exe + "\",\"args\":[]}]}]}}");
            TestRunner.Check(!HookSetup.NeedsUpdate(unlimited), "no timeout is Claude Code's own 600 s: enough");
            TestRunner.Check(!HookSetup.NeedsUpdate(NewSettings(Existing)), "not connected: nothing to update");
            TestRunner.Check(!HookSetup.NeedsUpdate(NewSettings(null)), "no settings.json: nothing to update");
        }

        static string NewSettings(string content)
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "settings.json");
            if (content != null) File.WriteAllText(path, content);
            return path;
        }

        static object Root(string path) { return Json.Parse(File.ReadAllText(path)); }

        static object FirstHandler(string path, string evt, int entry)
        {
            object[] entries = Json.Arr(Json.Get(Root(path), "hooks", evt));
            return Json.Arr(Json.Get(entries[entry], "hooks"))[0];
        }

        // Handlers whose command contains any of the given names.
        static int CountCommandsContaining(string path, params string[] names)
        {
            var hooks = Json.Obj(Json.Get(Root(path), "hooks"));
            if (hooks == null) return 0;
            int n = 0;
            foreach (var pair in hooks)
                foreach (object entry in Json.Arr(pair.Value))
                    foreach (object handler in Json.Arr(Json.Get(entry, "hooks")))
                    {
                        string command = Json.Str(Json.Get(handler, "command")) ?? "";
                        foreach (string name in names)
                            if (command.Contains(name)) { n++; break; }
                    }
            return n;
        }

        // Ours: the hook exe under its current name, or under the one it had when the app was called UsageNotch.
        static int CountOurs(string path)
        {
            return CountCommandsContaining(path, "capsule-hook.exe", "notch-hook.exe");
        }

        static void ConnectsIntoAMissingFile()
        {
            string path = NewSettings(null);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Eq(HookEvents.Wiring.Length, CountOurs(path), "one handler per wired event");
            TestRunner.Check(HookSetup.IsConnected(path), "reported connected");
            object[] pre = Json.Arr(Json.Get(Root(path), "hooks", "PreToolUse"));
            TestRunner.Eq("AskUserQuestion|ExitPlanMode", Json.Str(Json.Get(pre[0], "matcher")), "matcher written");
            object handler = FirstHandler(path, "PreToolUse", 0);
            TestRunner.Eq(Exe, Json.Str(Json.Get(handler, "command")), "command is the exe path");
            TestRunner.Eq(0, Json.Arr(Json.Get(handler, "args")).Length, "exec form: an empty args array");
            TestRunner.Near(5, Json.Num(Json.Get(handler, "timeout")).Value, "timeout");
            object[] stop = Json.Arr(Json.Get(Root(path), "hooks", "Stop"));
            TestRunner.Check(Json.Get(stop[0], "matcher") == null, "no matcher for Stop");
        }

        static void KeepsOtherSettingsAndHooks()
        {
            string path = NewSettings(Existing);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            var root = Json.Obj(Root(path));
            TestRunner.Eq("model,hooks,env", string.Join(",", root.Keys), "top-level keys and order kept");
            TestRunner.Eq("opus", Json.Str(root["model"]), "other setting kept");
            object[] stop = Json.Arr(Json.Get(root, "hooks", "Stop"));
            TestRunner.Eq(2, stop.Length, "their Stop hook kept, ours added");
            TestRunner.Eq("node other.js", Json.Str(Json.Get(FirstHandler(path, "Stop", 0), "command")), "theirs first");
            TestRunner.Check(File.Exists(path + ".capsule-bak-20260929-173000"), "backup written");
        }

        static void ConnectingTwiceAddsNothing()
        {
            string path = NewSettings(Existing);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Eq(HookEvents.Wiring.Length, CountOurs(path), "still one handler per event");
            TestRunner.Eq(Existing, File.ReadAllText(path + ".capsule-bak-20260929-173000"), "the first backup is the untouched original");
            TestRunner.Check(File.Exists(path + ".capsule-bak-20260929-173000-2"), "a same-second backup gets its own name");
        }

        // settings.json still lists the hook under its name from when the app was called UsageNotch.
        static void ConnectReplacesTheOldHookName()
        {
            string path = NewSettings("{\n  \"hooks\": {\n    \"Stop\": [\n      { \"hooks\": [ { \"type\": \"command\", \"command\": \"C:\\\\old\\\\bin\\\\notch-hook.exe\" } ] }\n    ]\n  }\n}");
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Eq(0, CountCommandsContaining(path, "notch-hook.exe"), "no handler keeps the old hook name");
            TestRunner.Check(HookSetup.IsConnected(path), "reported connected");
        }

        static void DisconnectRemovesOnlyOurs()
        {
            string path = NewSettings(Existing);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            HookSetup.Disconnect(path, When);
            TestRunner.Eq(0, CountOurs(path), "ours gone");
            TestRunner.Check(!HookSetup.IsConnected(path), "reported disconnected");
            var hooks = Json.Obj(Json.Get(Root(path), "hooks"));
            TestRunner.Eq("Stop", string.Join(",", hooks.Keys), "events we emptied are removed; theirs stays");
        }

        static void ProbeWiringUsesTheProbeArgument()
        {
            string path = NewSettings(null);
            HookSetup.Connect(path, Exe, HookEvents.ProbeWiring, new[] { "--probe" }, false, When);
            TestRunner.Eq("--probe", Json.Str(Json.Arr(Json.Get(FirstHandler(path, "PreToolUse", 0), "args"))[0]), "probe argument");
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Eq(HookEvents.Wiring.Length, CountOurs(path), "a normal connect replaces the probe entries");
        }

        static void ShellFormQuotesThePath()
        {
            string path = NewSettings(null);
            HookSetup.Connect(path, "C:\\Some Folder\\bin\\capsule-hook.exe", HookEvents.Wiring, new[] { "--probe" }, true, When);
            object handler = FirstHandler(path, "Stop", 0);
            TestRunner.Eq("\"C:/Some Folder/bin/capsule-hook.exe\" --probe", Json.Str(Json.Get(handler, "command")), "quoted, forward slashes, arguments after");
            TestRunner.Check(Json.Get(handler, "args") == null, "no args key in shell form");
            TestRunner.Check(HookSetup.IsConnected(path), "still recognised as ours");
        }

        static void InvalidJsonIsLeftAlone()
        {
            string path = NewSettings("{ \"model\": ");
            bool threw = false;
            try { HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When); }
            catch (InvalidOperationException) { threw = true; }
            TestRunner.Check(threw, "refuses to edit invalid JSON");
            TestRunner.Eq("{ \"model\": ", File.ReadAllText(path), "file unchanged");
            TestRunner.Check(!HookSetup.IsConnected(Path.Combine(TestRunner.NewTempDir(), "none.json")), "missing file is not connected");
        }

        static void UnreadableSettingsAreUnknown()
        {
            TestRunner.Eq(false, HookSetup.ConnectedState(NewSettings(null)), "no settings.json: not connected");
            TestRunner.Eq(null, HookSetup.ConnectedState(NewSettings("{ \"hooks\": ")), "half-written settings.json: unknown");
            string path = NewSettings(null);
            HookSetup.Connect(path, Exe, HookEvents.Wiring, new string[0], false, When);
            TestRunner.Eq(true, HookSetup.ConnectedState(path), "connected");
        }
    }
}
