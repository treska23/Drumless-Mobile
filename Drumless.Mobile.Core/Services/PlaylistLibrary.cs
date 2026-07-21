using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed class PlaylistLibrary
{
    public PlaylistLibrary(LibraryState? state = null)
    {
        State = state ?? new LibraryState();
        Normalize();
    }

    public LibraryState State { get; }

    public Playlist? SelectedPlaylist =>
        State.Playlists.FirstOrDefault(playlist =>
            string.Equals(playlist.Id, State.SelectedPlaylistId, StringComparison.Ordinal));

    public Playlist Create(string? name)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? $"Playlist {State.Playlists.Count + 1}"
            : name.Trim();

        var playlist = new Playlist { Name = MakeUniqueName(normalizedName) };
        State.Playlists.Add(playlist);
        State.SelectedPlaylistId = playlist.Id;
        return playlist;
    }

    public bool Select(string playlistId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        if (State.Playlists.All(playlist =>
                !string.Equals(playlist.Id, playlistId, StringComparison.Ordinal)))
        {
            return false;
        }

        State.SelectedPlaylistId = playlistId;
        return true;
    }

    public bool Rename(string playlistId, string name)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedName = name.Trim();
        if (State.Playlists.Any(other =>
                !ReferenceEquals(other, playlist) &&
                string.Equals(other.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        playlist.Name = normalizedName;
        return true;
    }

    public Playlist? Delete(string playlistId)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist is null)
        {
            return null;
        }

        var index = State.Playlists.IndexOf(playlist);
        State.Playlists.RemoveAt(index);
        if (string.Equals(State.SelectedPlaylistId, playlistId, StringComparison.Ordinal))
        {
            State.SelectedPlaylistId = State.Playlists.Count == 0
                ? null
                : State.Playlists[Math.Min(index, State.Playlists.Count - 1)].Id;
        }

        return playlist;
    }

    public bool Add(string playlistId, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var playlist = FindPlaylist(playlistId);
        if (playlist is null ||
            playlist.Items.Any(existing =>
                string.Equals(existing.MediaKey, item.MediaKey, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        playlist.Items.Add(item);
        return true;
    }

    public int AddRange(string playlistId, IEnumerable<MediaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = 0;
        foreach (var item in items)
        {
            if (Add(playlistId, item))
            {
                added++;
            }
        }

        return added;
    }

    public MediaItem? Remove(string playlistId, string itemId)
    {
        var playlist = FindPlaylist(playlistId);
        var item = playlist?.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (playlist is null || item is null)
        {
            return null;
        }

        playlist.Items.Remove(item);
        return item;
    }

    public IReadOnlyList<MediaItem> RemoveRange(
        string playlistId,
        IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var playlist = FindPlaylist(playlistId);
        if (playlist is null)
        {
            return [];
        }

        var selectedIds = itemIds.ToHashSet(StringComparer.Ordinal);
        var removed = playlist.Items
            .Where(item => selectedIds.Contains(item.Id))
            .ToArray();
        foreach (var item in removed)
        {
            playlist.Items.Remove(item);
        }

        return removed;
    }

    public bool Move(string playlistId, string itemId, int offset)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist is null)
        {
            return false;
        }

        var currentIndex = playlist.Items.FindIndex(item =>
            string.Equals(item.Id, itemId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return false;
        }

        var targetIndex = Math.Clamp(currentIndex + offset, 0, playlist.Items.Count - 1);
        if (targetIndex == currentIndex)
        {
            return false;
        }

        var item = playlist.Items[currentIndex];
        playlist.Items.RemoveAt(currentIndex);
        playlist.Items.Insert(targetIndex, item);
        return true;
    }

    public int MoveSelection(
        string playlistId,
        IEnumerable<string> itemIds,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (offset is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "El desplazamiento debe ser -1 o 1.");
        }

        var playlist = FindPlaylist(playlistId);
        if (playlist is null)
        {
            return 0;
        }

        var selectedIds = itemIds.ToHashSet(StringComparer.Ordinal);
        var selectedItems = playlist.Items
            .Where(item => selectedIds.Contains(item.Id))
            .ToArray();
        if (offset > 0)
        {
            Array.Reverse(selectedItems);
        }

        var moved = 0;
        foreach (var item in selectedItems)
        {
            var currentIndex = playlist.Items.IndexOf(item);
            var targetIndex = currentIndex + offset;
            if (targetIndex < 0 ||
                targetIndex >= playlist.Items.Count ||
                selectedIds.Contains(playlist.Items[targetIndex].Id))
            {
                continue;
            }

            playlist.Items.RemoveAt(currentIndex);
            playlist.Items.Insert(targetIndex, item);
            moved++;
        }

        return moved;
    }

    public Playlist? FindPlaylist(string? playlistId) =>
        State.Playlists.FirstOrDefault(playlist =>
            string.Equals(playlist.Id, playlistId, StringComparison.Ordinal));

    private string MakeUniqueName(string proposedName)
    {
        if (State.Playlists.All(playlist =>
                !string.Equals(playlist.Name, proposedName, StringComparison.OrdinalIgnoreCase)))
        {
            return proposedName;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{proposedName} {suffix++}";
        }
        while (State.Playlists.Any(playlist =>
                   string.Equals(playlist.Name, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }

    private void Normalize()
    {
        State.SchemaVersion = Math.Max(State.SchemaVersion, 2);
        State.Playlists ??= [];
        foreach (var playlist in State.Playlists)
        {
            playlist.Id = string.IsNullOrWhiteSpace(playlist.Id)
                ? Guid.NewGuid().ToString("N")
                : playlist.Id;
            playlist.Name = string.IsNullOrWhiteSpace(playlist.Name)
                ? "Playlist"
                : playlist.Name.Trim();
            playlist.Items ??= [];
            foreach (var item in playlist.Items)
            {
                item.PerformanceSessions ??= [];
                if (item.Tempo is not null)
                {
                    item.Tempo = TempoProfile.Normalize(item.Tempo);
                }
            }
        }

        if (SelectedPlaylist is null)
        {
            State.SelectedPlaylistId = State.Playlists.FirstOrDefault()?.Id;
        }
    }
}
