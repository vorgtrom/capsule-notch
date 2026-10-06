using System;
using System.IO;

namespace Capsule
{
    // Tests.exe (in bin\ for build.cmd, in bin-dev\ for build.cmd dev): a tiny assertion harness. Exit code 0 means every
    // check passed.
    public static class TestRunner
    {
        static int passed, failed;
        static bool groups;   // each group's count too: with --groups, and always on GitHub Actions (which sets CI=true)

        [STAThread]
        public static int Main(string[] args)
        {
            // A copy of this exe renamed Capsule.exe, started by the hook tests as a stand-in Capsule (see PromptHookTests).
            if (args.Length == 3 && args[0] == "--fake-capsule") return PromptHookTests.FakeCapsule(args[1], args[2]);
            groups = (args.Length == 1 && args[0] == "--groups") || Environment.GetEnvironmentVariable("CI") == "true";
            // The hook's client only talks to a server that is Capsule.exe: for the tests, that is this process.
            PipePeer.ExpectedImageForTests = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            // Keep everything the tests write out of the real data folder (child processes inherit this too).
            Environment.SetEnvironmentVariable("CAPSULE_DATA", NewTempDir());
            // And off the running Capsule's pipe: a hook the tests start must never reach it.
            Environment.SetEnvironmentVariable("CAPSULE_PIPE", "capsule-tests-" + Guid.NewGuid().ToString("N"));
            Run("Json", JsonTests.Run);
            Run("HookEvents", HookEventsTests.Run);
            Run("HookSetup", HookSetupTests.Run);
            Run("Readings", ReadingsTests.Run);
            Run("ClaudeUsage", ClaudeUsageTests.Run);
            Run("CodexUsage", CodexUsageTests.Run);
            Run("CodexActivity", CodexActivityTests.Run);
            Run("Sessions", SessionsTests.Run);
            Run("NotchView", NotchViewTests.Run);
            Run("CardView", CardViewTests.Run);
            Run("Layout", LayoutTests.Run);
            Run("Native", NativeTests.Run);
            Run("GlassShape", GlassShapeTests.Run);
            Run("Theme", ThemeTests.Run);
            Run("Tray", TrayTests.Run);
            Run("Modules", ModulesTests.Run);
            Run("Notion", NotionTests.Run);
            Run("Ideas", IdeasTests.Run);
            Run("PanelView", PanelViewTests.Run);
            Run("Hotkey", HotkeyTests.Run);
            Run("CardMotion", CardMotionTests.Run);
            Run("PromptPipe", PromptPipeTests.Run);
            Run("PromptMessages", PromptMessagesTests.Run);
            Run("PromptServer", PromptServerTests.Run);
            Run("PromptHook", PromptHookTests.Run);
            Run("ClaudeApp", ClaudeAppTests.Run);
            Run("QuestionFlow", QuestionFlowTests.Run);
            Run("PromptBroker", PromptBrokerTests.Run);
            Run("PromptCard", PromptCardTests.Run);
            Run("PromptView", PromptViewTests.Run);
            Run("GoogleAuth", GoogleAuthTests.Run);
            Run("GoogleCalendar", GoogleCalendarTests.Run);
            Run("GoogleTasks", GoogleTasksTests.Run);
            Run("CalendarDay", CalendarDayTests.Run);
            Run("CalendarMonth", CalendarMonthTests.Run);
            Run("Ics", IcsTests.Run);
            Run("CalendarModule", CalendarModuleTests.Run);
            Run("CalendarView", CalendarViewTests.Run);
            Run("Log", LogTests.Run);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action group)
        {
            int before = passed;
            try { group(); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " threw " + e); }
            if (groups) Console.WriteLine("  " + name + ": " + (passed - before) + " passed");
        }

        public static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL " + what); }
        }

        public static void Eq(object expected, object actual, string what)
        {
            Check(Equals(expected, actual), what + " (expected <" + expected + ">, got <" + actual + ">)");
        }

        public static void Near(double expected, double actual, string what)
        {
            Check(Math.Abs(expected - actual) < 1e-6, what + " (expected " + expected + ", got " + actual + ")");
        }

        public static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "capsule-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string RepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "build.cmd"))) dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            return dir;
        }

        public static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", name));
        }
    }
}
