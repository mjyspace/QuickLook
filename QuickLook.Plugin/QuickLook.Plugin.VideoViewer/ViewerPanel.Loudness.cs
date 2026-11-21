// Copyright © 2017-2026 QL-Win Contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using QuickLook.Common.Helpers;
using QuickLook.MediaInfo;
using QuickLook.MediaInfo.Core;
using QuickLook.Plugin.VideoViewer.AudioTrack;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

public partial class ViewerPanel
{
    private const string SettingsDomain = "QuickLook.Plugin.VideoViewer";
    private const string ReferenceSetting = "LoudnessReferenceLufs";
    private readonly CancellationTokenSource _previewLifetime = new();
    // Accessed on the UI thread. An explicit calibration can finish after its preview closes.
    private static CancellationTokenSource _pendingReference;
    private string _translationFile;
    private string _mediaPath;
    private int _audioTrackCount;
    private int? _selectedAudioTrack;
    private double _audibleGainDb;
    private bool _waitingForLoudness;

    // Owned by the player's MTA after the source is opened. A decoder rebuild must
    // use the same gain even if the background scan has since finished.
    private double? _referenceAtOpen;
    private int? _measuredTrack;
    private double _fixedGainDb;
    private PlaybackGain _gainFilter;
    private string _graphStatus;

    private void InitializeLoudnessControls(string translationFile)
    {
        _translationFile = translationFile;
        loudnessControls.Visibility = Visibility.Collapsed;
        buttonSetLoudnessReference.Content = Localize("BTN_SetLoudnessReference");
        buttonDisableLoudness.Content = Localize("BTN_DisableLoudness");
        buttonSetLoudnessReference.Click += SetLoudnessReference;
        buttonDisableLoudness.Click += DisableLoudness;
    }

    private string Localize(string key) => TranslationHelper.Get(key, _translationFile);

