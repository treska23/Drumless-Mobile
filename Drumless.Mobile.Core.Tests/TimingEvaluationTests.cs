using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;

namespace Drumless.Mobile.Core.Tests;

[TestClass]
public sealed class TimingEvaluationTests
{
    [TestMethod]
    public void Scorer_ClassifiesHitsAndAppliesBluetoothCompensation()
    {
        var scorer = new DrumPerformanceScorer();
        scorer.Start(
            new TempoProfile(120d, 0.25d, 1d, "Prueba"),
            latencyCompensationMilliseconds: 120d);

        scorer.Record(0.370d, 36, 110);
        scorer.Record(0.295d, 38, 100);
        scorer.Record(0.445d, 42, 90);
        var result = scorer.Finish();

        Assert.AreEqual(3, result.TotalHits);
        Assert.AreEqual(1, result.AccurateHits);
        Assert.AreEqual(1, result.EarlyHits);
        Assert.AreEqual(1, result.LateHits);
    }

    [TestMethod]
    public void Grid_UsesConfiguredSixteenthNotePhase()
    {
        var tempo = new TempoProfile(120d, 0.25d, 1d, "Prueba");

        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(0.25d, tempo), 1e-9d);
        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(0.375d, tempo), 1e-9d);
        Assert.AreEqual(0.030d, TempoGrid.NearestGridErrorSeconds(0.405d, tempo), 1e-9d);
    }

    [TestMethod]
    public void TapTempo_EstimatesBpmAndFirstTap()
    {
        var estimator = new TapTempoEstimator();
        TempoAnalysisResult? result = null;
        foreach (var position in new[] { 1.25d, 1.75d, 2.25d, 2.75d, 3.25d })
        {
            result = estimator.AddTap(position);
        }

        Assert.IsNotNull(result);
        Assert.AreEqual(120d, result.Bpm, 0.01d);
        Assert.AreEqual(1.25d, result.FirstBeatSeconds, 1e-9d);
        Assert.IsGreaterThan(0.95d, result.Confidence);
    }

    [TestMethod]
    public void EnvelopeAnalyzer_DetectsSynthetic120Bpm()
    {
        var rms = new double[TempoEnvelopeAnalyzer.EnvelopeRate * 18];
        var first = (int)(0.25d * TempoEnvelopeAnalyzer.EnvelopeRate);
        var step = (int)(0.5d * TempoEnvelopeAnalyzer.EnvelopeRate);
        for (var beat = first; beat < rms.Length; beat += step)
        {
            rms[beat] = 1d;
            if (beat + 1 < rms.Length)
            {
                rms[beat + 1] = 0.35d;
            }
        }

        var result = TempoEnvelopeAnalyzer.AnalyzeRms(rms);

        Assert.AreEqual(120d, result.Bpm, 1.5d);
        Assert.AreEqual(0.25d, result.FirstBeatSeconds, 0.04d);
    }
}
