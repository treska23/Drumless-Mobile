using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public static class TempoEnvelopeAnalyzer
{
    public const int EnvelopeRate = 200;
    private const double MinimumBpm = 60d;
    private const double MaximumBpm = 200d;

    public static TempoAnalysisResult AnalyzeRms(
        IReadOnlyList<double> rmsEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rmsEnvelope);
        var onset = new double[rmsEnvelope.Count];
        var previous = rmsEnvelope.Count > 0 ? rmsEnvelope[0] : 0d;
        for (var index = 1; index < rmsEnvelope.Count; index++)
        {
            var smoothedPrevious = previous * 0.92d + rmsEnvelope[index - 1] * 0.08d;
            onset[index] = Math.Max(0d, rmsEnvelope[index] - smoothedPrevious);
            previous = smoothedPrevious;
        }

        return AnalyzeOnsets(onset, cancellationToken);
    }

    public static TempoAnalysisResult AnalyzeOnsets(
        IReadOnlyList<double> onsetEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onsetEnvelope);
        if (onsetEnvelope.Count < EnvelopeRate * 4 ||
            onsetEnvelope.Count == 0 ||
            onsetEnvelope.Max() <= 1e-8d)
        {
            throw new InvalidDataException(
                "La pista no contiene suficientes pulsos para estimar el tempo.");
        }

        var minimumLag = (int)Math.Floor(EnvelopeRate * 60d / MaximumBpm);
        var maximumLag = (int)Math.Ceiling(EnvelopeRate * 60d / MinimumBpm);
        var scores = new double[maximumLag + 1];
        var bestLag = minimumLag;
        var bestScore = double.MinValue;
        for (var lag = minimumLag; lag <= maximumLag; lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double numerator = 0d;
            double leftEnergy = 0d;
            double rightEnergy = 0d;
            for (var index = lag; index < onsetEnvelope.Count; index++)
            {
                var left = onsetEnvelope[index];
                var right = onsetEnvelope[index - lag];
                numerator += left * right;
                leftEnergy += left * left;
                rightEnergy += right * right;
            }

            var score = numerator /
                        Math.Sqrt(Math.Max(1e-20d, leftEnergy * rightEnergy));
            scores[lag] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        var onsetLag = EstimateOnsetInterval(onsetEnvelope, minimumLag, maximumLag);
        if (onsetLag is { } observedLag &&
            scores[observedLag] >= bestScore * 0.85d)
        {
            bestLag = observedLag;
            bestScore = scores[observedLag];
        }

        var bpm = 60d * EnvelopeRate / bestLag;
        var searchLimit = Math.Min(onsetEnvelope.Count, EnvelopeRate * 30);
        var maximumOnset = onsetEnvelope.Take(searchLimit).Max();
        var threshold = maximumOnset * 0.55d;
        var firstOnset = -1;
        for (var index = 0; index < searchLimit; index++)
        {
            if (onsetEnvelope[index] >= threshold)
            {
                firstOnset = index;
                break;
            }
        }
        if (firstOnset < 0)
        {
            firstOnset = onsetEnvelope
                .Take(searchLimit)
                .Select((value, index) => (value, index))
                .MaxBy(pair => pair.value).index;
        }

        var sortedScores = scores
            .Skip(minimumLag)
            .Where(score => score > 0d)
            .OrderDescending()
            .Take(8)
            .ToArray();
        var competing = sortedScores.Length > 1 ? sortedScores[1] : 0d;
        var distinctness = bestScore <= 0d
            ? 0d
            : Math.Clamp((bestScore - competing) / bestScore, 0d, 1d);
        var confidence = Math.Clamp(
            bestScore * 0.75d + distinctness * 0.25d,
            0d,
            1d);
        return new TempoAnalysisResult(
            Math.Round(bpm, 2),
            firstOnset / (double)EnvelopeRate,
            confidence);
    }

    private static int? EstimateOnsetInterval(
        IReadOnlyList<double> envelope,
        int minimumLag,
        int maximumLag)
    {
        var maximum = envelope.Max();
        var threshold = maximum * 0.42d;
        var refractory = Math.Max(1, minimumLag / 2);
        var peaks = new List<int>();
        var lastPeak = -refractory;
        for (var index = 1; index < envelope.Count - 1; index++)
        {
            if (envelope[index] < threshold ||
                envelope[index] < envelope[index - 1] ||
                envelope[index] < envelope[index + 1] ||
                index - lastPeak < refractory)
            {
                continue;
            }

            peaks.Add(index);
            lastPeak = index;
        }

        if (peaks.Count < 4)
        {
            return null;
        }

        var intervals = peaks.Zip(peaks.Skip(1), (left, right) => right - left)
            .Where(interval => interval >= minimumLag && interval <= maximumLag)
            .Order()
            .ToArray();
        return intervals.Length < 3 ? null : intervals[intervals.Length / 2];
    }
}
