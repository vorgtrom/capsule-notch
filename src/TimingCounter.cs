using System.Diagnostics;

namespace Capsule
{
    internal sealed class TimingCounter
    {
        internal const long HourMs = 60 * 60 * 1000;
        readonly string name;
        readonly object gate = new object();
        long ticks, count;

        internal TimingCounter(string name) { this.name = name; }
        internal static long Start() { return Stopwatch.GetTimestamp(); }

        internal void Stop(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            lock (gate)
            {
                ticks += elapsed;
                count++;
            }
        }

        internal string Take()
        {
            lock (gate)
            {
                string text = name + "=" + ticks * 1000 / Stopwatch.Frequency + "ms/" + count;
                ticks = count = 0;
                return text;
            }
        }
    }
}
