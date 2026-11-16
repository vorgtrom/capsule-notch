// Standard API token prices as of 2026-11-16, in USD per million tokens.
// https://platform.claude.com/docs/en/about-claude/pricing#model-pricing
// https://developers.openai.com/api/docs/pricing.md#standard-pricing-data
// Use base context rates. Codex's estimate uses input, cached input and output only.
using System;
using System.Globalization;

namespace Capsule
{
    public static class Prices
    {
        sealed class Rate
        {
            public readonly string Prefix;
            public readonly decimal Input, Write5m, Write1h, CacheRead, Output;

            public Rate(string prefix, decimal input, decimal write5m, decimal write1h, decimal cacheRead, decimal output)
            {
                Prefix = prefix;
                Input = input;
                Write5m = write5m;
                Write1h = write1h;
                CacheRead = cacheRead;
                Output = output;
            }
        }

        static readonly Rate[] rates =
        {
            // Model prefix, input, 5-minute write, 1-hour write, cache read, output.
            new Rate("claude-opus-4",        15m, 18.75m,   30m,  1.50m,   75m),
            new Rate("claude-opus-4-1",      15m, 18.75m,   30m,  1.50m,   75m),
            new Rate("claude-opus-4-5",       5m,  6.25m,   10m,  0.50m,   25m),
            new Rate("claude-opus-4-6",       5m,  6.25m,   10m,  0.50m,   25m),
            new Rate("claude-opus-4-7",       5m,  6.25m,   10m,  0.50m,   25m),
            new Rate("claude-opus-4-8",       5m,  6.25m,   10m,  0.50m,   25m),
            new Rate("claude-opus-5",         5m,  6.25m,   10m,  0.50m,   25m),
            new Rate("claude-opus-5-5",       4m,     5m,    8m,  0.20m,   20m),
            new Rate("claude-sonnet-4",       3m,  3.75m,    6m,  0.30m,   15m),
            new Rate("claude-sonnet-4-5",     3m,  3.75m,    6m,  0.30m,   15m),
            new Rate("claude-sonnet-4-6",     3m,  3.75m,    6m,  0.30m,   15m),
            new Rate("claude-sonnet-5",       2m,  2.50m,    4m,  0.20m,   10m),
            new Rate("claude-sonnet-5-5",     2m,  2.50m,    4m,  0.10m,   10m),
            new Rate("claude-haiku-4-5",      1m,  1.25m,    2m,  0.10m,    5m),
            new Rate("claude-haiku-5-5",   0.10m, 0.125m, 0.20m,  0.01m, 0.50m),
            new Rate("claude-fable-5",       10m, 12.50m,   20m,     1m,   50m),
            new Rate("claude-fable-5-1",     10m, 12.50m,   20m,  0.25m,   50m),
            new Rate("claude-fable-5-2",      8m,    10m,   16m,  0.20m,   40m),

            new Rate("gpt-6-astra",          10m,     0m,    0m,     1m,   50m),
            new Rate("gpt-6.1-sol",           2m,     0m,    0m,  0.10m,   10m),
            new Rate("gpt-6.1-terra",         1m,     0m,    0m,  0.05m,    5m),
            new Rate("gpt-6-sol",             2m,     0m,    0m,  0.20m,   10m),
            new Rate("gpt-6-luna",         0.10m,     0m,    0m,  0.01m, 0.50m),
            new Rate("gpt-5.6-sol",           4m,     0m,    0m,  0.40m,   20m),
            new Rate("gpt-5.6-terra",         2m,     0m,    0m,  0.20m,   12m),
            new Rate("gpt-5.6-luna",       0.20m,     0m,    0m,  0.02m, 1.20m),
            new Rate("gpt-5.5",               5m,     0m,    0m,  0.50m,   30m),
            new Rate("gpt-5.5-pro",          30m,     0m,    0m,     0m,  180m),
            new Rate("gpt-5.4",            2.50m,     0m,    0m,  0.25m,   15m),
            new Rate("gpt-5.4-pro",          30m,     0m,    0m,     0m,  180m),
            new Rate("gpt-5.4-mini",       0.75m,     0m,    0m, 0.075m, 4.50m),
            new Rate("gpt-5.4-nano",       0.20m,     0m,    0m,  0.02m, 1.25m),
            new Rate("gpt-5.3-codex",      1.75m,     0m,    0m, 0.175m,   14m),
            new Rate("gpt-5.2",            1.75m,     0m,    0m, 0.175m,   14m),
            new Rate("gpt-5.2-pro",          21m,     0m,    0m,     0m,  168m),
            new Rate("gpt-5.1",            1.25m,     0m,    0m, 0.125m,   10m),
            new Rate("gpt-5",              1.25m,     0m,    0m, 0.125m,   10m),
            new Rate("gpt-5-pro",            15m,     0m,    0m,     0m,  120m),
            new Rate("gpt-5-mini",         0.25m,     0m,    0m, 0.025m,    2m),
            new Rate("gpt-5-nano",         0.05m,     0m,    0m, 0.005m, 0.40m),
            new Rate("gpt-4.1",               2m,     0m,    0m,  0.50m,    8m),
            new Rate("gpt-4.1-mini",       0.40m,     0m,    0m,  0.10m, 1.60m),
            new Rate("gpt-4o",             2.50m,     0m,    0m,  1.25m,   10m),
            new Rate("gpt-4o-mini",        0.15m,     0m,    0m, 0.075m, 0.60m),
            new Rate("o3",                    2m,     0m,    0m,  0.50m,    8m),
            new Rate("o3-pro",               20m,     0m,    0m,     0m,   80m)
        };

        static readonly string[] snapshotDateFormats = { "yyyyMMdd", "yyyy-MM-dd" };

        public static decimal? Estimate(string model, long input, long write5m, long write1h, long cacheRead, long output)
        {
            if (string.IsNullOrEmpty(model)) return null;
            Rate best = null;
            foreach (Rate rate in rates)
                if ((best == null || rate.Prefix.Length > best.Prefix.Length)
                    && model.StartsWith(rate.Prefix, StringComparison.OrdinalIgnoreCase))
                    best = rate;
            if (best == null) return null;

            // Dated snapshots use the model's rate. Unlisted model variants have no estimate.
            int length = best.Prefix.Length;
            DateTime date;
            if (model.Length != length && (model[length] != '-' || !DateTime.TryParseExact(model.Substring(length + 1),
                snapshotDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))) return null;

            return (input * best.Input + write5m * best.Write5m + write1h * best.Write1h
                + cacheRead * best.CacheRead + output * best.Output) / 1000000m;
        }
    }
}
