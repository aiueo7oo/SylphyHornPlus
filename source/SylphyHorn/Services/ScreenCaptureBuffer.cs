using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SylphyHorn.Services
{
	/// <summary>
	/// A GDI buffer that captures a screen, and copies the captured pixels into a WPF bitmap.
	/// </summary>
	/// <remarks>
	/// <see cref="Capture"/> can run on any thread, so capturing a 4K screen in the background does not block the UI thread.
	/// <see cref="CopyTo"/> must be called on the UI thread.
	/// A <see cref="WriteableBitmap"/> is used for display because WPF does not pick up changes of an InteropBitmap
	/// over a memory section reliably: once rendered, it kept showing the first captured image.
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

		public WriteableBitmap CreateBitmap()
		{
			return new WriteableBitmap(this._bounds.Width, this._bounds.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
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
		/// Copies the captured pixels into the bitmap. Call on the UI thread.
		/// </summary>
		public void CopyTo(WriteableBitmap target)
		{
			lock (this._sync)
			{
				if (this._bits == IntPtr.Zero) return;

				var stride = this._bounds.Width * 4;
				target.WritePixels(new Int32Rect(0, 0, this._bounds.Width, this._bounds.Height), this._bits, stride * this._bounds.Height, stride);
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