    private static double? ReadLoudnessReference()
    {
        var value = SettingHelper.Get(ReferenceSetting, string.Empty, SettingsDomain);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var target)
               && !double.IsNaN(target) && !double.IsInfinity(target) ? target : null;
    }

    private void PrepareLoudness(string path, MediaInfoNative info)
    {
        if (string.IsNullOrWhiteSpace(info?.Get(StreamKind.Audio, 0, "Format"))) return;

        _mediaPath = path;
        int.TryParse(info.Get(StreamKind.General, 0, "AudioCount"), out _audioTrackCount);
        _referenceAtOpen = ReadLoudnessReference();
        _waitingForLoudness = _referenceAtOpen.HasValue;
        if (_waitingForLoudness)
            mediaElement.LoadedBehavior = MediaState.Manual;
        loudnessControls.Visibility = Visibility.Visible;
        buttonSetLoudnessReference.IsEnabled = false;
        buttonDisableLoudness.IsEnabled = false;
        UpdateLoudnessStatus(_referenceAtOpen.HasValue ? "Loudness_Analyzing" : null);
        mediaElement.MediaUriPlayer.MediaOpened += PrepareGraphLoudness;
    }

    // MediaUriPlayer raises this synchronously on its DirectShow thread after connecting
    // the graph and before Run. This also covers autoplay, media keys and graph rebuilds.
    private void PrepareGraphLoudness()
    {
        var token = _previewLifetime.Token;
        if (token.IsCancellationRequested) return;
        var player = mediaElement.MediaUriPlayer;
        _gainFilter = null;
        int? track = null;
        try
        {
            track = _audioTrackCount == 1 ? 0 : PlaybackGain.GetAudioTrack(player.Graph);
            if (!track.HasValue)
                _graphStatus = "Loudness_Unavailable";
            else if (_referenceAtOpen.HasValue)
            {
                if (_measuredTrack != track)
                {
                    _measuredTrack = track;
                    var measurement = LoudnessAnalyzer.GetPreviewAsync(_mediaPath, track.Value,
                        TimeSpan.FromTicks(Math.Max(0L, player.Duration)), token).GetAwaiter().GetResult();
                    _fixedGainDb = measurement == null ? 0 : CalculateGain(_referenceAtOpen.Value, measurement);
                    _graphStatus = measurement == null ? AnalysisFailureStatus() : "Loudness_Active";
                }
                if (_fixedGainDb != 0 && !token.IsCancellationRequested)
                {
                    _gainFilter = PlaybackGain.TryAttach(player.Graph);
                    if (_gainFilter != null)
                        _gainFilter.GainDb = _fixedGainDb;
                    else
                        _graphStatus = "Loudness_Unavailable";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _graphStatus = "Loudness_Unavailable";
        }

        var gain = _gainFilter == null ? 0 : _fixedGainDb;
        var status = _graphStatus;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (token.IsCancellationRequested) return;
            _selectedAudioTrack = track;
            _audibleGainDb = gain;
            if (_waitingForLoudness)
            {
                _waitingForLoudness = false;
                mediaElement.Play();
            }
            _context.IsBusy = false;
            buttonSetLoudnessReference.IsEnabled = track.HasValue;
            buttonDisableLoudness.IsEnabled = true;
            UpdateLoudnessStatus(status);
        }));
    }

    private static double CalculateGain(double reference, LoudnessMeasurement measurement)
    {
        var gain = reference - measurement.IntegratedLufs;
        if (gain <= 0) return gain;
        // Keep 1 dB of true-peak headroom and avoid extreme boosts on near-silent material.
        return Math.Min(gain, Math.Min(10, Math.Max(0, -1 - measurement.TruePeakDb)));
    }

    private static string AnalysisFailureStatus() =>
        LoudnessAnalyzer.IsFfmpegAvailable() ? "Loudness_Unavailable" : "Loudness_FFmpegMissing";

    private void UpdateLoudnessStatus(string key)
    {
        textLoudnessStatus.Text = key == null ? string.Empty : Localize(key);
        UpdateDisableLoudnessButton();
    }

    private void UpdateDisableLoudnessButton()
    {
        buttonDisableLoudness.Visibility = ReadLoudnessReference().HasValue || _pendingReference != null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetLoudnessReference(object sender, RoutedEventArgs args)
    {
        if (!_selectedAudioTrack.HasValue) return;
        _pendingReference?.Cancel();
        using var request = new CancellationTokenSource();
        _pendingReference = request;
        var gainAtClick = _audibleGainDb;
        var trackAtClick = _selectedAudioTrack.Value;
        string status = "Loudness_Unavailable";
        buttonSetLoudnessReference.IsEnabled = false;
        UpdateLoudnessStatus("Loudness_Analyzing");
        try
        {
            var measurement = await LoudnessAnalyzer.GetFullAsync(_mediaPath, trackAtClick, request.Token);
            if (request.IsCancellationRequested) return;
            if (measurement != null)
            {
                // The shared volume slider and Windows volume apply equally to subsequent files.
                var target = measurement.IntegratedLufs + gainAtClick;
                SettingHelper.Set(ReferenceSetting, target.ToString("R", CultureInfo.InvariantCulture), SettingsDomain);
                if (!_previewLifetime.IsCancellationRequested)
                    mediaElement.MediaUriPlayer.Dispatcher.BeginInvoke(() =>
                    {
                        _referenceAtOpen = target;
                        _measuredTrack = trackAtClick;
                        _fixedGainDb = gainAtClick;
                        _graphStatus = "Loudness_Active";
                    });
                status = "Loudness_Active";
            }
            else
                status = AnalysisFailureStatus();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            if (ReferenceEquals(request, _pendingReference))
                _pendingReference = null;
            if (!_previewLifetime.IsCancellationRequested)
            {
                buttonSetLoudnessReference.IsEnabled = _selectedAudioTrack.HasValue;
                UpdateLoudnessStatus(request.IsCancellationRequested
                    ? (ReadLoudnessReference().HasValue ? "Loudness_Active" : null) : status);
            }
        }
    }

    private void DisableLoudness(object sender, RoutedEventArgs args)
    {
        _pendingReference?.Cancel();
        _pendingReference = null;
        SettingHelper.Set(ReferenceSetting, string.Empty, SettingsDomain);
        _audibleGainDb = 0;
        mediaElement.MediaUriPlayer.Dispatcher.BeginInvoke(() =>
        {
            _referenceAtOpen = null;
            _fixedGainDb = 0;
            _graphStatus = null;
            if (_gainFilter != null) _gainFilter.GainDb = 0;
        });
        UpdateLoudnessStatus(null);
    }
}
