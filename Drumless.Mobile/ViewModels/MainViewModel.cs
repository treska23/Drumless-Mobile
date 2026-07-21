using System.Collections.ObjectModel;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;
using Drumless.Mobile.Services;

namespace Drumless.Mobile.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IYouTubeMetadataService _youtubeMetadata;
    private readonly PlaybackNavigator _navigator = new();
    private readonly DrumPerformanceScorer _performanceScorer = new();
    private readonly TapTempoEstimator _tapTempoEstimator = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private PlaylistLibrary _library = new();
    private LibraryStateStore? _store;
    private PlaylistViewModel? _selectedPlaylist;
    private MediaItemViewModel? _currentItem;
    private string _statusMessage = "Preparando tu biblioteca…";
    private bool _isInitialized;
    private bool _isPlaying;
    private bool _isYouTubeActive;
    private int _playbackModeIndex = 1;
    private string _midiStatus = "MIDI sin conectar";
    private string _timingResult = "Configura el tempo para evaluar tus golpes";
    private int _titleRefreshStarted;
    private bool _isTrackSelectionMode;

    public MainViewModel(IYouTubeMetadataService youtubeMetadata)
    {
        _youtubeMetadata = youtubeMetadata;
    }

    public event EventHandler<MediaItem>? PlaybackRequested;
    public event EventHandler? PlaybackToggleRequested;
    public event EventHandler? StopPlaybackRequested;

    public ObservableCollection<PlaylistViewModel> Playlists { get; } = [];
    public IReadOnlyList<string> PlaybackModes { get; } =
        ["Una pista", "En orden", "Aleatorio"];

    public PlaylistViewModel? SelectedPlaylist
    {
        get => _selectedPlaylist;
        private set
        {
            if (SetProperty(ref _selectedPlaylist, value))
            {
                OnPropertyChanged(nameof(HasSelectedPlaylist));
                OnPropertyChanged(nameof(SelectedPlaylistTitle));
                OnPropertyChanged(nameof(SelectedPlaylistCount));
            }
        }
    }

    public bool HasSelectedPlaylist => SelectedPlaylist is not null;
    public string SelectedPlaylistTitle => SelectedPlaylist?.Name ?? "Sin playlist";
    public string SelectedPlaylistCount => SelectedPlaylist?.CountLabel ?? "Crea una para empezar";
    public int SelectedTrackCount =>
        SelectedPlaylist?.Items.Count(item => item.IsSelected) ?? 0;
    public bool HasSelectedTracks => SelectedTrackCount > 0;
    public bool IsTrackSelectionMode => _isTrackSelectionMode;
    public bool IsTrackPlaybackMode => !IsTrackSelectionMode;
    public string TrackSelectionSummary => !IsTrackSelectionMode
        ? "Toca una pista para reproducirla"
        : SelectedTrackCount switch
        {
            0 => "Marca una o varias pistas",
            1 => "1 pista seleccionada",
            _ => $"{SelectedTrackCount} pistas seleccionadas"
        };
    public string TrackActionsText => SelectedTrackCount == 0
        ? "Acciones"
        : $"Acciones ({SelectedTrackCount})";

    public MediaItemViewModel? CurrentItem
    {
        get => _currentItem;
        private set
        {
            if (_currentItem is not null)
            {
                _currentItem.IsCurrent = false;
            }

            if (SetProperty(ref _currentItem, value))
            {
                if (value is not null)
                {
                    value.IsCurrent = true;
                }

                OnPropertyChanged(nameof(HasCurrentItem));
                OnPropertyChanged(nameof(NowPlayingTitle));
                OnPropertyChanged(nameof(NowPlayingSource));
                OnPropertyChanged(nameof(TempoSummary));
                OnPropertyChanged(nameof(TempoActionText));
            }
        }
    }

    public bool HasCurrentItem => CurrentItem is not null;
    public string NowPlayingTitle => CurrentItem?.Title ?? "Nada en reproducción";
    public string NowPlayingSource => CurrentItem?.SourceLabel ?? "Elige una pista";
    public string TempoSummary => CurrentItem?.Model.Tempo is { } tempo
        ? $"{tempo.Bpm:0.##} BPM · pulso 1 en {tempo.FirstBeatSeconds:0.000}s · " +
          $"{tempo.Source}"
        : "Tempo sin configurar";
    public string TempoActionText =>
        CurrentItem?.Model.Kind == MediaKind.LocalAudio ? "Analizar" : "Tap pulso";

    public string MidiStatus
    {
        get => _midiStatus;
        private set => SetProperty(ref _midiStatus, value);
    }

    public string TimingResult
    {
        get => _timingResult;
        private set => SetProperty(ref _timingResult, value);
    }

    public bool IsEvaluationActive => _performanceScorer.IsActive;
    public string EvaluationButtonText => IsEvaluationActive ? "Finalizar" : "Evaluar";

    public double TimingLatencyCompensationMilliseconds
    {
        get => _library.State.TimingLatencyCompensationMilliseconds;
        private set
        {
            var normalized = double.IsFinite(value)
                ? Math.Clamp(value, -1_000d, 1_000d)
                : 0d;
            if (Math.Abs(
                    _library.State.TimingLatencyCompensationMilliseconds -
                    normalized) < 0.001d)
            {
                return;
            }

            _library.State.TimingLatencyCompensationMilliseconds = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencySummary));
        }
    }

    public string LatencySummary =>
        $"{TimingLatencyCompensationMilliseconds:+0;-0;0} ms";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseText));
            }
        }
    }

    public string PlayPauseText => IsPlaying ? "⏸" : "▶";

    public string SingleModeBackground =>
        PlaybackModeIndex == (int)PlaybackMode.Single ? "#176B5B" : "#1A222C";
    public string SequentialModeBackground =>
        PlaybackModeIndex == (int)PlaybackMode.Sequential ? "#176B5B" : "#1A222C";
    public string ShuffleModeBackground =>
        PlaybackModeIndex == (int)PlaybackMode.Shuffle ? "#176B5B" : "#1A222C";

    public bool IsYouTubeActive
    {
        get => _isYouTubeActive;
        private set
        {
            if (SetProperty(ref _isYouTubeActive, value))
            {
                OnPropertyChanged(nameof(IsLocalPlaybackActive));
            }
        }
    }

    public bool IsLocalPlaybackActive => !IsYouTubeActive;

    public int PlaybackModeIndex
    {
        get => _playbackModeIndex;
        set
        {
            var bounded = Math.Clamp(value, 0, PlaybackModes.Count - 1);
            if (!SetProperty(ref _playbackModeIndex, bounded))
            {
                return;
            }

            var mode = (PlaybackMode)bounded;
            _library.State.PlaybackMode = mode;
            _navigator.Mode = mode;
            OnPropertyChanged(nameof(SingleModeBackground));
            OnPropertyChanged(nameof(SequentialModeBackground));
            OnPropertyChanged(nameof(ShuffleModeBackground));
            StatusMessage = $"Modo de reproducción: {PlaybackModes[bounded]}";
            _ = SaveAsync();
        }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _store = new LibraryStateStore(
            Path.Combine(FileSystem.AppDataDirectory, "library.json"));
        _library = new PlaylistLibrary(await _store.LoadAsync());
        if (_library.State.Playlists.Count == 0)
        {
            _library.Create("Mi primera playlist");
        }

        RebuildPlaylists();
        _playbackModeIndex = (int)_library.State.PlaybackMode;
        OnPropertyChanged(nameof(PlaybackModeIndex));
        OnPropertyChanged(nameof(SingleModeBackground));
        OnPropertyChanged(nameof(SequentialModeBackground));
        OnPropertyChanged(nameof(ShuffleModeBackground));
        OnPropertyChanged(nameof(TimingLatencyCompensationMilliseconds));
        OnPropertyChanged(nameof(LatencySummary));
        StatusMessage = "Añade audio del teléfono o pega un enlace de YouTube";
        _isInitialized = true;
        await SaveAsync();
        _ = RefreshGeneratedYouTubeTitlesAsync();
    }

    public async Task CreatePlaylistAsync(string? name)
    {
        var playlist = _library.Create(name);
        RebuildPlaylists(playlist.Id);
        StatusMessage = $"Playlist creada: {playlist.Name}";
        await SaveAsync();
    }

    public async Task<bool> RenameSelectedPlaylistAsync(string name)
    {
        if (SelectedPlaylist is null || !_library.Rename(SelectedPlaylist.Id, name))
        {
            StatusMessage = "El nombre está vacío o ya existe";
            return false;
        }

        RebuildPlaylists(SelectedPlaylist.Id);
        StatusMessage = $"Playlist renombrada: {name.Trim()}";
        await SaveAsync();
        return true;
    }

    public async Task DeleteSelectedPlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        var deleted = _library.Delete(SelectedPlaylist.Id);
        if (deleted is null)
        {
            return;
        }

        StopPlaybackRequested?.Invoke(this, EventArgs.Empty);
        CurrentItem = null;
        IsPlaying = false;
        IsYouTubeActive = false;
        DeleteUnusedLocalCopies(deleted.Items);
        RebuildPlaylists();
        StatusMessage = $"Playlist eliminada: {deleted.Name}";
        await SaveAsync();
    }

    public async Task SelectPlaylistAsync(string playlistId)
    {
        if (!_library.Select(playlistId))
        {
            return;
        }

        StopPlaybackRequested?.Invoke(this, EventArgs.Empty);
        CurrentItem = null;
        IsPlaying = false;
        IsYouTubeActive = false;
        ExitTrackSelectionMode();
        SelectedPlaylist = Playlists.FirstOrDefault(playlist => playlist.Id == playlistId);
        NotifyTrackSelectionChanged();
        ResetQueue();
        StatusMessage = SelectedPlaylist is null
            ? "Selecciona una playlist"
            : $"{SelectedPlaylist.Name} · {SelectedPlaylist.CountLabel}";
        await SaveAsync();
    }

    public async Task<int> AddLocalAudioAsync(IEnumerable<ImportedAudio> tracks)
    {
        if (SelectedPlaylist is null)
        {
            return 0;
        }

        var selectedId = SelectedPlaylist.Id;
        var added = 0;
        foreach (var track in tracks)
        {
            var item = new MediaItem
            {
                Kind = MediaKind.LocalAudio,
                Title = track.Title,
                LocalPath = track.Path,
                OriginalFileName = track.OriginalFileName
            };
            if (_library.Add(selectedId, item))
            {
                added++;
            }
            else if (File.Exists(track.Path))
            {
                File.Delete(track.Path);
            }
        }

        RebuildPlaylists(selectedId);
        StatusMessage = added == 1
            ? "1 pista local añadida"
            : $"{added} pistas locales añadidas";
        await SaveAsync();
        return added;
    }

    public async Task<bool> AddYouTubeAsync(string url, string? title)
    {
        if (SelectedPlaylist is null ||
            !YouTubeUrlParser.TryParse(url, out var video))
        {
            StatusMessage = "Ese enlace de YouTube no es válido";
            return false;
        }

        var selectedId = SelectedPlaylist.Id;
        var hasManualTitle = !string.IsNullOrWhiteSpace(title);
        YouTubeVideoMetadata? metadata = null;
        if (!hasManualTitle)
        {
            StatusMessage = "Obteniendo el título real de YouTube…";
            metadata = await _youtubeMetadata.GetAsync(video.VideoId);
        }

        var normalizedTitle = hasManualTitle
            ? title!.Trim()
            : metadata?.Title ?? YouTubeTitlePolicy.CreateFallbackTitle(video.VideoId);
        var item = new MediaItem
        {
            Kind = MediaKind.YouTube,
            Title = normalizedTitle,
            TitleOrigin = hasManualTitle
                ? MediaTitleOrigin.Manual
                : metadata is null
                    ? MediaTitleOrigin.Fallback
                    : MediaTitleOrigin.YouTube,
            YouTubeVideoId = video.VideoId,
            YouTubeUrl = video.CanonicalUrl,
            ThumbnailUrl = metadata?.ThumbnailUrl ?? video.ThumbnailUrl
        };
        var refreshed = metadata is null ? 0 : ApplyYouTubeMetadata(metadata);
        if (!_library.Add(selectedId, item))
        {
            StatusMessage = "Ese vídeo ya está en la playlist";
            if (refreshed > 0)
            {
                await SaveAsync();
            }
            return false;
        }

        RebuildPlaylists(selectedId);
        StatusMessage = $"Vídeo añadido: {normalizedTitle}";
        await SaveAsync();
        return true;
    }

    public void BeginYouTubePlaylistImport()
    {
        if (_performanceScorer.IsActive)
        {
            FinishEvaluation();
        }

        StopPlaybackRequested?.Invoke(this, EventArgs.Empty);
        CurrentItem = null;
        IsPlaying = false;
        IsYouTubeActive = true;
        _tapTempoEstimator.Reset();
        StatusMessage = "Leyendo la playlist completa de YouTube…";
    }

    public void EndYouTubePlaylistImport()
    {
        if (CurrentItem is null)
        {
            IsYouTubeActive = false;
        }
    }

    public async Task<YouTubePlaylistImportResult> ImportYouTubePlaylistAsync(
        IEnumerable<string> videoIds)
    {
        ArgumentNullException.ThrowIfNull(videoIds);
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Crea o selecciona una playlist antes de importar";
            return new YouTubePlaylistImportResult(0, 0, 0);
        }

        var references = videoIds
            .Where(YouTubeUrlParser.IsValidVideoId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        StatusMessage = $"Obteniendo {references.Length} títulos reales de YouTube…";
        var metadataByVideoId = await _youtubeMetadata.GetManyAsync(references);
        foreach (var metadata in metadataByVideoId.Values)
        {
            ApplyYouTubeMetadata(metadata);
        }

        var selectedId = SelectedPlaylist.Id;
        var items = references
            .Select(videoId =>
            {
                metadataByVideoId.TryGetValue(videoId, out var metadata);
                return new MediaItem
                {
                    Kind = MediaKind.YouTube,
                    Title = metadata?.Title ??
                            YouTubeTitlePolicy.CreateFallbackTitle(videoId),
                    TitleOrigin = metadata is null
                        ? MediaTitleOrigin.Fallback
                        : MediaTitleOrigin.YouTube,
                    YouTubeVideoId = videoId,
                    YouTubeUrl = $"https://www.youtube.com/watch?v={videoId}",
                    ThumbnailUrl = metadata?.ThumbnailUrl ??
                                   $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
                };
            })
            .ToArray();
        var added = _library.AddRange(selectedId, items);

        RebuildPlaylists(selectedId);
        ResetQueue();
        var duplicates = references.Length - added;
        var titlesUnavailable = references.Length - metadataByVideoId.Count;
        StatusMessage = added == 0
            ? $"{duplicates} vídeos ya estaban en la playlist"
            : $"{added} vídeos añadidos · {duplicates} duplicados omitidos" +
              (titlesUnavailable > 0
                  ? $" · {titlesUnavailable} títulos no disponibles"
                  : string.Empty);
        await SaveAsync();
        return new YouTubePlaylistImportResult(
            references.Length,
            added,
            duplicates,
            titlesUnavailable);
    }

    public async Task RefreshGeneratedYouTubeTitlesAsync()
    {
        if (!_isInitialized ||
            Interlocked.Exchange(ref _titleRefreshStarted, 1) != 0)
        {
            return;
        }

        var candidates = _library.State.Playlists
            .SelectMany(playlist => playlist.Items)
            .Where(YouTubeTitlePolicy.ShouldReplaceWithYouTubeTitle)
            .Where(item => YouTubeUrlParser.IsValidVideoId(item.YouTubeVideoId))
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        const string progressPrefix = "Actualizando títulos";
        StatusMessage = $"{progressPrefix} antiguos de YouTube…";
        try
        {
            var metadataByVideoId = await _youtubeMetadata.GetManyAsync(
                candidates.Select(item => item.YouTubeVideoId!));
            var updated = 0;
            foreach (var metadata in metadataByVideoId.Values)
            {
                updated += ApplyYouTubeMetadata(metadata);
            }

            if (updated > 0)
            {
                await SaveAsync();
            }

            if (StatusMessage.StartsWith(progressPrefix, StringComparison.Ordinal))
            {
                StatusMessage = updated == 0
                    ? "No se pudieron actualizar los títulos antiguos"
                    : updated == 1
                        ? "1 título antiguo actualizado"
                        : $"{updated} títulos antiguos actualizados";
            }
        }
        catch (OperationCanceledException)
        {
            if (StatusMessage.StartsWith(progressPrefix, StringComparison.Ordinal))
            {
                StatusMessage = "Actualización de títulos pendiente";
            }
        }
    }

    public async Task<int> RemoveSelectedItemsAsync()
    {
        if (SelectedPlaylist is null)
        {
            return 0;
        }

        var selectedPlaylist = SelectedPlaylist;
        var selectedIds = selectedPlaylist.Items
            .Where(item => item.IsSelected)
            .Select(item => item.Id)
            .ToArray();
        var removed = _library.RemoveRange(selectedPlaylist.Id, selectedIds);
        if (removed.Count == 0)
        {
            return 0;
        }

        if (removed.Any(item => string.Equals(
                CurrentItem?.Id,
                item.Id,
                StringComparison.Ordinal)))
        {
            StopPlaybackRequested?.Invoke(this, EventArgs.Empty);
            CurrentItem = null;
            IsPlaying = false;
            IsYouTubeActive = false;
        }

        DeleteUnusedLocalCopies(removed);
        foreach (var item in removed)
        {
            selectedPlaylist.RemoveItem(item.Id);
        }

        OnPropertyChanged(nameof(SelectedPlaylistCount));
        ResetQueue(CurrentItem?.Id);
        NotifyTrackSelectionChanged();
        StatusMessage = removed.Count == 1
            ? $"Quitada: {removed[0].Title}"
            : $"{removed.Count} pistas quitadas de la playlist";
        await SaveAsync();
        return removed.Count;
    }

    public async Task<int> MoveSelectedItemsAsync(int offset)
    {
        if (SelectedPlaylist is null)
        {
            return 0;
        }

        var selectedPlaylist = SelectedPlaylist;
        var selectedIds = selectedPlaylist.Items
            .Where(item => item.IsSelected)
            .Select(item => item.Id)
            .ToArray();
        var moved = _library.MoveSelection(selectedPlaylist.Id, selectedIds, offset);
        if (moved == 0)
        {
            StatusMessage = offset < 0
                ? "La selección ya está todo lo arriba posible"
                : "La selección ya está todo lo abajo posible";
            return 0;
        }

        selectedPlaylist.SynchronizeOrder();
        ResetQueue(CurrentItem?.Id);
        StatusMessage = moved == 1
            ? "1 pista movida"
            : $"{moved} pistas movidas";
        await SaveAsync();
        return moved;
    }

    public void NotifyTrackSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTrackCount));
        OnPropertyChanged(nameof(HasSelectedTracks));
        OnPropertyChanged(nameof(TrackSelectionSummary));
        OnPropertyChanged(nameof(TrackActionsText));
    }

    public void EnterTrackSelectionMode(string? initiallySelectedItemId = null)
    {
        if (!_isTrackSelectionMode)
        {
            _isTrackSelectionMode = true;
            if (SelectedPlaylist is not null)
            {
                foreach (var item in SelectedPlaylist.Items)
                {
                    item.IsSelectionEnabled = true;
                }
            }

            OnPropertyChanged(nameof(IsTrackSelectionMode));
            OnPropertyChanged(nameof(IsTrackPlaybackMode));
        }

        if (!string.IsNullOrWhiteSpace(initiallySelectedItemId))
        {
            var item = SelectedPlaylist?.Items.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Id,
                    initiallySelectedItemId,
                    StringComparison.Ordinal));
            if (item is not null)
            {
                item.IsSelected = true;
            }
        }

        NotifyTrackSelectionChanged();
    }

    public void ToggleTrackSelection(string itemId)
    {
        if (!IsTrackSelectionMode)
        {
            EnterTrackSelectionMode(itemId);
            return;
        }

        var item = SelectedPlaylist?.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
        NotifyTrackSelectionChanged();
    }

    public void ExitTrackSelectionMode()
    {
        ClearTrackSelection();
        if (!_isTrackSelectionMode)
        {
            return;
        }

        _isTrackSelectionMode = false;
        if (SelectedPlaylist is not null)
        {
            foreach (var item in SelectedPlaylist.Items)
            {
                item.IsSelectionEnabled = false;
            }
        }

        OnPropertyChanged(nameof(IsTrackSelectionMode));
        OnPropertyChanged(nameof(IsTrackPlaybackMode));
        NotifyTrackSelectionChanged();
    }

    public void SelectAllTracks()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        EnterTrackSelectionMode();
        foreach (var item in SelectedPlaylist.Items)
        {
            item.IsSelected = true;
        }

        NotifyTrackSelectionChanged();
    }

    public void ClearTrackSelection()
    {
        if (SelectedPlaylist is not null)
        {
            foreach (var item in SelectedPlaylist.Items)
            {
                item.IsSelected = false;
            }
        }

        NotifyTrackSelectionChanged();
    }

    public void Play(string itemId)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        ResetQueue(CurrentItem?.Id);
        if (!_navigator.Select(itemId))
        {
            return;
        }

        RequestPlayback(itemId);
    }

    public void PlayPlaylist()
    {
        if (SelectedPlaylist?.Items.FirstOrDefault() is { } first)
        {
            Play(first.Id);
        }
        else
        {
            StatusMessage = "La playlist está vacía";
        }
    }

    public void TogglePlayback()
    {
        if (CurrentItem is null)
        {
            PlayPlaylist();
            return;
        }

        PlaybackToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    public void CloseCurrentPlayback()
    {
        if (_performanceScorer.IsActive)
        {
            FinishEvaluation();
        }

        StopPlaybackRequested?.Invoke(this, EventArgs.Empty);
        CurrentItem = null;
        IsPlaying = false;
        IsYouTubeActive = false;
        StatusMessage = "Reproductor cerrado";
    }

    public void Next(bool automatic)
    {
        var nextId = _navigator.Next(automatic);
        if (nextId is null)
        {
            IsPlaying = false;
            StatusMessage = automatic ? "Fin de la playlist" : "No hay una pista siguiente";
            return;
        }

        if (_performanceScorer.IsActive)
        {
            FinishEvaluation();
        }
        RequestPlayback(nextId);
    }

    public void Previous()
    {
        var previousId = _navigator.Previous();
        if (previousId is null)
        {
            StatusMessage = "No hay una pista anterior";
            return;
        }

        if (_performanceScorer.IsActive)
        {
            FinishEvaluation();
        }
        RequestPlayback(previousId);
    }

    public void ReportPlaybackState(bool isPlaying)
    {
        IsPlaying = isPlaying;
        StatusMessage = isPlaying
            ? $"Reproduciendo: {NowPlayingTitle}"
            : $"En pausa: {NowPlayingTitle}";
    }

    public void ReportPlaybackFailure(string message)
    {
        IsPlaying = false;
        StatusMessage = message;
    }

    public void SetMidiStatus(string status) => MidiStatus = status;

    public async Task ApplyAutomaticTempoAsync(TempoAnalysisResult result)
    {
        if (CurrentItem is null)
        {
            return;
        }

        CurrentItem.Model.Tempo = TempoProfile.Normalize(new TempoProfile(
            result.Bpm,
            result.FirstBeatSeconds,
            result.Confidence,
            "Análisis local"));
        _tapTempoEstimator.Reset();
        OnPropertyChanged(nameof(TempoSummary));
        TimingResult =
            $"Detectado {result.Bpm:0.##} BPM · confianza {result.Confidence:P0}";
        await SaveAsync();
    }

    public async Task SetManualTempoAsync(double bpm, double firstBeatSeconds)
    {
        if (CurrentItem is null)
        {
            return;
        }

        CurrentItem.Model.Tempo = TempoProfile.Normalize(new TempoProfile(
            bpm,
            firstBeatSeconds,
            1d,
            "Manual"));
        _tapTempoEstimator.Reset();
        OnPropertyChanged(nameof(TempoSummary));
        TimingResult = $"Tempo manual: {CurrentItem.Model.Tempo.Bpm:0.##} BPM";
        await SaveAsync();
    }

    public async Task TapTempoAsync(double trackPositionSeconds)
    {
        if (CurrentItem is null)
        {
            TimingResult = "Reproduce una pista antes de marcar el pulso";
            return;
        }

        var result = _tapTempoEstimator.AddTap(trackPositionSeconds);
        if (result is null)
        {
            TimingResult =
                $"Tap {Math.Min(_tapTempoEstimator.TapCount, 3)}/4 · sigue pulsando al ritmo";
            return;
        }

        CurrentItem.Model.Tempo = TempoProfile.Normalize(new TempoProfile(
            result.Bpm,
            result.FirstBeatSeconds,
            result.Confidence,
            "Tap"));
        OnPropertyChanged(nameof(TempoSummary));
        TimingResult =
            $"Tap: {result.Bpm:0.##} BPM · regularidad {result.Confidence:P0}";
        await SaveAsync();
    }

    public async Task MarkFirstBeatAsync(double trackPositionSeconds)
    {
        if (CurrentItem?.Model.Tempo is not { } tempo)
        {
            TimingResult = "Primero analiza o introduce el BPM";
            return;
        }

        CurrentItem.Model.Tempo = tempo with
        {
            FirstBeatSeconds = Math.Max(0d, trackPositionSeconds),
            Source = tempo.Source.Contains("ajustado", StringComparison.OrdinalIgnoreCase)
                ? tempo.Source
                : $"{tempo.Source} ajustado"
        };
        OnPropertyChanged(nameof(TempoSummary));
        TimingResult = $"Primer pulso marcado en {trackPositionSeconds:0.000}s";
        await SaveAsync();
    }

    public async Task SetTimingLatencyAsync(double milliseconds)
    {
        TimingLatencyCompensationMilliseconds = milliseconds;
        TimingResult = $"Compensación aplicada: {LatencySummary}";
        await SaveAsync();
    }

    public void ToggleEvaluation()
    {
        if (_performanceScorer.IsActive)
        {
            FinishEvaluation();
            return;
        }

        if (CurrentItem?.Model.Tempo is not { } tempo)
        {
            TimingResult = "Analiza o introduce el tempo antes de evaluar";
            return;
        }

        _performanceScorer.Start(
            tempo,
            TimingLatencyCompensationMilliseconds);
        TimingResult =
            $"Evaluación activa · tolerancia ±{DrumPerformanceScorer.AccurateToleranceMilliseconds:0} ms";
        OnPropertyChanged(nameof(IsEvaluationActive));
        OnPropertyChanged(nameof(EvaluationButtonText));
    }

    public void RecordMidiHit(
        double trackPositionSeconds,
        int midiNote,
        int velocity)
    {
        var hit = _performanceScorer.Record(
            trackPositionSeconds,
            midiNote,
            velocity);
        if (hit is null)
        {
            return;
        }

        TimingResult = Math.Abs(hit.ErrorMilliseconds) <=
                       DrumPerformanceScorer.AccurateToleranceMilliseconds
            ? $"A tiempo · {hit.ErrorMilliseconds:+0;-0;0} ms · nota {midiNote}"
            : hit.ErrorMilliseconds < 0d
                ? $"Adelantado · {hit.ErrorMilliseconds:0} ms · nota {midiNote}"
                : $"Atrasado · +{hit.ErrorMilliseconds:0} ms · nota {midiNote}";
    }

    private void FinishEvaluation()
    {
        var result = _performanceScorer.Finish();
        if (CurrentItem is not null && result.TotalHits > 0)
        {
            CurrentItem.Model.PerformanceSessions.Add(new PerformanceSession(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                result.TotalHits,
                result.AccurateHits,
                result.EarlyHits,
                result.LateHits,
                result.AccuracyPercent,
                result.MeanAbsoluteErrorMilliseconds,
                result.MaximumErrorMilliseconds,
                TimingLatencyCompensationMilliseconds));
            _ = SaveAsync();
        }

        TimingResult = result.TotalHits == 0
            ? "Evaluación terminada · no se recibieron golpes MIDI"
            : $"Precisión {result.AccuracyPercent:0.0}% · " +
              $"{result.AccurateHits}/{result.TotalHits} a tiempo · " +
              $"{result.EarlyHits} adelantados · {result.LateHits} atrasados · " +
              $"error medio {result.MeanAbsoluteErrorMilliseconds:0} ms";
        OnPropertyChanged(nameof(IsEvaluationActive));
        OnPropertyChanged(nameof(EvaluationButtonText));
    }

    private void RequestPlayback(string itemId)
    {
        var item = SelectedPlaylist?.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return;
        }

        if (item.IsMissing)
        {
            StatusMessage = $"No se encuentra el archivo: {item.Title}";
            Next(automatic: true);
            return;
        }

        if (_performanceScorer.IsActive &&
            !string.Equals(CurrentItem?.Id, itemId, StringComparison.Ordinal))
        {
            FinishEvaluation();
        }
        CurrentItem = item;
        _tapTempoEstimator.Reset();
        IsYouTubeActive = item.Model.Kind == MediaKind.YouTube;
        IsPlaying = false;
        StatusMessage = $"Cargando: {item.Title}";
        PlaybackRequested?.Invoke(this, item.Model);
    }

    private void RebuildPlaylists(string? selectedId = null)
    {
        _isTrackSelectionMode = false;
        OnPropertyChanged(nameof(IsTrackSelectionMode));
        OnPropertyChanged(nameof(IsTrackPlaybackMode));
        selectedId ??= _library.State.SelectedPlaylistId;
        var currentId = CurrentItem?.Id;
        Playlists.Clear();
        foreach (var playlist in _library.State.Playlists)
        {
            Playlists.Add(new PlaylistViewModel(playlist));
        }

        SelectedPlaylist = Playlists.FirstOrDefault(playlist => playlist.Id == selectedId)
                           ?? Playlists.FirstOrDefault();
        if (SelectedPlaylist is not null)
        {
            _library.Select(SelectedPlaylist.Id);
        }

        CurrentItem = SelectedPlaylist?.Items.FirstOrDefault(item => item.Id == currentId);
        ResetQueue(CurrentItem?.Id);
        NotifyTrackSelectionChanged();
    }

    private void ResetQueue(string? currentId = null)
    {
        _navigator.Mode = _library.State.PlaybackMode;
        _navigator.SetQueue(
            SelectedPlaylist?.Items.Select(item => item.Id) ?? [],
            currentId);
    }

    private int ApplyYouTubeMetadata(YouTubeVideoMetadata metadata)
    {
        var changedItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _library.State.Playlists
                     .SelectMany(playlist => playlist.Items)
                     .Where(item => string.Equals(
                         item.YouTubeVideoId,
                         metadata.VideoId,
                         StringComparison.Ordinal)))
        {
            if (YouTubeTitlePolicy.Apply(item, metadata))
            {
                changedItemIds.Add(item.Id);
            }
        }

        if (changedItemIds.Count == 0)
        {
            return 0;
        }

        foreach (var item in Playlists
                     .SelectMany(playlist => playlist.Items)
                     .Where(item => changedItemIds.Contains(item.Id)))
        {
            item.RefreshMetadata();
        }

        if (CurrentItem is not null && changedItemIds.Contains(CurrentItem.Id))
        {
            OnPropertyChanged(nameof(NowPlayingTitle));
        }

        return changedItemIds.Count;
    }

    private void DeleteUnusedLocalCopies(IEnumerable<MediaItem> candidates)
    {
        var referenced = _library.State.Playlists
            .SelectMany(playlist => playlist.Items)
            .Where(item => item.Kind == MediaKind.LocalAudio)
            .Select(item => item.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in candidates.Where(item => item.Kind == MediaKind.LocalAudio))
        {
            if (!string.IsNullOrWhiteSpace(item.LocalPath) &&
                !referenced.Contains(item.LocalPath) &&
                File.Exists(item.LocalPath))
            {
                File.Delete(item.LocalPath);
            }
        }
    }

    private async Task SaveAsync()
    {
        if (_store is null)
        {
            return;
        }

        await _saveLock.WaitAsync();
        try
        {
            await _store.SaveAsync(_library.State);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
