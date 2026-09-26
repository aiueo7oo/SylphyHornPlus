using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MetroRadiance.Interop.Win32;
using SylphyHorn.Interop;
using SylphyHorn.UI.Bindings;
using WindowsDesktop;
using MONITORINFOEX = SylphyHorn.Interop.MONITORINFOEX;

namespace SylphyHorn.UI
{
	partial class SettingsWindow
	{
		public static SettingsWindow Instance { get; set; }

		public SettingsWindow()
		{
			this.InitializeComponent();
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			this.FitToWorkArea();
		}

		// The default size can exceed a small or highly scaled screen. Shrink the window into the work area of
		// its monitor instead of enforcing a minimum; the tab list and each page scroll vertically.
		private void FitToWorkArea()
		{
			// The root visual may not be connected yet during SourceInitialized; resolve the source from the handle.
			var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
			if (source?.CompositionTarget == null) return;
			var monitor = NativeMethods.MonitorFromWindow(source.Handle, MonitorDefaultTo.MONITOR_DEFAULTTONEAREST);
			var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
			if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(source.Handle, out var bounds)) return;
			var fromDevice = source.CompositionTarget.TransformFromDevice;
			var work = new Rect(
				fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top)),
				fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
			var window = new Rect(
				fromDevice.Transform(new Point(bounds.Left, bounds.Top)),
				fromDevice.Transform(new Point(bounds.Right, bounds.Bottom)));
			if (work.IsEmpty || (window.Width <= work.Width && window.Height <= work.Height && work.Contains(window))) return;
			var width = Math.Min(window.Width, work.Width);
			var height = Math.Min(window.Height, work.Height);
			this.Width = width;
			this.Height = height;
			this.Left = Math.Max(work.Left, Math.Min(window.Left, work.Right - width));
			this.Top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - height));
		}

		protected override void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);
			(this.DataContext as SettingsWindowViewModel)?.Initialize();
			this.Pin();
		}

		protected override void OnClosed(EventArgs e)
		{
			base.OnClosed(e);
			(this.DataContext as IDisposable)?.Dispose();
		}
	}
}
