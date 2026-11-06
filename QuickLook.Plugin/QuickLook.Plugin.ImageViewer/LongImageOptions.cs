// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using System.Windows;

namespace QuickLook.Plugin.ImageViewer;

internal sealed class LongImageOptions
{
    private const string Domain = "QuickLook.Plugin.ImageViewer";

    public bool Enabled { get; private set; }
    public double AspectRatio { get; private set; }
    public double ViewportRatio { get; private set; }
    public double WindowRatio { get; private set; }

    public static LongImageOptions Load() => new()
    {
        Enabled = SettingHelper.Get("LongImageMode", true, Domain),
        AspectRatio = ReadRatio("LongImageAspectRatio", 2.5d, 1d, double.MaxValue),
        ViewportRatio = ReadRatio("LongImageViewportRatio", 1.5d, 1d, double.MaxValue),
        WindowRatio = ReadRatio("LongImageWindowRatio", 0.9d, 0.1d, 1d),
    };

    public bool IsCandidate(Size size) => Enabled && IsValidSize(size) && size.Height / size.Width >= AspectRatio;

    public bool ShouldFitWidth(Size image, Size viewport) => IsCandidate(image) && IsValidSize(viewport)
        && image.Height / image.Width * viewport.Width / viewport.Height >= ViewportRatio;

    internal static bool IsValidSize(Size size) => !size.IsEmpty && size.Width > 0 && size.Height > 0
        && !double.IsInfinity(size.Width) && !double.IsInfinity(size.Height)
        && !double.IsNaN(size.Width) && !double.IsNaN(size.Height);

    private static double ReadRatio(string name, double fallback, double minimum, double maximum)
    {
        var value = SettingHelper.Get(name, fallback, Domain);
        return double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum
            ? fallback : value;
    }
}
