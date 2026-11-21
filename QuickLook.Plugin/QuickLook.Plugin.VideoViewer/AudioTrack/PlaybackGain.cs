// Copyright © 2017-2026 QL-Win Contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using DirectShowLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickLook.Plugin.VideoViewer.AudioTrack;

// The renderer owns the user's volume slider. Apply per-file gain to decoded samples
// before that renderer so quiet files can also be boosted at 100% slider volume.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed partial class PlaybackGain : ISampleGrabberCB
{
    private double _amplitude = 1;
    private SampleFormat _format;

    private PlaybackGain(SampleFormat format) => _format = format;

    internal double GainDb
    {
        get => 20 * Math.Log10(Volatile.Read(ref _amplitude));
        set => Volatile.Write(ref _amplitude, Math.Pow(10, value / 20));
    }

    // IAMStreamSelect also lists video and subtitle tracks. FFmpeg's 0:a:N counts
    // only audio streams. A graph with no stream selector needs a metadata fallback.
    internal static int? GetAudioTrack(IGraphBuilder graph)
    {
        foreach (var filter in Filters(graph))
        {
            if (!(filter is IAMStreamSelect selector) || selector.Count(out int count) < 0)
                continue;

            int audioOrdinal = 0;
            for (int index = 0; index < count; index++)
            {
                int hr = selector.Info(index, out var type, out var flags, out _, out _,
                    out _, out var stream, out var related);
                try
                {
                    if (hr < 0) break; // Cannot identify later ordinals reliably.
                    if (type?.majorType != MediaType.Audio) continue;
                    if (flags != AMStreamSelectInfoFlags.Disabled) return audioOrdinal;
                    audioOrdinal++;
                }
                finally
                {
                    DsUtils.FreeAMMediaType(type);
                    DsUtils.ReleaseComObject(stream);
                    DsUtils.ReleaseComObject(related);
                }
            }
        }

        return null;
    }

    // Called on the player's MTA after graph construction and before preroll.
    internal static PlaybackGain TryAttach(IGraphBuilder graph)
    {
        if (!(graph is IMediaControl control) || control.GetState(0, out var state) != 0 ||
            state != FilterState.Stopped) return null;

        foreach (var filter in Filters(graph))
        {
            // A renderer is a sink. Only touch its connected, uncompressed audio input.
            var output = DsFindPin.ByDirection(filter, PinDirection.Output, 0);
            if (output != null)
            {
                DsUtils.ReleaseComObject(output);
                continue;
            }

            var input = DsFindPin.ByDirection(filter, PinDirection.Input, 0);
            var type = new AMMediaType();
            IPin upstream = null;
            try
            {
                if (input == null || input.ConnectionMediaType(type) < 0) continue;
                var format = ReadFormat(type);
                if (format != SampleFormat.Unsupported && input.ConnectedTo(out upstream) == 0)
                    return Insert(graph, upstream, input, type, format);
            }
            finally
            {
                DsUtils.FreeAMMediaType(type);
                DsUtils.ReleaseComObject(upstream);
                DsUtils.ReleaseComObject(input);
            }
        }

        return null;
    }

    private static PlaybackGain Insert(IGraphBuilder graph, IPin source, IPin sink, AMMediaType type,
        SampleFormat format)
    {
        IBaseFilter filter = null;
        IPin input = null, output = null;
        bool added = false, disconnected = false;
        try
        {
            filter = (IBaseFilter)new SampleGrabber();
            var grabber = (ISampleGrabber)filter;
            var gain = new PlaybackGain(format);
            DsError.ThrowExceptionForHR(grabber.SetMediaType(type));
            DsError.ThrowExceptionForHR(grabber.SetCallback(gain, 0)); // SampleCB sees the original sample.
            DsError.ThrowExceptionForHR(graph.AddFilter(filter, "QuickLook Playback Gain"));
            added = true;
            input = DsFindPin.ByDirection(filter, PinDirection.Input, 0);
            output = DsFindPin.ByDirection(filter, PinDirection.Output, 0);
            if (input == null || output == null) throw new InvalidOperationException("No sample grabber pins");

            DsError.ThrowExceptionForHR(graph.Disconnect(source));
            disconnected = true;
            DsError.ThrowExceptionForHR(graph.Disconnect(sink));
            DsError.ThrowExceptionForHR(graph.ConnectDirect(source, input, type));
            DsError.ThrowExceptionForHR(graph.ConnectDirect(output, sink, type));
            return gain;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            if (added) graph.RemoveFilter(filter);
            if (disconnected)
            {
                graph.Disconnect(source);
                graph.Disconnect(sink);
                DsError.ThrowExceptionForHR(graph.ConnectDirect(source, sink, type));
            }
            return null;
        }
        finally
        {
            DsUtils.ReleaseComObject(output);
            DsUtils.ReleaseComObject(input);
            DsUtils.ReleaseComObject(filter);
        }
    }

    private static IEnumerable<IBaseFilter> Filters(IGraphBuilder graph)
    {
        if (graph == null || graph.EnumFilters(out var filters) < 0) yield break;
        try
        {
            var item = new IBaseFilter[1];
            while (filters.Next(1, item, IntPtr.Zero) == 0)
            {
                try { yield return item[0]; }
                finally { DsUtils.ReleaseComObject(item[0]); }
            }
        }
        finally { DsUtils.ReleaseComObject(filters); }
    }

    int ISampleGrabberCB.SampleCB(double sampleTime, IMediaSample sample)
    {
        try
        {
            if (sample.GetMediaType(out var changed) == 0 && changed != null)
            {
                try { _format = ReadFormat(changed); }
                finally { DsUtils.FreeAMMediaType(changed); }
            }

            double gain = Volatile.Read(ref _amplitude);
            if (gain != 1 && _format != SampleFormat.Unsupported && sample.GetPointer(out var buffer) == 0)
            {
                int size = sample.GetActualDataLength();
                if (buffer != IntPtr.Zero && size > 0 && size <= sample.GetSize())
                    Scale(buffer, size, _format, gain);
            }
        }
        finally
        {
            // Return the allocator's sample promptly; waiting for a GC can exhaust its pool.
            DsUtils.ReleaseComObject(sample);
        }
        return 0;
    }

    int ISampleGrabberCB.BufferCB(double sampleTime, IntPtr buffer, int size) => 0;
}
