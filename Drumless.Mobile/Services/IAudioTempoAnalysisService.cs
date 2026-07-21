using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Services;

public interface IAudioTempoAnalysisService
{
    Task<TempoAnalysisResult> AnalyzeAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
