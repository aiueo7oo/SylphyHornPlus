using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SylphyHorn.Services
{
	/// <summary>
	/// A GDI buffer that captures a screen, and creates WPF bitmaps from the captured pixels.
	/// </summary>
	/// <remarks>
	/// <see cref="Capture"/> can run on any thread, so capturing a 4K screen in the background does not block the UI thread.
	/// Each capture becomes a new frozen bitmap (<see cref="CreateSnapshot"/>):
	/// WPF did not pick up changes of an InteropBitmap over a memory section once it had been rendered,
	/// and a WriteableBitmap keeps two copies of the pixels (back and front buffers).
	/// </remarks>
	internal sealed class ScreenCaptureBuffer : IDisposable
	{
		private readonly System.Drawing.Rectangle _bounds;
		private readonly object _sync = new object();
		private IntPtr _bitmap;
		private IntPtr _bits;
		private IntPtr _memoryDc;
		private IntPtr _oldBitmap;

		public ScreenCaptureBuffer(System.Drawing.Rectangle bounds)
		{
			this._bounds = bounds;

			var screenDc = GetDC(IntPtr.Zero);
			try
			{
				var info = new BITMAPINFOHEADER
				{
					biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
					biWidth = bounds.Width,
					biHeight = -bounds.Height, // top-down, the same layout as the WPF bitmap
					biPlanes = 1,
					biBitCount = 32,
				};
				this._bitmap = CreateDIBSection(screenDc, ref info, DIB_RGB_COLORS, out this._bits, IntPtr.Zero, 0);
				this._memoryDc = CreateCompatibleDC(screenDc);
			}
			finally
			{
				ReleaseDC(IntPtr.Zero, screenDc);
			}

			if (this._bitmap == IntPtr.Zero || this._memoryDc == IntPtr.Zero)
			{
				this.Dispose();
				throw new InvalidOperationException("Failed to create a screen capture buffer.");
			}

			this._oldBitmap = SelectObject(this._memoryDc, this._bitmap);
		}

		/// <summary>
		/// Copies the current content of the screen into the buffer. Thread-safe.
		/// </summary>
		public void Capture()
		{
			lock (this._sync)
			{
				if (this._memoryDc == IntPtr.Zero) return;

				var screenDc = GetDC(IntPtr.Zero);
				try
				{
					BitBlt(this._memoryDc, 0, 0, this._bounds.Width, this._bounds.Height, screenDc, this._bounds.Left, this._bounds.Top, SRCCOPY);
					GdiFlush();
				}
				finally
				{
					ReleaseDC(IntPtr.Zero, screenDc);
				}
			}
		}

		/// <summary>
		/// Creates a frozen bitmap from the captured pixels.
		/// </summary>
		public BitmapSource CreateSnapshot()
		{
			lock (this._sync)
			{
				if (this._bits == IntPtr.Zero) return null;

				var stride = this._bounds.Width * 4;
				var snapshot = BitmapSource.Create(
					this._bounds.Width, this._bounds.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
					this._bits, stride * this._bounds.Height, stride);
				snapshot.Freeze();
				return snapshot;
			}
		}

		public void Dispose()
		{
			lock (this._sync)
			{
				if (this._memoryDc != IntPtr.Zero)
				{
					SelectObject(this._memoryDc, this._oldBitmap);
					DeleteDC(this._memoryDc);
					this._memoryDc = IntPtr.Zero;
				}

				if (this._bitmap != IntPtr.Zero)
				{
					DeleteObject(this._bitmap);
					this._bitmap = IntPtr.Zero;
					this._bits = IntPtr.Zero;
				}
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct BITMAPINFOHEADER
		{
			public int biSize;
			public int biWidth;
			public int biHeight;
			public short biPlanes;
			public short biBitCount;
			public int biCompression;
			public int biSizeImage;
			public int biXPelsPerMeter;
			public int biYPelsPerMeter;
			public int biClrUsed;
			public int biClrImportant;
		}

		private const int SRCCOPY = 0x00CC0020;
		private const uint DIB_RGB_COLORS = 0;

		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DeleteDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

		[DllImport("gdi32.dll")]
		private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

		[DllImport("gdi32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DeleteObject(IntPtr hObject);

		[DllImport("gdi32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

		[DllImport("gdi32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GdiFlush();
	}
}
