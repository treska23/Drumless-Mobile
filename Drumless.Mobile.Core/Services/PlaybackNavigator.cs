using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed class PlaybackNavigator
{
    private readonly Random _random;
    private readonly List<string> _queue = [];
    private readonly Stack<string> _history = [];
    private readonly List<string> _shuffleRemaining = [];
    private PlaybackMode _mode = PlaybackMode.Sequential;

    public PlaybackNavigator(Random? random = null) => _random = random ?? Random.Shared;

    public string? CurrentItemId { get; private set; }

    public PlaybackMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            _history.Clear();
            ResetShuffleCycle();
        }
    }

    public void SetQueue(IEnumerable<string> itemIds, string? currentItemId = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        _queue.Clear();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            if (!string.IsNullOrWhiteSpace(itemId) && seen.Add(itemId))
            {
                _queue.Add(itemId);
            }
        }

        CurrentItemId = FindCanonicalId(currentItemId);
        _history.Clear();
        ResetShuffleCycle();
    }

    public bool Select(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var selected = FindCanonicalId(itemId);
        if (selected is null)
        {
            return false;
        }

        if (!string.Equals(CurrentItemId, selected, StringComparison.Ordinal))
        {
            if (CurrentItemId is not null)
            {
                _history.Push(CurrentItemId);
            }

            CurrentItemId = selected;
        }

        _shuffleRemaining.RemoveAll(id => string.Equals(id, selected, StringComparison.Ordinal));
        return true;
    }

    public string? Next(bool automatic)
    {
        if (automatic && Mode == PlaybackMode.Single)
        {
            return null;
        }

        return Mode == PlaybackMode.Shuffle ? MoveShuffle() : MoveSequential();
    }

    public string? Previous()
    {
        while (_history.TryPop(out var previous))
        {
            if (FindCanonicalId(previous) is { } canonical)
            {
                CurrentItemId = canonical;
                return canonical;
            }
        }

        if (Mode == PlaybackMode.Shuffle || CurrentItemId is null)
        {
            return null;
        }

        var index = _queue.IndexOf(CurrentItemId);
        if (index <= 0)
        {
            return null;
        }

        CurrentItemId = _queue[index - 1];
        return CurrentItemId;
    }

    private string? MoveSequential()
    {
        if (_queue.Count == 0)
        {
            return null;
        }

        var index = CurrentItemId is null ? -1 : _queue.IndexOf(CurrentItemId);
        return index >= _queue.Count - 1 ? null : MoveTo(_queue[index + 1]);
    }

    private string? MoveShuffle()
    {
        if (_shuffleRemaining.Count == 0)
        {
            return null;
        }

        var index = _random.Next(_shuffleRemaining.Count);
        var selected = _shuffleRemaining[index];
        _shuffleRemaining.RemoveAt(index);
        return MoveTo(selected);
    }

    private string MoveTo(string itemId)
    {
        if (CurrentItemId is not null &&
            !string.Equals(CurrentItemId, itemId, StringComparison.Ordinal))
        {
            _history.Push(CurrentItemId);
        }

        CurrentItemId = itemId;
        _shuffleRemaining.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
        return itemId;
    }

    private string? FindCanonicalId(string? itemId) =>
        itemId is not null
            ? _queue.FirstOrDefault(id => string.Equals(id, itemId, StringComparison.Ordinal))
            : null;

    private void ResetShuffleCycle()
    {
        _shuffleRemaining.Clear();
        _shuffleRemaining.AddRange(_queue.Where(id =>
            !string.Equals(id, CurrentItemId, StringComparison.Ordinal)));
    }
}
