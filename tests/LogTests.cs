using System;
using System.IO;

namespace Capsule
{
    // A log line names an exception by its type, never by its message: a message can carry what a tool, a question or a
    // typed answer said, a file's path, or part of a response.
    public static class LogTests
    {
        public static void Run()
        {
            AnExceptionIsNamedByItsTypeNeverItsMessage();
            AGuardedActionThatThrowsLeavesNoMessageInTheLog();
            ClipboardDefaultsDependOnExistingConfig();
        }

        const string Marker = "SAMPLE-MARKER-4471";

        // What the log gained since `before`.
        static string LogSince(int before)
        {
            string log = Files.ReadText(Paths.LogFile) ?? "";
            return log.Length > before ? log.Substring(before) : "";
        }

        static int LogLength() { return (Files.ReadText(Paths.LogFile) ?? "").Length; }

        static void AnExceptionIsNamedByItsTypeNeverItsMessage()
        {
            int before = LogLength();
            Log.Error("sample step", new IOException("could not open C:\\Users\\sample\\.claude\\settings.json " + Marker));
            Log.Error("sample step without one", null);
            string said = LogSince(before);
            TestRunner.Check(said.Contains("sample step (IOException)"), "the log names the exception's type: " + said.Trim());
            TestRunner.Check(!said.Contains(Marker) && !said.Contains(".claude"), "and nothing its message said");
            TestRunner.Check(said.Contains("sample step without one") && !said.Contains("without one ("), "an error with no exception is just its text");
        }

        // Every Controller callback is wrapped in Guard, over tool input, question text and typed answers.
        static void AGuardedActionThatThrowsLeavesNoMessageInTheLog()
        {
            int before = LogLength();
            Controller.Guard("prompts", delegate { throw new InvalidOperationException("the answer was " + Marker, new IOException(Marker)); });
            string said = LogSince(before);
            TestRunner.Check(said.Contains("prompts (InvalidOperationException)"), "a guarded action that throws is logged by type: " + said.Trim());
            TestRunner.Check(!said.Contains(Marker), "and its message, which can carry a typed answer, is not");
            TestRunner.Check(said.Contains("inner=IOException") && said.Contains("Controller.Guard") && said.Contains(" < "),
                "includes inner exception type and compact stack frames");
            bool ranOn = false;
            Controller.Guard("prompts", delegate { ranOn = true; });
            TestRunner.Check(ranOn, "the next module can run after a guarded tick fails");
        }

        static void ClipboardDefaultsDependOnExistingConfig()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            TestRunner.Check(!new Config().ClipboardHistory && !Config.Load(path).ClipboardHistory, "clipboard history is off on a new install");
            File.WriteAllText(path, "{}");
            TestRunner.Check(Config.Load(path).ClipboardHistory, "an existing config without a key keeps the old default");
            File.WriteAllText(path, "{\"clipboard_history\":false}");
            TestRunner.Check(!Config.Load(path).ClipboardHistory, "an existing explicit choice stays off");
        }
    }
}
