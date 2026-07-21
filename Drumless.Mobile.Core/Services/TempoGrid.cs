using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public static class TempoGrid
{
    public static double NearestGridErrorSeconds(
        double positionSeconds,
        TempoProfile tempo)
    {
        tempo = TempoProfile.Normalize(tempo);
        var step = 60d / tempo.Bpm / tempo.SubdivisionsPerBeat;
        var relative = positionSeconds - tempo.FirstBeatSeconds;
        var nearest = Math.Round(relative / step, MidpointRounding.AwayFromZero);
        return relative - nearest * step;
    }
}
