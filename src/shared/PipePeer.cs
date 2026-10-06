using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Capsule
{
    // Who is on the other end of the pipe (act-from-notch spec §4). The hook sends a request, with tool inputs in it, only
    // to the real Capsule: the process that made the pipe instance it connected to must belong to the current user, run at
    // medium integrity or higher, and be Capsule.exe in the hook's own folder. Anything else, and any failure to find
    // out, counts as no Capsule. kernel32 and advapi32 through mscorlib only, as the hook may reference nothing else.
    public static class PipePeer
    {
        // Tests only (the test process is Tests.exe, not Capsule.exe): the image the server must be. Never read from the
        // environment or a file, so nothing a project's settings can set switches the check off.
        internal static string ExpectedImageForTests { get; set; }

        const uint ProcessQueryLimitedInformation = 0x1000;
        const uint TokenQuery = 0x8;
        const int TokenUserClass = 1, TokenIntegrityLevelClass = 25;
        const int MediumRid = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returned);

        // True only when the pipe's server is the real Capsule of the current user.
        public static bool IsCapsule(SafePipeHandle pipe)
        {
            try
            {
                uint pid;
                if (pipe == null || pipe.IsInvalid || pipe.IsClosed) return false;
                if (!GetNamedPipeServerProcessId(pipe, out pid) || pid == 0) return false;
                return IsCapsuleProcess(pid, ExpectedImage());
            }
            catch (Exception) { return false; }
        }

        // The process pid belongs to the current user, is at medium integrity or above, and runs the image given.
        internal static bool IsCapsuleProcess(uint pid, string expectedImage)
        {
            try
            {
                if (string.IsNullOrEmpty(expectedImage)) return false;
                IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (process == IntPtr.Zero) return false;
                try
                {
                    IntPtr token;
                    if (!OpenProcessToken(process, TokenQuery, out token)) return false;
                    try
                    {
                        SecurityIdentifier user = TokenSid(token, TokenUserClass);
                        using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                            if (user == null || me.User == null || !user.Equals(me.User)) return false;
                        if (IntegrityRid(token) < MediumRid) return false;
                    }
                    finally { CloseHandle(token); }
                    string image = ImagePath(process);
                    return image != null && SameFile(image, expectedImage);
                }
                finally { CloseHandle(process); }
            }
            catch (Exception) { return false; }
        }

        // Capsule.exe next to the hook, or the tests' stand-in.
        static string ExpectedImage()
        {
            return ExpectedImageForTests ?? Path.Combine(Paths.ExeDir, "Capsule.exe");
        }

        static bool SameFile(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        // The user of a process, for the helpers' tests and the check.
        internal static SecurityIdentifier UserOf(uint pid)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, TokenQuery, out token)) return null;
                try { return TokenSid(token, TokenUserClass); }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        // The integrity level's RID (0x2000 medium, 0x3000 high ...), or -1 when it can't be read.
        internal static int IntegrityOf(uint pid)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return -1;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, TokenQuery, out token)) return -1;
                try { return IntegrityRid(token); }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        internal static string ImageOf(uint pid)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return null;
            try { return ImagePath(process); }
            finally { CloseHandle(process); }
        }

        static string ImagePath(IntPtr process)
        {
            var name = new StringBuilder(32768);
            int size = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString(0, size) : null;
        }

        // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with a SID_AND_ATTRIBUTES: a pointer to the SID, then flags.
        static SecurityIdentifier TokenSid(IntPtr token, int informationClass)
        {
            int needed;
            GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out needed);
            if (needed <= 0 || needed > 4096) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                int returned;
                if (!GetTokenInformation(token, informationClass, buffer, needed, out returned)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // The last part of the integrity SID S-1-16-<rid>.
        static int IntegrityRid(IntPtr token)
        {
            SecurityIdentifier label = TokenSid(token, TokenIntegrityLevelClass);
            if (label == null) return -1;
            string text = label.Value;
            const string prefix = "S-1-16-";
            int rid;
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            return int.TryParse(text.Substring(prefix.Length), out rid) ? rid : -1;
        }
    }
}
