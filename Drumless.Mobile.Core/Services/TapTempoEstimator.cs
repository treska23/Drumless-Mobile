using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed class TapTempoEstimator
{
    private readonly List<double> _positions = [];

    public int TapCount => _positions.Count;

    public void Reset() => _positions.Clear();

    public TempoAnalysisResult? AddTap(double trackPositionSeconds)
    {
        if (!double.IsFinite(trackPositionSeconds) || trackPositionSeconds < 0d)
        {
            return null;
        }

        if (_positions.Count > 0 &&
            trackPositionSeconds - _positions[^1] > 3d)
        {
            _positions.Clear();
        }

        _positions.Add(trackPositionSeconds);
        if (_positions.Count > 16)
        {
            _positions.RemoveAt(0);
        }
        if (_positions.Count < 4)
        {
            return null;
        }

        var intervals = _positions
            .Zip(_positions.Skip(1), (left, right) => right - left)
            .Where(interval => interval is >= 0.18d and <= 1.5d)
            .Order()
            .ToArray();
        if (intervals.Length < 3)
        {
            return null;
        }

        var median = intervals[intervals.Length / 2];
        var bpm = 60d / median;
        while (bpm < 60d)
        {
            bpm *= 2d;
        }
        while (bpm > 200d)
        {
            bpm /= 2d;
        }

        var meanDeviation = intervals.Average(interval =>
            Math.Abs(interval - median));
        var confidence = Math.Clamp(
            1d - meanDeviation / Math.Max(0.001d, median * 0.18d),
            0d,
            1d);
        return new TempoAnalysisResult(
            Math.Round(bpm, 2),
            _positions[0],
            confidence);
    }
}
