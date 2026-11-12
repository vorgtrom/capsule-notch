using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Capsule
{
    internal static class HookInstall
    {
        internal static string Prepare()
        {
            string staged = Paths.StagedHook;
            using (var capsule = Process.GetCurrentProcess())
                Stage(Path.Combine(Paths.ExeDir, "capsule-hook.exe"), staged, capsule.MainModule.FileName);
            return staged;
        }

        internal static void RefreshConnected()
        {
            string claude = Paths.ClaudeSettings, codex = Paths.CodexHooks;
            bool? claudeConnected = HookSetup.ConnectedState(claude), codexConnected = HookSetup.ConnectedState(codex);
            if (claudeConnected == true || codexConnected == true)
            {
                string staged = Prepare();
                DateTime now = DateTime.Now;
                if (claudeConnected == true) HookSetup.Migrate(claude, staged, now);
                if (codexConnected == true) HookSetup.Migrate(codex, staged, now);
            }
            if (!claudeConnected.HasValue || !codexConnected.HasValue)
                throw new IOException("Hook settings are temporarily unreadable.");
        }

        internal static void Stage(string source, string staged, string capsuleImage)
        {
            CodexHook.Command(staged); // Validate the registered path before changing anything.
            string dir = Path.GetDirectoryName(staged);
            Directory.CreateDirectory(dir);
            // UI and CLI connect can run at the same time. A sharing failure leaves the existing files in place.
            using (File.Open(Path.Combine(dir, "stage.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (!SameContents(source, staged))
                {
                    string next = staged + ".new";
                    File.Delete(next);
                    try
                    {
                        File.Copy(source, next);
                        if (File.Exists(staged)) File.Replace(next, staged, null);
                        else File.Move(next, staged);
                    }
                    finally { try { File.Delete(next); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }

                string marker = Path.Combine(dir, Paths.HookImageName);
                string image = capsuleImage + "\n";
                if (Files.ReadText(marker) != image && !Files.WriteAtomic(marker, image))
                    throw new IOException("Couldn't update the Capsule hook image marker.");
            }
        }

        static bool SameContents(string source, string staged)
        {
            if (!File.Exists(staged)) return false;
            using (var a = File.OpenRead(source))
            using (var b = File.OpenRead(staged))
            using (var hash = SHA256.Create())
                return a.Length == b.Length && hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b));
        }
    }
}
