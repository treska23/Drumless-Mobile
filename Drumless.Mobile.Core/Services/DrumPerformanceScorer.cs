using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed class DrumPerformanceScorer
{
    public const double AccurateToleranceMilliseconds = 45d;

    private readonly List<DrumHit> _hits = [];
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public TempoProfile? Tempo { get; private set; }
    public double LatencyCompensationMilliseconds { get; private set; }
    public IReadOnlyList<DrumHit> Hits => _hits;

    public void Start(TempoProfile tempo, double latencyCompensationMilliseconds)
    {
        lock (_gate)
        {
            Tempo = TempoProfile.Normalize(tempo);
            LatencyCompensationMilliseconds = Math.Clamp(
                latencyCompensationMilliseconds,
                -1_000d,
                1_000d);
            _hits.Clear();
            IsActive = true;
        }
    }

    public DrumHit? Record(double transportPositionSeconds, int midiNote, int velocity)
    {
        lock (_gate)
        {
            if (!IsActive || Tempo is null || velocity <= 0)
            {
                return null;
            }

            var compensated = Math.Max(
                0d,
                transportPositionSeconds -
                LatencyCompensationMilliseconds / 1_000d);
            var errorMilliseconds =
                TempoGrid.NearestGridErrorSeconds(compensated, Tempo) * 1_000d;
            var hit = new DrumHit(compensated, midiNote, velocity, errorMilliseconds);
            _hits.Add(hit);
            return hit;
        }
    }

    public DrumPerformanceResult Finish()
    {
        lock (_gate)
        {
            IsActive = false;
            if (_hits.Count == 0)
            {
                return new DrumPerformanceResult(0, 0, 0, 0, 0d, 0d, 0d);
            }

            var errors = _hits.Select(hit => hit.ErrorMilliseconds).ToArray();
            var accurate = errors.Count(error =>
                Math.Abs(error) <= AccurateToleranceMilliseconds);
            var early = errors.Count(error =>
                error < -AccurateToleranceMilliseconds);
            var late = errors.Count(error =>
                error > AccurateToleranceMilliseconds);
            return new DrumPerformanceResult(
                errors.Length,
                accurate,
                early,
                late,
                accurate * 100d / errors.Length,
                errors.Average(Math.Abs),
                errors.Max(error => Math.Abs(error)));
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            IsActive = false;
            _hits.Clear();
        }
    }
}
