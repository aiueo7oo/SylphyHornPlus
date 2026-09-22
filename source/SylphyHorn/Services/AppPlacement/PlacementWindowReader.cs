using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>Candidate-only inspection on the placement worker, never inside a WinEvent callback.</summary>
	internal sealed class PlacementWindowReader
	{
		private readonly PlacementPackageCatalog _catalog = new PlacementPackageCatalog();

		internal PlacementWindowInspection Read(IntPtr window)
		{
			if (window == IntPtr.Zero || !IsWindow(window)) return Result(PlacementInspectionStatus.Excluded, "WindowClosed");
			var thread = GetWindowThreadProcessId(window, out var pid);
			if (thread == 0 || pid == 0) return Result(PlacementInspectionStatus.Unavailable, "WindowOwnerUnavailable");
			if (GetAncestor(window, 2) != window) return Result(PlacementInspectionStatus.Excluded, "ChildWindow");
			var style = unchecked((uint)GetWindowLong(window, -20));
			if ((style & (0x80u | 0x08000000u)) != 0 || (GetWindow(window, 4) != IntPtr.Zero && (style & 0x40000u) == 0))
				return Result(PlacementInspectionStatus.Excluded, "AuxiliaryWindow");
			try
			{
				var name = new StringBuilder(256);
				if (GetClassName(window, name, name.Capacity) == 0) return Result(PlacementInspectionStatus.Unavailable, "WindowClassUnavailable");
				var owner = ReadProcess(pid);
				var explicitId = ReadWindowAppId(window);
				var children = new List<PlacementProcessIdentity>();
				if (name.ToString() == "ApplicationFrameWindow")
				{
					var childIds = new HashSet<uint>();
					var count = 0;
					var overflow = false;
					EnumChildWindows(
						window,
						(child, state) =>
						{
							if (++count > 128)
							{
								overflow = true;
								return false;
							}
							GetWindowThreadProcessId(child, out var childPid);
							if (childPid != 0 && childPid != pid) childIds.Add(childPid);
							if (childIds.Count > 8)
							{
								overflow = true;
								return false;
							}
							return true;
						},
						IntPtr.Zero);
					if (overflow) return Result(PlacementInspectionStatus.Unavailable, "HostChildCapacity");
					foreach (var child in childIds) children.Add(ReadProcess(child));
				}
				var resolved = PlacementAppIdentityResolver.Resolve(window, thread, name.ToString(), owner, explicitId, children, this._catalog.Contains);
				if (GetWindowThreadProcessId(window, out var afterPid) != thread || afterPid != pid || !IsWindow(window))
					return Result(PlacementInspectionStatus.Excluded, "WindowOwnerChanged");
				if (resolved.Status != PlacementInspectionStatus.Ready) return resolved;
				if (!IsWindowVisible(window)) return Result(PlacementInspectionStatus.NotReady, "WindowNotVisible", resolved.Identity);
				var status = DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int));
				if (status < 0) return Result(PlacementInspectionStatus.Unavailable, "CloakStateUnavailable", resolved.Identity);
				// DWM_CLOAKED_SHELL (2) includes windows on another virtual desktop; do not reject those.
				if ((cloaked & ~2) != 0) return Result(PlacementInspectionStatus.NotReady, "WindowPreparing", resolved.Identity);
				return resolved;
			}
			catch (Win32Exception ex)
			{
				return Result(PlacementInspectionStatus.Unavailable, ex.NativeErrorCode == 5 ? "AccessDenied" : "ProcessInformationUnavailable");
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

		private static PlacementProcessIdentity ReadProcess(uint pid)
		{
			using (var process = OpenProcess(0x1000, false, pid))
			{
				if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
				uint length = 32768;
				var path = new StringBuilder((int)length);
				if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
				if (!GetProcessTimes(process, out var created, out var exit, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
				if (exit != 0) throw new Win32Exception(6);
				var family = ReadProcessString(process, GetPackageFamilyName, 15700);
				var appId = ReadProcessString(process, GetApplicationUserModelId, 15703);
				return new PlacementProcessIdentity(pid, created, path.ToString(), family, appId);
			}
		}

		private delegate int ProcessString(SafeProcessHandle process, ref uint length, StringBuilder value);

		private static string ReadProcessString(SafeProcessHandle process, ProcessString read, int absent)
		{
			uint length = 0;
			var status = read(process, ref length, null);
			if (status == absent) return null;
			if (status != 122 || length == 0 || length > 4096) throw new Win32Exception(status);
			var value = new StringBuilder((int)length);
			status = read(process, ref length, value);
			if (status != 0) throw new Win32Exception(status);
			return value.ToString();
		}

		private static string ReadWindowAppId(IntPtr window)
		{
			var iid = typeof(IPropertyStore).GUID;
			IPropertyStore store = null;
			try
			{
				Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
				var key = new PropertyKey
				{
					Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
					Id = 5
				};
				var value = new PropVariant();
				try
				{
					Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
					if (value.Type == 0) return null;
					if (value.Type != 31) throw new COMException("Unexpected AppUserModelId property type.");
					return value.Pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(value.Pointer);
				}
				finally
				{
					PropVariantClear(ref value);
				}
			}
			finally
			{
				if (store != null) Marshal.ReleaseComObject(store);
			}
		}

		private delegate bool EnumWindow(IntPtr window, IntPtr state);

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
		private static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

		[DllImport("user32.dll")]
		private static extern IntPtr GetAncestor(IntPtr window, uint flags);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr window, uint command);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
		private static extern int GetWindowLong(IntPtr window, int index);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

		[DllImport("user32.dll")]
		private static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr state);

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
