// Copyright © 2017-2026 QL-Win Contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using QuickLook.Common.Helpers;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuickLook.Plugin.VideoViewer.AudioTrack;

internal sealed class LoudnessCache
{
    private readonly string _source;
    private readonly string _entry;
    private readonly string _fingerprint;

    internal LoudnessCache(string path, int audioTrack)
    {
        _source = Path.GetFullPath(path);
        _fingerprint = Fingerprint();
        using var sha = SHA256.Create();
        var key = _source + "\0" + audioTrack.ToString(CultureInfo.InvariantCulture);
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
        _entry = Path.Combine(SettingHelper.LocalDataPath, "LoudnessCache", "v1", hash + ".txt");
    }

    private string Fingerprint()
    {
        var file = new FileInfo(_source);
        return string.Format(CultureInfo.InvariantCulture, "{0} {1}", file.Length, file.LastWriteTimeUtc.Ticks);
    }

    internal bool SourceIsCurrent() => _fingerprint == Fingerprint();

    internal LoudnessMeasurement Read()
    {
        try
        {
            using var stream = new FileStream(_entry, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 256) return null;
            using var reader = new StreamReader(stream);
            if (reader.ReadLine() == _fingerprint)
                return LoudnessMeasurement.FromStrings(reader.ReadLine(), reader.ReadLine());
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (LoudnessAnalyzer.IsMediaError(e)) { Debug.WriteLine(e); }
        return null;
    }

    internal void Write(LoudnessMeasurement measured)
    {
        var temporary = _entry + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_entry));
            File.WriteAllLines(temporary, [_fingerprint, measured.IntegratedLufs.ToString("R", CultureInfo.InvariantCulture),
                measured.TruePeakDb.ToString("R", CultureInfo.InvariantCulture)]);
            // Only publish complete files, including when two QuickLook processes finish together.
            if (File.Exists(_entry))
                File.Replace(temporary, _entry, null);
            else
                File.Move(temporary, _entry);
        }
        catch (Exception e) when (LoudnessAnalyzer.IsMediaError(e)) { Debug.WriteLine(e); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (LoudnessAnalyzer.IsMediaError(e)) { Debug.WriteLine(e); }
        }
    }
}
