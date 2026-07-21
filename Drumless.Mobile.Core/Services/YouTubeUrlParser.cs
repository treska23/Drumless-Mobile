using System.Text.RegularExpressions;

namespace Drumless.Mobile.Core.Services;

public sealed record YouTubeVideoReference(
    string VideoId,
    string CanonicalUrl,
    string ThumbnailUrl);

public sealed record YouTubePlaylistReference(
    string PlaylistId,
    string CanonicalUrl);

public sealed record YouTubePlaylistImportResult(
    int Found,
    int Added,
    int Duplicates,
    int TitlesUnavailable = 0);

public static partial class YouTubeUrlParser
{
    public static bool TryParse(string? input, out YouTubeVideoReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (!TryCreateYouTubeUri(input, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        string? videoId = null;
        if (host is "youtu.be" or "www.youtu.be")
        {
            videoId = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
        }
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                videoId = GetQueryValue(uri.Query, "v");
            }
            else if (segments.Length >= 2 &&
                     (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
                      segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                      segments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
            {
                videoId = segments[1];
            }
        }

        if (videoId is null || !VideoIdPattern().IsMatch(videoId))
        {
            return false;
        }

        reference = new YouTubeVideoReference(
            videoId,
            $"https://www.youtube.com/watch?v={videoId}",
            $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg");
        return true;
    }

    public static bool TryParsePlaylist(
        string? input,
        out YouTubePlaylistReference reference)
    {
        reference = null!;
        if (!TryCreateYouTubeUri(input, out var uri))
        {
            return false;
        }

        var playlistId = GetQueryValue(uri.Query, "list");
        if (playlistId is null || !PlaylistIdPattern().IsMatch(playlistId))
        {
            return false;
        }

        reference = new YouTubePlaylistReference(
            playlistId,
            $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}");
        return true;
    }

    public static bool IsValidVideoId(string? videoId) =>
        videoId is not null && VideoIdPattern().IsMatch(videoId);

    private static bool TryCreateYouTubeUri(string? input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var candidate = input.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        if (host is not ("youtu.be" or "www.youtu.be" or
            "youtube.com" or "www.youtube.com" or
            "m.youtube.com" or "music.youtube.com"))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (Uri.UnescapeDataString(name).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistIdPattern();
}
