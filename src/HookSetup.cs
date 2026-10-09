using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Capsule
{
    // Adds and removes Capsule's hook entries in Claude Code's settings.json. A handler is ours when its
    // command mentions capsule-hook.exe (or the hook's old name, see IsOurs); nothing else in the file is ever
    // changed, and a backup is written first.
    public static class HookSetup
    {
        const string HookExeName = "capsule-hook.exe";

        // false: exec form ("command" = exe path, "args" = []), spawned with no shell.
        // Task 4 switches this to true only if exec-form hooks turn out not to run in desktop sessions.
        public static readonly bool UseShellForm = false;

        // true or false from settings.json, or null when it exists but can't be read or parsed right now (Claude Code
        // may be rewriting it), so a caller can keep its last answer.
        public static bool? ConnectedState(string settingsPath)
        {
            object root = Json.TryParse(Files.ReadText(settingsPath));
            if (root == null) return File.Exists(settingsPath) ? (bool?)null : false;
            var hooks = Json.Obj(Json.Get(root, "hooks"));
            if (hooks == null) return false;
            foreach (var pair in hooks)
                foreach (object entry in Json.Arr(pair.Value) ?? new object[0])
                    foreach (object handler in Json.Arr(Json.Get(entry, "hooks")) ?? new object[0])
                        if (IsOurs(handler)) return true;
            return false;
        }

        public static bool IsConnected(string settingsPath) { return ConnectedState(settingsPath) == true; }

        // How long Claude Code lets the PermissionRequest hook run: it waits up to 90 s for an answer from the capsule
        // (act-from-notch spec §4). Every other event's hook is done at once and gets 5 s.
        public const int PromptTimeoutSeconds = 120;

        // Connected by an earlier Capsule, which gave the PermissionRequest hook 5 s like every other: Claude Code would stop
        // it long before you could answer from the capsule. Connecting again (Connect replaces our entries) updates it.
        // False when not connected, or when settings.json can't be read.
        public static bool NeedsUpdate(string settingsPath)
        {
            object root = Json.TryParse(Files.ReadText(settingsPath));
            foreach (object entry in Json.Arr(Json.Get(root, "hooks", HookEvents.PermissionRequest)) ?? new object[0])
                foreach (object handler in Json.Arr(Json.Get(entry, "hooks")) ?? new object[0])
                {
                    if (!IsOurs(handler)) continue;
                    double? timeout = Json.Num(Json.Get(handler, "timeout"));
                    if (timeout.HasValue && timeout.Value < PromptTimeoutSeconds) return true;   // none: Claude Code's own 600 s
                }
            return false;
        }

        // The tray menu's item for the hooks: Reconnect over an earlier Capsule's entries.
        public static string MenuText(bool connected, bool needsUpdate)
        {
            if (!connected) return "Connect to Claude Code";
            return needsUpdate ? "Reconnect to Claude Code" : "Disconnect from Claude Code";
        }

        public static string Connect(string settingsPath, string hookExePath, string[][] wiring, string[] args, bool shellForm, DateTime now)
        {
            Dictionary<string, object> root = LoadForEdit(settingsPath);
            Dictionary<string, object> hooks = Json.Obj(Json.Get(root, "hooks"));
            if (hooks == null)
            {
                hooks = new Dictionary<string, object>();
                root["hooks"] = hooks;
            }
            Backup(settingsPath, now);
            int replaced = RemoveOurs(hooks);
            foreach (string[] wire in wiring)
            {
                var handler = new Dictionary<string, object>();
                handler["type"] = "command";
                if (Array.IndexOf(args, "--codex") >= 0)
                {
                    handler["command"] = CodexHook.Command(hookExePath);
                }
                else if (shellForm)
                {
                    handler["command"] = "\"" + hookExePath.Replace('\\', '/') + "\"" + (args.Length > 0 ? " " + string.Join(" ", args) : "");
                }
                else
                {
                    handler["command"] = hookExePath;
                    handler["args"] = args;
                }
                handler["timeout"] = wire[0] == HookEvents.PermissionRequest || Array.IndexOf(args, "--codex") >= 0 ? PromptTimeoutSeconds : 5;
                var entry = new Dictionary<string, object>();
                if (wire[1] != null) entry["matcher"] = wire[1];
                entry["hooks"] = new object[] { handler };
                var list = new List<object>(Json.Arr(Json.Get(hooks, wire[0])) ?? new object[0]);
                list.Add(entry);
                hooks[wire[0]] = list.ToArray();
            }
            Save(settingsPath, root);
            return "connected " + wiring.Length + " hook events (replaced " + replaced + " old entries)";
        }

        public static string ConnectCodex(string settingsPath, string hookExePath, DateTime now)
        {
            CodexHook.Command(hookExePath); // Validate before touching settings or making a backup.
            return Connect(settingsPath, hookExePath, CodexHook.Wiring, new[] { "--codex" }, true, now);
        }

        public static string Disconnect(string settingsPath, DateTime now)
        {
            if (!File.Exists(settingsPath)) return "no settings.json; nothing to remove";
            Dictionary<string, object> root = LoadForEdit(settingsPath);
            Dictionary<string, object> hooks = Json.Obj(Json.Get(root, "hooks"));
            if (hooks == null) return "no hooks in settings.json; nothing to remove";
            Backup(settingsPath, now);
            int removed = RemoveOurs(hooks);
            Save(settingsPath, root);
            return "removed " + removed + " Capsule hook entries";
        }

        // Also matches notch-hook.exe, the hook's name from when the app was called UsageNotch, so connecting replaces
        // the entries that version left in settings.json and disconnecting removes them.
        static bool IsOurs(object handler)
        {
            string command = Json.Str(Json.Get(handler, "command"));
            if (command == null) return false;
            return command.IndexOf(HookExeName, StringComparison.OrdinalIgnoreCase) >= 0
                || command.IndexOf("notch-hook.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Removes our handlers, then entries left with no handlers, then events left with no entries.
        static int RemoveOurs(Dictionary<string, object> hooks)
        {
            int removed = 0;
            foreach (string evt in new List<string>(hooks.Keys))
            {
                object[] entries = Json.Arr(hooks[evt]);
                if (entries == null) continue;
                var keptEntries = new List<object>();
                bool touched = false;
                foreach (object entry in entries)
                {
                    var entryObj = Json.Obj(entry);
                    object[] handlers = entryObj == null ? null : Json.Arr(Json.Get(entryObj, "hooks"));
                    if (handlers == null) { keptEntries.Add(entry); continue; }
                    var keptHandlers = new List<object>();
                    foreach (object handler in handlers)
                    {
                        if (IsOurs(handler)) { removed++; touched = true; }
                        else keptHandlers.Add(handler);
                    }
                    if (keptHandlers.Count == handlers.Length) keptEntries.Add(entry);
                    else if (keptHandlers.Count > 0) { entryObj["hooks"] = keptHandlers.ToArray(); keptEntries.Add(entryObj); }
                }
                if (!touched) continue;
                if (keptEntries.Count > 0) hooks[evt] = keptEntries.ToArray();
                else hooks.Remove(evt);
            }
            return removed;
        }

        static Dictionary<string, object> LoadForEdit(string settingsPath)
        {
            if (!File.Exists(settingsPath)) return new Dictionary<string, object>();
            string text = Files.ReadText(settingsPath);
            if (text == null) throw new InvalidOperationException("couldn't read settings.json, so it was left unchanged");
            if (text.Trim().Length == 0) return new Dictionary<string, object>();
            var root = Json.Obj(Json.TryParse(text));
            if (root == null) throw new InvalidOperationException("settings.json isn't valid JSON, so it was left unchanged");
            return root;
        }

        // Never overwrites: a second backup in the same second gets "-2", "-3", ...
        static void Backup(string settingsPath, DateTime now)
        {
            if (!File.Exists(settingsPath)) return;
            string stem = settingsPath + ".capsule-bak-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = stem;
            for (int n = 2; File.Exists(target); n++) target = stem + "-" + n;
            File.Copy(settingsPath, target, false);
        }

        static void Save(string settingsPath, Dictionary<string, object> root)
        {
            string dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!Files.WriteAtomic(settingsPath, Json.Write(root) + "\n")) throw new IOException("couldn't write settings.json");
        }
    }
}
