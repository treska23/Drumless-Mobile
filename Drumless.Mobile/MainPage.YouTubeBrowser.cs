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
    private CancellationTokenSource? _youTubeBrowserMonitorCancellation;
    private string? _youTubeBrowserItemId;
    private bool _youTubeBrowserHasPlayed;
    private bool _youTubeBrowserTransitioning;
    private bool _youTubeBrowserExpanded;
    private bool? _youTubeBrowserReportedPlaying;

    private void EnableYouTubeBrowserPlaybackIntegration()
    {
        // Actual YouTube tracks play only in the full mobile YouTube site hosted by Drumless'
        // own WebView. The legacy iframe handler remains available only for playlist inspection.
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

        // The compact browser floats at the bottom. The playlist remains visible and usable behind
        // it; the user can explicitly expand the browser only when account/page interaction is needed.
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

        CancelYouTubeBrowserMonitor();
        _youTubeBrowserItemId = item.Id;
        _youTubeBrowserHasPlayed = false;
        _youTubeBrowserTransitioning = false;
        _youTubeBrowserReportedPlaying = null;
        _youtubePositionSeconds = 0;
        _youtubeIsPlaying = false;

        // Always start compact. The track list must remain on screen during normal playback.
        _youTubeBrowserExpanded = false;
        _youTubeBrowserOverlay.HeightRequest = YouTubeBrowserCollapsedHeight;
        _youTubeBrowserOverlay.VerticalOptions = LayoutOptions.End;
        _youTubeBrowserOverlay.Margin = new Thickness(8);
        if (_youTubeBrowserExpandButton is not null)
        {
            _youTubeBrowserExpandButton.Text = "Ampliar";
        }

        _youTubeBrowserOverlay.IsVisible = true;
        var url = $"https://m.youtube.com/watch?v={Uri.EscapeDataString(item.YouTubeVideoId)}";
        _youTubeBrowser.Source = url;

        var cancellation = new CancellationTokenSource();
        _youTubeBrowserMonitorCancellation = cancellation;
        _ = MonitorYouTubeBrowserAsync(item.Id, item.YouTubeVideoId, cancellation.Token);
    }

    private async void OnYouTubeBrowserNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_youTubeBrowser is null ||
            _viewModel.CurrentItem?.Model.Kind != MediaKind.YouTube ||
            string.IsNullOrWhiteSpace(_youTubeBrowserItemId))
        {
            return;
        }

        try
        {
            await EnsureYouTubeBrowserAudibleAndPlayingAsync();
        }
        catch (Exception)
        {
            // The monitor keeps waiting while the page or sign-in flow loads.
        }
    }

    private async Task EnsureYouTubeBrowserAudibleAndPlayingAsync()
    {
        if (_youTubeBrowser is null)
        {
            return;
        }

        // Keep this light: EvaluateJavaScriptAsync executes through the WebView/UI thread. A few
        // spaced attempts are enough for YouTube's SPA to create the <video> element without
        // hammering Chromium during the first seconds of playback.
        var delays = new[] { 500, 1000, 1500 };
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            if (_youTubeBrowser is null)
            {
                return;
            }

            var result = await MainThread.InvokeOnMainThreadAsync(() =>
                _youTubeBrowser.EvaluateJavaScriptAsync(
                    "(() => { const v=document.querySelector('video'); if(!v) return 'NOV'; const b=document.querySelector('.ytp-mute-button'); if(v.muted && b){try{b.click();}catch(e){}} v.removeAttribute('muted'); v.defaultMuted=false; v.muted=false; v.volume=1; const p=v.play(); if(p&&p.catch){p.catch(()=>{});} return 'OK'; })()"));
            var state = NormalizeJavaScriptString(result);
            if (string.Equals(state, "OK", StringComparison.Ordinal))
            {
                return;
            }
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
        CancelYouTubeBrowserMonitor();
        _youTubeBrowserItemId = null;
        _youTubeBrowserHasPlayed = false;
        _youTubeBrowserTransitioning = false;
        _youTubeBrowserReportedPlaying = null;
        _youtubeIsPlaying = false;

        if (_youTubeBrowser is not null)
        {
            try
            {
                await _youTubeBrowser.EvaluateJavaScriptAsync(
                    "(() => { const v=document.querySelector('video'); if(v){v.pause();} return true; })()");
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

    private void CancelYouTubeBrowserMonitor()
    {
        var cancellation = _youTubeBrowserMonitorCancellation;
        _youTubeBrowserMonitorCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task MonitorYouTubeBrowserAsync(
        string itemId,
        string expectedVideoId,
        CancellationToken cancellationToken)
    {
        var nextPollDelay = 2000;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(nextPollDelay, cancellationToken);
                if (_youTubeBrowser is null ||
                    !string.Equals(_youTubeBrowserItemId, itemId, StringComparison.Ordinal) ||
                    !string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
                {
                    return;
                }

                // Android WebView requires evaluateJavascript on the UI thread. Keep these calls
                // infrequent during normal playback, then tighten the cadence only near the end.
                var raw = await MainThread.InvokeOnMainThreadAsync(() =>
                    _youTubeBrowser.EvaluateJavaScriptAsync(
                        "(() => { const v=document.querySelector('video'); const h=location.href; if(!v) return 'NOV|'+h; return [v.ended?'1':'0',v.paused?'1':'0',Number(v.currentTime||0).toFixed(3),Number(v.duration||0).toFixed(3),h].join('|'); })()"));
                var state = NormalizeJavaScriptString(raw);
                if (string.IsNullOrWhiteSpace(state) || state.StartsWith("NOV|", StringComparison.Ordinal))
                {
                    nextPollDelay = 2000;
                    continue;
                }

                var parts = state.Split('|', 5);
                if (parts.Length < 5 ||
                    !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var position) ||
                    !double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var duration))
                {
                    nextPollDelay = 2000;
                    continue;
                }

                var ended = parts[0] == "1";
                var paused = parts[1] == "1";
                var href = parts[4];
                var observedPlaying = !paused;

                _youtubePositionSeconds = Math.Max(0d, position);
                _youtubePositionReceivedTimestamp = Stopwatch.GetTimestamp();
                _youtubeIsPlaying = observedPlaying;

                if (observedPlaying && position > 0.05d)
                {
                    _youTubeBrowserHasPlayed = true;
                }

                // Do not rewrite StatusMessage and bound UI state on every poll. That periodic UI
                // invalidation was unnecessary work while Chromium was decoding and playing audio.
                if (_youTubeBrowserReportedPlaying != observedPlaying)
                {
                    _youTubeBrowserReportedPlaying = observedPlaying;
                    _viewModel.ReportPlaybackState(observedPlaying);
                }

                var urlChangedToAnotherVideo =
                    TryGetVideoIdFromYouTubeUrl(href, out var currentVideoId) &&
                    !string.Equals(currentVideoId, expectedVideoId, StringComparison.Ordinal);
                var reachedEnd = duration > 0d && position >= duration - 0.7d;

                if (_youTubeBrowserHasPlayed && !_youTubeBrowserTransitioning &&
                    (ended || reachedEnd || urlChangedToAnotherVideo))
                {
                    _youTubeBrowserTransitioning = true;
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try
                        {
                            // Drumless owns playlist sequencing. Stop the YouTube page first so its
                            // own autoplay cannot continue, then advance using Sequential/Shuffle/Single.
                            if (_youTubeBrowser is not null)
                            {
                                await _youTubeBrowser.EvaluateJavaScriptAsync(
                                    "(() => { const v=document.querySelector('video'); if(v){v.pause();} return true; })()");
                            }

                            if (!string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
                            {
                                return;
                            }

                            _youTubeBrowserReportedPlaying = false;
                            _viewModel.ReportPlaybackState(false);
                            _viewModel.Next(automatic: true);
                        }
                        finally
                        {
                            _youTubeBrowserTransitioning = false;
                        }
                    });
                    return;
                }

                if (!observedPlaying)
                {
                    nextPollDelay = 1500;
                    continue;
                }

                if (duration <= 0d)
                {
                    nextPollDelay = 2000;
                    continue;
                }

                var remaining = Math.Max(0d, duration - position);
                nextPollDelay = remaining switch
                {
                    <= 4d => 250,
                    <= 10d => 750,
                    _ => 2500
                };
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when changing tracks.
        }
        catch (Exception exception)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                _viewModel.ReportPlaybackFailure($"No se pudo supervisar YouTube: {exception.Message}"));
        }
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

    private static bool TryGetVideoIdFromYouTubeUrl(string? url, out string? videoId)
    {
        videoId = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || !string.Equals(pair[..separator], "v", StringComparison.Ordinal))
            {
                continue;
            }

            videoId = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return !string.IsNullOrWhiteSpace(videoId);
        }

        return false;
    }
}
