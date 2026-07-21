namespace Drumless.Mobile.Services;

public interface IAudioPlaybackService : IDisposable
{
    event EventHandler? PlaybackEnded;
    event EventHandler<string>? PlaybackFailed;
    event EventHandler<bool>? PlaybackStateChanged;

    bool IsPlaying { get; }
    double PositionSeconds { get; }

    Task PlayAsync(string filePath);
    void Toggle();
    void Pause();
    void Stop();
}
