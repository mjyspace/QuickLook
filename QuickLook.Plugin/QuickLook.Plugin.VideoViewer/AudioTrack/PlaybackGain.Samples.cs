// Copyright © 2017-2026 QL-Win Contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using DirectShowLib;
using System;
using System.Runtime.InteropServices;

namespace QuickLook.Plugin.VideoViewer.AudioTrack;

public sealed partial class PlaybackGain
{
    private enum SampleFormat { Unsupported, Unsigned8, Signed16, Signed24, Signed32, Float32, Float64 }
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00AA00389B71");

    private static SampleFormat ReadFormat(AMMediaType type)
    {
        if (type.majorType != MediaType.Audio || type.formatType != FormatType.WaveEx ||
            type.formatPtr == IntPtr.Zero || type.formatSize < 16) return SampleFormat.Unsupported;

        var ptr = type.formatPtr;
        int tag = (ushort)Marshal.ReadInt16(ptr);
        int bits = Marshal.ReadInt16(ptr, 14);
        if (tag == 0xfffe) // WAVEFORMATEXTENSIBLE
        {
            if (type.formatSize < 40 || (ushort)Marshal.ReadInt16(ptr, 16) < 22 ||
                Marshal.PtrToStructure<Guid>(IntPtr.Add(ptr, 24)) != type.subType)
                return SampleFormat.Unsupported;
        }
        else if ((tag != 1 || type.subType != Pcm) && (tag != 3 || type.subType != Float))
            return SampleFormat.Unsupported;

        if (type.subType == Float)
            return bits switch { 32 => SampleFormat.Float32, 64 => SampleFormat.Float64, _ => SampleFormat.Unsupported };
        if (type.subType != Pcm) return SampleFormat.Unsupported; // e.g. IEC 61937 passthrough
        return bits switch
        {
            8 => SampleFormat.Unsigned8,
            16 => SampleFormat.Signed16,
            24 => SampleFormat.Signed24,
            32 => SampleFormat.Signed32,
            _ => SampleFormat.Unsupported
        };
    }

    private static unsafe void Scale(IntPtr buffer, int size, SampleFormat format, double gain)
    {
        // Sampled loudness estimates can miss later peaks. Saturate before converting
        // back to PCM so overload never wraps from positive to negative.
        switch (format)
        {
            case SampleFormat.Unsigned8:
                var bytes = (byte*)buffer;
                for (int i = 0; i < size; i++)
                    bytes[i] = (byte)Clamp((bytes[i] - 128) * gain + 128, 0, byte.MaxValue);
                break;
            case SampleFormat.Signed16:
                var shorts = (short*)buffer;
                for (int i = 0; i < size / sizeof(short); i++)
                    shorts[i] = (short)Clamp(shorts[i] * gain, short.MinValue, short.MaxValue);
                break;
            case SampleFormat.Signed24:
                var packed = (byte*)buffer;
                for (int i = 0; i <= size - 3; i += 3)
                {
                    int value = packed[i] | (packed[i + 1] << 8) | ((sbyte)packed[i + 2] << 16);
                    int scaled = (int)Clamp(value * gain, -8388608, 8388607);
                    packed[i] = (byte)scaled;
                    packed[i + 1] = (byte)(scaled >> 8);
                    packed[i + 2] = (byte)(scaled >> 16);
                }
                break;
            case SampleFormat.Signed32:
                var ints = (int*)buffer;
                for (int i = 0; i < size / sizeof(int); i++)
                    ints[i] = (int)Clamp(ints[i] * gain, int.MinValue, int.MaxValue);
                break;
            case SampleFormat.Float32:
                var floats = (float*)buffer;
                for (int i = 0; i < size / sizeof(float); i++)
                    floats[i] = (float)Clamp(floats[i] * gain, -1, 1);
                break;
            case SampleFormat.Float64:
                var doubles = (double*)buffer;
                for (int i = 0; i < size / sizeof(double); i++)
                    doubles[i] = Clamp(doubles[i] * gain, -1, 1);
                break;
        }
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
}
