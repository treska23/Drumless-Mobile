namespace Drumless.Mobile.Core.Services;

public sealed class RmsEnvelopeBuilder
{
    private readonly int _framesPerEnvelope;
    private readonly List<double> _energies = [];
    private double _energy;
    private int _frames;

    public RmsEnvelopeBuilder(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _framesPerEnvelope = Math.Max(
            1,
            sampleRate / TempoEnvelopeAnalyzer.EnvelopeRate);
    }

    public void Add(double monoSample)
    {
        if (!double.IsFinite(monoSample))
        {
            return;
        }

        _energy += monoSample * monoSample;
        _frames++;
        if (_frames < _framesPerEnvelope)
        {
            return;
        }

        Flush();
    }

    public double[] Finish()
    {
        if (_frames > 0)
        {
            Flush();
        }

        return _energies.ToArray();
    }

    private void Flush()
    {
        _energies.Add(Math.Sqrt(_energy / _frames));
        _energy = 0d;
        _frames = 0;
    }
}
