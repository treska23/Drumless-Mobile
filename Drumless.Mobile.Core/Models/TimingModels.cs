namespace Drumless.Mobile.Core.Models;

public sealed record TempoAnalysisResult(
    double Bpm,
    double FirstBeatSeconds,
    double Confidence);

public sealed record DrumHit(
    double TrackPositionSeconds,
    int MidiNote,
    int Velocity,
    double ErrorMilliseconds);

public sealed record DrumPerformanceResult(
    int TotalHits,
    int AccurateHits,
    int EarlyHits,
    int LateHits,
    double AccuracyPercent,
    double MeanAbsoluteErrorMilliseconds,
    double MaximumErrorMilliseconds);

public sealed record MidiNoteEvent(
    int Note,
    int Velocity,
    long TimestampNanoseconds);
