using System.Diagnostics;
using System.Text.Json;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile;

public partial class MainPage
{
    private const double YouTubeBrowserCollapsedHeight = 190d;

    private WebView? _youTubeBrowser;
    private Grid? _youTubeBrowserOverlay;
    private Button? _youTubeBrowserExpandButton;
    private string? _youTubeBrowserItemId;
    private string? _youTubeBrowserVideoId;
    private bool _youTubeBrowserTransitioning;
    private bool _youTubeBrowserExpanded;
    private bool? _youTubeBrowserReportedPlaying;
    private long _youTubeBrowserBridgeGeneration;

    private void EnableYouTubeBrowserPlaybackIntegration()
    {
        _viewModel.PlaybackRequested -= OnPlaybackRequested;
        _viewModel.PlaybackRequested -= OnYouTubeBrowserPlaybackRequested;
        _viewModel.PlaybackRequested += OnYouTubeBrowserPlaybackRequested;

        _viewModel.PlaybackToggleRequested -= OnPlaybackToggleRequested;
        _viewModel.PlaybackToggleRequested -= OnYouTubeBrowserPlaybackToggleRequested;
        _viewModel.PlaybackToggleRequested += OnYouTubeBrowserPlaybackToggleRequested;

        _viewModel.StopPlaybackRequested -= OnStopPlaybackRequested;
        _viewModel.StopPlaybackRequested -= OnYouTubeBrowserStopRequested;
        _viewModel.StopPlaybackRequested += OnYouTubeBrowserStopRequested;
        _viewModel.StopPlaybackRequested += OnStopPlaybackRequested;
    }

