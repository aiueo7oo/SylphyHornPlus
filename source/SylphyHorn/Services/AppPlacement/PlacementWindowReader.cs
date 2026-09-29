using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static SylphyHorn.Services.AppPlacement.PlacementNativeMethods;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>Candidate-only inspection on the placement worker, never inside a WinEvent callback.</summary>
	internal sealed class PlacementWindowReader
	{
		private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";
		private const int ClassNameCapacity = 256;
		// A frame host with more child windows or child processes than this is not inspected.
		private const int HostChildWindowLimit = 128;
		private const int HostChildProcessLimit = 8;
		private const int ProcessPathCapacity = 32768;
		private const int ProcessStringLimit = 4096;

		private const int GWL_EXSTYLE = -20;
		private const uint WS_EX_TOOLWINDOW = 0x00000080;
		private const uint WS_EX_APPWINDOW = 0x00040000;
		private const uint WS_EX_NOACTIVATE = 0x08000000;
		private const uint GW_OWNER = 4;
		private const int DWMWA_CLOAKED = 14;
		private const int DWM_CLOAKED_SHELL = 0x2;
		private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		private const int ERROR_ACCESS_DENIED = 5;
		private const int ERROR_INVALID_HANDLE = 6;
		private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
		private const int APPMODEL_ERROR_NO_APPLICATION = 15703;
		private const ushort VT_EMPTY = 0;
		private const ushort VT_LPWSTR = 31;

		private static readonly PropertyKey PKEY_AppUserModel_ID = new PropertyKey
		{
			Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
			Id = 5
		};

		private readonly PlacementPackageCatalog _catalog = new PlacementPackageCatalog();

		internal PlacementWindowInspection Read(IntPtr window)
		{
			if (window == IntPtr.Zero || !IsWindow(window))
			{
				return Result(PlacementInspectionStatus.Excluded, "WindowClosed");
			}
			var thread = GetWindowThreadProcessId(window, out var pid);
			if (thread == 0 || pid == 0)
			{
				return Result(PlacementInspectionStatus.Unavailable, "WindowOwnerUnavailable");
			}
			if (GetAncestor(window, GA_ROOT) != window)
			{
				return Result(PlacementInspectionStatus.Excluded, "ChildWindow");
			}
			if (IsAuxiliaryWindow(window))
			{
				return Result(PlacementInspectionStatus.Excluded, "AuxiliaryWindow");
			}
			try
			{
				var name = new StringBuilder(ClassNameCapacity);
				if (GetClassName(window, name, name.Capacity) == 0)
				{
					return Result(PlacementInspectionStatus.Unavailable, "WindowClassUnavailable");
				}
				var className = name.ToString();
				var owner = ReadProcess(pid);
				var explicitId = ReadWindowAppId(window);
				var children = new List<PlacementProcessIdentity>();
				if (className == ApplicationFrameWindowClass)
				{
					var childIds = ReadHostChildProcessIds(window, pid);
					if (childIds == null)
					{
						return Result(PlacementInspectionStatus.Unavailable, "HostChildCapacity");
					}
					foreach (var child in childIds)
					{
						children.Add(ReadProcess(child));
					}
				}
				var resolved = PlacementAppIdentityResolver.Resolve(window, thread, className, owner, explicitId, children, this._catalog.Contains);
				if (GetWindowThreadProcessId(window, out var afterPid) != thread || afterPid != pid || !IsWindow(window))
				{
					return Result(PlacementInspectionStatus.Excluded, "WindowOwnerChanged");
				}
				if (resolved.Status != PlacementInspectionStatus.Ready) return resolved;
				if (!IsWindowVisible(window))
				{
					return Result(PlacementInspectionStatus.NotReady, "WindowNotVisible", resolved.Identity);
				}
				var status = DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int));
				if (status < 0)
				{
					return Result(PlacementInspectionStatus.Unavailable, "CloakStateUnavailable", resolved.Identity);
				}
				// DWM_CLOAKED_SHELL includes windows on another virtual desktop; do not reject those.
				if ((cloaked & ~DWM_CLOAKED_SHELL) != 0)
				{
					return Result(PlacementInspectionStatus.NotReady, "WindowPreparing", resolved.Identity);
				}
				return resolved;
			}
			catch (Win32Exception ex)
			{
				return Result(PlacementInspectionStatus.Unavailable, ex.NativeErrorCode == ERROR_ACCESS_DENIED ? "AccessDenied" : "ProcessInformationUnavailable");
			}
			catch (COMException)
			{
				return Result(PlacementInspectionStatus.Unavailable, "ShellInformationUnavailable");
			}
			catch (InvalidOperationException)
			{
				return Result(PlacementInspectionStatus.Unavailable, "AppCatalogUnavailable");
			}
		}

		private static PlacementWindowInspection Result(PlacementInspectionStatus status, string reason, PlacementWindowIdentity identity = null)
			=> new PlacementWindowInspection(status, reason, identity);

		// Tool and no-activate windows are auxiliary, as is an owned window that does not opt into the taskbar.
		private static bool IsAuxiliaryWindow(IntPtr window)
		{
			var exStyle = unchecked((uint)GetWindowLong(window, GWL_EXSTYLE));
			if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
			return GetWindow(window, GW_OWNER) != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0;
		}

		// Processes other than the frame host that own its child windows. Null when a limit is exceeded.
		private static HashSet<uint> ReadHostChildProcessIds(IntPtr frame, uint hostPid)
		{
			var childIds = new HashSet<uint>();
			var count = 0;
			var overflow = false;
			EnumChildWindows(
				frame,
				(child, state) =>
				{
					if (++count > HostChildWindowLimit)
					{
						overflow = true;
						return false;
					}
					GetWindowThreadProcessId(child, out var childPid);
					if (childPid != 0 && childPid != hostPid)
					{
						childIds.Add(childPid);
					}
					if (childIds.Count > HostChildProcessLimit)
					{
						overflow = true;
						return false;
					}
					return true;
				},
				IntPtr.Zero);
			return overflow ? null : childIds;
		}

		private static PlacementProcessIdentity ReadProcess(uint pid)
		{
			using (var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid))
			{
				if (process.IsInvalid)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				uint length = ProcessPathCapacity;
				var path = new StringBuilder((int)length);
				if (!QueryFullProcessImageName(process, 0, path, ref length))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				if (!GetProcessTimes(process, out var created, out var exit, out var kernel, out var user))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				// An exit time means the handle refers to a process that has already ended.
				if (exit != 0)
				{
					throw new Win32Exception(ERROR_INVALID_HANDLE);
				}
				var family = ReadProcessString(process, GetPackageFamilyName, APPMODEL_ERROR_NO_PACKAGE);
				var appId = ReadProcessString(process, GetApplicationUserModelId, APPMODEL_ERROR_NO_APPLICATION);
				return new PlacementProcessIdentity(pid, created, path.ToString(), family, appId);
			}
		}

		private delegate int ProcessString(SafeProcessHandle process, ref uint length, StringBuilder value);

		private static string ReadProcessString(SafeProcessHandle process, ProcessString read, int absentStatus)
		{
			uint length = 0;
			var status = read(process, ref length, null);
			if (status == absentStatus) return null;
			if (status != ERROR_INSUFFICIENT_BUFFER || length == 0 || length > ProcessStringLimit)
			{
				throw new Win32Exception(status);
			}
			var value = new StringBuilder((int)length);
			status = read(process, ref length, value);
			if (status != ERROR_SUCCESS)
			{
				throw new Win32Exception(status);
			}
			return value.ToString();
		}

		private static string ReadWindowAppId(IntPtr window)
		{
			var iid = typeof(IPropertyStore).GUID;
			IPropertyStore store = null;
			try
			{
				Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
				var key = PKEY_AppUserModel_ID;
				var value = new PropVariant();
				try
				{
					Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
					if (value.Type == VT_EMPTY) return null;
					if (value.Type != VT_LPWSTR)
					{
						throw new COMException("Unexpected AppUserModelId property type.");
					}
					return value.Pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(value.Pointer);
				}
				finally
				{
					PropVariantClear(ref value);
				}
			}
			finally
			{
				if (store != null)
				{
					Marshal.ReleaseComObject(store);
				}
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PropertyKey
		{
			internal Guid Format;
			internal uint Id;
		}
		// The union is 16 bytes on x64, 8 bytes on x86. A larger buffer is safe for both native writes.
		[StructLayout(LayoutKind.Explicit, Size = 24)]
		private struct PropVariant
		{
			[FieldOffset(0)]
			internal ushort Type;
			[FieldOffset(8)]
			internal IntPtr Pointer;
		}

		[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IPropertyStore
		{
			[PreserveSig]
			int GetCount(out uint count);

			[PreserveSig]
			int GetAt(uint index, out PropertyKey key);

			[PreserveSig]
			int GetValue(ref PropertyKey key, out PropVariant value);
		}

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr window);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr window, uint command);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
		private static extern int GetWindowLong(IntPtr window, int index);

		[DllImport("user32.dll")]
		private static extern bool EnumChildWindows(IntPtr window, EnumWindowsProc callback, IntPtr state);

		[DllImport("dwmapi.dll")]
		private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint size, StringBuilder value);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint size, StringBuilder value);

		[DllImport("shell32.dll")]
		private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);

		[DllImport("ole32.dll")]
		private static extern int PropVariantClear(ref PropVariant value);
	}
}
