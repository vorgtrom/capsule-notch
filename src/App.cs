using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Capsule
{
    public static class App
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                object thrown = e.ExceptionObject;
                Log.Error("unhandled (" + (thrown == null ? "none" : thrown.GetType().Name) + ")", null);   // the type only: a message could say anything
            };
            if (args.Length >= 2 && args[0] == "--preview")
            {
                try { Preview.Render(args[1]); return 0; }
                catch (Exception e) { Log.Error("preview", e); return 1; }
            }
            if (args.Length >= 1 && args[0].StartsWith("--", StringComparison.Ordinal)) return RunCommand(args[0]);

            bool firstInstance;
            using (var single = new Mutex(true, @"Local\Capsule", out firstInstance))
            {
                if (!firstInstance) return 0;   // already running
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var controller = new Controller(app);
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Error("ui (" + e.Exception.GetType().Name + ")", null);   // the type only: a message could say anything
                    e.Handled = true;
                };
                app.Startup += delegate
                {
                    try { controller.Start(); }
                    catch (Exception e)
                    {
                        // Half-started, the app would linger with no notch while holding the one-copy lock.
                        Log.Error("start (" + e.GetType().Name + ")", null);   // the type only: a message could say anything
                        app.Shutdown(1);
                    }
                };
                // Raised by every shutdown (Quit, a failed start, Windows signing out) after WPF has closed the windows and
                // while its dispatcher still runs: the moment the blur must go (spec §4.7). An exception here would not
                // end the process (WPF's DispatcherUnhandledException handler above takes it), but it is caught here so the
                // log gets its type only.
                app.Exit += delegate
                {
                    try { controller.Dispose(); }
                    catch (Exception e) { Log.Error("stop (" + e.GetType().Name + ")", null); }
                };
                app.Run();
                controller.Dispose();   // already done in Exit; a second call does nothing
                Log.Info("stopped");
            }
            return 0;
        }

        // --connect, --connect-probe, --connect-probe-shell and --disconnect change Claude Code's hooks
        // without the UI. The result goes to log.txt; the exit code is 0 on success.
        static int RunCommand(string command)
        {
            string hookExe = Path.Combine(Paths.ExeDir, "capsule-hook.exe");
            if (command.StartsWith("--connect", StringComparison.Ordinal) && !File.Exists(hookExe))
            {
                // A hook pointing at a missing file would make every Claude Code session report hook errors.
                Log.Info("hooks: not connected, capsule-hook.exe is missing");
                return 1;
            }
            try
            {
                string result;
                if (command == "--connect") result = HookSetup.Connect(Paths.ClaudeSettings, hookExe, HookEvents.Wiring, new string[0], HookSetup.UseShellForm, DateTime.Now);
                else if (command == "--connect-probe") result = HookSetup.Connect(Paths.ClaudeSettings, hookExe, HookEvents.ProbeWiring, new[] { "--probe" }, false, DateTime.Now);
                else if (command == "--connect-probe-shell") result = HookSetup.Connect(Paths.ClaudeSettings, hookExe, HookEvents.ProbeWiring, new[] { "--probe" }, true, DateTime.Now);
                else if (command == "--disconnect") result = HookSetup.Disconnect(Paths.ClaudeSettings, DateTime.Now);
                else { Log.Info("unknown command " + command); return 2; }
                Log.Info("hooks: " + result);
                return 0;
            }
            catch (Exception e)
            {
                Log.Error("hooks " + command, e);
                return 1;
            }
        }
    }
}
