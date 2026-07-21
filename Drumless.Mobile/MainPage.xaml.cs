using System.Text.Json;
using System.Diagnostics;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;
using Drumless.Mobile.Services;
using Drumless.Mobile.ViewModels;

namespace Drumless.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly IAudioPlaybackService _audio;
    private readonly LocalAudioImportService _audioImporter;
    private readonly IAudioTempoAnalysisService _tempoAnalysis;
    private readonly IMidiInputService _midi;
    private bool _youtubeReady;
    private string? _pendingYouTubeVideoId;
    private bool _isImporting;
    private bool _midiStarted;
    private double _youtubePositionSeconds;
    private long _youtubePositionReceivedTimestamp;
    private bool _youtubeIsPlaying;
    private bool _isYouTubePlaylistImporting;
    private string? _playlistImportRequestId;
    private string? _playlistImportId;
    private bool _playlistImportCommandSent;
    private TaskCompletionSource<IReadOnlyList<string>>? _playlistImportCompletion;
    private bool _youtubeErrorDialogVisible;
    private bool _initialScrollCompleted;
    private CancellationTokenSource? _trackPressCancellation;
    private string? _pressedTrackId;
    private bool _trackLongPressTriggered;

    public MainPage(
        MainViewModel viewModel,
        IAudioPlaybackService audio,
        LocalAudioImportService audioImporter,
        IAudioTempoAnalysisService tempoAnalysis,
        IMidiInputService midi)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _audio = audio;
        _audioImporter = audioImporter;
        _tempoAnalysis = tempoAnalysis;
        _midi = midi;

        _viewModel.PlaybackRequested += OnPlaybackRequested;
        _viewModel.PlaybackToggleRequested += OnPlaybackToggleRequested;
        _viewModel.StopPlaybackRequested += OnStopPlaybackRequested;
        _audio.PlaybackEnded += OnLocalPlaybackEnded;
        _audio.PlaybackFailed += OnLocalPlaybackFailed;
        _audio.PlaybackStateChanged += OnLocalPlaybackStateChanged;
        _midi.NoteReceived += OnMidiNoteReceived;
        _midi.StatusChanged += OnMidiStatusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
        if (!_midiStarted)
        {
            _midiStarted = true;
            await _midi.StartAsync();
            _viewModel.SetMidiStatus(_midi.Status);
        }

        if (!_initialScrollCompleted)
        {
            _initialScrollCompleted = true;
            await Task.Delay(100);
            await MainScroll.ScrollToAsync(
                TrackList,
                ScrollToPosition.Start,
                animated: false);
        }
    }

    public void PauseForBackground()
    {
        _audio.Pause();
        SendYouTubeCommand(new { type = "pause" });
        if (_viewModel.HasCurrentItem)
        {
            _viewModel.ReportPlaybackState(false);
        }
    }

    private async void OnCreatePlaylistClicked(object? sender, EventArgs e)
    {
        var name = await DisplayPromptAsync(
            "Nueva playlist",
            "¿Cómo quieres llamarla?",
            "Crear",
            "Cancelar",
            "Por ejemplo: Ensayo del viernes",
            maxLength: 80);
        if (name is not null)
        {
            await _viewModel.CreatePlaylistAsync(name);
        }
    }

    private async void OnRenamePlaylistClicked(object? sender, EventArgs e)
    {
        if (_viewModel.SelectedPlaylist is null)
        {
            return;
        }

        var name = await DisplayPromptAsync(
            "Renombrar playlist",
            "Nuevo nombre",
            "Guardar",
            "Cancelar",
            initialValue: _viewModel.SelectedPlaylist.Name,
            maxLength: 80);
        if (name is not null)
        {
            await _viewModel.RenameSelectedPlaylistAsync(name);
        }
    }

    private async void OnDeletePlaylistClicked(object? sender, EventArgs e)
    {
        if (_viewModel.SelectedPlaylist is null)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "Eliminar playlist",
            $"¿Eliminar «{_viewModel.SelectedPlaylist.Name}»? Los archivos originales del teléfono no se modifican.",
            "Eliminar",
            "Cancelar");
        if (confirmed)
        {
            await _viewModel.DeleteSelectedPlaylistAsync();
        }
    }

    private async void OnPlaylistSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is PlaylistViewModel selected &&
            !string.Equals(
                selected.Id,
                _viewModel.SelectedPlaylist?.Id,
                StringComparison.Ordinal))
        {
            await _viewModel.SelectPlaylistAsync(selected.Id);
        }
    }

    private async void OnAddLocalAudioClicked(object? sender, EventArgs e)
    {
        if (_isImporting)
        {
            return;
        }

        _isImporting = true;
        try
        {
            var tracks = await _audioImporter.PickAndImportAsync();
            await _viewModel.AddLocalAudioAsync(tracks);
        }
        catch (TaskCanceledException)
        {
            // El usuario cerró el selector.
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                "No se pudo importar",
                exception.Message,
                "Aceptar");
        }
        finally
        {
            _isImporting = false;
        }
    }

    private async void OnAddYouTubeClicked(object? sender, EventArgs e)
    {
        var url = await DisplayPromptAsync(
            "Añadir YouTube",
            "Pega el enlace de un vídeo o de una playlist completa",
            "Continuar",
            "Cancelar",
            "https://youtube.com/playlist?list=…",
            keyboard: Keyboard.Url);
        if (url is null)
        {
            return;
        }

        if (YouTubeUrlParser.TryParsePlaylist(url, out var playlist))
        {
            await ImportYouTubePlaylistAsync(playlist);
            return;
        }

        if (!YouTubeUrlParser.TryParse(url, out _))
        {
            await DisplayAlertAsync(
                "Enlace no válido",
                "Pega un enlace de vídeo o de playlist de YouTube.",
                "Aceptar");
            return;
        }

        var title = await DisplayPromptAsync(
            "Nombre de la pista",
            "Déjalo vacío para usar automáticamente el título real de YouTube",
            "Añadir",
            "Cancelar",
            "Título",
            maxLength: 120);
        if (title is not null)
        {
            await _viewModel.AddYouTubeAsync(url, title);
        }
    }

    private async Task ImportYouTubePlaylistAsync(
        YouTubePlaylistReference playlist)
    {
        if (_isYouTubePlaylistImporting)
        {
            return;
        }

        _isYouTubePlaylistImporting = true;
        _viewModel.BeginYouTubePlaylistImport();
        try
        {
            var videoIds = await ReadYouTubePlaylistAsync(playlist.PlaylistId);
            var result = await _viewModel.ImportYouTubePlaylistAsync(videoIds);
            await DisplayAlertAsync(
                "Playlist importada",
                result.Added == 0
                    ? $"{result.Duplicates} vídeos ya estaban en la playlist activa." +
                      FormatUnavailableTitles(result.TitlesUnavailable)
                    : $"{result.Added} vídeos añadidos en su orden original.\n" +
                      $"{result.Duplicates} duplicados omitidos." +
                      FormatUnavailableTitles(result.TitlesUnavailable),
                "Aceptar");
        }
        catch (TimeoutException)
        {
            _viewModel.ReportPlaybackFailure(
                "YouTube tardó demasiado en cargar la playlist");
            await DisplayAlertAsync(
                "No se pudo importar",
                "YouTube tardó demasiado en devolver los vídeos. Comprueba la conexión y vuelve a intentarlo.",
                "Aceptar");
        }
        catch (Exception exception)
        {
            _viewModel.ReportPlaybackFailure(
                $"No se pudo importar la playlist: {exception.Message}");
            await DisplayAlertAsync(
                "No se pudo importar",
                exception.Message,
                "Aceptar");
        }
        finally
        {
            CancelPendingPlaylistImport();
            _viewModel.EndYouTubePlaylistImport();
            _isYouTubePlaylistImporting = false;
        }
    }

    private static string FormatUnavailableTitles(int count) =>
        count <= 0
            ? string.Empty
            : $"\n{count} títulos no estaban disponibles y se identificarán por su ID.";

    private void OnTrackPressed(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: MediaItemViewModel item })
        {
            return;
        }

        _trackPressCancellation?.Cancel();
        _trackPressCancellation?.Dispose();
        _trackPressCancellation = new CancellationTokenSource();
        _pressedTrackId = item.Id;
        _trackLongPressTriggered = false;
        _ = DetectTrackLongPressAsync(item, _trackPressCancellation.Token);
    }

    private void OnTrackReleased(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: MediaItemViewModel item } ||
            !string.Equals(_pressedTrackId, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        _trackPressCancellation?.Cancel();
        _ = ResetTrackPressAfterReleaseAsync(item.Id);
    }

    private void OnTrackClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: MediaItemViewModel item } ||
            !string.Equals(_pressedTrackId, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (!_trackLongPressTriggered)
        {
            if (_viewModel.IsTrackSelectionMode)
            {
                _viewModel.ToggleTrackSelection(item.Id);
            }
            else
            {
                _viewModel.Play(item.Id);
            }
        }

        ResetTrackPressState(item.Id);
    }

    private async Task DetectTrackLongPressAsync(
        MediaItemViewModel item,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(800, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!string.Equals(_pressedTrackId, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        _trackLongPressTriggered = true;
        _viewModel.EnterTrackSelectionMode(item.Id);
    }

    private async Task ResetTrackPressAfterReleaseAsync(string itemId)
    {
        await Task.Delay(75);
        ResetTrackPressState(itemId);
    }

    private void ResetTrackPressState(string itemId)
    {
        if (!string.Equals(_pressedTrackId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        _trackPressCancellation?.Dispose();
        _trackPressCancellation = null;
        _pressedTrackId = null;
        _trackLongPressTriggered = false;
    }

    private void OnEnterTrackSelectionModeClicked(object? sender, EventArgs e) =>
        _viewModel.EnterTrackSelectionMode();

    private void OnSelectAllTracksClicked(object? sender, EventArgs e) =>
        _viewModel.SelectAllTracks();

    private void OnExitTrackSelectionModeClicked(object? sender, EventArgs e) =>
        _viewModel.ExitTrackSelectionMode();

    private async void OnTrackActionsClicked(object? sender, EventArgs e)
    {
        var selectedCount = _viewModel.SelectedTrackCount;
        if (selectedCount == 0)
        {
            return;
        }

        var action = await DisplayActionSheetAsync(
            selectedCount == 1
                ? "1 pista seleccionada"
                : $"{selectedCount} pistas seleccionadas",
            "Cancelar",
            "Quitar de la playlist",
            "Mover arriba",
            "Mover abajo",
            "Limpiar selección");
        switch (action)
        {
            case "Mover arriba":
                await _viewModel.MoveSelectedItemsAsync(-1);
                break;
            case "Mover abajo":
                await _viewModel.MoveSelectedItemsAsync(1);
                break;
            case "Limpiar selección":
                _viewModel.ClearTrackSelection();
                break;
            case "Quitar de la playlist":
                await ConfirmRemoveSelectedTracksAsync();
                break;
        }
    }

    private async Task ConfirmRemoveSelectedTracksAsync()
    {
        var selected = _viewModel.SelectedPlaylist?.Items
            .Where(item => item.IsSelected)
            .ToArray() ?? [];
        if (selected.Length == 0)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            selected.Length == 1
                ? "Quitar pista"
                : $"Quitar {selected.Length} pistas",
            selected.Length == 1
                ? $"¿Quitar «{selected[0].Title}» de la playlist?"
                : $"¿Quitar las {selected.Length} pistas seleccionadas de la playlist?",
            "Quitar",
            "Cancelar");
        if (confirmed)
        {
            await _viewModel.RemoveSelectedItemsAsync();
        }
    }

    private void OnPreviousClicked(object? sender, EventArgs e) => _viewModel.Previous();

    private void OnPlayPauseClicked(object? sender, EventArgs e) =>
        _viewModel.TogglePlayback();

    private void OnNextClicked(object? sender, EventArgs e) =>
        _viewModel.Next(automatic: false);

    private void OnTimingToolsClicked(object? sender, EventArgs e)
    {
        TimingTools.IsVisible = !TimingTools.IsVisible;
        TimingToolsButton.Text = TimingTools.IsVisible
            ? "Ritmo, latencia y batería  ▴"
            : "Ritmo, latencia y batería  ▾";
    }

    private async void OnOpenInYouTubeClicked(object? sender, EventArgs e) =>
        await OpenInYouTubeAsync(_viewModel.CurrentItem?.Model);

    private void OnCloseYouTubeClicked(object? sender, EventArgs e)
    {
        _pendingYouTubeVideoId = null;
        _youtubeIsPlaying = false;
        _viewModel.CloseCurrentPlayback();
    }

    private async void OnShowYouTubePlayerClicked(object? sender, EventArgs e) =>
        await MainScroll.ScrollToAsync(
            PlayerSection,
            ScrollToPosition.Start,
            animated: true);

    private async void OnShowTrackListClicked(object? sender, EventArgs e) =>
        await MainScroll.ScrollToAsync(
            TrackList,
            ScrollToPosition.Start,
            animated: true);

    private void OnPlaybackModeClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string parameter } &&
            int.TryParse(parameter, out var modeIndex))
        {
            _viewModel.PlaybackModeIndex = modeIndex;
        }
    }

    private async void OnTempoActionClicked(object? sender, EventArgs e)
    {
        var current = _viewModel.CurrentItem;
        if (current is null)
        {
            await DisplayAlertAsync(
                "Tempo",
                "Primero reproduce una pista de la playlist.",
                "Aceptar");
            return;
        }

        if (current.Model.Kind == MediaKind.YouTube)
        {
            await _viewModel.TapTempoAsync(GetTransportPositionSeconds());
            return;
        }

        if (string.IsNullOrWhiteSpace(current.Model.LocalPath))
        {
            return;
        }

        try
        {
            var result = await _tempoAnalysis.AnalyzeAsync(current.Model.LocalPath);
            await _viewModel.ApplyAutomaticTempoAsync(result);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                "No se pudo detectar el tempo",
                $"{exception.Message}\n\nPuedes introducirlo manualmente.",
                "Aceptar");
        }
    }

    private async void OnManualTempoClicked(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentItem is null)
        {
            return;
        }

        var initial = _viewModel.CurrentItem.Model.Tempo?.Bpm
                      .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                      ?? "120";
        var text = await DisplayPromptAsync(
            "Tempo manual",
            "BPM de la pista",
            "Guardar",
            "Cancelar",
            keyboard: Keyboard.Numeric,
            initialValue: initial);
        if (text is null ||
            !double.TryParse(
                text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var bpm))
        {
            return;
        }

        await _viewModel.SetManualTempoAsync(
            bpm,
            _viewModel.CurrentItem.Model.Tempo?.FirstBeatSeconds ??
            GetTransportPositionSeconds());
    }

    private async void OnMarkFirstBeatClicked(object? sender, EventArgs e) =>
        await _viewModel.MarkFirstBeatAsync(GetTransportPositionSeconds());

    private async void OnLatencyClicked(object? sender, EventArgs e)
    {
        var text = await DisplayPromptAsync(
            "Compensación Bluetooth",
            "Milisegundos entre la posición de la pista y el sonido que escuchas. Empieza probando 150 ms.",
            "Aplicar",
            "Cancelar",
            keyboard: Keyboard.Numeric,
            initialValue: _viewModel.TimingLatencyCompensationMilliseconds
                .ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        if (text is null ||
            !double.TryParse(
                text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var milliseconds))
        {
            return;
        }

        await _viewModel.SetTimingLatencyAsync(milliseconds);
    }

    private void OnEvaluationClicked(object? sender, EventArgs e) =>
        _viewModel.ToggleEvaluation();

    private async void OnReconnectMidiClicked(object? sender, EventArgs e)
    {
        await _midi.StartAsync();
        _viewModel.SetMidiStatus(_midi.Status);
    }

    private async void OnPlaybackRequested(object? sender, MediaItem item)
    {
        _audio.Stop();
        SendYouTubeCommand(new { type = "pause" });

        if (item.Kind == MediaKind.LocalAudio)
        {
            if (string.IsNullOrWhiteSpace(item.LocalPath))
            {
                _viewModel.ReportPlaybackFailure("La pista local no tiene un archivo asociado");
                return;
            }

            await _audio.PlayAsync(item.LocalPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.YouTubeVideoId))
        {
            _viewModel.ReportPlaybackFailure("El vídeo no tiene un identificador válido");
            return;
        }

        _pendingYouTubeVideoId = item.YouTubeVideoId;
        TryStartPendingYouTube();
    }

    private void OnPlaybackToggleRequested(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentItem?.Model.Kind == MediaKind.YouTube)
        {
            SendYouTubeCommand(new { type = "toggle" });
        }
        else
        {
            _audio.Toggle();
        }
    }

    private void OnStopPlaybackRequested(object? sender, EventArgs e)
    {
        _audio.Stop();
        SendYouTubeCommand(new { type = "pause" });
        _pendingYouTubeVideoId = null;
    }

    private void OnLocalPlaybackEnded(object? sender, EventArgs e) =>
        _viewModel.Next(automatic: true);

    private void OnLocalPlaybackFailed(object? sender, string message) =>
        _viewModel.ReportPlaybackFailure(message);

    private void OnLocalPlaybackStateChanged(object? sender, bool isPlaying)
    {
        if (_viewModel.CurrentItem?.Model.Kind == MediaKind.LocalAudio)
        {
            _viewModel.ReportPlaybackState(isPlaying);
        }
    }

    private async void OnYouTubeMessageReceived(
        object? sender,
        HybridWebViewRawMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Message))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.Message);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            switch (type)
            {
                case "ready":
                    _youtubeReady = true;
                    TryStartPendingYouTube();
                    TryStartPendingPlaylistImport();
                    break;
                case "state" when root.TryGetProperty("state", out var stateElement):
                    HandleYouTubeState(stateElement.GetInt32());
                    break;
                case "position"
                    when root.TryGetProperty("seconds", out var secondsElement):
                    _youtubePositionSeconds = Math.Max(0d, secondsElement.GetDouble());
                    _youtubePositionReceivedTimestamp = Stopwatch.GetTimestamp();
                    _youtubeIsPlaying =
                        root.TryGetProperty("playing", out var playingElement) &&
                        playingElement.GetBoolean();
                    break;
                case "playlist":
                    CompletePlaylistImport(root);
                    break;
                case "playlistError":
                    FailPlaylistImport(root);
                    break;
                case "autoplayBlocked":
                    _viewModel.ReportPlaybackFailure(
                        "YouTube necesita que pulses ▶ en el reproductor para continuar");
                    break;
                case "error":
                    var code = root.TryGetProperty("code", out var codeElement) &&
                               codeElement.TryGetInt32(out var parsedCode)
                        ? parsedCode
                        : (int?)null;
                    await HandleYouTubePlaybackErrorAsync(code);
                    break;
            }
        }
        catch (JsonException)
        {
            _viewModel.ReportPlaybackFailure("Respuesta inesperada del reproductor de YouTube");
        }
    }

    private async Task HandleYouTubePlaybackErrorAsync(int? code)
    {
        var failedItem = _viewModel.CurrentItem?.Model;
        if (failedItem?.Kind != MediaKind.YouTube)
        {
            _viewModel.ReportPlaybackFailure("YouTube no pudo reproducir este vídeo");
            return;
        }

        _pendingYouTubeVideoId = null;
        _youtubeIsPlaying = false;
        var reason = code switch
        {
            2 => "El enlace del vídeo no es válido.",
            5 => "El formato de este vídeo no funciona en el reproductor integrado.",
            100 => "El vídeo se ha eliminado, es privado o ya no está disponible.",
            101 or 150 => "El propietario no permite reproducir este vídeo dentro de otras aplicaciones.",
            153 => "YouTube no ha podido identificar correctamente este reproductor.",
            _ => "YouTube no ha permitido reproducir este vídeo aquí."
        };
        var codeSuffix = code is null ? string.Empty : $" · código {code}";
        _viewModel.ReportPlaybackFailure($"{reason}{codeSuffix}");

        if (_youtubeErrorDialogVisible)
        {
            return;
        }

        _youtubeErrorDialogVisible = true;
        try
        {
            var action = await DisplayActionSheetAsync(
                reason,
                "Cerrar",
                null,
                "Abrir en YouTube",
                "Saltar pista");
            if (string.Equals(action, "Abrir en YouTube", StringComparison.Ordinal))
            {
                await OpenInYouTubeAsync(failedItem);
            }
            else if (string.Equals(action, "Saltar pista", StringComparison.Ordinal) &&
                     string.Equals(
                         _viewModel.CurrentItem?.Id,
                         failedItem.Id,
                         StringComparison.Ordinal))
            {
                _viewModel.Next(automatic: true);
            }
        }
        finally
        {
            _youtubeErrorDialogVisible = false;
        }
    }

    private async Task OpenInYouTubeAsync(MediaItem? item)
    {
        if (item?.Kind != MediaKind.YouTube ||
            string.IsNullOrWhiteSpace(item.YouTubeUrl))
        {
            return;
        }

        SendYouTubeCommand(new { type = "pause" });
        _pendingYouTubeVideoId = null;
        _youtubeIsPlaying = false;
        _viewModel.ReportPlaybackState(false);
        if (!await Launcher.Default.OpenAsync(item.YouTubeUrl))
        {
            await DisplayAlertAsync(
                "No se pudo abrir YouTube",
                "Comprueba que la aplicación de YouTube o un navegador estén disponibles.",
                "Aceptar");
        }
    }

    private void HandleYouTubeState(int state)
    {
        if (_viewModel.CurrentItem?.Model.Kind != MediaKind.YouTube)
        {
            return;
        }

        switch (state)
        {
            case 0:
                _youtubeIsPlaying = false;
                _viewModel.ReportPlaybackState(false);
                _viewModel.Next(automatic: true);
                break;
            case 1:
                _youtubeIsPlaying = true;
                _pendingYouTubeVideoId = null;
                _viewModel.ReportPlaybackState(true);
                break;
            case 2:
                _youtubeIsPlaying = false;
                _viewModel.ReportPlaybackState(false);
                break;
        }
    }

    private void TryStartPendingYouTube()
    {
        if (!_youtubeReady || string.IsNullOrWhiteSpace(_pendingYouTubeVideoId))
        {
            return;
        }

        SendYouTubeCommand(new
        {
            type = "load",
            videoId = _pendingYouTubeVideoId
        });
    }

    private async Task<IReadOnlyList<string>> ReadYouTubePlaylistAsync(
        string playlistId)
    {
        if (_playlistImportCompletion is not null)
        {
            throw new InvalidOperationException(
                "Ya hay una playlist de YouTube cargándose.");
        }

        _playlistImportRequestId = Guid.NewGuid().ToString("N");
        _playlistImportId = playlistId;
        _playlistImportCommandSent = false;
        _playlistImportCompletion =
            new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        TryStartPendingPlaylistImport();
        return await _playlistImportCompletion.Task.WaitAsync(
            TimeSpan.FromSeconds(30));
    }

    private void TryStartPendingPlaylistImport()
    {
        if (!_youtubeReady ||
            _playlistImportCommandSent ||
            string.IsNullOrWhiteSpace(_playlistImportRequestId) ||
            string.IsNullOrWhiteSpace(_playlistImportId))
        {
            return;
        }

        _playlistImportCommandSent = true;
        SendYouTubeCommand(new
        {
            type = "inspectPlaylist",
            requestId = _playlistImportRequestId,
            playlistId = _playlistImportId
        });
    }

    private void CompletePlaylistImport(JsonElement root)
    {
        if (_playlistImportCompletion is null ||
            !MatchesPendingPlaylistRequest(root) ||
            !root.TryGetProperty("videoIds", out var videoIdsElement) ||
            videoIdsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var videoIds = videoIdsElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(YouTubeUrlParser.IsValidVideoId)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (videoIds.Length == 0)
        {
            _playlistImportCompletion.TrySetException(
                new InvalidDataException(
                    "La playlist no contiene vídeos públicos que se puedan importar."));
            return;
        }

        _playlistImportCompletion.TrySetResult(videoIds);
    }

    private void FailPlaylistImport(JsonElement root)
    {
        if (_playlistImportCompletion is null ||
            !MatchesPendingPlaylistRequest(root))
        {
            return;
        }

        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        _playlistImportCompletion.TrySetException(
            new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "YouTube no pudo abrir esa playlist."
                    : message));
    }

    private bool MatchesPendingPlaylistRequest(JsonElement root) =>
        root.TryGetProperty("requestId", out var requestElement) &&
        string.Equals(
            requestElement.GetString(),
            _playlistImportRequestId,
            StringComparison.Ordinal);

    private void CancelPendingPlaylistImport()
    {
        _playlistImportCompletion = null;
        _playlistImportRequestId = null;
        _playlistImportId = null;
        _playlistImportCommandSent = false;
    }

    private void SendYouTubeCommand(object command)
    {
        try
        {
            YouTubePlayer.SendRawMessage(JsonSerializer.Serialize(command));
        }
        catch (InvalidOperationException)
        {
            // El WebView aún no ha terminado de inicializarse; la pista queda pendiente.
        }
    }

    private void OnMidiStatusChanged(object? sender, string status) =>
        MainThread.BeginInvokeOnMainThread(() =>
            _viewModel.SetMidiStatus(status));

    private void OnMidiNoteReceived(object? sender, MidiNoteEvent note)
    {
        var position = GetTransportPositionSeconds();
        MainThread.BeginInvokeOnMainThread(() =>
            _viewModel.RecordMidiHit(position, note.Note, note.Velocity));
    }

    private double GetTransportPositionSeconds()
    {
        if (_viewModel.CurrentItem?.Model.Kind == MediaKind.LocalAudio)
        {
            return _audio.PositionSeconds;
        }

        var position = _youtubePositionSeconds;
        var timestamp = Interlocked.Read(ref _youtubePositionReceivedTimestamp);
        if (_youtubeIsPlaying && timestamp > 0)
        {
            position += Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
        }
        return Math.Max(0d, position);
    }
}
