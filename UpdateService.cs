using Velopack;
using Velopack.Sources;

namespace TikTokLiveAutoLiker;

/// <summary>
/// Watches GitHub releases for a newer build and downloads it in the background.
///
/// It never restarts on its own. This app is meant to be left running for hours, and pulling
/// the rug mid-session would drop an active run without warning, so applying the update is
/// always the user's call.
///
/// The repository is public, so no token is needed and none is baked into the binary.
/// </summary>
sealed class UpdateService : IDisposable
{
    const string RepoUrl = "https://github.com/hexbyt3/tiktok-live-auto-liker";
    static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    readonly CancellationTokenSource _cts = new();
    UpdateManager? _manager;
    UpdateInfo? _pending;

    /// <summary>Raised on the UI thread once a newer release is downloaded and ready.</summary>
    public event Action<string>? UpdateReady;

    public bool HasPendingUpdate => _pending is not null;

    /// <summary>Polls until disposed. Safe to fire and forget from the UI thread.</summary>
    public async Task RunAsync()
    {
        // Dev builds and the portable zip aren't installed through Velopack, so there is
        // nothing for the updater to replace.
        if (!VelopackRuntimeInfo.IsWindows) return;

        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
            if (!_manager.IsInstalled) return;
        }
        catch (Exception)
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            // A failed check must never take the app down; transient GitHub errors just
            // resolve on the next pass.
            try
            {
                await CheckOnceAsync();
            }
            catch (Exception) { }

            try
            {
                await Task.Delay(PollInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task CheckOnceAsync()
    {
        if (_manager is not { } manager) return;

        var info = await manager.CheckForUpdatesAsync();
        if (info is null) return;

        var version = info.TargetFullRelease?.Version?.ToString() ?? "";
        if (_pending?.TargetFullRelease?.Version?.ToString() == version) return;

        await manager.DownloadUpdatesAsync(info);
        _pending = info;
        UpdateReady?.Invoke(version);
    }

    public void ApplyAndRestart()
    {
        if (_manager is { } manager && _pending is { } info)
            manager.ApplyUpdatesAndRestart(info);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
