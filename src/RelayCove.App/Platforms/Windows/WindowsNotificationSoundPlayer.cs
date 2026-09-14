using Windows.Media.Core;
using Windows.Media.Playback;

namespace RelayCove.App.Platforms.Windows;

internal sealed class WindowsNotificationSoundPlayer : IDisposable
{
    private readonly Func<bool> _isSoundAllowed;
    private readonly Action _play;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private bool _disposed;

    internal WindowsNotificationSoundPlayer()
    {
        _isSoundAllowed = WindowsNotificationSoundPolicy.IsSoundAllowed;
        _play = PlayCore;
    }

    internal WindowsNotificationSoundPlayer(Func<bool> isSoundAllowed, Action play)
    {
        _isSoundAllowed = isSoundAllowed;
        _play = play;
    }

    internal bool TryPlay()
    {
        if (_disposed) return false;
        try
        {
            if (!_isSoundAllowed()) return false;
            _play();
            return true;
        }
        catch (Exception)
        {
            // Audio is best effort; it must never prevent the message or toast.
            return false;
        }
    }

    private void PlayCore()
    {
        if (_player is null)
        {
            var player = new MediaPlayer
            {
                AutoPlay = false,
                IsLoopingEnabled = false,
                AudioCategory = MediaPlayerAudioCategory.SoundEffects
            };
            MediaSource? source = null;
            try
            {
                player.CommandManager.IsEnabled = false;
                source = MediaSource.CreateFromUri(new Uri(Path.Combine(
                    AppContext.BaseDirectory, "Assets", "Audio", "mattermost_bing.mp3")));
                player.Source = source;
                _source = source;
                _player = player;
            }
            catch
            {
                player.Dispose();
                source?.Dispose();
                throw;
            }
        }

        // Reuse one player, so a burst of notifications never overlaps multiple sounds.
        _player.PlaybackSession.Position = TimeSpan.Zero;
        _player.Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player?.Dispose();
        _player = null;
        _source?.Dispose();
        _source = null;
    }
}
