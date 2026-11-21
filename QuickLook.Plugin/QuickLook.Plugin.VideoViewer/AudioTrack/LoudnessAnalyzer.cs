// Copyright © 2017-2026 QL-Win Contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Plugin.VideoViewer.AudioTrack;

internal sealed class LoudnessMeasurement
{
    internal double IntegratedLufs { get; }
    internal double TruePeakDb { get; }

    internal LoudnessMeasurement(double integratedLufs, double truePeakDb)
    {
        IntegratedLufs = integratedLufs;
        TruePeakDb = truePeakDb;
    }

    internal static LoudnessMeasurement FromStrings(string loudness, string peak)
    {
        if (!double.TryParse(loudness, NumberStyles.Float, CultureInfo.InvariantCulture, out var integrated) ||
            !double.TryParse(peak, NumberStyles.Float, CultureInfo.InvariantCulture, out var truePeak) ||
            !(integrated > -70 && integrated < 100 && truePeak > -200 && truePeak < 100))
            return null; // EBU R128's absolute gate also excludes silence and empty measurements.

        return new LoudnessMeasurement(integrated, truePeak);
    }
}

internal static class LoudnessAnalyzer
{
    private const string Meter = "ebur128=peak=true:framelog=verbose";
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly SemaphoreSlim FullScanSlot = new(1, 1);
    private static readonly List<BackgroundScan> BackgroundScans = [];
    private static volatile bool _lastLookupFoundFfmpeg = true;

