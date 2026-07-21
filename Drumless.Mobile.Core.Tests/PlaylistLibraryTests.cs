using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;

namespace Drumless.Mobile.Core.Tests;

[TestClass]
public sealed class PlaylistLibraryTests
{
    [TestMethod]
    public void Library_CreatesUniqueNamesAndPreventsDuplicateMedia()
    {
        var library = new PlaylistLibrary();
        var first = library.Create("Ensayo");
        var second = library.Create("Ensayo");

        Assert.AreEqual("Ensayo", first.Name);
        Assert.AreEqual("Ensayo 2", second.Name);

        var item = new MediaItem
        {
            Kind = MediaKind.YouTube,
            Title = "Vídeo",
            YouTubeVideoId = "M7lc1UVf-VE",
            YouTubeUrl = "https://www.youtube.com/watch?v=M7lc1UVf-VE"
        };
        Assert.IsTrue(library.Add(second.Id, item));
        Assert.IsFalse(library.Add(second.Id, new MediaItem
        {
            Kind = MediaKind.YouTube,
            Title = "El mismo vídeo",
            YouTubeVideoId = "M7lc1UVf-VE"
        }));
    }

    [TestMethod]
    public void Library_MovesAndDeletesWithoutDeletingAnotherPlaylist()
    {
        var library = new PlaylistLibrary();
        var playlist = library.Create("Directo");
        var other = library.Create("Casa");
        var first = new MediaItem
        {
            Kind = MediaKind.LocalAudio,
            Title = "Uno",
            LocalPath = "uno.mp3"
        };
        var second = new MediaItem
        {
            Kind = MediaKind.LocalAudio,
            Title = "Dos",
            LocalPath = "dos.mp3"
        };
        library.Add(playlist.Id, first);
        library.Add(playlist.Id, second);

        Assert.IsTrue(library.Move(playlist.Id, second.Id, -1));
        Assert.AreEqual(second.Id, playlist.Items[0].Id);
        Assert.AreSame(playlist, library.Delete(playlist.Id));
        Assert.AreEqual(other.Id, library.State.SelectedPlaylistId);
    }

    [TestMethod]
    public void AddRange_PreservesOrderAndSkipsExistingAndRepeatedVideos()
    {
        var library = new PlaylistLibrary();
        var playlist = library.Create("YouTube");
        library.Add(playlist.Id, YouTube("M7lc1UVf-VE", "Existente"));

        var added = library.AddRange(
            playlist.Id,
            [
                YouTube("video000001", "Uno"),
                YouTube("M7lc1UVf-VE", "Duplicado existente"),
                YouTube("video000002", "Dos"),
                YouTube("video000001", "Duplicado interno")
            ]);

        Assert.AreEqual(2, added);
        CollectionAssert.AreEqual(
            new[] { "M7lc1UVf-VE", "video000001", "video000002" },
            playlist.Items.Select(item => item.YouTubeVideoId).ToArray());
    }

    [TestMethod]
    public void MoveSelection_MovesSeveralItemsAsAGroupAndPreservesTheirOrder()
    {
        var library = new PlaylistLibrary();
        var playlist = library.Create("Edición");
        var a = Local("a");
        var b = Local("b");
        var c = Local("c");
        var d = Local("d");
        library.AddRange(playlist.Id, [a, b, c, d]);

        Assert.AreEqual(
            2,
            library.MoveSelection(playlist.Id, [b.Id, c.Id], -1));
        CollectionAssert.AreEqual(
            new[] { "b", "c", "a", "d" },
            playlist.Items.Select(item => item.Title).ToArray());

        Assert.AreEqual(
            2,
            library.MoveSelection(playlist.Id, [b.Id, c.Id], 1));
        CollectionAssert.AreEqual(
            new[] { "a", "b", "c", "d" },
            playlist.Items.Select(item => item.Title).ToArray());
    }

    [TestMethod]
    public void RemoveRange_RemovesOnlyTheSelectedItems()
    {
        var library = new PlaylistLibrary();
        var playlist = library.Create("Edición");
        var a = Local("a");
        var b = Local("b");
        var c = Local("c");
        library.AddRange(playlist.Id, [a, b, c]);

        var removed = library.RemoveRange(playlist.Id, [a.Id, c.Id]);

        Assert.HasCount(2, removed);
        CollectionAssert.AreEqual(
            new[] { "b" },
            playlist.Items.Select(item => item.Title).ToArray());
    }

    private static MediaItem Local(string title) => new()
    {
        Kind = MediaKind.LocalAudio,
        Title = title,
        LocalPath = $"{title}.mp3"
    };

    private static MediaItem YouTube(string videoId, string title) => new()
    {
        Kind = MediaKind.YouTube,
        Title = title,
        YouTubeVideoId = videoId,
        YouTubeUrl = $"https://www.youtube.com/watch?v={videoId}"
    };
}
