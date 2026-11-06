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

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLook.Plugin.ImageViewer;

public partial class ImagePanel
{
    private enum ImageFitMode { Width, Whole, Manual }

    // Only the ImageViewer constructor opts in. PDF and ThumbnailViewer also use
    // this control and keep their existing zoom/scroll behavior.
    private LongImageOptions _longImageOptions;
    private bool _imageFitDecided;
    private bool _imageReadingActive;
    private bool _imageUserZoomed;
    private bool _imageFitQueued;
    private bool _imageFitResetToTop;
    private bool _applyingImageFit;
    private bool _imageReadingDisposed;
    private ImageFitMode _imageFitMode;
    private Size _lastImageSourceSize = Size.Empty;
    private Point? _imageSourceAnchor;
    private double? _imageFitCenterY;
    private double _imageSourceRenderedWidth;

    private void InitializeImageReading(LongImageOptions options)
    {
        _longImageOptions = options;
        Loaded += OnImageReadingLoaded;
        viewPanel.ScrollChanged += OnImageViewportChanged;
        viewPanelImage.ImageSourceChanged += OnImageSourceChanged;
    }

    private void DisposeImageReading()
    {
        _imageReadingDisposed = true;
        Loaded -= OnImageReadingLoaded;
        viewPanel.ScrollChanged -= OnImageViewportChanged;
        if (viewPanelImage != null)
            viewPanelImage.ImageSourceChanged -= OnImageSourceChanged;
    }

    private void OnImageReadingLoaded(object sender, RoutedEventArgs e) => QueueImageFit();

