using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile;

public partial class MainPage
{
    private CancellationTokenSource? _youTubeAudioGuardCancellation;

    private void EnableYouTubeAudioGuardIntegration()
    {
        _viewModel.PlaybackRequested -= OnYouTubeAudioGuardPlaybackRequested;
        _viewModel.PlaybackRequested += OnYouTubeAudioGuardPlaybackRequested;

        _viewModel.StopPlaybackRequested -= OnYouTubeAudioGuardStopRequested;
        _viewModel.StopPlaybackRequested += OnYouTubeAudioGuardStopRequested;

        HookYouTubeAudioGuardNavigation();
    }

    private void OnYouTubeAudioGuardPlaybackRequested(object? sender, MediaItem item)
    {
        CancelYouTubeAudioGuardInstallation();
        if (item.Kind != MediaKind.YouTube)
        {
            return;
        }

        // The browser playback handler runs first and creates the WebView synchronously before its
        // first await. Hook navigation now and install the guard as the YouTube page is being built.
        HookYouTubeAudioGuardNavigation();
        StartYouTubeAudioGuardInstallation(item.Id);
    }

    private void OnYouTubeAudioGuardStopRequested(object? sender, EventArgs e) =>
        CancelYouTubeAudioGuardInstallation();

    private void HookYouTubeAudioGuardNavigation()
    {
        if (_youTubeBrowser is null)
        {
            return;
        }

        _youTubeBrowser.Navigated -= OnYouTubeAudioGuardNavigated;
        _youTubeBrowser.Navigated += OnYouTubeAudioGuardNavigated;
    }

    private void OnYouTubeAudioGuardNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var item = _viewModel.CurrentItem;
        if (item?.Model.Kind != MediaKind.YouTube)
        {
            return;
        }

        StartYouTubeAudioGuardInstallation(item.Id);
    }

    private void StartYouTubeAudioGuardInstallation(string itemId)
    {
        CancelYouTubeAudioGuardInstallation();
        var cancellation = new CancellationTokenSource();
        _youTubeAudioGuardCancellation = cancellation;
        _ = InstallYouTubeAudioGuardAsync(itemId, cancellation.Token);
    }

    private void CancelYouTubeAudioGuardInstallation()
    {
        var cancellation = _youTubeAudioGuardCancellation;
        _youTubeAudioGuardCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task InstallYouTubeAudioGuardAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        // YouTube builds and sometimes replaces its <video> element after navigation. Install a
        // page-local guard instead of repeatedly crossing the .NET/WebView bridge during playback.
        var delays = new[] { 250, 400, 700, 1_100, 1_700, 2_500 };
        foreach (var delay in delays)
        {
            await Task.Delay(delay, cancellationToken);
            if (_youTubeBrowser is null ||
                !string.Equals(_viewModel.CurrentItem?.Id, itemId, StringComparison.Ordinal))
            {
                return;
            }

            string? raw;
            try
            {
                raw = await MainThread.InvokeOnMainThreadAsync(() =>
                    _youTubeBrowser.EvaluateJavaScriptAsync(
                        """
                        (() => {
                          const key = '__drumlessAudioGuardV2';
                          if (!window[key]) {
                            const state = {
                              applying: false,
                              scheduled: false,
                              enforce() {
                                if (this.applying) return false;
                                const videos = Array.from(document.querySelectorAll('video'));
                                if (!videos.length) return false;
                                this.applying = true;
                                try {
                                  for (const video of videos) {
                                    video.removeAttribute('muted');
                                    video.defaultMuted = false;
                                    if (video.muted) video.muted = false;
                                    if (!Number.isFinite(video.volume) || video.volume < 0.99) {
                                      video.volume = 1;
                                    }
                                    if (!video.__drumlessAudioGuardAttached) {
                                      video.__drumlessAudioGuardAttached = true;
                                      const repair = () => {
                                        if (video.muted || video.volume < 0.99) {
                                          window[key]?.enforce();
                                        }
                                      };
                                      video.addEventListener('volumechange', repair, { passive: true });
                                      video.addEventListener('loadedmetadata', repair, { passive: true });
                                      video.addEventListener('canplay', repair, { passive: true });
                                      video.addEventListener('playing', repair, { passive: true });
                                    }
                                  }
                                } finally {
                                  this.applying = false;
                                }
                                return true;
                              },
                              schedule() {
                                if (this.scheduled) return;
                                this.scheduled = true;
                                requestAnimationFrame(() => {
                                  this.scheduled = false;
                                  this.enforce();
                                });
                              }
                            };
                            state.observer = new MutationObserver(() => state.schedule());
                            state.observer.observe(document.documentElement || document, {
                              childList: true,
                              subtree: true
                            });
                            window[key] = state;
                          }

                          const found = window[key].enforce();
                          const video = document.querySelector('video');
                          if (video && video.paused) {
                            const play = video.play();
                            if (play && play.catch) play.catch(() => {});
                          }
                          return found ? 'READY' : 'WAIT';
                        })()
                        """));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (string.Equals(
                    NormalizeJavaScriptString(raw),
                    "READY",
                    StringComparison.Ordinal))
            {
                return;
            }
        }
    }
}
