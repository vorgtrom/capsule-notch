using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Capsule
{
    public static class ReadingsTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        const long Now = 1790690940000;   // 2026-09-29 14:09 UTC
        const long Min = 60 * 1000;

        public static void Run()
        {
            ColoursFollowTheThresholds();
            PercentRoundsAndHandlesMissing();
            ResetTextAtEachBoundary();
            AgesAndPlans();
            HeadlineAndDimming();
            TheDisplayWindowShowsTheHighestUsage();
            ReadingsRoundTripThroughTheCache();
            ConfigDefaultsAndRoundTrip();
        }

        static void ColoursFollowTheThresholds()
        {
            TestRunner.Eq(Palette.Green, Palette.ForUsed(0), "0");
            TestRunner.Eq(Palette.Green, Palette.ForUsed(49.9), "49.9");
            TestRunner.Eq(Palette.Yellow, Palette.ForUsed(50), "50");
            TestRunner.Eq(Palette.Yellow, Palette.ForUsed(69.9), "69.9");
            TestRunner.Eq(Palette.Red, Palette.ForUsed(70), "70");
            TestRunner.Eq(Palette.Red, Palette.ForUsed(100), "100");
        }

        static void PercentRoundsAndHandlesMissing()
        {
            TestRunner.Eq("73%", Format.Percent(new LimitWindow { Used = 72.5 }), "rounds half up");
            TestRunner.Eq("0%", Format.Percent(new LimitWindow { Used = 0.2 }), "small");
            TestRunner.Eq("–", Format.Percent(null), "unknown");
        }

        static void ResetTextAtEachBoundary()
        {
            TestRunner.Eq("", Format.ResetText(0, Now, En), "unknown reset");
            TestRunner.Eq("Resets in <1 min", Format.ResetText(Now + 59999, Now, En), "under a minute");
            TestRunner.Eq("Resets in 1 min", Format.ResetText(Now + Min, Now, En), "one minute");
            TestRunner.Eq("Resets in 51 min", Format.ResetText(Now + 51 * Min + 30000, Now, En), "51 min");
            TestRunner.Eq("Resets in 1 hr", Format.ResetText(Now + 60 * Min, Now, En), "one hour");
            TestRunner.Eq("Resets in 3 hr 5 min", Format.ResetText(Now + 185 * Min, Now, En), "hours and minutes");
            TestRunner.Eq("Resets in 23 hr 59 min", Format.ResetText(Now + 86399999, Now, En), "just under a day");
            long later = Now + 3 * 86400000L;
            DateTime local = Clock.FromMs(later).ToLocalTime();
            TestRunner.Eq("Resets " + local.ToString("ddd", En) + " " + local.ToString("h:mm tt", En), Format.ResetText(later, Now, En), "a day or more: weekday and time");
        }

        static void AgesAndPlans()
        {
            TestRunner.Eq("just now", Format.Ago(Now - 30000, Now), "seconds");
            TestRunner.Eq("12 min ago", Format.Ago(Now - 12 * Min, Now), "minutes");
            TestRunner.Eq("2 hr ago", Format.Ago(Now - 121 * Min, Now), "hours");
            TestRunner.Eq("<1m", Format.Elapsed(59000), "under a minute");
            TestRunner.Eq("2m", Format.Elapsed(2 * Min + 5000), "minutes");
            TestRunner.Eq("1h 5m", Format.Elapsed(65 * Min), "hours");
            TestRunner.Eq("Max", Format.PlanName("max"), "plan");
            TestRunner.Eq("", Format.PlanName(null), "no plan");
        }

        static Reading Sample()
        {
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = Now - Min };
            r.Windows.Add(new LimitWindow { Id = "seven_day", Label = "Weekly · all models", Used = 7, ResetsAtMs = Now + 3 * 86400000L });
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = Now + 51 * Min });
            return r;
        }

        static void HeadlineAndDimming()
        {
            Reading r = Sample();
            TestRunner.Eq("five_hour", r.Headline.Id, "headline found by id, not position");
            TestRunner.Check(!r.IsDimmed(Now), "fresh reading not dimmed");
            Reading failed = r.Clone();
            failed.LastCheckFailed = true;
            TestRunner.Check(!failed.IsDimmed(Now), "failed, but only a minute old");
            TestRunner.Check(failed.IsDimmed(Now + 10 * Min), "failed and over 10 minutes old");
            Reading logs = failed.Clone();
            logs.FromLogs = true;
            TestRunner.Check(logs.IsDimmed(Now + 60 * Min), "old log snapshots dim too");
            failed.Windows[0].Used = 99;
            TestRunner.Near(7, r.Windows[0].Used, "clone is deep");
        }

        static void TheDisplayWindowShowsTheHighestUsage()
        {
            Reading r = Sample();
            r.Windows[0].Used = 99;
            r.Windows[1].Used = 10;
            TestRunner.Eq("seven_day", r.DisplayWindow.Id, "99% weekly usage cannot hide behind a 10% session");
            UsageTile tile = UsageTile.From(r, Now, En);
            TestRunner.Eq("99%", tile.Percent, "the panel shows the highest usage");
            TestRunner.Eq("Weekly · all models", tile.WindowLabel, "the panel names that window");
            TestRunner.Eq("99%", NotchView.CellFor(r, null, Now).Text, "the ring agrees with the panel");
            r.Windows[0].Used = 10;
            TestRunner.Eq("five_hour", r.DisplayWindow.Id, "equal usage keeps the usual window");
            r.Windows.Clear();
            TestRunner.Check(r.DisplayWindow == null, "an empty reading has no display window");
        }

        static void ReadingsRoundTripThroughTheCache()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "readings.json");
            var codex = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", FromLogs = true, Note = "as of", DataAtMs = Now };
            codex.Windows.Add(new LimitWindow { Id = "primary", Label = "5-hour limit", Used = 21.5, Note = "reset since last use" });
            ReadingsCache.Save(path, new[] { Sample(), codex });
            Dictionary<string, Reading> back = ReadingsCache.Load(path);
            TestRunner.Eq(2, back.Count, "both providers");
            TestRunner.Eq("Max", back["claude"].Plan, "plan");
            TestRunner.Eq(Now - Min, back["claude"].DataAtMs, "time");
            TestRunner.Eq("Current session", back["claude"].Headline.Label, "window label");
            TestRunner.Near(21.5, back["codex"].Windows[0].Used, "used");
            TestRunner.Check(back["codex"].FromLogs, "from logs");
            TestRunner.Eq("reset since last use", back["codex"].Windows[0].Note, "window note");
            TestRunner.Eq(0, ReadingsCache.Load(Path.Combine(TestRunner.NewTempDir(), "missing.json")).Count, "missing cache is empty");
        }

        static void ConfigDefaultsAndRoundTrip()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            Config c = Config.Load(path);
            TestRunner.Eq("right", c.Edge, "default edge");
            TestRunner.Near(0.5, c.Y, "default position");
            TestRunner.Check(c.ShowNotch && !c.FirstRunDone, "default flags");
            c.Edge = "left";
            c.Monitor = "\\\\.\\DISPLAY2";
            c.Y = 0.25;
            c.FirstRunDone = true;
            c.Save(path);
            Config back = Config.Load(path);
            TestRunner.Eq("left", back.Edge, "edge");
            TestRunner.Eq("\\\\.\\DISPLAY2", back.Monitor, "monitor");
            TestRunner.Near(0.25, back.Y, "position");
            TestRunner.Check(back.FirstRunDone, "first-run flag");
            File.WriteAllText(path, "{\"edge\":\"sideways\",\"y\":7}");
            Config odd = Config.Load(path);
            TestRunner.Eq("right", odd.Edge, "unknown edge falls back");
            TestRunner.Near(1, odd.Y, "position clamped");
        }
    }
}
