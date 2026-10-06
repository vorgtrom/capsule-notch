using System;

namespace Capsule
{
    public static class ClaudeAppTests
    {
        public static void Run()
        {
            KnowsTheAppByItsExecutable();
            OtherClaudesDontCount();
            TheWindowInFrontCanBeAsked();
        }

        static void KnowsTheAppByItsExecutable()
        {
            TestRunner.Check(ClaudeApp.IsAppPath(@"C:\Program Files\WindowsApps\Claude_2.19675.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe"), "the Microsoft Store app");
            TestRunner.Check(ClaudeApp.IsAppPath(@"C:\PROGRAM FILES\WINDOWSAPPS\CLAUDE_3.0.0.0_ARM64__PZS8SXRJXFJJC\APP\CLAUDE.EXE"), "in any case, on any machine type");
            TestRunner.Check(ClaudeApp.IsAppPath(@"D:\Apps\WindowsApps\Claude_2.0.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe"), "on another drive");
            TestRunner.Check(ClaudeApp.IsAppPath(@"C:\Users\User\AppData\Local\AnthropicClaude\app-0.14.10\claude.exe"), "the per-user install");
            TestRunner.Check(ClaudeApp.IsAppPath("C:/Program Files/WindowsApps/Claude_2.19675.0.0_x64__pzs8sxrjxfjjc/app/Claude.exe"), "with forward slashes");
            TestRunner.Check(ClaudeApp.IsAppPath("C:/Users/User/AppData/Local/AnthropicClaude/app-0.14.10/claude.exe"), "the per-user install too");
            TestRunner.Check(ClaudeApp.IsAppPath(@"C:\Users\User/AppData\Local/AnthropicClaude\app-0.14.10/claude.exe"), "and with both mixed");
        }

        static void OtherClaudesDontCount()
        {
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Users\User\AppData\Roaming\Claude\claude-code\2.1.286\635c1867224a\claude.exe"), "not Claude Code's own claude.exe");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Users\User\.local\bin\claude.exe"), "nor the CLI");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Program Files\WindowsApps\Claude_2.19675.0.0_x64__pzs8sxrjxfjjc\app\resources\claude.exe"), "nor a claude.exe elsewhere in the package");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Program Files\WindowsApps\Claudette_1.0.0.0_x64__abc\app\Claude.exe"), "nor another package");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Program Files\WindowsApps\Claude_2.19675.0.0_x64__pzs8sxrjxfjjc\app\Claude Helper.exe"), "nor another executable");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"C:\Users\User\AppData\Local\AnthropicClaude\claude.exe"), "nor the installer's own");
            TestRunner.Check(!ClaudeApp.IsAppPath("claude.exe"), "nor a bare name");
            TestRunner.Check(!ClaudeApp.IsAppPath(null) && !ClaudeApp.IsAppPath(""), "nor nothing");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"app\Claude.exe"), "nor two segments");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"Claude_2.0.0.0_x64__abc\app\Claude.exe"), "nor three, with no WindowsApps above");
            TestRunner.Check(!ClaudeApp.IsAppPath(@"AnthropicClaude\app-0.14.10\claude.exe"), "nor the per-user install's last three segments alone");
            TestRunner.Check(!ClaudeApp.IsAppPath("Claude_2.0.0.0_x64__abc/app/Claude.exe"), "or with forward slashes");
        }

        // Whatever is in front while the tests run, asking must not throw.
        static void TheWindowInFrontCanBeAsked()
        {
            Exception thrown = null;
            string exe = null;
            try
            {
                exe = ClaudeApp.ForegroundExe();
                ClaudeApp.IsInFront();
            }
            catch (Exception e) { thrown = e; }
            TestRunner.Check(thrown == null, "asking which app is in front doesn't throw");
            TestRunner.Check(exe == null || (System.IO.Path.IsPathRooted(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), "and the answer is nothing, or the full path of an .exe");
        }
    }
}
