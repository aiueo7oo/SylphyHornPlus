using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroRadiance.Interop.Win32;

namespace SylphyHorn.UI
{
	/// <summary>
	/// An overlay that shows a snapshot of the next desktop and slides in from an edge of the screen.
	/// </summary>
	/// <remarks>
	/// The slide moves the window itself instead of re-rendering the content, so the cost of each frame
	/// does not depend on the screen resolution.
	/// Creating and first showing a full-screen window takes a few hundred milliseconds,
	/// so instances are created in advance and reused by hiding and showing them.
	/// </remarks>
	internal sealed class SwitchAnimationWindow : Window
	{
		private readonly Image _image;
		private IntPtr _handle;
		private bool _cloaked;

		/// <summary>
		/// The bounds of the screen in physical pixels.
		/// </summary>
		public System.Drawing.Rectangle ScreenBounds { get; }

		public SwitchAnimationWindow(System.Drawing.Rectangle bounds)
		{
			this.ScreenBounds = bounds;
			this._image = new Image { Stretch = Stretch.Fill, };
			RenderOptions.SetBitmapScalingMode(this._image, BitmapScalingMode.NearestNeighbor);

			this.WindowStyle = WindowStyle.None;
			this.ResizeMode = ResizeMode.NoResize;
			this.ShowInTaskbar = false;
			this.ShowActivated = false;
			this.Topmost = true;
			this.Focusable = false;
			this.IsHitTestVisible = false;
			this.Background = Brushes.Black;
			this.Content = this._image;

			// Initial placement in DIPs; the window is placed in physical pixels by SetWindowPos afterward.
			var scale = GetSystemDpiScale();
			this.Left = bounds.Left / scale;
			this.Top = bounds.Top / scale;
			this.Width = bounds.Width / scale;
			this.Height = bounds.Height / scale;
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			this._handle = new WindowInteropHelper(this).Handle;

			// Tool windows are not managed by virtual desktops, so the overlay stays visible across the switch.
			var style = User32.GetWindowLongEx(this._handle);
			style |= WindowExStyles.WS_EX_TOOLWINDOW | WindowExStyles.WS_EX_NOACTIVATE | WindowExStyles.WS_EX_TRANSPARENT;
			User32.SetWindowLongEx(this._handle, style);

			// Keep the overlay out of screen captures so that the screen beneath it can be captured during the animation.
			SetWindowDisplayAffinity(this._handle, WDA_EXCLUDEFROMCAPTURE);

			this.Cloak(true);
			this.MoveTo(this.ScreenBounds.Left, this.ScreenBounds.Top);
		}

		/// <summary>
		/// Renders the window once without showing it, so that the first animation starts without delay.
		/// </summary>
		public void WarmUp(Action waitForPresent)
		{
			new WindowInteropHelper(this).EnsureHandle();
			this.ShowCloaked();
			waitForPresent();
			this.Hide();
		}

		/// <summary>
		/// Shows the snapshot cloaked, so that it is rendered before it appears.
		/// </summary>
		/// <remarks>
		/// WPF does not render a window outside of all screens, so the window is rendered on the screen while it is cloaked.
		/// </remarks>
		public void ShowSnapshot(BitmapSource snapshot)
		{
			this._image.Source = snapshot;
			this.ShowCloaked();
		}

		private void ShowCloaked()
		{
			this.Cloak(true);
			this.MoveTo(this.ScreenBounds.Left, this.ScreenBounds.Top);
			this.Show();
		}

		/// <summary>
		/// Places the window for the given progress of a slide in which the outgoing and incoming desktops move together.
		/// </summary>
		/// <param name="progress">0.0 (the outgoing desktop covers the screen) to 1.0 (the incoming desktop covers the screen).</param>
		/// <param name="direction">
		/// 1 when moving to the right desktop (the desktops move to the left), -1 when moving to the left desktop (they move to the right).
		/// </param>
		/// <param name="incoming">true for the incoming desktop, which follows the outgoing one.</param>
		/// <remarks>
		/// A window region cannot be used to clip the part outside of the screen (WPF stops rendering a window with a region),
		/// so do not slide on a screen with an adjacent screen.
		/// </remarks>
		public void Slide(double progress, int direction, bool incoming)
		{
			var width = this.ScreenBounds.Width;
			var offset = (int)Math.Round(-direction * width * Math.Max(0.0, Math.Min(1.0, progress)));
			if (incoming)
			{
				offset += direction * width;
			}

			this.MoveTo(this.ScreenBounds.Left + offset, this.ScreenBounds.Top);
			this.Cloak(false);
		}

		private void Cloak(bool cloak)
		{
			if (this._handle == IntPtr.Zero || this._cloaked == cloak) return;

			var value = cloak ? 1 : 0;
			DwmSetWindowAttribute(this._handle, DWMWA_CLOAK, ref value, sizeof(int));
			this._cloaked = cloak;
		}

		private void MoveTo(int x, int y)
		{
			if (this._handle == IntPtr.Zero) return;

			SetWindowPos(this._handle, HWND_TOPMOST, x, y, this.ScreenBounds.Width, this.ScreenBounds.Height, SWP_NOACTIVATE);
		}

		/// <summary>
		/// Hides the window and releases the snapshot.
		/// </summary>
		public void Release()
		{
			this.Hide();
			this._image.Source = null;
		}

		private static double GetSystemDpiScale()
		{
			using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
			{
				return g.DpiX / 96.0;
			}
		}

		private const int DWMWA_CLOAK = 13;
		private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
		private const uint SWP_NOACTIVATE = 0x0010;
		private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	}
}
