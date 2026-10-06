namespace Capsule
{
    // Which Capsule this is. "dev" when built from source; the Release workflow writes the release's version (such as
    // v1.2.0) here before it builds, so a downloaded Capsule can tell when a newer one is out.
    public static class AppVersion
    {
        public const string Current = "dev";
    }
}
