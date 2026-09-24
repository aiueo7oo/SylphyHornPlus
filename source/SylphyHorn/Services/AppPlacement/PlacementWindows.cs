using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsDesktop;

namespace SylphyHorn.Services.AppPlacement
{
	internal sealed class PlacementWindows : IPlacementWindows, IPlacementForeground
	{
		private readonly PlacementWindowReader _reader = new PlacementWindowReader();
		private readonly uint _ownProcess = unchecked((uint)Process.GetCurrentProcess().Id);

		private readonly Func<IntPtr> _foreground;
		private readonly Func<uint?> _lastInput;
		private readonly Func<Guid?> _currentDesktop;
		private readonly Action<Guid> _switch;

		internal PlacementWindows(Func<IntPtr> foreground = null, Func<uint?> lastInput = null,
			Func<Guid?> currentDesktop = null, Action<Guid> switchDesktop = null)
		{
			this._foreground = foreground ?? InteropHelper.GetForegroundWindowEx;
			this._lastInput = lastInput ?? ReadLastInput;
			this._currentDesktop = currentDesktop ?? (() => VirtualDesktop.Current?.Id);
			this._switch = switchDesktop ?? (id => VirtualDesktop.FromId(id)?.Switch());
		}

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

		public Action PrepareFollow(PlacementWindowIdentity identity, Guid source, Guid target, Func<bool> current)
		{
			// Moving the foreground window itself can change focus without user input.
			// Compare input timestamps instead of requiring that window to remain foreground.
			var input = this._lastInput();
			if (!input.HasValue || this._foreground() != identity.Window) return null;
			return () =>
			{
				if (!current() || this._lastInput() != input || this._currentDesktop() != source) return;
				this._switch(target);
			};
		}

		private static uint? ReadLastInput()
		{
			var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
			return GetLastInputInfo(ref input) ? input.Time : (uint?)null;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct LastInputInfo
		{
			internal uint Size;
			internal uint Time;
		}

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetLastInputInfo(ref LastInputInfo info);

		public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
			PlacementMovePermit permit, Func<bool> stillCurrent, Action beforeMove = null)
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
			beforeMove?.Invoke();
			VirtualDesktopHelper.MoveToDesktop(expected.Window, destination);
			return PlacementMoveStatus.Requested;
		}
	}
}
