using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace Capsule
{
    public enum Cue { None, Attention, Done }

    // Short sounds for what needs you: made here as WAV in memory (two soft bell notes each), so there are no sound files
    // to ship and nothing is downloaded. They play at Windows' own volume.
    public static class Sounds
    {
        const int Rate = 22050;
        const long GapMs = 1500;   // a burst of events plays one sound
        static readonly Dictionary<Cue, SoundPlayer> players = new Dictionary<Cue, SoundPlayer>();
        static long lastAt;

        // Rising for "needs you", falling for "done".
        static double[] Notes(Cue cue) { return cue == Cue.Attention ? new[] { 659.25, 987.77 } : new[] { 1046.5, 783.99 }; }

        public static void Play(Cue cue, long now)
        {
            if (cue == Cue.None || now - lastAt < GapMs) return;
            lastAt = now;
            try
            {
                SoundPlayer player;
                if (!players.TryGetValue(cue, out player))
                {
                    player = new SoundPlayer(new MemoryStream(Wav(Notes(cue))));
                    player.Load();
                    players[cue] = player;   // kept: a player that is let go can cut its sound short
                }
                player.Play();   // plays on its own thread
            }
            catch (Exception e) { Log.Error("sound", e); }
        }

        // 16-bit mono PCM: each note a bell (a sine with a touch of its octave) that fades, the second starting 110 ms in.
        public static byte[] Wav(double[] notes)
        {
            int length = (int)(Rate * 0.75);
            var samples = new double[length];
            for (int n = 0; n < notes.Length; n++)
            {
                int start = (int)(Rate * 0.11 * n);
                for (int i = start; i < length; i++)
                {
                    double t = (double)(i - start) / Rate;
                    double env = Math.Min(1, t / 0.004) * Math.Exp(-t / 0.18);
                    samples[i] += 0.22 * env * (Math.Sin(2 * Math.PI * notes[n] * t) + 0.25 * Math.Sin(4 * Math.PI * notes[n] * t) * Math.Exp(-t / 0.05));
                }
            }
            var data = new MemoryStream();
            var w = new BinaryWriter(data);
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + length * 2);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);       // PCM
            w.Write((short)1);       // mono
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(length * 2);
            foreach (double s in samples) w.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s * short.MaxValue)));
            w.Flush();
            return data.ToArray();
        }
    }

    // Which sound a change in the sessions calls for: a session that started waiting on you, or one that finished its work.
    // Remembers each session's last state; the first look plays nothing.
    public sealed class SessionCues
    {
        Dictionary<string, string> last;

        public Cue Next(IEnumerable<SessionStatus> sessions)
        {
            var now = new Dictionary<string, string>();
            foreach (SessionStatus s in sessions) now[s.SessionId] = s.State;
            Dictionary<string, string> before = last;
            last = now;
            if (before == null) return Cue.None;
            Cue cue = Cue.None;
            foreach (KeyValuePair<string, string> s in now)
            {
                string was;
                before.TryGetValue(s.Key, out was);
                if (s.Value == States.Waiting && was != States.Waiting) return Cue.Attention;   // the most urgent wins
                if (s.Value == States.Done && was == States.Working) cue = Cue.Done;
            }
            return cue;
        }
    }
}
