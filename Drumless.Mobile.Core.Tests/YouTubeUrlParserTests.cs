using Drumless.Mobile.Core.Services;

namespace Drumless.Mobile.Core.Tests;

[TestClass]
public sealed class YouTubeUrlParserTests
{
    [TestMethod]
    [DataRow("https://www.youtube.com/watch?v=M7lc1UVf-VE")]
    [DataRow("https://youtu.be/M7lc1UVf-VE?t=12")]
    [DataRow("youtube.com/shorts/M7lc1UVf-VE")]
    [DataRow("https://music.youtube.com/watch?v=M7lc1UVf-VE&list=RD")]
    public void TryParse_AcceptsCommonYouTubeUrls(string input)
    {
        Assert.IsTrue(YouTubeUrlParser.TryParse(input, out var result));
        Assert.AreEqual("M7lc1UVf-VE", result.VideoId);
        Assert.AreEqual(
            "https://www.youtube.com/watch?v=M7lc1UVf-VE",
            result.CanonicalUrl);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("https://example.com/watch?v=M7lc1UVf-VE")]
    [DataRow("https://youtube.com/watch?v=demasiado-corto")]
    public void TryParse_RejectsInvalidUrls(string input) =>
        Assert.IsFalse(YouTubeUrlParser.TryParse(input, out _));

    [TestMethod]
    [DataRow(
        "https://www.youtube.com/playlist?list=PLC77007E23FF423C6",
        "PLC77007E23FF423C6")]
    [DataRow(
        "https://www.youtube.com/watch?v=M7lc1UVf-VE&list=PL1234567890_ABC",
        "PL1234567890_ABC")]
    [DataRow(
        "music.youtube.com/playlist?list=OLAK5uy_1234567890",
        "OLAK5uy_1234567890")]
    [DataRow(
        "https://youtu.be/M7lc1UVf-VE?list=PL1234567890_ABC",
        "PL1234567890_ABC")]
    public void TryParsePlaylist_AcceptsOfficialPlaylistUrls(
        string input,
        string expectedId)
    {
        Assert.IsTrue(YouTubeUrlParser.TryParsePlaylist(input, out var result));
        Assert.AreEqual(expectedId, result.PlaylistId);
        StringAssert.StartsWith(
            result.CanonicalUrl,
            "https://www.youtube.com/playlist?list=");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("https://example.com/playlist?list=PLC77007E23FF423C6")]
    [DataRow("https://youtube.com/playlist?list=short")]
    [DataRow("ftp://youtube.com/playlist?list=PLC77007E23FF423C6")]
    public void TryParsePlaylist_RejectsInvalidUrls(string input) =>
        Assert.IsFalse(YouTubeUrlParser.TryParsePlaylist(input, out _));
}
