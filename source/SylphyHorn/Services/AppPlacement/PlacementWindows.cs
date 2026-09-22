using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsDesktop;

namespace SylphyHorn.Services.AppPlacement
{
	internal sealed class PlacementWindows : IPlacementWindows
	{
		private readonly PlacementWindowReader _reader = new PlacementWindowReader();
		private readonly uint _ownProcess = unchecked((uint)Process.GetCurrentProcess().Id);

		public PlacementWindowInspection Inspect(IntPtr window)
		{
			var result = this._reader.Read(window);
			return result.Identity?.Owner.Id == this._ownProcess
				? new PlacementWindowInspection(PlacementInspectionStatus.Excluded, "OwnProcess") : result;
		}

		public PlacementWindowLocation Locate(IntPtr window)
		{
			try
			{
				if (VirtualDesktop.IsPinnedWindow(window)) return new PlacementWindowLocation(Guid.Empty, true);
				var appId = ApplicationHelper.GetAppId(window);
				if (string.IsNullOrEmpty(appId)) return null;
				if (VirtualDesktop.IsPinnedApplication(appId)) return new PlacementWindowLocation(Guid.Empty, true);
				var desktop = VirtualDesktop.FromHwnd(window);
				return desktop == null ? null : new PlacementWindowLocation(desktop.Id, false);
			}
			// A newly created HWND can precede its Shell application view. Both lookup failures
			// mean location is not available yet; the processor owns the bounded readiness retry.
			catch (COMException ex) when (ex.HResult == unchecked((int)0x8002802B) || ex.HResult == unchecked((int)0x80070490))
			{
				return null;
			}
		}

		public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target, PlacementMovePermit permit, Func<bool> stillCurrent)
		{
			var destination = VirtualDesktop.FromId(target);
			if (destination == null) return PlacementMoveStatus.MissingDestination;
			var inspection = this.Inspect(expected.Window);
			if (inspection.Status != PlacementInspectionStatus.Ready || !expected.SameInstance(inspection.Identity)) return PlacementMoveStatus.Changed;
			var location = this.Locate(expected.Window);
			if (location == null) return PlacementMoveStatus.Changed;
			if (location.Pinned) return PlacementMoveStatus.Pinned;
			if (location.Desktop == target) return PlacementMoveStatus.AlreadyPlaced;
			if (location.Desktop != source) return PlacementMoveStatus.Changed;
			if (!stillCurrent() || permit == null || !permit.TryStart()) return PlacementMoveStatus.Cancelled;
			VirtualDesktopHelper.MoveToDesktop(expected.Window, destination);
			return PlacementMoveStatus.Requested;
		}
	}
}
