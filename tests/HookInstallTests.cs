using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    static class HookInstallTests
    {
        public static void Run()
        {
            StagesAndRefreshesTheHook();
            CodexConnectRejectsUnsupportedStagedPath();
            RefreshWithUnsupportedCodexPath(claudeConnected: true, codexConnected: false);
            RefreshWithUnsupportedCodexPath(claudeConnected: true, codexConnected: true);
            RefreshWithUnsupportedCodexPath(claudeConnected: false, codexConnected: true);
            MarkerControlsOnlyTheStagedHook();
            MigratesOnlyCapsuleCommands();
        }

        static void StagesAndRefreshesTheHook()
        {
            string root = TestRunner.NewTempDir();
            string install = Directory.CreateDirectory(Path.Combine(root, "Capsule (1)")).FullName;
            string source = Path.Combine(install, "capsule-hook.exe");
            string staged = Path.Combine(root, "bin", "capsule-hook.exe");
            string image = Path.Combine(install, "Capsule.exe");
            File.WriteAllText(source, "old");

            HookInstall.Stage(source, staged, image);
            TestRunner.Eq("old", File.ReadAllText(staged), "can stage from a folder with parentheses");
            string marker = Path.Combine(Path.GetDirectoryName(staged), Paths.HookImageName);
            TestRunner.Eq(image, File.ReadAllText(marker).Trim(), "records the running image");
            File.SetLastWriteTimeUtc(staged, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            DateTime stamp = File.GetLastWriteTimeUtc(staged);

            string moved = Path.Combine(root, "moved", "Capsule.exe");
            HookInstall.Stage(source, staged, moved);
            TestRunner.Eq(stamp.Ticks, File.GetLastWriteTimeUtc(staged).Ticks, "same hook content leaves the file alone");
            TestRunner.Eq(moved, File.ReadAllText(marker).Trim(), "a move refreshes the marker even when the hook is unchanged");

            File.WriteAllText(source, "new");
            bool retry = false;
            using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { HookInstall.Stage(source, staged, moved); }
                catch (IOException) { retry = true; }
                TestRunner.Check(retry, "a locked hook reports that staging must retry");
                TestRunner.Eq("old", File.ReadAllText(staged), "the locked hook is left intact");
                TestRunner.Check(!File.Exists(staged + ".new"), "a failed replacement removes the temporary copy");
            }
            HookInstall.Stage(source, staged, moved);
            TestRunner.Eq("new", File.ReadAllText(staged), "the next attempt replaces changed content of the same length");
        }

        static void CodexConnectRejectsUnsupportedStagedPath()
        {
            string data = Path.Combine(TestRunner.NewTempDir(), "User (Work)");
            string previous = Environment.GetEnvironmentVariable("CAPSULE_DATA");
            bool rejected = false;
            try
            {
                Environment.SetEnvironmentVariable("CAPSULE_DATA", data);
                HookInstall.Prepare(codex: true);
            }
            catch (ArgumentException e) { rejected = e.Message.Contains("Rename the folder"); }
            finally { Environment.SetEnvironmentVariable("CAPSULE_DATA", previous); }
            TestRunner.Check(rejected, "Codex connect keeps the rename message for unsupported staged paths");
            TestRunner.Check(!Directory.Exists(Path.Combine(data, "bin")), "Codex connect validates before staging");
        }

        static void RefreshWithUnsupportedCodexPath(bool claudeConnected, bool codexConnected)
        {
            string root = TestRunner.NewTempDir();
            string claude = Path.Combine(root, "settings.json"), codex = Path.Combine(root, "hooks.json");
            string source = Path.Combine(root, "capsule-hook.exe");
            string staged = Path.Combine(root, "User (Work) & $team `ops`", "bin", "capsule-hook.exe");
            string image = Path.Combine(root, "Capsule.exe");
            DateTime now = new DateTime(2026, 11, 13);
            File.WriteAllText(source, "hook");

            if (claudeConnected)
                HookSetup.Connect(claude, source, new[] { new[] { "Stop", "*" } }, new[] { "--probe" }, true, now);
            if (codexConnected) HookSetup.ConnectCodex(codex, source, now);
            string before = Files.ReadText(codex);

            TestRunner.Check(!HookInstall.RefreshConnected(claude, codex, source, staged, image),
                "a Codex-incompatible path completes refresh without reporting a Codex migration");
            if (claudeConnected)
            {
                TestRunner.Eq("hook", File.ReadAllText(staged), "Claude stages the hook in a folder with parentheses");
                object[] entries = Json.Arr(Json.Get(Json.Parse(File.ReadAllText(claude)), "hooks", "Stop"));
                string quoted = "\"" + root.Replace('\\', '/') + "/User (Work) & \\$team \\`ops\\`/bin/capsule-hook.exe\"";
                TestRunner.Eq(quoted + " --probe", Command(entries[0]), "Claude migrates its shell hook with a literal path");
                TestRunner.Check(!HookSetup.Migrate(claude, staged, now), "an escaped shell path needs no further rewrite");
            }
            else TestRunner.Check(!File.Exists(staged), "a rejected Codex-only migration does not stage an unused hook");

            TestRunner.Eq(before ?? "<missing>", Files.ReadText(codex) ?? "<missing>", "refresh leaves Codex settings untouched");
            TestRunner.Eq(0, Directory.GetFiles(root, "hooks.json.capsule-bak-*").Length, "a skipped Codex migration makes no backup");
        }

        static void MarkerControlsOnlyTheStagedHook()
        {
            string legacy = TestRunner.NewTempDir(), staged = TestRunner.NewTempDir();
            string marker = Path.Combine(staged, Paths.HookImageName);
            string image = Path.Combine(legacy, "Capsule.exe");
            TestRunner.Eq(image, PipePeer.ExpectedImage(legacy, staged), "the legacy hook still trusts its neighbor");
            TestRunner.Check(PipePeer.ExpectedImage(staged, staged) == null, "the staged hook fails closed when the marker is missing");
            foreach (string bad in new[] { "", "Capsule.exe", @"relative\Capsule.exe", @"C:Capsule.exe", @"\Capsule.exe", @"\\server\Capsule.exe", Path.Combine(legacy, "Other.exe") })
            {
                File.WriteAllText(marker, bad);
                TestRunner.Check(PipePeer.ExpectedImage(staged, staged) == null, "rejects an empty, relative, or wrong image path: " + bad);
            }
            File.WriteAllText(marker, " " + image + "\r\n");
            TestRunner.Eq(image, PipePeer.ExpectedImage(staged + "\\", staged), "reads the staged image and trims whitespace");
            string other = TestRunner.NewTempDir();
            TestRunner.Eq(Path.Combine(other, "Capsule.exe"), PipePeer.ExpectedImage(other, staged), "the marker cannot redirect a legacy hook");
        }

        static void MigratesOnlyCapsuleCommands()
        {
            string root = TestRunner.NewTempDir();
            string claude = Path.Combine(root, "settings.json"), codex = Path.Combine(root, "hooks.json");
            string old = Path.Combine(root, "old copy", "capsule-hook.exe");
            string staged = Path.Combine(root, "bin", "capsule-hook.exe");
            DateTime now = new DateTime(2026, 11, 13);
            string foreign = "node " + old;
            const string stop = "Stop";
            string[][] wiring = { new[] { stop, "matcher" } };
            HookSetup.Connect(claude, old, wiring, new[] { "--probe" }, true, now);
            var settings = Json.Obj(Json.Parse(File.ReadAllText(claude)));
            var hooks = Json.Obj(Json.Get(settings, "hooks"));
            var other = new Dictionary<string, object> { { "command", foreign } };
            var otherEntry = new Dictionary<string, object> { { "hooks", new object[] { other } } };
            hooks[stop] = new[] { Json.Arr(hooks[stop])[0], otherEntry };
            File.WriteAllText(claude, Json.Write(settings));

            HookSetup.Migrate(claude, staged, now);
            object[] entries = Json.Arr(Json.Get(Json.Parse(File.ReadAllText(claude)), "hooks", stop));
            TestRunner.Eq("\"" + staged.Replace('\\', '/') + "\" --probe", Command(entries[0]), "Claude shell migration preserves the argument");
            TestRunner.Eq("matcher", Json.Str(Json.Get(entries[0], "matcher")), "Claude migration keeps the matcher");
            TestRunner.Eq(foreign, Command(entries[1]), "migration leaves a foreign command mentioning the old hook untouched");

            HookSetup.Connect(claude, old, wiring, new[] { "--probe" }, false, now);
            HookSetup.Migrate(claude, staged, now);
            object exec = Json.Arr(Json.Get(Json.Parse(File.ReadAllText(claude)), "hooks", stop))[0];
            TestRunner.Eq(staged, Command(exec), "Claude exec migration replaces the executable path");
            TestRunner.Eq("--probe", Json.Str(Json.Arr(Json.Get(Json.Arr(Json.Get(exec, "hooks"))[0], "args"))[0]), "exec migration keeps its argument list");

            HookSetup.ConnectCodex(codex, old, now);
            TestRunner.Check(HookSetup.Migrate(codex, staged, now), "moving a Codex hook from its old path reports a change");
            object request = Json.Arr(Json.Get(Json.Parse(File.ReadAllText(codex)), "hooks", "PermissionRequest"))[0];
            TestRunner.Eq(CodexHook.Command(staged), Command(request), "Codex migration preserves the command wrapper");
            int backups = Directory.GetFiles(root, "hooks.json.capsule-bak-*").Length;
            TestRunner.Eq(1, backups, "migration backs up the original hooks");
            TestRunner.Check(!HookSetup.Migrate(codex, staged, now), "an already staged Codex hook reports no change");
            TestRunner.Eq(backups, Directory.GetFiles(root, "hooks.json.capsule-bak-*").Length, "already staged hooks need no rewrite or backup");

            HookSetup.ConnectCodex(codex, old, now);
            string source = Path.Combine(root, "capsule-hook.exe"), image = Path.Combine(root, "Capsule.exe");
            File.WriteAllText(source, "hook");
            TestRunner.Check(HookInstall.RefreshConnected(claude, codex, source, staged, image), "startup reports a Codex migration for the trust notice");
            TestRunner.Check(!HookInstall.RefreshConnected(claude, codex, source, staged, image), "another refresh does not report the same migration");
        }

        static string Command(object entry)
        {
            return Json.Str(Json.Get(Json.Arr(Json.Get(entry, "hooks"))[0], "command"));
        }
    }
}
