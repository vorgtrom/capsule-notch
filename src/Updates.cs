using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Capsule
{
    // Whether a newer Capsule is out: about a minute after starting, then once a day, one GET of the latest release from
    // GitHub (api.github.com), unauthenticated. Only its tag (v1.2.0) is read; nothing about the user is sent but the
    // request itself. A release build only: one built from source is "dev" and never asks. Found is raised once per
    // newer version seen; the menu offers its release page, which is built here from the checked tag, never taken from
    // the reply.
    public sealed class UpdateCheck
    {
        public const string LatestUrl = "https://api.github.com/repos/vorgtrom/capsule-notch/releases/latest";
        public const string PageBase = "https://github.com/vorgtrom/capsule-notch/releases/tag/";
        public const long FirstAfterMs = 60 * 1000, EveryMs = 24 * 3600 * 1000L, RetryMs = 3600 * 1000L;
        public const int TimeoutMs = 15000;
        static readonly Regex Shape = new Regex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$");

        readonly string current;
        readonly TaskScheduler ui;
        long due;
        bool busy;

        public Func<HttpResult> Fetch;          // set by tests; null = api.github.com
        public bool Enabled = true;             // the menu's Check for updates
        public string Available { get; private set; }   // the newer version found ("v1.2.0"), or null
        public event Action Found;
        public Task LastCheck { get; private set; }

        public UpdateCheck(string current, long now, TaskScheduler ui)
        {
            this.current = current;
            this.ui = ui;
            due = now + FirstAfterMs;
        }

        // A release build, which can tell whether another release is newer.
        public bool CanCheck { get { return Parse(current) != null; } }

        public string PageUrl { get { return Available != null ? PageBase + Available : ""; } }

        public void Tick(long now)
        {
            if (!Enabled || !CanCheck || busy || now < due) return;
            busy = true;
            Func<HttpResult> fetch = Fetch ?? Latest;
            LastCheck = Task.Run(fetch).ContinueWith(t => Apply(t, now), ui);
        }

        static HttpResult Latest()
        {
            var headers = new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } };
            return Http.Get(LatestUrl, headers, TimeoutMs);
        }

        void Apply(Task<HttpResult> t, long now)
        {
            busy = false;
            try
            {
                HttpResult r = t.IsFaulted ? null : t.Result;
                string tag = r != null && r.Status == 200 ? Json.Str(Json.Get(Json.TryParse(r.Body), "tag_name")) : null;
                if (tag == null || Parse(tag) == null)
                {
                    due = now + RetryMs;
                    Log.Info("updates: couldn't check (" + (t.IsFaulted ? t.Exception.GetBaseException().GetType().Name : r == null ? "no reply" : r.Status != 200 ? "status " + r.Status : "no version") + ")");
                    return;
                }
                due = now + EveryMs;
                if (!Newer(tag, current))
                {
                    Log.Info("updates: up to date");
                    return;
                }
                if (tag == Available) return;
                Available = tag;
                Log.Info("updates: " + tag + " is available");
                if (Found != null) Found();
            }
            catch (Exception e) { Log.Error("updates: applying a check threw " + e.GetType().Name, null); }
        }

        // v1.2.3 as {1, 2, 3}; null for anything else ("dev", "v1.2", "1.2.3", "v1.2.3-beta").
        public static int[] Parse(string version)
        {
            Match m = version != null ? Shape.Match(version) : null;
            if (m == null || !m.Success) return null;
            return new[]
            {
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            };
        }

        // Whether candidate is a later version than current; false when either isn't one.
        public static bool Newer(string candidate, string current)
        {
            int[] a = Parse(candidate), b = Parse(current);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
                if (a[i] != b[i]) return a[i] > b[i];
            return false;
        }
    }
}
