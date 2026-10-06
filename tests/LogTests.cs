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
            Controller.Guard("prompts", delegate { throw new InvalidOperationException("the answer was " + Marker); });
            string said = LogSince(before);
            TestRunner.Check(said.Contains("prompts (InvalidOperationException)"), "a guarded action that throws is logged by type: " + said.Trim());
            TestRunner.Check(!said.Contains(Marker), "and its message, which can carry a typed answer, is not");
            bool ranOn = false;
            Controller.Guard("prompts", delegate { ranOn = true; });
            TestRunner.Check(ranOn, "an action that doesn't throw just runs");
        }
    }
}
