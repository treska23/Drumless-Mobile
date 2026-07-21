using System.Buffers.Binary;
using Android.Media;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;
using Drumless.Mobile.Services;

namespace Drumless.Mobile;

public sealed class PlatformTempoAnalysisService : IAudioTempoAnalysisService
{
    public Task<TempoAnalysisResult> AnalyzeAsync(
        string filePath,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(filePath, cancellationToken),
        cancellationToken);

    private static TempoAnalysisResult Analyze(
        string filePath,
        CancellationToken cancellationToken)
    {
        using var extractor = new MediaExtractor();
        extractor.SetDataSource(filePath);
        MediaFormat? inputFormat = null;
        for (var index = 0; index < extractor.TrackCount; index++)
        {
            var candidate = extractor.GetTrackFormat(index);
            var mime = candidate.GetString(MediaFormat.KeyMime);
            if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true)
            {
                candidate.Dispose();
                continue;
            }

            inputFormat = candidate;
            extractor.SelectTrack(index);
            break;
        }

        if (inputFormat is null)
        {
            throw new InvalidDataException("El archivo no contiene una pista de audio compatible.");
        }

        using (inputFormat)
        {
            var mime = inputFormat.GetString(MediaFormat.KeyMime) ??
                       throw new InvalidDataException("No se pudo identificar el formato de audio.");
            var sampleRate = inputFormat.GetInteger(MediaFormat.KeySampleRate);
            var channels = inputFormat.GetInteger(MediaFormat.KeyChannelCount);
            var pcmEncoding = (int)Encoding.Pcm16bit;
            var envelope = new RmsEnvelopeBuilder(sampleRate);
            using var codec = MediaCodec.CreateDecoderByType(mime) ??
                              throw new InvalidDataException(
                                  $"Android no tiene un decodificador para {mime}.");
            codec.Configure(inputFormat, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            var info = new MediaCodec.BufferInfo();
            var inputEnded = false;
            var outputEnded = false;
            try
            {
                while (!outputEnded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!inputEnded)
                    {
                        var inputIndex = codec.DequeueInputBuffer(10_000);
                        if (inputIndex >= 0)
                        {
                            var inputBuffer = codec.GetInputBuffer(inputIndex) ??
                                              throw new InvalidDataException(
                                                  "El decodificador no proporcionó un búfer de entrada.");
                            var size = extractor.ReadSampleData(inputBuffer, 0);
                            if (size < 0)
                            {
                                codec.QueueInputBuffer(
                                    inputIndex,
                                    0,
                                    0,
                                    0,
                                    MediaCodecBufferFlags.EndOfStream);
                                inputEnded = true;
                            }
                            else
                            {
                                codec.QueueInputBuffer(
                                    inputIndex,
                                    0,
                                    size,
                                    Math.Max(0, extractor.SampleTime),
                                    MediaCodecBufferFlags.None);
                                extractor.Advance();
                            }
                        }
                    }

                    var outputIndex = codec.DequeueOutputBuffer(info, 10_000);
                    if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                    {
                        var outputFormat = codec.OutputFormat;
                        if (outputFormat.ContainsKey(MediaFormat.KeySampleRate))
                        {
                            sampleRate = outputFormat.GetInteger(MediaFormat.KeySampleRate);
                        }
                        if (outputFormat.ContainsKey(MediaFormat.KeyChannelCount))
                        {
                            channels = outputFormat.GetInteger(MediaFormat.KeyChannelCount);
                        }
                        if (OperatingSystem.IsAndroidVersionAtLeast(24) &&
                            outputFormat.ContainsKey(MediaFormat.KeyPcmEncoding))
                        {
                            pcmEncoding = outputFormat.GetInteger(MediaFormat.KeyPcmEncoding);
                        }
                        envelope = new RmsEnvelopeBuilder(sampleRate);
                        continue;
                    }

                    if (outputIndex < 0)
                    {
                        continue;
                    }

                    if (info.Size > 0)
                    {
                        var outputBuffer = codec.GetOutputBuffer(outputIndex) ??
                                           throw new InvalidDataException(
                                               "El decodificador no proporcionó audio PCM.");
                        outputBuffer.Position(info.Offset);
                        outputBuffer.Limit(info.Offset + info.Size);
                        var bytes = new byte[info.Size];
                        outputBuffer.Get(bytes);
                        AddPcm(bytes, channels, pcmEncoding, envelope);
                    }

                    outputEnded =
                        (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                    codec.ReleaseOutputBuffer(outputIndex, false);
                }
            }
            finally
            {
                codec.Stop();
            }

            return TempoEnvelopeAnalyzer.AnalyzeRms(
                envelope.Finish(),
                cancellationToken);
        }
    }

    private static void AddPcm(
        ReadOnlySpan<byte> bytes,
        int channels,
        int pcmEncoding,
        RmsEnvelopeBuilder envelope)
    {
        channels = Math.Max(1, channels);
        if (pcmEncoding == (int)Encoding.PcmFloat)
        {
            var frameBytes = channels * sizeof(float);
            for (var offset = 0; offset + frameBytes <= bytes.Length; offset += frameBytes)
            {
                double mono = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    var bits = BinaryPrimitives.ReadInt32LittleEndian(
                        bytes.Slice(offset + channel * sizeof(float), sizeof(float)));
                    mono += BitConverter.Int32BitsToSingle(bits);
                }
                envelope.Add(mono / channels);
            }
            return;
        }

        var pcm16FrameBytes = channels * sizeof(short);
        for (var offset = 0;
             offset + pcm16FrameBytes <= bytes.Length;
             offset += pcm16FrameBytes)
        {
            double mono = 0d;
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(
                    bytes.Slice(offset + channel * sizeof(short), sizeof(short)));
                mono += sample / 32768d;
            }
            envelope.Add(mono / channels);
        }
    }
}
