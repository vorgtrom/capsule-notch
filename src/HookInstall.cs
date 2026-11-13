using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Capsule
{
    internal static class HookInstall
    {
        static string BundledHook { get { return Path.Combine(Paths.ExeDir, "capsule-hook.exe"); } }

        internal static string Prepare(bool codex = false)
        {
            string staged = Paths.StagedHook;
            if (codex) CodexHook.Command(staged);
            using (var capsule = Process.GetCurrentProcess())
                Stage(BundledHook, staged, capsule.MainModule.FileName);
            return staged;
        }

        internal static bool RefreshConnected()
        {
            using (var capsule = Process.GetCurrentProcess())
                return RefreshConnected(Paths.ClaudeSettings, Paths.CodexHooks, BundledHook, Paths.StagedHook, capsule.MainModule.FileName);
        }

        internal static bool RefreshConnected(string claude, string codex, string source, string staged, string capsuleImage)
        {
            bool? claudeConnected = HookSetup.ConnectedState(claude), codexConnected = HookSetup.ConnectedState(codex);
            DateTime now = DateTime.Now;
            if (claudeConnected == true)
            {
                Stage(source, staged, capsuleImage);
                HookSetup.Migrate(claude, staged, now);
            }
            if (!claudeConnected.HasValue || !codexConnected.HasValue)
                throw new IOException("Hook settings are temporarily unreadable.");
            if (codexConnected != true) return false;

            try { CodexHook.Command(staged); }
            catch (ArgumentException e)
            {
                Log.Error("Codex hook migration skipped: staged path contains unsupported characters", e);
                return false;
            }
            if (claudeConnected != true) Stage(source, staged, capsuleImage);
            return HookSetup.Migrate(codex, staged, now);
        }

        internal static void Stage(string source, string staged, string capsuleImage)
        {
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
