using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WindowsDesktop;
using WindowsDesktop.Interop;
using static SylphyHorn.Services.AppPlacement.PlacementNativeMethods;

namespace SylphyHorn.Services.AppPlacement
{
	internal sealed class PlacementOccupancyObservation
	{
		internal PlacementOccupancyObservation(bool complete, IEnumerable<Guid> occupied, Func<bool> stillCurrent, IEnumerable<Guid> used = null)
		{
			this.Complete = complete;
			this.Used = new HashSet<Guid>(used ?? Array.Empty<Guid>());
			this.Occupied = new HashSet<Guid>(occupied);
			this.StillCurrent = stillCurrent ?? throw new ArgumentNullException(nameof(stillCurrent));
		}

		internal bool Complete { get; }

		internal ISet<Guid> Used { get; }

		internal ISet<Guid> Occupied { get; }

		internal Func<bool> StillCurrent { get; }
	}

	internal sealed class PlacementDesktopOccupancyReader
	{
		private const int WindowLimit = 4096;
		private const int ClassNameCapacity = 256;

		internal PlacementOccupancyObservation Read(CancellationToken cancellation, Func<bool> stillCurrent)
		{
			var occupied = new HashSet<Guid>();
			var complete = true;
			var count = 0;
			var manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID.VirtualDesktopManager));
			try
			{
				var enumerated = EnumWindows(
					(window, _) =>
					{
						if (cancellation.IsCancellationRequested || ++count > WindowLimit)
						{
							complete = false;
							return false;
						}
						if (!IsWindowVisible(window)) return true;
						var name = new StringBuilder(ClassNameCapacity);
						if (GetClassName(window, name, name.Capacity) == 0)
						{
							complete = false;
							return false;
						}
						// Shell surfaces are not user windows. Owned dialogs and tool windows are deliberately included.
						if (IsShellSurface(name.ToString())) return true;
						try
						{
							var desktop = LocateDesktop(() => manager.GetWindowDesktopId(window), () => VirtualDesktop.IsPinnedWindow(window));
							if (!desktop.HasValue) return true;
							try
							{
								if (VirtualDesktop.IsPinnedWindow(window)) return true;
								var appId = ApplicationHelper.GetAppId(window);
								if (!string.IsNullOrEmpty(appId) && VirtualDesktop.IsPinnedApplication(appId)) return true;
							}
							catch (COMException ex) when (IsMissingView(ex))
							{
								// A known desktop still counts as occupied when pin metadata is unavailable.
							}
							occupied.Add(desktop.Value);
						}
						catch (Exception)
						{
							complete = false;
							return false;
						}
						return true;
					},
					IntPtr.Zero);
				return new PlacementOccupancyObservation(enumerated && complete && !cancellation.IsCancellationRequested, occupied, stillCurrent);
			}
			finally
			{
				Marshal.ReleaseComObject(manager);
			}
		}

		internal static Guid? LocateDesktop(Func<Guid> locate, Func<bool> pinned)
		{
			try
			{
				var id = locate();
				return id == Guid.Empty ? (Guid?)null : id;
			}
			catch (COMException ex) when (IsMissingView(ex))
			{
				// Both Shell lookups must confirm absence before excluding an unregistered helper.
				try
				{
					if (pinned()) return null;
				}
				catch (COMException pinFailure) when (IsMissingView(pinFailure))
				{
					return null;
				}
				throw;
			}
		}

		private static bool IsShellSurface(string windowClass)
			=> windowClass == "Progman" || windowClass == "WorkerW" || windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd";
	}
}
