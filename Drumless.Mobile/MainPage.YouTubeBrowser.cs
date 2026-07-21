using System.Diagnostics;
using System.Text.Json;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile;

public partial class MainPage
{
    private WebView? _youTubeBrowser;
    private Grid? _youTubeBrowserOverlay;
    private CancellationTokenSource? _youTubeBrowserMonitorCancellation;
    private string? _youTubeBrowserItemId;
    private bool _youTubeBrowserHasPlayed;
    private bool _youTubeBrowserTransitioning;

    private void EnableYouTubeBrowserPlaybackIntegration()
    {
        // Do not construct Android WebView during MainPage startup. The browser is created lazily
        // only when the first YouTube item is actually requested. This keeps normal app startup
        // on the same stable path as before the internal-browser experiment.

        // Actual YouTube tracks now play in the full mobile YouTube site hosted by Drumless' own
        // WebView. Remove the original playback handlers so no legacy iframe/external-app route can
        // compete with the browser route.
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
            Text = "YouTube · inicia sesión aquí para usar tu cuenta",
            TextColor = Color.FromArgb("#F4F7FA"),
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var listButton = new Button
        {
            Text = "Ver lista",
            BackgroundColor = Color.FromArgb("#1A222C"),
            TextColor = Color.FromArgb("#F4F7FA"),
            CornerRadius = 10,
            Padding = new Thickness(12, 6),
            FontSize = 12
        };
        listButton.Clicked += (_, _) =>
        {
            if (_youTubeBrowserOverlay is not null)
            {
                _youTubeBrowserOverlay.IsVisible = false;
            }
        };

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
        header.Add(listButton, 1);

        var overlay = new Grid
        {
            IsVisible = false,
            ZIndex = 1000,
            BackgroundColor = Colors.Black,
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
    }

    private async void OnYouTubeBrowserPlaybackRequested(object? sender, MediaItem item)
    {
        try
        {
            if (item.Kind == MediaKind.YouTube)
            {
                _audio.Stop();
                SendYouTubeCommand(new { type = "pause" });
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
        _youtubePositionSeconds = 0;
        _youtubeIsPlaying = false;

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
            await Task.Delay(500);
            await _youTubeBrowser.EvaluateJavaScriptAsync(
                "(() => { const v=document.querySelector('video'); if(v){ v.play().catch(()=>{}); } return true; })()");
        }
        catch (Exception)
        {
            // The monitor keeps waiting while the page or sign-in flow loads.
        }
    }

    private async Task ToggleYouTubeBrowserPlaybackAsync()
    {
        if (_youTubeBrowser is null)
        {
            return;
        }

        await _youTubeBrowser.EvaluateJavaScriptAsync(
            "(() => { const v=document.querySelector('video'); if(!v) return false; if(v.paused){v.play().catch(()=>{});}else{v.pause();} return true; })()");
    }

    private async Task StopYouTubeBrowserAsync(bool hide)
    {
        CancelYouTubeBrowserMonitor();
        _youTubeBrowserItemId = null;
        _youTubeBrowserHasPlayed = false;
        _youTubeBrowserTransitioning = false;
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
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(300, cancellationToken);
                if (_youTubeBrowser is null ||
                    !string.Equals(_youTubeBrowserItemId, itemId, StringComparison.Ordinal) ||
                    !string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
                {
                    return;
                }

                var raw = await MainThread.InvokeOnMainThreadAsync(() =>
                    _youTubeBrowser.EvaluateJavaScriptAsync(
                        "(() => { const v=document.querySelector('video'); const h=location.href; if(!v) return 'NOV|'+h; return [v.ended?'1':'0',v.paused?'1':'0',Number(v.currentTime||0).toFixed(3),Number(v.duration||0).toFixed(3),h].join('|'); })()"));
                var state = NormalizeJavaScriptString(raw);
                if (string.IsNullOrWhiteSpace(state) || state.StartsWith("NOV|", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = state.Split('|', 5);
                if (parts.Length < 5 ||
                    !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var position) ||
                    !double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var duration))
                {
                    continue;
                }

                var ended = parts[0] == "1";
                var paused = parts[1] == "1";
                var href = parts[4];

                _youtubePositionSeconds = Math.Max(0d, position);
                _youtubePositionReceivedTimestamp = Stopwatch.GetTimestamp();
                _youtubeIsPlaying = !paused;

                if (!paused && position > 0.05d)
                {
                    _youTubeBrowserHasPlayed = true;
                    _viewModel.ReportPlaybackState(true);
                }

                var urlChangedToAnotherVideo =
                    TryGetVideoIdFromYouTubeUrl(href, out var currentVideoId) &&
                    !string.Equals(currentVideoId, expectedVideoId, StringComparison.Ordinal);
                var reachedEnd = duration > 0d && position >= duration - 0.65d;

                if (!_youTubeBrowserHasPlayed || _youTubeBrowserTransitioning ||
                    (!ended && !reachedEnd && !urlChangedToAnotherVideo))
                {
                    continue;
                }

                _youTubeBrowserTransitioning = true;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        if (_youTubeBrowser is not null)
                        {
                            await _youTubeBrowser.EvaluateJavaScriptAsync(
                                "(() => { const v=document.querySelector('video'); if(v){v.pause();} return true; })()");
                        }

                        if (!string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
                        {
                            return;
                        }

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