    private void OnImageViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            if (!_imageFitDecided || _imageReadingActive)
                QueueImageFit();
        }
    }

    private void OnImageSourceChanged(object sender, EventArgs e)
    {
        var size = GetImageSourceSize();
        if (!LongImageOptions.IsValidSize(size) || size == _lastImageSourceSize)
            return;

        if (_imageReadingActive && LongImageOptions.IsValidSize(_lastImageSourceSize))
        {
            // Capture before the new source's measure can clamp the old offsets.
            // Some decoders replace a thumbnail with a different logical size.
            _imageSourceRenderedWidth = _lastImageSourceSize.Width * ZoomFactor;
            _imageSourceAnchor = new Point(
                viewPanel.HorizontalOffset / _imageSourceRenderedWidth,
                viewPanel.VerticalOffset / (_lastImageSourceSize.Height * ZoomFactor));
        }

        if (!_imageFitDecided || _imageReadingActive)
            QueueImageFit();
    }

    private bool HandleImageReadingSizeChanged()
    {
        if (_longImageOptions == null || _imageFitDecided && !_imageReadingActive)
            return false;

        QueueImageFit();
        return true;
    }

    private void OnImageFitRequested()
    {
        if (_longImageOptions == null)
            DoZoomToFit();
        else if (!_imageFitDecided || _imageReadingActive)
            QueueImageFit();
        else if (!_imageUserZoomed)
            DoZoomToFit();
    }

    private void QueueImageFit(bool resetToTop = false)
    {
        if (_imageReadingDisposed || _applyingImageFit || _longImageOptions == null)
            return;

        _imageFitResetToTop |= resetToTop;
        if (_imageFitQueued)
            return;

        _imageFitQueued = true;
        // The host restores placement/maximization before this first decision.
        // Coalesce Loaded, decoder and viewport events after layout has settled.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _imageFitQueued = false;
            if (!_imageReadingDisposed)
                RefreshImageFit();
        }), DispatcherPriority.ContextIdle);
    }

    private Size GetImageSourceSize() => viewPanelImage?.Source is ImageSource source
        ? new Size(source.Width, source.Height) : Size.Empty;

    private Size GetImageViewportSize() => new(viewPanel.ViewportWidth, viewPanel.ViewportHeight);

    private void RefreshImageFit()
    {
        var source = GetImageSourceSize();
        var viewport = GetImageViewportSize();
        if (!IsLoaded || !LongImageOptions.IsValidSize(source) || !LongImageOptions.IsValidSize(viewport))
            return;

        if (!_imageFitDecided)
        {
            _imageFitDecided = true;
            if (_longImageOptions.ShouldFitWidth(source, viewport))
            {
                _imageReadingActive = true;
                _imageFitMode = ImageFitMode.Width;
                _imageFitResetToTop = true;
                ZoomWithControlKey = true;
            }
        }

        if (!_imageReadingActive)
        {
            _lastImageSourceSize = source;
            if (!_imageUserZoomed)
                DoZoomToFit();
            return;
        }

        var anchor = _imageSourceAnchor ?? GetImageScrollAnchor(source);
        var factor = ZoomFactor;
        if (_imageFitMode == ImageFitMode.Manual && _imageSourceAnchor.HasValue)
            factor = _imageSourceRenderedWidth / source.Width;

        ApplyImageFit(factor, _imageFitResetToTop, anchor, false, _imageFitCenterY);
        _imageFitResetToTop = false;
        _imageSourceAnchor = null;
        _imageFitCenterY = null;
        _lastImageSourceSize = source;
    }

    internal void FitImageWidth() => SelectImageFit(ImageFitMode.Width);

    internal void FitWholeImage() => SelectImageFit(ImageFitMode.Whole);

    private bool ToggleImageReadingFit(Point imagePoint)
    {
        var source = GetImageSourceSize();
        if (!_imageReadingActive || _imageReadingDisposed || !LongImageOptions.IsValidSize(source))
            return false;

        if (_imageFitMode == ImageFitMode.Whole)
        {
            // Image coordinates already exclude the overview's centered margins.
            // Keep a relative position so a late full-resolution decode can still
            // put the selected content in the middle of the reading viewport.
            var centerY = Math.Max(0, Math.Min(1, imagePoint.Y / source.Height));
            SelectImageFit(ImageFitMode.Width, centerY);
        }
        else
        {
            FitWholeImage();
        }

        return true;
    }

    private void SelectImageFit(ImageFitMode mode, double? centerY = null)
    {
        if (_longImageOptions == null || _imageReadingDisposed)
            return;

        _imageFitDecided = true;
        _imageReadingActive = true;
        _imageUserZoomed = false;
        _imageFitMode = mode;
        _imageFitCenterY = centerY;
        _imageSourceAnchor = null;
        if (mode == ImageFitMode.Width)
            ZoomWithControlKey = true;
        QueueImageFit(true);
    }

    private void ZoomReadingImage(double factor, bool suppressEvent)
    {
        _imageFitMode = ImageFitMode.Manual;
        _imageFitResetToTop = false;
        _imageSourceAnchor = null;
        _imageFitCenterY = null;
        ApplyImageFit(factor, false, default, true);
        _lastImageSourceSize = GetImageSourceSize();
        if (!suppressEvent)
            FireZoomChangedEvent();
    }

    private Point GetImageScrollAnchor(Size source) => new(
        viewPanel.HorizontalOffset / (source.Width * ZoomFactor),
        viewPanel.VerticalOffset / (source.Height * ZoomFactor));

    private void ApplyImageFit(double factor, bool resetToTop, Point anchor, bool useMouseAnchor,
        double? centerY = null)
    {
        var source = GetImageSourceSize();
        if (!LongImageOptions.IsValidSize(source) || _applyingImageFit)
            return;

        var mouseImagePoint = Mouse.GetPosition(viewPanelImage);
        var mouseViewportPoint = Mouse.GetPosition(viewPanel);
        _applyingImageFit = true;
        try
        {
            // Use our explicit mode, rather than the shared control's fit flag.
            ZoomToFit = false;
            viewPanel.HorizontalScrollBarVisibility = _imageFitMode == ImageFitMode.Width
                ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            viewPanelImage.HorizontalAlignment = HorizontalAlignment.Center;
            viewPanelImage.VerticalAlignment = _imageFitMode == ImageFitMode.Whole
                ? VerticalAlignment.Center : VerticalAlignment.Top;

            // A vertical scrollbar can reduce the viewport after fitting. A small
            // bounded pass settles its width without another mode decision.
            for (var pass = 0; pass < 3; pass++)
            {
                var viewport = GetImageViewportSize();
                if (!LongImageOptions.IsValidSize(viewport))
                    return;

                var wholeFactor = Math.Min(viewPanel.ActualWidth / source.Width,
                    viewPanel.ActualHeight / source.Height);
                var widthFactor = viewport.Width / source.Width;
                if (_imageFitMode == ImageFitMode.Width)
                    factor = widthFactor;
                else if (_imageFitMode == ImageFitMode.Whole)
                    factor = wholeFactor;

                // Very large images must still be able to fit below the shared
                // control's 10% minimum; low-resolution images keep its zoom cap.
                var minimum = Math.Min(MinZoomFactor, Math.Min(wholeFactor, widthFactor));
                factor = Math.Min(MaxZoomFactor, Math.Max(minimum, factor));
                if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0)
                    return;

                ZoomToFitFactor = _imageFitMode == ImageFitMode.Whole ? wholeFactor : widthFactor;
                ZoomFactor = factor;
                viewPanelImage.LayoutTransform = new ScaleTransform(factor, factor);
                viewPanel.ScrollToHorizontalOffset(0);
                viewPanel.ScrollToVerticalOffset(0);
                UpdateLayout();

                if (centerY.HasValue)
                {
                    // Use the settled viewport, including any scrollbar. Near
                    // either end, stop at the image edge instead of adding space.
                    var offset = centerY.Value * source.Height * factor - viewPanel.ViewportHeight / 2;
                    viewPanel.ScrollToVerticalOffset(Math.Max(0, Math.Min(viewPanel.ScrollableHeight, offset)));
                    UpdateLayout();
                }
                else if (!resetToTop)
                {
                    var offset = useMouseAnchor
                        ? viewPanelImage.TranslatePoint(mouseImagePoint, viewPanel) - mouseViewportPoint
                        : new Vector(anchor.X * source.Width * factor, anchor.Y * source.Height * factor);
                    viewPanel.ScrollToHorizontalOffset(offset.X);
                    viewPanel.ScrollToVerticalOffset(offset.Y);
                    UpdateLayout();
                }

                if (_imageFitMode != ImageFitMode.Width || Math.Abs(viewPanel.ViewportWidth - viewport.Width) < 0.1d)
                    break;
            }
        }
        finally
        {
            _applyingImageFit = false;
        }
    }
}