    static LoudnessAnalyzer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Cancel();
    }

    // The UI must not probe potentially offline PATH directories to display an error.
    // Every actual scan resolves PATH afresh, including after an earlier failure.
    internal static bool IsFfmpegAvailable() => _lastLookupFoundFfmpeg;

    internal static async Task<LoudnessMeasurement> GetPreviewAsync(string path, int audioTrack,
        TimeSpan duration, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sampleToken = startup.Token;
        var deadline = Task.Delay(TimeSpan.FromSeconds(3), sampleToken);
        token.ThrowIfCancellationRequested();
        var scan = StartBackgroundScan(path, audioTrack);
        var full = scan.Task;

        // Short files usually finish within the budget. Prepare a sample as insurance
        // on slower drives. For longer files, start as soon as the sample is ready.
        var preferFull = duration > TimeSpan.Zero && duration <= TimeSpan.FromMinutes(20);
        var sample = Task.Run(async () =>
        {
            if (!await scan.NeedsMeasurement.Task.ConfigureAwait(false)) return null;
            if (preferFull)
                await Task.Delay(1800, sampleToken).ConfigureAwait(false);
            return Measure(path, audioTrack, duration, sampleToken);
        }, sampleToken);

        try
        {
            if (!preferFull && await Task.WhenAny(full, sample, deadline).ConfigureAwait(false) == sample &&
                !full.IsCompleted)
            {
                var estimate = await sample.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (estimate != null) return estimate;
            }

            await Task.WhenAny(full, deadline).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (full.Status == TaskStatus.RanToCompletion)
                return await full.ConfigureAwait(false);
            if (full.IsCanceled)
                await Task.WhenAny(sample, deadline).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return sample.Status == TaskStatus.RanToCompletion ? sample.Result : null;
        }
        finally
        {
            startup.Cancel(); // Only the full measurement is allowed to continue for the cache.
        }
    }

    internal static Task<LoudnessMeasurement> GetFullAsync(string path, int audioTrack, CancellationToken token)
        => ScanFullAsync(path, audioTrack, token, null);

    private sealed class BackgroundScan
    {
        internal string Path;
        internal int Track;
        internal CancellationTokenSource Cancellation;
        internal Task<LoudnessMeasurement> Task;
        internal readonly TaskCompletionSource<bool> NeedsMeasurement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static BackgroundScan StartBackgroundScan(string path, int audioTrack)
    {
        lock (BackgroundScans)
        {
            BackgroundScans.RemoveAll(s => s.Task.IsCompleted);
            var same = BackgroundScans.Find(s => s.Track == audioTrack &&
                string.Equals(s.Path, path, StringComparison.Ordinal));
            if (same != null) return same;

            // Keep an active whole-file scan and one recent request. Rapidly stepping
            // through Explorer must not enqueue a whole directory of scans.
            if (BackgroundScans.Count == 2)
            {
                BackgroundScans[1].Cancellation.Cancel();
                BackgroundScans.RemoveAt(1);
            }
            var scan = new BackgroundScan { Path = path, Track = audioTrack, Cancellation = new CancellationTokenSource() };
            scan.Task = ScanFullAsync(path, audioTrack, scan.Cancellation.Token, scan.NeedsMeasurement);
            BackgroundScans.Add(scan);
            _ = scan.Task.ContinueWith(_ =>
            {
                // A queued scan may be cancelled before its delegate ever starts.
                scan.NeedsMeasurement.TrySetResult(scan.Task.IsCanceled);
                lock (BackgroundScans)
                    BackgroundScans.Remove(scan);
                scan.Cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return scan;
        }
    }

    private static Task<LoudnessMeasurement> ScanFullAsync(string path, int audioTrack, CancellationToken token,
        TaskCompletionSource<bool> needsMeasurement)
    {
        return Task.Run(async () =>
        {
            try
            {
                var cache = new LoudnessCache(path, audioTrack);
                var cached = cache.Read();
                needsMeasurement?.TrySetResult(cached == null);
                if (cached != null) return cached;

                if (needsMeasurement != null) await FullScanSlot.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    // A previous preview or a reference measurement may have just finished.
                    if (needsMeasurement != null)
                    {
                        cached = cache.Read();
                        if (cached != null) return cached;
                    }
                    var measured = Measure(path, audioTrack, null, token);
                    if (measured == null || !cache.SourceIsCurrent()) return null;
                    cache.Write(measured);
                    return measured;
                }
                finally
                {
                    if (needsMeasurement != null) FullScanSlot.Release();
                }
            }
            catch (Exception e) when (IsMediaError(e))
            {
                Debug.WriteLine(e);
                return null;
            }
        }, token);
    }

    private static LoudnessMeasurement Measure(string path, int track, TimeSpan? sampleDuration, CancellationToken request)
    {
        // Reference scans can outlive their preview, but no FFmpeg child should outlive the host.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, Shutdown.Token);
        var token = lifetime.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var ffmpeg = FindFfmpeg();
            _lastLookupFoundFfmpeg = ffmpeg != null;
            if (ffmpeg == null || track < 0) return null;
            path = Path.GetFullPath(path);
            var arguments = new StringBuilder("-hide_banner -nostdin -nostats -loglevel info ");
            if (sampleDuration.HasValue && sampleDuration.Value.TotalSeconds >= 6)
            {
                var duration = sampleDuration.Value.TotalSeconds;
                var seconds = Math.Min(20, duration / 6);
                // Read disjoint excerpts and measure their concatenated PCM together.
                // Averaging separate LUFS values would lose R128's programme-wide gating.
                var graph = new StringBuilder();
                for (var i = 0; i < 3; i++)
                {
                    var start = duration * (i + 0.5) / 3 - seconds / 2;
                    arguments.AppendFormat(CultureInfo.InvariantCulture, "-ss {0:F3} -t {1:F3} ", start, seconds);
                    AddInput(arguments, path);
                    graph.AppendFormat(CultureInfo.InvariantCulture, "[{0}:a:{1}]asetpts=PTS-STARTPTS[a{0}];", i, track);
                }
                arguments.Append($"-filter_complex \"{graph}[a0][a1][a2]concat=n=3:v=0:a=1,{Meter}[out]\" -map \"[out]\" ");
            }
            else
            {
                if (sampleDuration.HasValue) arguments.Append("-t 20 ");
                AddInput(arguments, path);
                arguments.AppendFormat(CultureInfo.InvariantCulture, "-map 0:a:{0} -af {1} ", track, Meter);
            }
            arguments.Append("-vn -sn -dn -f null -");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, arguments.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            token.ThrowIfCancellationRequested();
            process.Start();
            using var cancel = token.Register(() =>
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { } // Already exited.
                catch (Win32Exception e) { Debug.WriteLine(e); }
            });

            string integrated = null, peak = null;
            var inSummary = false;
            string line;
            while ((line = process.StandardError.ReadLine()) != null)
            {
                if (line.StartsWith("[Parsed_ebur128_", StringComparison.Ordinal) &&
                    line.EndsWith(" Summary:", StringComparison.Ordinal))
                {
                    inSummary = true;
                    integrated = peak = null;
                }
                else if (inSummary)
                {
                    line = line.Trim();
                    if (line.StartsWith("I:", StringComparison.Ordinal)) integrated = ReadValue(line);
                    if (line.StartsWith("Peak:", StringComparison.Ordinal)) peak = ReadValue(line);
                }
            }
            process.WaitForExit();
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? LoudnessMeasurement.FromStrings(integrated, peak) : null;
        }
        catch (Exception e) when (IsMediaError(e))
        {
            Debug.WriteLine(e);
            return null;
        }
    }

    private static string ReadValue(string line)
    {
        var value = line.Substring(line.IndexOf(':') + 1).Trim();
        var end = value.IndexOf(' ');
        return end < 0 ? value : value.Substring(0, end);
    }

    private static void AddInput(StringBuilder arguments, string path)
    {
        // Windows file names cannot contain double quotes. Launch directly without a shell.
        arguments.Append("-protocol_whitelist file,crypto,data -i \"").Append(path).Append("\" ");
    }

    private static string FindFfmpeg()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            // Ignore relative PATH entries: the current directory may be the previewed folder.
            if (!directory.StartsWith(@"\\", StringComparison.Ordinal) &&
                !(directory.Length > 2 && directory[1] == ':' && (directory[2] == '\\' || directory[2] == '/')))
                continue;
            try
            {
                var executable = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(executable)) return executable;
            }
            catch (ArgumentException) { } // An unrelated invalid entry does not hide later entries.
        }
        return null;
    }

    internal static bool IsMediaError(Exception e) =>
        e is IOException || e is UnauthorizedAccessException || e is Win32Exception ||
        e is ArgumentException || e is NotSupportedException || e is System.Security.SecurityException;
}
