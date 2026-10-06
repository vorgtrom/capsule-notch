using System;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class UpdatesTests
    {
        public static void Run()
        {
            VersionsAreCompared();
            ANewerReleaseIsFoundOnce();
            FailuresTryAgainLater();
            SourceBuildsAndTurnedOffNeverAsk();
            TheChoiceIsKept();
        }

        static void TheChoiceIsKept()
        {
            string path = System.IO.Path.Combine(TestRunner.NewTempDir(), "config.json");
            bool byDefault = Config.Load(path).CheckUpdates;
            new Config { CheckUpdates = false }.Save(path);
            TestRunner.Check(byDefault && !Config.Load(path).CheckUpdates, "Check for updates is on by default, and turning it off is kept");
        }

        static HttpResult Release(string tag)
        {
            return new HttpResult { Status = 200, Body = "{\"tag_name\": \"" + tag + "\", \"html_url\": \"https://example.com/elsewhere\", \"name\": \"Capsule " + tag + "\"}" };
        }

        // A check made and applied: Tick, then the fetch and its result, on the test's thread.
        static void Check(UpdateCheck u, long now)
        {
            Task before = u.LastCheck;
            u.Tick(now);
            if (u.LastCheck != null && u.LastCheck != before) u.LastCheck.Wait(5000);
        }

        static void VersionsAreCompared()
        {
            TestRunner.Check(UpdateCheck.Newer("v1.2.0", "v1.1.0") && UpdateCheck.Newer("v1.10.0", "v1.9.9") && UpdateCheck.Newer("v2.0.0", "v1.99.99"), "a later version is newer, by numbers not by text");
            TestRunner.Check(!UpdateCheck.Newer("v1.1.0", "v1.1.0") && !UpdateCheck.Newer("v1.0.9", "v1.1.0"), "the same or an older one isn't");
            TestRunner.Check(UpdateCheck.Parse("dev") == null && UpdateCheck.Parse("1.2.0") == null && UpdateCheck.Parse("v1.2") == null && UpdateCheck.Parse("v1.2.0-beta") == null
                && UpdateCheck.Parse("v1.2.0/../../evil") == null && !UpdateCheck.Newer("v9.0.0", "dev"), "only v1.2.3 is a version; a source build has none");
        }

        static void ANewerReleaseIsFoundOnce()
        {
            long now = 1000000;
            int fetches = 0, found = 0;
            string tag = "v1.2.0";
            var u = new UpdateCheck("v1.1.0", now, TaskScheduler.Default);
            u.Fetch = () => { Interlocked.Increment(ref fetches); return Release(tag); };
            u.Found += delegate { found++; };
            Check(u, now + 30 * 1000);
            TestRunner.Check(fetches == 0, "not asked in the first minute after starting");
            Check(u, now + UpdateCheck.FirstAfterMs);
            TestRunner.Check(fetches == 1 && found == 1 && u.Available == "v1.2.0", "then a newer release is found");
            TestRunner.Eq("https://github.com/vorgtrom/capsule-notch/releases/tag/v1.2.0", u.PageUrl, "its page is built from the tag, not taken from the reply");
            Check(u, now + UpdateCheck.FirstAfterMs + 3600 * 1000L);
            TestRunner.Check(fetches == 1, "asked once a day, not every tick");
            Check(u, now + UpdateCheck.FirstAfterMs + UpdateCheck.EveryMs);
            TestRunner.Check(fetches == 2 && found == 1, "the same release found again isn't announced again");
            tag = "v1.3.0";
            Check(u, now + UpdateCheck.FirstAfterMs + 2 * UpdateCheck.EveryMs);
            TestRunner.Check(found == 2 && u.Available == "v1.3.0", "a later one is");
            var upToDate = new UpdateCheck("v1.3.0", now, TaskScheduler.Default);
            upToDate.Fetch = () => Release("v1.3.0");
            Check(upToDate, now + UpdateCheck.FirstAfterMs);
            TestRunner.Check(upToDate.Available == null && upToDate.PageUrl == "", "up to date: nothing is offered");
        }

        static void FailuresTryAgainLater()
        {
            long now = 1000000;
            int fetches = 0;
            HttpResult reply = new HttpResult { Status = 0, Error = "NameResolutionFailure" };
            var u = new UpdateCheck("v1.1.0", now, TaskScheduler.Default);
            u.Fetch = () => { Interlocked.Increment(ref fetches); return reply; };
            Check(u, now + UpdateCheck.FirstAfterMs);
            Check(u, now + UpdateCheck.FirstAfterMs + UpdateCheck.RetryMs - 1000);
            TestRunner.Check(fetches == 1 && u.Available == null, "offline: nothing found, and not asked again at once");
            reply = new HttpResult { Status = 200, Body = "{\"tag_name\": \"latest\"}" };
            Check(u, now + UpdateCheck.FirstAfterMs + UpdateCheck.RetryMs);
            TestRunner.Check(fetches == 2 && u.Available == null, "an hour later it asks again; a tag that isn't a version is ignored");
            u.Fetch = () => { throw new InvalidOperationException(); };
            Check(u, now + UpdateCheck.FirstAfterMs + 2 * UpdateCheck.RetryMs);
            TestRunner.Check(u.Available == null, "a fetch that throws is a failed check, not a crash");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("updates: couldn't check (status 0)") || log.Contains("updates: couldn't check (no version)"), "the log says only why");
        }

        static void SourceBuildsAndTurnedOffNeverAsk()
        {
            long now = 1000000;
            int fetches = 0;
            var dev = new UpdateCheck("dev", now, TaskScheduler.Default);
            dev.Fetch = () => { fetches++; return Release("v9.0.0"); };
            Check(dev, now + 2 * UpdateCheck.EveryMs);
            TestRunner.Check(!dev.CanCheck && fetches == 0 && dev.Available == null, "a build from source never asks");
            var off = new UpdateCheck("v1.1.0", now, TaskScheduler.Default);
            off.Fetch = () => { fetches++; return Release("v9.0.0"); };
            off.Enabled = false;
            Check(off, now + 2 * UpdateCheck.EveryMs);
            TestRunner.Check(off.CanCheck && fetches == 0, "with Check for updates off, nothing is asked");
        }
    }
}
