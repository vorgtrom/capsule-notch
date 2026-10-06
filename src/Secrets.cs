using System;
using System.IO;
using System.Security.Cryptography;

namespace Capsule
{
    // Secrets (spec §3; calendar spec §3): the Notion secret, and Google's client secret and refresh token, each
    // encrypted with Windows DPAPI for the current Windows user, so only that user on this PC can read it back. Each
    // kind has its own purpose (DPAPI's extra entropy), so one can't be read back as another. Never written, logged or
    // shown in plain text.
    public static class SecretStore
    {
        public const string NotionPurpose = "Capsule Notion secret";
        public const string GoogleClientPurpose = "Capsule Google client secret";
        public const string GoogleTokenPurpose = "Capsule Google refresh token";
        public const string GoogleLinkPurpose = "Capsule Google calendar link";

        // The Notion secret.
        public static bool Save(string path, string secret) { return Save(path, secret, NotionPurpose, "notion"); }
        public static string Load(string path) { return Load(path, NotionPurpose); }

        // True when the secret is saved. Never throws: a secret that can't be encrypted or written is reported as false,
        // and the log gets the exception's type, under what ("notion", "calendar"), never the secret.
        public static bool Save(string path, string secret, string purpose, string what)
        {
            try
            {
                byte[] locked = ProtectedData.Protect(Files.Utf8.GetBytes(secret), Files.Utf8.GetBytes(purpose), DataProtectionScope.CurrentUser);
                return Files.WriteAtomic(path, Convert.ToBase64String(locked));
            }
            catch (Exception e)
            {
                Log.Error(what + ": couldn't save the secret", e);
                return false;
            }
        }

        // The secret, or null when none is saved or it can't be read here (another user's, damaged, or saved for
        // another purpose).
        public static string Load(string path, string purpose)
        {
            string text = Files.ReadText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return Files.Utf8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text.Trim()), Files.Utf8.GetBytes(purpose), DataProtectionScope.CurrentUser)); }
            catch (Exception) { return null; }
        }

        public static bool Exists(string path) { return File.Exists(path); }

        // Gone for good. True when no file is left.
        public static bool Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("couldn't delete a secret file", e);
                return false;
            }
        }
    }
}
