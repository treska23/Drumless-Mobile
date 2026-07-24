using Android.Media;
using Drumless.Mobile.Services;

namespace Drumless.Mobile;

public sealed class PlatformAudioPlaybackService : IAudioPlaybackService
{
    private MediaPlayer? _player;
    private PlayerErrorListener? _errorListener;
    private long _playbackGeneration;

    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<bool>? PlaybackStateChanged;

    public bool IsPlaying
    {
        get
        {
            try
            {
                return _player?.IsPlaying == true;
            }
            catch (Java.Lang.IllegalStateException)
            {
                return false;
            }
        }
    }

    public double PositionSeconds
    {
        get
        {
            try
            {
                return Math.Max(0d, (_player?.CurrentPosition ?? 0) / 1_000d);
            }
            catch (Java.Lang.IllegalStateException)
            {
                return 0d;
            }
        }
    }

    public Task PlayAsync(string filePath)
    {
        Stop();
        var generation = Volatile.Read(ref _playbackGeneration);

        try
        {
            var uri = Android.Net.Uri.FromFile(new Java.IO.File(filePath));
            var player = MediaPlayer.Create(Android.App.Application.Context, uri);
            if (player is null)
            {
                throw new InvalidOperationException("Android no pudo abrir este formato de audio.");
            }

            _player = player;
            _errorListener = new PlayerErrorListener((failedPlayer, what, extra) =>
            {
                QueueUnreadableFileSkip(
                    failedPlayer,
                    generation,
                    $"Android no pudo leer este archivo de audio (error {(int)what}/{extra}).");
                return true;
            });
            player.SetOnErrorListener(_errorListener);

            player.Completion += (_, _) =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (generation != Volatile.Read(ref _playbackGeneration) ||
                        !ReferenceEquals(_player, player))
                    {
                        return;
                    }

                    ReleasePlayer(player);
                    PlaybackStateChanged?.Invoke(this, false);
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                });

            player.Start();
            PlaybackStateChanged?.Invoke(this, true);
        }
        catch (Exception exception)
        {
            QueueUnreadableFileSkip(
                _player,
                generation,
                $"No se pudo reproducir el audio: {exception.Message}");
        }

        return Task.CompletedTask;
    }

    public void Toggle()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                PlaybackStateChanged?.Invoke(this, false);
            }
            else
            {
                _player.Start();
                PlaybackStateChanged?.Invoke(this, true);
            }
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, $"No se pudo controlar el audio: {exception.Message}");
        }
    }

    public void Pause()
    {
        if (_player is null || !IsPlaying)
        {
            return;
        }

        try
        {
            _player.Pause();
            PlaybackStateChanged?.Invoke(this, false);
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, $"No se pudo pausar el audio: {exception.Message}");
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _playbackGeneration);
        if (_player is null)
        {
            return;
        }

        var player = _player;
        try
        {
            player.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // El reproductor todavía no había alcanzado un estado reproducible.
        }
        finally
        {
            ReleasePlayer(player);
            PlaybackStateChanged?.Invoke(this, false);
        }
    }

    private void QueueUnreadableFileSkip(
        MediaPlayer? failedPlayer,
        long generation,
        string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // A delayed error from a track the user has already changed must not skip the new one.
            if (generation != Volatile.Read(ref _playbackGeneration))
            {
                return;
            }

            if (failedPlayer is not null && !ReferenceEquals(_player, failedPlayer))
            {
                return;
            }

            if (_player is not null)
            {
                ReleasePlayer(_player);
                PlaybackStateChanged?.Invoke(this, false);
            }

            PlaybackFailed?.Invoke(this, $"{message} Se salta la pista.");

            // MainPage already advances automatically on PlaybackEnded. Raising it asynchronously
            // breaks the failure call stack, so several unreadable files can be skipped safely.
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ReleasePlayer(MediaPlayer expectedPlayer)
    {
        if (!ReferenceEquals(_player, expectedPlayer))
        {
            return;
        }

        _player = null;
        try
        {
            expectedPlayer.Release();
        }
        finally
        {
            expectedPlayer.Dispose();
            _errorListener?.Dispose();
            _errorListener = null;
        }
    }

    public void Dispose() => Stop();

    private sealed class PlayerErrorListener : Java.Lang.Object, MediaPlayer.IOnErrorListener
    {
        private readonly Func<MediaPlayer?, MediaError, int, bool> _handler;

        public PlayerErrorListener(Func<MediaPlayer?, MediaError, int, bool> handler)
        {
            _handler = handler;
        }

        public bool OnError(MediaPlayer? mp, MediaError what, int extra) =>
            _handler(mp, what, extra);
    }
}
