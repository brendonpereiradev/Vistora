using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vistora.Desktop;

internal static class WindowPlacement
{
    internal static void FitStartup(Window window)
    {
        if (window.WindowStartupLocation == WindowStartupLocation.Manual || window.WindowState != WindowState.Normal ||
            window.SizeToContent != SizeToContent.Manual || window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)) return;

        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (source?.CompositionTarget is not { } target || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
        {
            FitToWorkArea(window, SystemParameters.WorkArea);
            return;
        }

        // Monitor bounds are pixels; WPF window dimensions use logical units at this window's DPI.
        var fromDevice = target.TransformFromDevice;
        var work = monitor.Work;
        FitToWorkArea(window, new Rect(fromDevice.Transform(new Point(work.Left, work.Top)),
            fromDevice.Transform(new Point(work.Right, work.Bottom))));
    }

    internal static void FitToWorkArea(Window window, Rect work)
    {
        const double margin = 16;
        var width = Math.Min(window.Width, Math.Max(1, work.Width - margin * 2));
        var height = Math.Min(window.Height, Math.Max(1, work.Height - margin * 2));
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (work.Height - height) / 2;
        if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner && window.Owner is not null &&
            double.IsFinite(window.Left) && double.IsFinite(window.Top))
        {
            left = window.Left + (window.Width - width) / 2;
            top = window.Top + (window.Height - height) / 2;
        }

        // Lower oversized minimums so they cannot push the title bar beyond a small or scaled display.
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = width; window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Clamp(left, work.Left, work.Right - width);
        window.Top = Math.Clamp(top, work.Top, work.Bottom - height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
