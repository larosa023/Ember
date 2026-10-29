namespace Ember;

public static class AppInfo
{
    /// <summary>Your GitHub repo. The built-in updater checks this repo's Releases.</summary>
    public const string RepoUrl = "https://github.com/larosa023/Ember";

    public static string Version
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
