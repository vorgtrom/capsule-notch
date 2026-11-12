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
            StagedPathIsValidated();
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

        static void StagedPathIsValidated()
        {
            string root = TestRunner.NewTempDir();
            string folder = Path.Combine(root, "User (Work)", "bin");
            bool rejected = false;
            try { HookInstall.Stage(Path.Combine(root, "source.exe"), Path.Combine(folder, "capsule-hook.exe"), Path.Combine(root, "Capsule.exe")); }
            catch (ArgumentException e) { rejected = e.Message.Contains("Rename the folder"); }
            TestRunner.Check(rejected, "special characters in the staged path trigger the rename message");
            TestRunner.Check(!Directory.Exists(folder), "validation precedes staging");
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
            HookSetup.Migrate(codex, staged, now);
            object request = Json.Arr(Json.Get(Json.Parse(File.ReadAllText(codex)), "hooks", "PermissionRequest"))[0];
            TestRunner.Eq(CodexHook.Command(staged), Command(request), "Codex migration preserves the command wrapper");
            int backups = Directory.GetFiles(root, "hooks.json.capsule-bak-*").Length;
            TestRunner.Eq(1, backups, "migration backs up the original hooks");
            HookSetup.Migrate(codex, staged, now);
            TestRunner.Eq(backups, Directory.GetFiles(root, "hooks.json.capsule-bak-*").Length, "already staged hooks need no rewrite or backup");
        }

        static string Command(object entry)
        {
            return Json.Str(Json.Get(Json.Arr(Json.Get(entry, "hooks"))[0], "command"));
        }
    }
}
