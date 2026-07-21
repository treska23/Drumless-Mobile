using System.Collections.ObjectModel;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.ViewModels;

public sealed class MediaItemViewModel : ObservableObject
{
    private bool _isCurrent;
    private bool _isSelected;
    private bool _isSelectionEnabled;

    public MediaItemViewModel(MediaItem model) => Model = model;

    public MediaItem Model { get; }
    public string Id => Model.Id;
    public string Title => Model.Title;
    public string SourceLabel => Model.Kind == MediaKind.YouTube ? "YouTube" : "En el teléfono";
    public string Detail => Model.Kind == MediaKind.YouTube
        ? Model.YouTubeUrl ?? string.Empty
        : Model.OriginalFileName ?? Path.GetFileName(Model.LocalPath) ?? string.Empty;
    public string KindIcon => Model.Kind == MediaKind.YouTube ? "▶" : "♫";
    public bool IsMissing =>
        Model.Kind == MediaKind.LocalAudio &&
        (string.IsNullOrWhiteSpace(Model.LocalPath) || !File.Exists(Model.LocalPath));

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsSelectionEnabled
    {
        get => _isSelectionEnabled;
        set => SetProperty(ref _isSelectionEnabled, value);
    }

    public void RefreshMetadata()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
    }
}

public sealed class PlaylistViewModel : ObservableObject
{
    public PlaylistViewModel(Playlist model)
    {
        Model = model;
        Items = new ObservableCollection<MediaItemViewModel>(
            model.Items.Select(item => new MediaItemViewModel(item)));
    }

    public Playlist Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string CountLabel => Model.Items.Count == 1 ? "1 pista" : $"{Model.Items.Count} pistas";
    public ObservableCollection<MediaItemViewModel> Items { get; }

    public bool RemoveItem(string itemId)
    {
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }

        Items.Remove(item);
        OnPropertyChanged(nameof(CountLabel));
        return true;
    }

    public bool MoveItem(string itemId, int offset)
    {
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }

        var oldIndex = Items.IndexOf(item);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Items.Count)
        {
            return false;
        }

        Items.Move(oldIndex, newIndex);
        return true;
    }

    public void SynchronizeOrder()
    {
        for (var targetIndex = 0; targetIndex < Model.Items.Count; targetIndex++)
        {
            var itemId = Model.Items[targetIndex].Id;
            var currentIndex = Items
                .Select((item, index) => (item, index))
                .First(pair => string.Equals(
                    pair.item.Id,
                    itemId,
                    StringComparison.Ordinal))
                .index;
            if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }
}
