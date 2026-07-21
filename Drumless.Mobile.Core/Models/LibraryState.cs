namespace Drumless.Mobile.Core.Models;

public enum MediaKind
{
    LocalAudio,
    YouTube
}

public enum PlaybackMode
{
    Single,
    Sequential,
    Shuffle
}

public enum MediaTitleOrigin
{
    Unknown,
    Manual,
    YouTube,
    Fallback
}

public sealed class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MediaKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public MediaTitleOrigin TitleOrigin { get; set; }
    public string? LocalPath { get; set; }
    public string? OriginalFileName { get; set; }
    public string? YouTubeVideoId { get; set; }
    public string? YouTubeUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public TempoProfile? Tempo { get; set; }
    public List<PerformanceSession> PerformanceSessions { get; set; } = [];

    public string MediaKey => Kind switch
    {
        MediaKind.LocalAudio => $"local:{LocalPath}",
        MediaKind.YouTube => $"youtube:{YouTubeVideoId}",
        _ => Id
    };
}

public sealed record TempoProfile(
    double Bpm,
    double FirstBeatSeconds,
    double Confidence,
    string Source,
    int SubdivisionsPerBeat = 4)
{
    public static TempoProfile Normalize(TempoProfile profile) => profile with
    {
        Bpm = double.IsFinite(profile.Bpm) ? Math.Clamp(profile.Bpm, 40d, 240d) : 120d,
        FirstBeatSeconds = double.IsFinite(profile.FirstBeatSeconds)
            ? Math.Max(0d, profile.FirstBeatSeconds)
            : 0d,
        Confidence = double.IsFinite(profile.Confidence)
            ? Math.Clamp(profile.Confidence, 0d, 1d)
            : 0d,
        Source = string.IsNullOrWhiteSpace(profile.Source) ? "Manual" : profile.Source.Trim(),
        SubdivisionsPerBeat = Math.Clamp(profile.SubdivisionsPerBeat, 1, 8)
    };
}

public sealed record PerformanceSession(
    string Id,
    DateTimeOffset FinishedAtUtc,
    int TotalHits,
    int AccurateHits,
    int EarlyHits,
    int LateHits,
    double AccuracyPercent,
    double MeanAbsoluteErrorMilliseconds,
    double MaximumErrorMilliseconds,
    double LatencyCompensationMilliseconds);

public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<MediaItem> Items { get; set; } = [];
}

public sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 2;
    public List<Playlist> Playlists { get; set; } = [];
    public string? SelectedPlaylistId { get; set; }
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    public double TimingLatencyCompensationMilliseconds { get; set; }
}
