using Velopack;
using Velopack.Sources;

namespace Ember.Services;

/// <summary>Built-in updater. Checks GitHub Releases (published by the release workflow) via Velopack.</summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
        }
        catch
        {
            _manager = null;
        }
    }

    /// <summary>False when running from Visual Studio / dotnet run instead of an installed build.</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    public async Task<UpdateInfo?> CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    public Task DownloadAsync(UpdateInfo info, Action<int> progress)
    {
        if (_manager is null) return Task.CompletedTask;
        return _manager.DownloadUpdatesAsync(info, progress);
    }

    public void ApplyAndRestart(UpdateInfo info) => _manager?.ApplyUpdatesAndRestart(info.TargetFullRelease);
}
