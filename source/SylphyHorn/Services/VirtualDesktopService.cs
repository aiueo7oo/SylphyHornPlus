using System;
using System.Linq;
using System.Media;
using SylphyHorn.Interop;
using SylphyHorn.Serialization;
using WindowsDesktop;

namespace SylphyHorn.Services
{
	internal static class VirtualDesktopService
	{
		#region Count

		public static int Count => VirtualDesktop.Count;

		#endregion

		#region Get

		public static VirtualDesktop GetLeft()
		{
			var current = VirtualDesktop.Current;
			var left = current.GetLeft();

			if (left == null && Count >= 2 && Settings.General.LoopDesktop)
			{
				var desktops = VirtualDesktop.AllDesktops;
				return desktops.Last();
			}

			return left;
		}

		public static VirtualDesktop GetRight()
		{
			var current = VirtualDesktop.Current;
			var right = current.GetRight();

			if (right == null && Count >= 2 && Settings.General.LoopDesktop)
			{
				var desktops = VirtualDesktop.AllDesktops;
				return desktops.First();
			}

			return right;
		}

		public static VirtualDesktop GetPrevious()
		{
			return VirtualDesktop.History.Previous;
		}

		public static VirtualDesktop GetByIndex(int index)
		{
			var desktops = VirtualDesktop.AllDesktops;

			return (index >= 0) && (index < desktops.Length) ? desktops[index] : null;
		}

		#endregion

		#region Switch

		/// <summary>
		/// Switches to the desktop with the animation selected in the settings.
		/// </summary>
		/// <param name="direction">1 when moving right, -1 when moving left, 0 to decide from the desktop order.</param>
		public static void SwitchTo(this VirtualDesktop desktop, int direction = 0)
		{
			switch ((SwitchAnimationMode)Settings.General.SwitchAnimationMode.Value)
			{
				case SwitchAnimationMode.Windows:
					desktop.Switch(true);
					break;

				case SwitchAnimationMode.Slide:
					var current = VirtualDesktop.Current;
					if (desktop == current) return;

					if (direction == 0)
					{
						var desktops = VirtualDesktop.AllDesktops;
						direction = Array.IndexOf(desktops, desktop) > Array.IndexOf(desktops, current) ? 1 : -1;
					}

					SwitchAnimationService.Switch(desktop, direction);
					break;

				default:
					desktop.Switch(false);
					break;
			}
		}

		#endregion

		#region Move Window

