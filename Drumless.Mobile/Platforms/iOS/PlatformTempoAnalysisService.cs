using AVFoundation;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;
using Drumless.Mobile.Services;
using Foundation;

namespace Drumless.Mobile;

public sealed class PlatformTempoAnalysisService : IAudioTempoAnalysisService
{
    public Task<TempoAnalysisResult> AnalyzeAsync(
        string filePath,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(filePath, cancellationToken),
        cancellationToken);

    private static unsafe TempoAnalysisResult Analyze(
        string filePath,
        CancellationToken cancellationToken)
    {
        using var url = NSUrl.FromFilename(filePath);
        using var file = new AVAudioFile(
            url,
            AVAudioCommonFormat.PCMFloat32,
            interleaved: false,
            out var openError);
        if (openError is not null)
        {
            throw new InvalidDataException(openError.LocalizedDescription);
        }

        var format = file.ProcessingFormat;
        var sampleRate = (int)Math.Round(format.SampleRate);
        var channels = (int)format.ChannelCount;
        var envelope = new RmsEnvelopeBuilder(sampleRate);
        using var buffer = new AVAudioPcmBuffer(format, 32_768);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.ReadIntoBuffer(buffer, out var readError);
            if (!read)
            {
                if (readError is not null)
                {
                    throw new InvalidDataException(readError.LocalizedDescription);
                }
                break;
            }
            if (buffer.FrameLength == 0)
            {
                break;
            }

            var channelData = (float**)buffer.FloatChannelData;
            for (var frame = 0; frame < buffer.FrameLength; frame++)
            {
                double mono = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    mono += channelData[channel][frame];
                }
                envelope.Add(mono / Math.Max(1, channels));
            }
        }

        return TempoEnvelopeAnalyzer.AnalyzeRms(
            envelope.Finish(),
            cancellationToken);
    }
}
