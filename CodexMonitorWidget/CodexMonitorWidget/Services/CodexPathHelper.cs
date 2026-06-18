namespace CodexMonitorWidget.Services;

public static class CodexPathHelper
{
    public static string DefaultHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(env))
                return ExpandTilde(env);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }
    }

    public static string DefaultHomePath => DefaultHome;

    public static string DefaultSessionsPath =>
        Path.Combine(DefaultHome, "sessions");

    public static bool HomeExists => Directory.Exists(DefaultHomePath);

    public static bool SessionsExists => Directory.Exists(DefaultSessionsPath);

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.Equals("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1
                ? home
                : Path.Combine(home, path[2..]);
        }

        return path;
    }
}