    private void EnsureYouTubeBrowser()
    {
        if (_youTubeBrowser is not null || Content is not Grid root)
        {
            return;
        }

        var browser = new WebView
        {
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        browser.Navigating += OnYouTubeBrowserNavigating;
        browser.Navigated += OnYouTubeBrowserNavigated;

        var title = new Label
        {
            Text = "YouTube · toca aquí para ampliar",
            TextColor = Color.FromArgb("#F4F7FA"),
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var expandButton = new Button
        {
            Text = "Ampliar",
            BackgroundColor = Color.FromArgb("#1A222C"),
            TextColor = Color.FromArgb("#F4F7FA"),
            CornerRadius = 10,
            Padding = new Thickness(12, 6),
            FontSize = 12
        };
        expandButton.Clicked += (_, _) => ToggleYouTubeBrowserSize();

        var header = new Grid
        {
            Padding = new Thickness(10, 7),
            BackgroundColor = Color.FromArgb("#131B24"),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        header.Add(title);
        header.Add(expandButton, 1);
        var tapHeader = new TapGestureRecognizer();
        tapHeader.Tapped += (_, _) => ToggleYouTubeBrowserSize();
        header.GestureRecognizers.Add(tapHeader);

        var overlay = new Grid
        {
            IsVisible = false,
            ZIndex = 1000,
            BackgroundColor = Colors.Black,
            HeightRequest = YouTubeBrowserCollapsedHeight,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(8),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        overlay.Add(header);
        overlay.Add(browser, 0, 1);
        Grid.SetRow(overlay, 1);

        root.Children.Add(overlay);
        _youTubeBrowser = browser;
        _youTubeBrowserOverlay = overlay;
        _youTubeBrowserExpandButton = expandButton;
    }

    private void ToggleYouTubeBrowserSize()
    {
        if (_youTubeBrowserOverlay is null)
        {
            return;
        }

        _youTubeBrowserExpanded = !_youTubeBrowserExpanded;
        if (_youTubeBrowserExpanded)
        {
            _youTubeBrowserOverlay.HeightRequest = -1;
            _youTubeBrowserOverlay.VerticalOptions = LayoutOptions.Fill;
            _youTubeBrowserOverlay.Margin = new Thickness(0);
            if (_youTubeBrowserExpandButton is not null)
            {
                _youTubeBrowserExpandButton.Text = "Minimizar";
            }
        }
        else
        {
            _youTubeBrowserOverlay.HeightRequest = YouTubeBrowserCollapsedHeight;
            _youTubeBrowserOverlay.VerticalOptions = LayoutOptions.End;
            _youTubeBrowserOverlay.Margin = new Thickness(8);
            if (_youTubeBrowserExpandButton is not null)
            {
                _youTubeBrowserExpandButton.Text = "Ampliar";
            }
        }
    }

    private async void OnYouTubeBrowserPlaybackRequested(object? sender, MediaItem item)
    {
        try
        {
            if (item.Kind == MediaKind.YouTube)
            {
                _audio.Stop();
                _pendingYouTubeVideoId = null;
                await PlayYouTubeInBrowserAsync(item);
                return;
            }

            await StopYouTubeBrowserAsync(hide: true);
            OnPlaybackRequested(sender, item);
        }
        catch (Exception exception)
        {
            _viewModel.ReportPlaybackFailure($"No se pudo cambiar de pista: {exception.Message}");
        }
    }

    private async void OnYouTubeBrowserPlaybackToggleRequested(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentItem?.Model.Kind != MediaKind.YouTube)
        {
            OnPlaybackToggleRequested(sender, e);
            return;
        }

        try
        {
            await ToggleYouTubeBrowserPlaybackAsync();
        }
        catch (Exception exception)
        {
            _viewModel.ReportPlaybackFailure($"No se pudo controlar YouTube: {exception.Message}");
        }
    }

    private void OnYouTubeBrowserStopRequested(object? sender, EventArgs e) =>
        _ = StopYouTubeBrowserAsync(hide: true);

    private async Task PlayYouTubeInBrowserAsync(MediaItem item)
    {
        EnsureYouTubeBrowser();
        if (_youTubeBrowser is null || _youTubeBrowserOverlay is null ||
            string.IsNullOrWhiteSpace(item.YouTubeVideoId))
        {
            _viewModel.ReportPlaybackFailure("No se pudo abrir el navegador interno de YouTube");
            return;
        }

        Interlocked.Increment(ref _youTubeBrowserBridgeGeneration);
        _youTubeBrowserItemId = item.Id;
        _youTubeBrowserVideoId = item.YouTubeVideoId;
        _youTubeBrowserTransitioning = false;
        _youTubeBrowserReportedPlaying = null;
        _youtubePositionSeconds = 0;
        _youtubePositionReceivedTimestamp = Stopwatch.GetTimestamp();
        _youtubeIsPlaying = false;

        _youTubeBrowserExpanded = false;
        _youTubeBrowserOverlay.HeightRequest = YouTubeBrowserCollapsedHeight;
        _youTubeBrowserOverlay.VerticalOptions = LayoutOptions.End;
        _youTubeBrowserOverlay.Margin = new Thickness(8);
        if (_youTubeBrowserExpandButton is not null)
        {
            _youTubeBrowserExpandButton.Text = "Ampliar";
        }

        _youTubeBrowserOverlay.IsVisible = true;
        _youTubeBrowser.Source =
            $"https://m.youtube.com/watch?v={Uri.EscapeDataString(item.YouTubeVideoId)}";

        await Task.CompletedTask;
    }

    private void OnYouTubeBrowserNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "drumless", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        MainThread.BeginInvokeOnMainThread(() => HandleYouTubeBrowserSignal(uri));
    }

    private async void OnYouTubeBrowserNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var itemId = _youTubeBrowserItemId;
        var videoId = _youTubeBrowserVideoId;
        if (_youTubeBrowser is null ||
            string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(videoId) ||
            !string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
        {
            return;
        }

        var generation = Volatile.Read(ref _youTubeBrowserBridgeGeneration);
        try
        {
            await InstallYouTubePageBridgeAsync(itemId, videoId, generation);
        }
        catch (Exception)
        {
            // YouTube can perform several SPA navigations while building the page. A later
            // Navigated event will retry without interrupting playback.
        }
    }

    private async Task InstallYouTubePageBridgeAsync(
        string itemId,
        string expectedVideoId,
        long generation)
    {
        if (_youTubeBrowser is null)
        {
            return;
        }

        var itemLiteral = JsonSerializer.Serialize(itemId);
        var videoLiteral = JsonSerializer.Serialize(expectedVideoId);
        var script = $$"""
            (() => {
              const itemId = {{itemLiteral}};
              const expectedVideoId = {{videoLiteral}};
              const key = '__drumlessBridgeV4';

              const send = (eventName, video) => {
                const position = Number(video?.currentTime || 0).toFixed(3);
                const duration = Number(video?.duration || 0).toFixed(3);
                const url = 'drumless://youtube?event=' + encodeURIComponent(eventName)
                  + '&item=' + encodeURIComponent(itemId)
                  + '&position=' + encodeURIComponent(position)
                  + '&duration=' + encodeURIComponent(duration);
                window.location.href = url;
              };

              if (window[key]?.dispose) {
                window[key].dispose();
              }

              const state = {
                video: null,
                finished: false,
                repairingVolume: false,
                listeners: [],

                currentVideoId() {
                  try {
                    return new URL(window.location.href).searchParams.get('v') || '';
                  } catch (_) {
                    return '';
                  }
                },

                forceAudible(video) {
                  if (!video || this.repairingVolume) return;
                  this.repairingVolume = true;
                  try {
                    video.removeAttribute('muted');
                    video.defaultMuted = false;
                    if (video.muted) video.muted = false;
                    if (!Number.isFinite(video.volume) || video.volume < 0.99) {
                      video.volume = 1;
                    }
                  } finally {
                    this.repairingVolume = false;
                  }
                },

                on(target, name, handler) {
                  target.addEventListener(name, handler, { passive: true });
                  this.listeners.push([target, name, handler]);
                },

                finish(reason) {
                  if (this.finished || !this.video) return;
                  this.finished = true;
                  try { this.video.pause(); } catch (_) {}
                  send('ended', this.video);
                },

                attach() {
                  const video = document.querySelector('video');
                  if (!video || video === this.video) return Boolean(video);

                  for (const [target, name, handler] of this.listeners) {
                    try { target.removeEventListener(name, handler); } catch (_) {}
                  }
                  this.listeners = [];
                  this.video = video;
                  this.finished = false;
                  this.forceAudible(video);

                  this.on(video, 'loadedmetadata', () => {
                    this.forceAudible(video);
                    if (this.currentVideoId() && this.currentVideoId() !== expectedVideoId) {
                      this.finish('video-changed');
                    }
                  });
                  this.on(video, 'canplay', () => this.forceAudible(video));
                  this.on(video, 'playing', () => {
                    this.forceAudible(video);
                    send('playing', video);
                  });
                  this.on(video, 'pause', () => {
                    if (!this.finished && !video.ended) send('paused', video);
                  });
                  this.on(video, 'seeked', () => {
                    send(video.paused ? 'paused' : 'playing', video);
                  });
                  this.on(video, 'volumechange', () => {
                    if (video.muted || video.volume < 0.99) this.forceAudible(video);
                  });
                  this.on(video, 'timeupdate', () => {
                    if (this.finished || !Number.isFinite(video.duration) || video.duration <= 0) return;
                    if (video.currentTime > 0.1 && video.duration - video.currentTime <= 0.55) {
                      this.finish('near-end');
                    }
                  });
                  this.on(video, 'ended', () => this.finish('ended'));

                  const play = video.play();
                  if (play?.catch) play.catch(() => {});
                  return true;
                },

                navigationFinished: null,
                dispose() {
                  for (const [target, name, handler] of this.listeners) {
                    try { target.removeEventListener(name, handler); } catch (_) {}
                  }
                  this.listeners = [];
                  if (this.navigationFinished) {
                    document.removeEventListener('yt-navigate-finish', this.navigationFinished);
                  }
                }
              };

              state.navigationFinished = () => {
                state.attach();
                const current = state.currentVideoId();
                if (current && current !== expectedVideoId) state.finish('navigation');
              };
              document.addEventListener('yt-navigate-finish', state.navigationFinished, { passive: true });
              window[key] = state;

              return state.attach() ? 'READY' : 'WAIT';
            })()
            """;

        var delays = new[] { 200, 450, 900, 1600 };
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            if (_youTubeBrowser is null ||
                generation != Volatile.Read(ref _youTubeBrowserBridgeGeneration) ||
                !string.Equals(_youTubeBrowserItemId, itemId, StringComparison.Ordinal) ||
                !string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
            {
                return;
            }

            var raw = await MainThread.InvokeOnMainThreadAsync(() =>
                _youTubeBrowser.EvaluateJavaScriptAsync(script));
            if (string.Equals(
                    NormalizeJavaScriptString(raw),
                    "READY",
                    StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private void HandleYouTubeBrowserSignal(Uri uri)
    {
        var values = ParseQuery(uri.Query);
        if (!values.TryGetValue("event", out var eventName) ||
            !values.TryGetValue("item", out var itemId) ||
            !string.Equals(itemId, _youTubeBrowserItemId, StringComparison.Ordinal) ||
            !string.Equals(itemId, _viewModel.CurrentItem?.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (values.TryGetValue("position", out var positionText) &&
            double.TryParse(
                positionText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var position))
        {
            _youtubePositionSeconds = Math.Max(0d, position);
            _youtubePositionReceivedTimestamp = Stopwatch.GetTimestamp();
        }

        switch (eventName)
        {
            case "playing":
                SetYouTubeBrowserPlaybackState(true);
                break;
            case "paused":
                SetYouTubeBrowserPlaybackState(false);
                break;
            case "ended":
                _ = CompleteYouTubeBrowserItemAsync(itemId);
                break;
        }
    }

    private void SetYouTubeBrowserPlaybackState(bool isPlaying)
    {
        _youtubeIsPlaying = isPlaying;
        if (_youTubeBrowserReportedPlaying == isPlaying)
        {
            return;
        }

        _youTubeBrowserReportedPlaying = isPlaying;
        _viewModel.ReportPlaybackState(isPlaying);
    }

    private async Task CompleteYouTubeBrowserItemAsync(string itemId)
    {
        if (_youTubeBrowserTransitioning ||
            !string.Equals(itemId, _youTubeBrowserItemId, StringComparison.Ordinal) ||
            !string.Equals(itemId, _viewModel.CurrentItem?.Id, StringComparison.Ordinal))
        {
            return;
        }

        _youTubeBrowserTransitioning = true;
        try
        {
            if (_youTubeBrowser is not null)
            {
                try
                {
                    await _youTubeBrowser.EvaluateJavaScriptAsync(
                        "(() => { const v=document.querySelector('video'); if(v){v.pause();} return true; })()");
                }
                catch (Exception)
                {
                    // The page can already be changing because YouTube attempted autoplay.
                }
            }

            if (!string.Equals(itemId, _viewModel.CurrentItem?.Id, StringComparison.Ordinal))
            {
                return;
            }

            SetYouTubeBrowserPlaybackState(false);
            _viewModel.Next(automatic: true);
        }
        finally
        {
            _youTubeBrowserTransitioning = false;
        }
    }

    private async Task ToggleYouTubeBrowserPlaybackAsync()
    {
        if (_youTubeBrowser is null)
        {
            return;
        }

        await _youTubeBrowser.EvaluateJavaScriptAsync(
            "(() => { const v=document.querySelector('video'); if(!v) return false; v.removeAttribute('muted'); v.defaultMuted=false; v.muted=false; v.volume=1; if(v.paused){const p=v.play();if(p&&p.catch)p.catch(()=>{});}else{v.pause();} return true; })()");
    }

    private async Task StopYouTubeBrowserAsync(bool hide)
    {
        Interlocked.Increment(ref _youTubeBrowserBridgeGeneration);
        _youTubeBrowserItemId = null;
        _youTubeBrowserVideoId = null;
        _youTubeBrowserTransitioning = false;
        _youTubeBrowserReportedPlaying = null;
        _youtubeIsPlaying = false;

        if (_youTubeBrowser is not null)
        {
            try
            {
                await _youTubeBrowser.EvaluateJavaScriptAsync(
                    "(() => { const b=window.__drumlessBridgeV4; if(b?.dispose)b.dispose(); const v=document.querySelector('video'); if(v){v.pause();} return true; })()");
            }
            catch (Exception)
            {
                // Navigation can be changing while a playlist item is switched.
            }
        }

        if (hide && _youTubeBrowserOverlay is not null)
        {
            _youTubeBrowserOverlay.IsVisible = false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return values;
    }

    private static string NormalizeJavaScriptString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch (JsonException)
            {
                return trimmed.Trim('"');
            }
        }

        return trimmed;
    }
}
