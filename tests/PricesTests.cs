namespace Capsule
{
    static class PricesTests
    {
        public static void Run()
        {
            const long m = 1000000;
            TestRunner.Eq(46.75m, Prices.Estimate("claude-opus-4-5-20251101", m, m, m, m, m),
                "the longer Claude prefix wins and all five token rates apply");
            TestRunner.Eq(15m, Prices.Estimate("claude-opus-4-20250514", m, 0, 0, 0, 0),
                "the shorter Claude model has its own price");
            TestRunner.Eq(0.20m, Prices.Estimate("claude-opus-5-5", 0, 0, 0, m, 0),
                "Claude Opus 5.5 has a lower cache-read multiplier");
            TestRunner.Eq(15.925m, Prices.Estimate("gpt-5.3-codex", m, 0, 0, m, m),
                "the Codex estimate uses separate input, cached input and output rates");
            TestRunner.Eq(0.00000175m, Prices.Estimate("gpt-5.2-2025-12-11", 1, 0, 0, 0, 0),
                "a dated OpenAI model keeps the precision of a single token price");
            TestRunner.Eq(17.6m, Prices.Estimate("gpt-6.1-sol", 2 * m, 0, 0, 6 * m, 1300000),
                "the GPT-6.1 Sol estimate uses the standard rates");
            TestRunner.Eq(0m, Prices.Estimate("gpt-5", 0, 0, 0, 0, 0), "known pricing can return zero");
            TestRunner.Eq(null, Prices.Estimate("gpt-5.7", m, 0, 0, 0, 0), "an unlisted version is unknown");
            TestRunner.Eq(null, Prices.Estimate("gpt-5-codex", m, 0, 0, 0, 0), "an unlisted variant is unknown");
            TestRunner.Eq(null, Prices.Estimate("claude-opus-4-50", m, 0, 0, 0, 0), "a similar model ID is unknown");
            TestRunner.Eq(null, Prices.Estimate(null, m, 0, 0, 0, 0), "a missing model is unknown");
        }
    }
}
