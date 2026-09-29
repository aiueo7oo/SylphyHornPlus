using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>Win32 declarations shared by the placement monitor, readers and worker.</summary>
	internal static class PlacementNativeMethods
	{
		internal const uint EVENT_OBJECT_CREATE = 0x8000;
		internal const uint EVENT_OBJECT_DESTROY = 0x8001;
		internal const uint EVENT_OBJECT_SHOW = 0x8002;
		internal const uint EVENT_OBJECT_HIDE = 0x8003;

		internal const uint GA_ROOT = 2;

		internal const int ERROR_SUCCESS = 0;
		internal const int ERROR_INSUFFICIENT_BUFFER = 122;

		internal const int TYPE_E_ELEMENTNOTFOUND = unchecked((int)0x8002802B);
		// HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
		internal const int HRESULT_ERROR_NOT_FOUND = unchecked((int)0x80070490);

		private const int WindowTitleCapacity = 1024;

		// A newly created or helper HWND can lack a Shell application view; view lookups then fail with either code.
		internal static bool IsMissingView(COMException exception)
			=> exception.HResult == TYPE_E_ELEMENTNOTFOUND || exception.HResult == HRESULT_ERROR_NOT_FOUND;

		internal static string GetWindowTitle(IntPtr window)
		{
			var title = new StringBuilder(WindowTitleCapacity);
			GetWindowText(window, title, title.Capacity);
			return title.ToString();
		}

		internal static void ReleaseComObject(object value)
		{
			if (value != null && Marshal.IsComObject(value))
			{
				Marshal.ReleaseComObject(value);
			}
		}

		internal delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetClassName(IntPtr window, StringBuilder name, int size);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

		[DllImport("kernel32.dll")]
		internal static extern ulong GetTickCount64();

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
	}
}
