using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SylphyHorn.Services
{
	/// <summary>
	/// A snapshot of a screen whose pixels are shared between a GDI DIB section and a WPF bitmap.
	/// </summary>
	/// <remarks>
	/// <see cref="Capture"/> can run on any thread and copies the screen directly into the memory shown by WPF,
	/// so capturing a 4K screen in the background does not block the UI thread and needs no extra copy.
	/// Create the instance and call <see cref="Invalidate"/> on the UI thread.
	/// </remarks>
	internal sealed class ScreenSnapshot : IDisposable
	{
		private readonly System.Drawing.Rectangle _bounds;
		private readonly object _sync = new object();
		private IntPtr _section;
		private IntPtr _bitmap;
		private IntPtr _memoryDc;
		private IntPtr _oldBitmap;

		public InteropBitmap Source { get; }

		public ScreenSnapshot(System.Drawing.Rectangle bounds)
		{
			this._bounds = bounds;

			var stride = bounds.Width * 4;
			var size = (uint)(stride * bounds.Height);
			this._section = CreateFileMapping(INVALID_HANDLE_VALUE, IntPtr.Zero, PAGE_READWRITE, 0, size, null);
			if (this._section == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

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
				this._bitmap = CreateDIBSection(screenDc, ref info, DIB_RGB_COLORS, out _, this._section, 0);
				this._memoryDc = CreateCompatibleDC(screenDc);
			}
			finally
			{
				ReleaseDC(IntPtr.Zero, screenDc);
			}

			if (this._bitmap == IntPtr.Zero || this._memoryDc == IntPtr.Zero)
			{
				this.Dispose();
				throw new InvalidOperationException("Failed to create a screen snapshot.");
			}

			this._oldBitmap = SelectObject(this._memoryDc, this._bitmap);
			this.Source = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(this._section, bounds.Width, bounds.Height, PixelFormats.Bgr32, stride, 0);
		}

		/// <summary>
		/// Copies the current content of the screen into the snapshot. Thread-safe.
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
		/// Lets WPF pick up the captured pixels. Call on the UI thread after <see cref="Capture"/>.
		/// </summary>
		public void Invalidate()
		{
			this.Source.Invalidate();
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
				}

				if (this._section != IntPtr.Zero)
				{
					CloseHandle(this._section);
					this._section = IntPtr.Zero;
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
		private const uint PAGE_READWRITE = 0x04;
		private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr hObject);

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
