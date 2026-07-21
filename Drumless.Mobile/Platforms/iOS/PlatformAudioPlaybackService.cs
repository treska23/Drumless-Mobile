using AVFoundation;
using Drumless.Mobile.Services;
using Foundation;

namespace Drumless.Mobile;

public sealed class PlatformAudioPlaybackService : IAudioPlaybackService
{
    private AVAudioPlayer? _player;

    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<bool>? PlaybackStateChanged;

    public bool IsPlaying => _player?.Playing == true;
    public double PositionSeconds => Math.Max(0d, _player?.CurrentTime ?? 0d);

    public Task PlayAsync(string filePath)
    {
        Stop();
        try
        {
            var session = AVAudioSession.SharedInstance();
            session.SetCategory(AVAudioSessionCategory.Playback);
            session.SetActive(true);

            _player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(filePath));
            if (_player is null)
            {
                throw new InvalidOperationException("iOS no pudo abrir este formato de audio.");
            }

            _player.FinishedPlaying += (_, _) =>
            {
                PlaybackStateChanged?.Invoke(this, false);
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            };
            _player.PrepareToPlay();
            if (!_player.Play())
            {
                throw new InvalidOperationException("iOS rechazó el inicio de la reproducción.");
            }

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

        if (_player.Playing)
        {
            _player.Pause();
            PlaybackStateChanged?.Invoke(this, false);
        }
        else
        {
            _player.Play();
            PlaybackStateChanged?.Invoke(this, true);
        }
    }

    public void Pause()
    {
        if (_player?.Playing != true)
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

        _player.Stop();
        _player.Dispose();
        _player = null;
        PlaybackStateChanged?.Invoke(this, false);
    }

    public void Dispose() => Stop();
}
