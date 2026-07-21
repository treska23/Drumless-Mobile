using System.Net;
using System.Text;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;

namespace Drumless.Mobile.Core.Tests;

[TestClass]
public sealed class YouTubeMetadataServiceTests
{
    [TestMethod]
    public async Task GetAsync_ReadsOfficialMetadataAndCachesIt()
    {
        var requests = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests++;
            StringAssert.Contains(
                request.RequestUri!.Query,
                Uri.EscapeDataString("https://www.youtube.com/watch?v=M7lc1UVf-VE"));
            return Json(
                """
                {
                  "title": "YouTube Developers Live: Embedded Web Player Customization",
                  "author_name": "Google for Developers",
                  "thumbnail_url": "https://i.ytimg.com/vi/M7lc1UVf-VE/hqdefault.jpg"
                }
                """);
        }));
        var service = new YouTubeMetadataService(httpClient);

        var first = await service.GetAsync("M7lc1UVf-VE");
        var second = await service.GetAsync("M7lc1UVf-VE");

        Assert.IsNotNull(first);
        Assert.AreEqual(
            "YouTube Developers Live: Embedded Web Player Customization",
            first.Title);
        Assert.AreEqual("Google for Developers", first.AuthorName);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task GetManyAsync_OmitsUnavailableVideosWithoutFailingTheBatch()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("M7lc1UVf-VE", StringComparison.Ordinal)
                ? Json("""{"title":"Disponible"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new YouTubeMetadataService(httpClient);

        var result = await service.GetManyAsync(
            ["M7lc1UVf-VE", "dQw4w9WgXcQ"]);

        Assert.HasCount(1, result);
        Assert.AreEqual("Disponible", result["M7lc1UVf-VE"].Title);
    }

    [TestMethod]
    public void TitlePolicy_ReplacesOnlyAutomaticOrFallbackTitles()
    {
        var legacy = YouTube("YouTube 01 · M7lc1UVf-VE");
        var meaningfulOldTitle = YouTube("Mi nombre personalizado");
        var explicitlyManual = YouTube(
            "YouTube 01 · M7lc1UVf-VE",
            MediaTitleOrigin.Manual);
        var fallback = YouTube(
            YouTubeTitlePolicy.CreateFallbackTitle("M7lc1UVf-VE"),
            MediaTitleOrigin.Fallback);
        var metadata = new YouTubeVideoMetadata(
            "M7lc1UVf-VE",
            "Título real",
            "https://i.ytimg.com/vi/M7lc1UVf-VE/maxresdefault.jpg",
            "Canal");

        Assert.IsTrue(YouTubeTitlePolicy.Apply(legacy, metadata));
        Assert.AreEqual("Título real", legacy.Title);
        Assert.AreEqual(MediaTitleOrigin.YouTube, legacy.TitleOrigin);
        Assert.IsFalse(YouTubeTitlePolicy.Apply(meaningfulOldTitle, metadata));
        Assert.AreEqual("Mi nombre personalizado", meaningfulOldTitle.Title);
        Assert.IsFalse(YouTubeTitlePolicy.Apply(explicitlyManual, metadata));
        Assert.IsTrue(YouTubeTitlePolicy.Apply(fallback, metadata));
    }

    private static MediaItem YouTube(
        string title,
        MediaTitleOrigin titleOrigin = MediaTitleOrigin.Unknown) => new()
    {
        Kind = MediaKind.YouTube,
        Title = title,
        TitleOrigin = titleOrigin,
        YouTubeVideoId = "M7lc1UVf-VE"
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
