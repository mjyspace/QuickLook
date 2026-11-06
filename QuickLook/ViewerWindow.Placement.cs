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

using Newtonsoft.Json;
using QuickLook.Common.Helpers;
using QuickLook.Common.NativeMethods;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace QuickLook;

public partial class ViewerWindow
{
    private bool _rememberWindowPlacement;
    private bool _placementInitialized;
    private bool _placementReady;
    private bool _hasUserWindowPlacement;
    private bool _placementDirty;
    private bool _userMovingWindow;
    private bool _placementSaveQueued;
    private bool _restoreMaximized;
    private bool _restoreFullscreen;
    private bool _temporaryNoActivate;
    private int _placementChangeDepth;
    private RememberedWindowPlacement _fullscreenRestorePlacement;

    private void InitializeWindowPlacement()
    {
        _rememberWindowPlacement = SettingHelper.Get("RememberWindowPlacement", true);
        LocationChanged += (_, _) => RememberInteractiveBoundsChange();
        SizeChanged += (_, _) => RememberInteractiveBoundsChange();
        StateChanged += (_, _) => QueueUserWindowPlacementSave();
    }

    private void AttachWindowPlacementHook()
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowPlacementHook);
    }

    private nint WindowPlacementHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_WINDOWPOSCHANGING = 0x0046;

        if (message == WM_WINDOWPOSCHANGING && _temporaryNoActivate)
        {
            var position = (NativeWindowPosition)Marshal.PtrToStructure(lParam, typeof(NativeWindowPosition));
            position.Flags |= SWP_NOACTIVATE;
            Marshal.StructureToPtr(position, lParam, false);
        }
        else if (message == WM_ENTERSIZEMOVE)
            _userMovingWindow = true;
        else if (message == WM_EXITSIZEMOVE)
        {
            _userMovingWindow = false;
            QueueUserWindowPlacementSave();
        }

        return IntPtr.Zero;
    }

    private void RememberInteractiveBoundsChange()
    {
        // The move loop covers dragging/resizing. Win+arrows also move/snap a
        // window without entering that loop, including between monitors.
        if (_userMovingWindow || (IsActive &&
            (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))))
            QueueUserWindowPlacementSave();
    }

    private void QueueUserWindowPlacementSave()
    {
        if (!_rememberWindowPlacement || !_placementReady || !IsVisible || Pinned ||
            _placementChangeDepth != 0 || WindowState == WindowState.Minimized)
            return;

        _placementDirty = true;
        _hasUserWindowPlacement = true;
        if (_placementSaveQueued)
            return;

        _placementSaveQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _placementSaveQueued = false;
            // Wait for the final drag geometry, not every intermediate move.
            if (!_userMovingWindow)
                SavePendingWindowPlacement();
        }), DispatcherPriority.ContextIdle);
    }

    private void SavePendingWindowPlacement()
    {
        if (!_placementDirty || !_rememberWindowPlacement || Pinned || _placementChangeDepth != 0)
            return;

        var placement = CaptureWindowPlacement();
        if (placement == null)
            return;

        try
        {
            SettingHelper.Set("WindowPlacement", JsonConvert.SerializeObject(placement));
            _placementDirty = false;
        }
        catch (Exception exception)
        {
            // A read-only configuration must not prevent closing the preview.
            ProcessHelper.WriteLog(exception.ToString());
        }
    }

    private RememberedWindowPlacement CaptureWindowPlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState == WindowState.Minimized)
            return null;

        var screen = WinForms.Screen.FromHandle(handle);
        if (_isFullscreen && _fullscreenRestorePlacement != null)
        {
            return new RememberedWindowPlacement
            {
                Monitor = screen.DeviceName,
                Left = _fullscreenRestorePlacement.Left,
                Top = _fullscreenRestorePlacement.Top,
                Width = _fullscreenRestorePlacement.Width,
                Height = _fullscreenRestorePlacement.Height,
                Maximized = _preFullscreenWindowState == WindowState.Maximized,
                Fullscreen = true,
            };
        }

        var native = new NativeWindowPlacement { Length = Marshal.SizeOf(typeof(NativeWindowPlacement)) };
        if (!GetWindowPlacement(handle, ref native))
            return null;

        var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);
        var bounds = native.NormalPosition;
        var origin = screen.Bounds.Location;
        // For a snapped normal window rcNormalPosition can describe the old
        // unsnapped bounds. Preserve the actual displayed rectangle instead.
        if (WindowState == WindowState.Normal)
        {
            if (!User32.GetWindowRect(handle, out bounds))
                return null;
            origin = screen.WorkingArea.Location;
        }
        else if ((User32.GetWindowLong(handle, User32.GWL_EXSTYLE) & 0x00000080) != 0) // WS_EX_TOOLWINDOW
        {
            origin = screen.WorkingArea.Location;
        }
        // WINDOWPLACEMENT uses workspace coordinates unless WS_EX_TOOLWINDOW
        // is set. Both paths become work-area-relative DIP offsets here.
        var placement = new RememberedWindowPlacement
        {
            Monitor = screen.DeviceName,
            Left = (bounds.Left - origin.X) / scale.Horizontal,
            Top = (bounds.Top - origin.Y) / scale.Vertical,
            Width = (bounds.Right - bounds.Left) / scale.Horizontal,
            Height = (bounds.Bottom - bounds.Top) / scale.Vertical,
            Maximized = WindowState == WindowState.Maximized,
        };
        return placement.IsValid() ? placement : null;
    }

    private void RestoreWindowPlacementBeforePreview()
    {
        if (_placementInitialized)
            return;
        _placementInitialized = true;
        if (!_rememberWindowPlacement)
            return;

        RememberedWindowPlacement placement;
        try
        {
            placement = JsonConvert.DeserializeObject<RememberedWindowPlacement>(
                SettingHelper.Get("WindowPlacement", string.Empty));
        }
        catch (JsonException)
        {
            return;
        }

        if (placement == null || !placement.IsValid())
            return;

        var screen = WinForms.Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, placement.Monitor, StringComparison.OrdinalIgnoreCase))
            ?? WinForms.Screen.FromHandle(User32.GetForegroundWindow());
        var workArea = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();

        _placementChangeDepth++;
        try
        {
            // Move the hidden HWND first: queries using ContextObject.Source
            // now see the target monitor rather than the Explorer's monitor.
            SetWindowPos(handle, IntPtr.Zero, workArea.Left, workArea.Top, 0, 0,
                SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);
            var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);
            var width = Math.Min(workArea.Width, Math.Max(MinWidth * scale.Horizontal,
                placement.Width * scale.Horizontal));
            var height = Math.Min(workArea.Height, Math.Max(MinHeight * scale.Vertical,
                placement.Height * scale.Vertical));
            var left = workArea.Left + Math.Max(0, Math.Min(workArea.Width - width,
                placement.Left * scale.Horizontal));
            var top = workArea.Top + Math.Max(0, Math.Min(workArea.Height - height,
                placement.Top * scale.Vertical));

            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top),
                (int)Math.Round(width), (int)Math.Round(height), SWP_NOACTIVATE | SWP_NOZORDER);
            _customWindowSize = new Size(width / scale.Horizontal, height / scale.Vertical);
            _hasUserWindowPlacement = true;
            _restoreMaximized = placement.Maximized;
            _restoreFullscreen = placement.Fullscreen;
        }
        finally
        {
            _placementChangeDepth--;
        }
    }

    private void ShowWindowWithRestoredPlacement()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _placementChangeDepth++;
        try
        {
            // Maximizing a native window can activate it even when WPF's
            // ShowActivated is false. Keep activation disabled through the
            // initial state transition, then restore ordinary click-to-focus.
            if (!SettingHelper.Get("FocusWindowOnOpen", false))
            {
                var style = User32.GetWindowLong(handle, User32.GWL_EXSTYLE);
                if ((style & User32.WS_EX_NOACTIVATE) == 0)
                {
                    User32.SetWindowLong(handle, User32.GWL_EXSTYLE, style | User32.WS_EX_NOACTIVATE);
                    _temporaryNoActivate = true;
                }
            }

            if (!SettingHelper.Get("ShowWindowTransition", true, "QuickLook"))
                this.ShowWithoutTransition();
            else
                Show();

            if (_restoreMaximized)
                WindowState = WindowState.Maximized;
            if (_restoreFullscreen)
                ChangeFullscreenState();
        }
        finally
        {
            _placementChangeDepth--;
        }
    }

    private void CompleteWindowPlacementRestore()
    {
        if (_temporaryNoActivate)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var style = User32.GetWindowLong(handle, User32.GWL_EXSTYLE);
                User32.SetWindowLong(handle, User32.GWL_EXSTYLE, style & ~User32.WS_EX_NOACTIVATE);
            }
            _temporaryNoActivate = false;
        }
        _restoreMaximized = false;
        _restoreFullscreen = false;
        _placementReady = IsVisible;
    }

    private sealed class RememberedWindowPlacement
    {
        public int Version { get; set; } = 1;
        public string Monitor { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public bool Fullscreen { get; set; }

        public bool IsValid()
        {
            return Version == 1 && !string.IsNullOrEmpty(Monitor) &&
                IsFinite(Left) && IsFinite(Top) && IsFinite(Width) && IsFinite(Height) &&
                Width > 0 && Height > 0;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public User32.RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public nint Window;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref NativeWindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);
}
