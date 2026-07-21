using Android.Media;
using Drumless.Mobile.Services;

namespace Drumless.Mobile;

public sealed class PlatformAudioPlaybackService : IAudioPlaybackService
{
    private MediaPlayer? _player;

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
        try
        {
            var uri = Android.Net.Uri.FromFile(new Java.IO.File(filePath));
            _player = MediaPlayer.Create(Android.App.Application.Context, uri);
            if (_player is null)
            {
                throw new InvalidOperationException("Android no pudo abrir este formato de audio.");
            }

            _player.Completion += (_, _) =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PlaybackStateChanged?.Invoke(this, false);
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                });
            _player.Start();
            PlaybackStateChanged?.Invoke(this, true);
        }
        catch (Exception exception)
        {
            Stop();
            PlaybackFailed?.Invoke(this, $"No se pudo reproducir el audio: {exception.Message}");
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

        _player.Pause();
        PlaybackStateChanged?.Invoke(this, false);
    }

    public void Stop()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            _player.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // El reproductor todavía no había alcanzado un estado reproducible.
        }

        _player.Release();
        _player.Dispose();
        _player = null;
        PlaybackStateChanged?.Invoke(this, false);
    }

    public void Dispose() => Stop();
}
