using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed record YouTubeVideoMetadata(
    string VideoId,
    string Title,
    string? ThumbnailUrl,
    string? AuthorName);

public interface IYouTubeMetadataService
{
    Task<YouTubeVideoMetadata?> GetAsync(
        string videoId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, YouTubeVideoMetadata>> GetManyAsync(
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default);
}

public sealed class YouTubeMetadataService : IYouTubeMetadataService
{
    private const string OEmbedEndpoint = "https://www.youtube.com/oembed";
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestSlots;
    private readonly ConcurrentDictionary<string, Lazy<Task<YouTubeVideoMetadata?>>> _cache =
        new(StringComparer.Ordinal);

    public YouTubeMetadataService(HttpClient httpClient, int maximumConcurrency = 6)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestSlots = new SemaphoreSlim(
            Math.Clamp(maximumConcurrency, 1, 12),
            Math.Clamp(maximumConcurrency, 1, 12));
    }

    public Task<YouTubeVideoMetadata?> GetAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        if (!YouTubeUrlParser.IsValidVideoId(videoId))
        {
            return Task.FromResult<YouTubeVideoMetadata?>(null);
        }

        var fetch = _cache.GetOrAdd(
            videoId,
            id => new Lazy<Task<YouTubeVideoMetadata?>>(
                () => FetchAsync(id),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return fetch.Value.WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, YouTubeVideoMetadata>> GetManyAsync(
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoIds);
        var distinctIds = videoIds
            .Where(YouTubeUrlParser.IsValidVideoId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var results = await Task.WhenAll(distinctIds.Select(async videoId =>
            (VideoId: videoId, Metadata: await GetAsync(videoId, cancellationToken))));

        return results
            .Where(result => result.Metadata is not null)
            .ToDictionary(
                result => result.VideoId,
                result => result.Metadata!,
                StringComparer.Ordinal);
    }

    private async Task<YouTubeVideoMetadata?> FetchAsync(string videoId)
    {
        await _requestSlots.WaitAsync();
        try
        {
            var watchUrl = $"https://www.youtube.com/watch?v={videoId}";
            var requestUrl =
                $"{OEmbedEndpoint}?url={Uri.EscapeDataString(watchUrl)}&format=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var title = ReadString(root, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new YouTubeVideoMetadata(
                videoId,
                title.Trim(),
                ReadString(root, "thumbnail_url"),
                ReadString(root, "author_name"));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            _requestSlots.Release();
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public static partial class YouTubeTitlePolicy
{
    public static bool ShouldReplaceWithYouTubeTitle(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != MediaKind.YouTube ||
            item.TitleOrigin is MediaTitleOrigin.Manual or MediaTitleOrigin.YouTube)
        {
            return false;
        }

        return item.TitleOrigin == MediaTitleOrigin.Fallback ||
               LooksLikeLegacyGeneratedTitle(item.Title, item.YouTubeVideoId);
    }

    public static bool Apply(MediaItem item, YouTubeVideoMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(
                item.YouTubeVideoId,
                metadata.VideoId,
                StringComparison.Ordinal) ||
            !ShouldReplaceWithYouTubeTitle(item))
        {
            return false;
        }

        item.Title = metadata.Title;
        item.TitleOrigin = MediaTitleOrigin.YouTube;
        if (!string.IsNullOrWhiteSpace(metadata.ThumbnailUrl))
        {
            item.ThumbnailUrl = metadata.ThumbnailUrl;
        }

        return true;
    }

    public static string CreateFallbackTitle(string videoId) =>
        $"Título no disponible · {videoId}";

    public static bool LooksLikeLegacyGeneratedTitle(string? title, string? videoId)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var match = LegacyGeneratedTitle().Match(title.Trim());
        if (!match.Success)
        {
            return false;
        }

        var titleVideoId = match.Groups["id"].Value;
        return string.IsNullOrWhiteSpace(titleVideoId) ||
               string.IsNullOrWhiteSpace(videoId) ||
               string.Equals(titleVideoId, videoId, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"^YouTube(?:\s+\d+)?(?:\s*[·•:—-]\s*(?<id>[A-Za-z0-9_-]{6,}))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyGeneratedTitle();
}