		public static VirtualDesktop MoveToLeft(this IntPtr hWnd)
		{
			var current = VirtualDesktop.FromHwnd(hWnd);
			if (current != null)
			{
				var left = current.GetLeft();
				if (left == null)
				{
					if (Settings.General.LoopDesktop)
					{
						var desktops = VirtualDesktop.AllDesktops;
						if (desktops.Length >= 2) left = desktops.Last();
					}
				}
				if (left != null)
				{
					VirtualDesktopHelper.MoveToDesktop(hWnd, left);
					return left;
				}
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToRight(this IntPtr hWnd)
		{
			var current = VirtualDesktop.FromHwnd(hWnd);
			if (current != null)
			{
				var right = current.GetRight();
				if (right == null)
				{
					if (Settings.General.LoopDesktop)
					{
						var desktops = VirtualDesktop.AllDesktops;
						if (desktops.Length >= 2) right = desktops.First();
					}
				}
				if (right != null)
				{
					VirtualDesktopHelper.MoveToDesktop(hWnd, right);
					return right;
				}
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToPrevious(this IntPtr hWnd)
		{
			var current = VirtualDesktop.FromHwnd(hWnd);
			if (current != null)
			{
				var previous = VirtualDesktop.History.Previous;
				if (previous != null)
				{
					VirtualDesktopHelper.MoveToDesktop(hWnd, previous);
					return previous;
				}
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToIndex(this IntPtr hWnd, int i)
		{
			var current = VirtualDesktop.FromHwnd(hWnd);
			if (current != null)
			{
				var target = GetByIndex(i);
				if (target != null)
				{
					VirtualDesktopHelper.MoveToDesktop(hWnd, target);
					return target;
				}
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToNew(this IntPtr hWnd)
		{
			var newone = VirtualDesktop.Create();
			if (newone != null)
			{
				VirtualDesktopHelper.MoveToDesktop(hWnd, newone);
				return newone;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		#endregion

		#region Move Desktop

		public static VirtualDesktop MoveToLeft(this VirtualDesktop current)
		{
			if (current != null)
			{
				var left = current.GetLeft();
				if (left == null)
				{
					if (Settings.General.LoopDesktop)
					{
						var desktops = VirtualDesktop.AllDesktops;
						if (desktops.Length >= 2) current.Move(desktops.Length - 1);
					}
				}
				else
				{
					current.Move(left.Index);
				}
				return current;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToRight(this VirtualDesktop current)
		{
			if (current != null)
			{
				var right = current.GetRight();
				if (right == null)
				{
					if (Settings.General.LoopDesktop)
					{
						var desktops = VirtualDesktop.AllDesktops;
						if (desktops.Length >= 2) current.Move(0);
					}
				}
				else
				{
					current.Move(right.Index);
				}
				return current;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToFirst(this VirtualDesktop current)
		{
			if (current != null && Count > 0)
			{
				current.Move(0);
				return current;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToLast(this VirtualDesktop current)
		{
			var desktopCount = Count;
			if (current != null && desktopCount > 0)
			{
				current.Move(desktopCount - 1);
				return current;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		public static VirtualDesktop MoveToIndex(this VirtualDesktop current, int i)
		{
			if (current != null && 0 <= i && i < Count)
			{
				current.Move(i);
				return current;
			}

			SystemSounds.Asterisk.Play();
			return null;
		}

		#endregion

		#region Swap Desktop

		public static VirtualDesktop SwapCurrentForLeft()
		{
			var current = VirtualDesktop.Current;

			return current.MoveToLeft();
		}

		public static VirtualDesktop SwapCurrentForRight()
		{
			var current = VirtualDesktop.Current;

			return current.MoveToRight();
		}

		public static VirtualDesktop SwapCurrentForFirst()
		{
			var current = VirtualDesktop.Current;

			return current.MoveToFirst();
		}

		public static VirtualDesktop SwapCurrentForLast()
		{
			var current = VirtualDesktop.Current;

			return current.MoveToLast();
		}

		public static VirtualDesktop SwapCurrentByIndex(int index)
		{
			var current = VirtualDesktop.Current;

			return current.MoveToIndex(index);
		}

		public static Tuple<VirtualDesktop, VirtualDesktop> SwapDesktops(int index1, int index2)
		{
			if (index1 >= 0 && index2 >= 0)
			{
				var desktopCount = Count;
			
				if (index1 < desktopCount && index2 < desktopCount)
				{
					var desktops = VirtualDesktop.AllDesktops;
					var desktop1 = desktops[index1];
					var desktop2 = desktops[index2];
					desktop1.Move(index2);
					desktop2.Move(index1);
					return new Tuple<VirtualDesktop, VirtualDesktop>(desktop1, desktop2);
				}
			}

			return new Tuple<VirtualDesktop, VirtualDesktop>(null, null);
		}

		#endregion

		#region Create

		public static void CreateAndSwitch()
		{
			VirtualDesktop.Create()?.SwitchTo();
		}

		#endregion

		#region Close

		public static void CloseAndSwitchLeft()
		{
			var current = VirtualDesktop.Current;
			
			if (Count > 1)
			{
				GetLeft()?.SwitchTo(-1);
				current.Remove();
			}
		}

		public static void CloseAndSwitchRight()
		{
			var current = VirtualDesktop.Current;

			if (Count > 1)
			{
				GetRight()?.SwitchTo(1);
				current.Remove();
			}
		}

		#endregion

		#region Task View

		public static void ShowTaskView()
		{
			InputInjector.ReleaseModifiersAndSendChord(InputInjector.VK_LWIN, InputInjector.VK_TAB);
		}

		public static void ShowWindowSwitch()
		{
			InputInjector.ReleaseModifiersAndSendChord(
				InputInjector.VK_CONTROL,
				InputInjector.VK_MENU,
				InputInjector.VK_TAB);
		}

		#endregion

		#region Pin / Unpin

		public static event EventHandler<WindowPinnedEventArgs> WindowPinned;


		public static void Pin(this IntPtr hWnd)
		{
			VirtualDesktop.PinWindow(hWnd);
			RaisePinnedEvent(hWnd, PinOperations.PinWindow);
		}

		public static void Unpin(this IntPtr hWnd)
		{
			VirtualDesktop.UnpinWindow(hWnd);
			RaisePinnedEvent(hWnd, PinOperations.UnpinWindow);
		}

		public static void TogglePin(this IntPtr hWnd)
		{
			if (VirtualDesktop.IsPinnedWindow(hWnd))
			{
				VirtualDesktop.UnpinWindow(hWnd);
				RaisePinnedEvent(hWnd, PinOperations.UnpinWindow);
			}
			else
			{
				VirtualDesktop.PinWindow(hWnd);
				RaisePinnedEvent(hWnd, PinOperations.PinWindow);
			}
		}

		public static void PinApp(this IntPtr hWnd)
		{
			var appId = ApplicationHelper.GetAppId(hWnd);
			if (appId == null) return;

			VirtualDesktop.PinApplication(appId);
			RaisePinnedEvent(hWnd, PinOperations.PinApp);
		}

		public static void UnpinApp(this IntPtr hWnd)
		{
			var appId = ApplicationHelper.GetAppId(hWnd);
			if (appId == null) return;

			VirtualDesktop.UnpinApplication(appId);
			RaisePinnedEvent(hWnd, PinOperations.UnpinApp);
		}

		public static void TogglePinApp(this IntPtr hWnd)
		{
			var appId = ApplicationHelper.GetAppId(hWnd);
			if (appId == null) return;

			if (VirtualDesktop.IsPinnedApplication(appId))
			{
				VirtualDesktop.UnpinApplication(appId);
				RaisePinnedEvent(hWnd, PinOperations.UnpinApp);
			}
			else
			{
				VirtualDesktop.PinApplication(appId);
				RaisePinnedEvent(hWnd, PinOperations.PinApp);
			}
		}

		private static void RaisePinnedEvent(IntPtr target, PinOperations operation)
		{
			WindowPinned?.Invoke(typeof(VirtualDesktopService), new WindowPinnedEventArgs(target, operation));
		}

		#endregion
	}

	internal class WindowPinnedEventArgs : EventArgs
	{
		public IntPtr Target { get; }
		public PinOperations PinOperation { get; }

		public WindowPinnedEventArgs(IntPtr target, PinOperations operation)
		{
			this.Target = target;
			this.PinOperation = operation;
		}
	}

	[Flags]
	internal enum PinOperations
	{
		Pin = 0x01,
		Unpin = 0x02,

		Window = 0x04,
		App = 0x08,

		PinWindow = Pin | Window,
		UnpinWindow = Unpin | Window,
		PinApp = Pin | App,
		UnpinApp = Unpin | App,
	}
}
