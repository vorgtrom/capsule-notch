namespace Capsule
{
    // Capsule's own Google OAuth client, for signing in to Google Calendar in one click. The source holds only these
    // placeholders: the Release workflow writes the real ID and secret in from the repository's secrets, so a source
    // build has no built-in client and offers the calendar link and your own client instead. Google treats a desktop
    // app's client secret as not secret (it ships inside the app); PKCE guards each sign-in.
    public static class BuiltInGoogle
    {
        public const string Id = "@CAPSULE_GOOGLE_CLIENT_ID@";
        public const string Secret = "@CAPSULE_GOOGLE_CLIENT_SECRET@";

        public static bool Available { get { return IsSet(Id) && IsSet(Secret); } }

        // A value the Release workflow filled in, not a placeholder or nothing.
        internal static bool IsSet(string value) { return !string.IsNullOrEmpty(value) && !value.StartsWith("@", System.StringComparison.Ordinal); }

        // The client secret for a client ID: the built-in one's from the build, any other the one saved for it.
        internal static string SecretFor(string clientId, string builtInId, string builtInSecret, System.Func<string> saved)
        {
            if (IsSet(builtInId) && IsSet(builtInSecret) && clientId == builtInId) return builtInSecret;
            return saved();
        }
    }
}
