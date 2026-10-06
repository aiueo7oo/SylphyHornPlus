using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using SylphyHorn.Serialization;
using SylphyHorn.UI;
using WindowsDesktop;
using Screen = System.Windows.Forms.Screen;

namespace SylphyHorn.Services
{
	public enum SwitchAnimationMode : uint
	{
		None = 0,
		Windows = 1,
		Slide = 2,
	}

	/// <summary>
	/// Slides a snapshot of the next desktop in over the screen, then switches the desktop beneath it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Nothing is captured when a switch is requested, so the animation starts right away regardless of the screen resolution.
	/// The snapshot of a desktop is captured in the background while the animation for leaving it is playing
	/// (the overlay is excluded from screen captures), and is used as the incoming image the next time.
	/// The snapshot of the current desktop is also refreshed in the background every few seconds and shortly after arriving
	/// at a desktop, because a desktop can be left by other means (Alt+Tab, the taskbar, Task View, ...).
	/// </para>
	/// <para>
	/// The desktop is switched after the animation, so neither the switch itself nor the handlers of the desktop change
	/// delay the animation. The overlay is kept for a moment after the switch because the shell updates the wallpaper
	/// and the taskbar about 100-150 ms after a switch.
	/// </para>
	/// <para>
	/// A desktop that has not been left since startup has no snapshot yet, and is switched to without the animation.
	/// </para>
	/// </remarks>
	internal static class SwitchAnimationService
	{
		private const int MinDuration = 50;
		private const int MaxDuration = 1000;

		/// <summary>
		/// The time to keep the overlay after switching, until the shell finishes updating the wallpaper and the taskbar.
		/// </summary>
		private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

		private static readonly List<ScreenOverlay> _screens = new List<ScreenOverlay>();
		private static readonly Stopwatch _stopwatch = new Stopwatch();

		private static VirtualDesktop _pendingTarget;
		private static Task _pendingCapture;
		private static List<ScreenSnapshot> _pendingSnapshots;
		private static Animation _animation;
		private static DispatcherTimer _settleTimer;

		/// <summary>
		/// Whether the desktop has been switched beneath the overlay and the shell may still be updating the screen.
		/// </summary>
		private static bool _settling;

		/// <summary>
		/// How often the snapshot of the current desktop is refreshed in the background.
		/// </summary>
		/// <remarks>
		/// The snapshot taken when leaving a desktop with a shortcut is not enough: a desktop can also be left by other means
		/// (Alt+Tab, the taskbar, Task View, ...), which would leave an old snapshot.
		/// </remarks>
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

		/// <summary>
		/// The delay before refreshing the snapshot after arriving at a desktop, until the shell has updated the screen.
		/// </summary>
		private static readonly TimeSpan ArrivalRefreshDelay = TimeSpan.FromMilliseconds(500);

		private static DispatcherTimer _refreshTimer;
		private static bool _refreshing;
		private static int _desktopChangeCount;

		/// <summary>
		/// Creates the overlay windows in advance so that the first switch is not delayed,
		/// and starts keeping the snapshot of the current desktop up to date.
		/// </summary>
		public static void Prepare()
		{
			try
			{
				EnsureScreens();
				StartRefresh();
			}
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
				Release();
			}
		}

		/// <summary>
		/// Closes the overlay windows and discards the snapshots.
		/// </summary>
		public static void Release()
		{
			StopRefresh();
			CompletePendingSwitch();
			HideAll();
			foreach (var screen in _screens)
			{
				screen.Dispose();
			}
			_screens.Clear();
		}

		public static void Switch(VirtualDesktop target, int direction)
		{
			// A background refresh in progress must not store the screen of another desktop.
			Interlocked.Increment(ref _desktopChangeCount);

			// A switch requested during the previous animation: finish the previous one first.
			CompletePendingSwitch();
			var settling = _settling;

			var current = VirtualDesktop.Current;
			if (target == current) return;

			if (direction == 0 || IsFullScreenAppRunning())
			{
				HideAll();
				target.Switch(false);
				return;
			}

			try
			{
				EnsureScreens();

				if (!_screens.All(x => x.HasSnapshot(target.Id)))
				{
					// No snapshot of the target yet: switch without the animation, but remember this desktop for next time.
					HideAll();
					if (!settling)
					{
						foreach (var snapshot in _screens.Select(x => x.GetSnapshot(current.Id)))
						{
							snapshot.Capture();
							snapshot.Invalidate();
						}
					}
					target.Switch(false);
					return;
				}

				StartAnimation(current, target, direction, settling);
			}
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
				_pendingTarget = null;
				HideAll();
				target.Switch(false);
			}
		}

		/// <summary>
		/// Performs the switch deferred until the end of the animation.
		/// </summary>
		/// <remarks>
		/// Call this before an operation that depends on the current desktop,
		/// because the current desktop does not change until the end of the animation.
		/// </remarks>
		/// <returns>true if a deferred switch has been performed.</returns>
		public static bool CompletePendingSwitch()
		{
			var target = _pendingTarget;
			if (target == null) return false;

			_pendingTarget = null;

			// An animation still playing (a new switch was requested): let it cover the whole screen at once.
			if (_animation != null)
			{
				_animation.Finish();
				_animation = null;
			}

			// The snapshot of the desktop being left must be taken before it is switched away.
			FinishCapture();

			_settling = true;
			try
			{
				target.Switch(false);
				RemoveClosedDesktops();
			}
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
			}

			return true;
		}

		private static void StartAnimation(VirtualDesktop current, VirtualDesktop target, int direction, bool settling)
		{
			StopSettleTimer();

			// Capture the desktop being left in the background. Right after a switch, the screen may still show the wallpaper
			// of the previous desktop, so keep the snapshot shown at the arrival in that case.
			if (!settling)
			{
				var snapshots = _screens.Select(x => x.GetSnapshot(current.Id)).ToList();
				_pendingSnapshots = snapshots;
				_pendingCapture = Task.Run(() => snapshots.ForEach(x => x.Capture()));
			}

			var windows = _screens.Select(x => x.ShowNext(target.Id)).ToList();

			// Make sure the snapshots are rendered before they appear; the windows are still far outside of the screens.
			WaitForPresent();

			// A window sliding in from an edge adjacent to another screen would appear on that screen,
			// so such a screen is covered at once instead.
			var sliding = new List<SwitchAnimationWindow>();
			for (var i = 0; i < _screens.Count; i++)
			{
				if (CanSlideIn(_screens[i].Bounds, direction))
				{
					windows[i].Place(0.0, direction);
					sliding.Add(windows[i]);
				}
				else
				{
					windows[i].Place(1.0, direction);
				}
			}

			_pendingTarget = target;
			if (sliding.Count == 0)
			{
				OnAnimationCompleted(null, EventArgs.Empty);
				return;
			}

			_animation = new Animation(sliding, direction, GetDuration());
			_animation.Completed += OnAnimationCompleted;
			_animation.Start();
		}

		private static bool CanSlideIn(System.Drawing.Rectangle screen, int direction)
		{
			var entry = direction > 0
				? new System.Drawing.Rectangle(screen.Right, screen.Top, screen.Width, screen.Height)
				: new System.Drawing.Rectangle(screen.Left - screen.Width, screen.Top, screen.Width, screen.Height);

			return _screens.All(x => !x.Bounds.IntersectsWith(entry));
		}

		private static void OnAnimationCompleted(object sender, EventArgs e)
		{
			if (sender != null && sender != _animation) return;
			_animation = null;

			if (CompletePendingSwitch())
			{
				// Only the latest windows are needed to cover the screen from now on.
				foreach (var screen in _screens)
				{
					screen.HideAllButLatest();
				}
				StartSettleTimer();
			}
			else
			{
				HideAll();
			}
		}

		private static void FinishCapture()
		{
			var capture = _pendingCapture;
			var snapshots = _pendingSnapshots;
			_pendingCapture = null;
			_pendingSnapshots = null;
			if (capture == null) return;

			try
			{
				capture.Wait();
				snapshots.ForEach(x => x.Invalidate());
			}
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
			}
		}

		private static void StartRefresh()
		{
			if (_refreshTimer != null)
			{
				_refreshTimer.Start();
				return;
			}

			_refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval, };
			_refreshTimer.Tick += (sender, args) =>
			{
				_refreshTimer.Interval = RefreshInterval;
				RefreshCurrentSnapshot();
			};
			_refreshTimer.Start();

			var dispatcher = Dispatcher.CurrentDispatcher;
			VirtualDesktop.CurrentChanged += (sender, args) =>
			{
				Interlocked.Increment(ref _desktopChangeCount);

				// Refresh soon after arriving at a desktop by any means, after the shell has updated the screen.
				dispatcher.BeginInvoke(new Action(() =>
				{
					if (_refreshTimer == null || !_refreshTimer.IsEnabled) return;
					_refreshTimer.Stop();
					_refreshTimer.Interval = ArrivalRefreshDelay;
					_refreshTimer.Start();
				}));
			};
		}

		private static void StopRefresh()
		{
			_refreshTimer?.Stop();
		}

		/// <summary>
		/// Captures the current desktop into a spare snapshot in the background,
		/// and replaces its snapshot only if no desktop switch happened meanwhile.
		/// </summary>
		private static void RefreshCurrentSnapshot()
		{
			if (_screens.Count == 0 || _refreshing) return;
			if (_animation != null || _pendingTarget != null || _settling) return;
			if (IsFullScreenAppRunning() || !IsInputDesktopDefault()) return;

			try
			{
				var desktopId = VirtualDesktop.Current.Id;
				var changeCount = Volatile.Read(ref _desktopChangeCount);
				var screens = _screens.ToArray();
				var spares = screens.Select(x => x.Spare).ToArray();
				var dispatcher = Dispatcher.CurrentDispatcher;

				_refreshing = true;
				Task.Run(() =>
				{
					foreach (var spare in spares)
					{
						spare.Capture();
					}
				}).ContinueWith(task => dispatcher.BeginInvoke(new Action(() =>
				{
					_refreshing = false;
					if (task.IsFaulted)
					{
						LoggingService.Instance.Register(task.Exception);
						return;
					}

					if (changeCount != Volatile.Read(ref _desktopChangeCount)) return;
					if (_animation != null || _pendingTarget != null || _settling) return;
					if (!screens.All(x => _screens.Contains(x))) return;
					if (VirtualDesktop.Current.Id != desktopId) return;

					foreach (var screen in screens)
					{
						screen.CommitSpare(desktopId);
					}
				})));
			}
			catch (Exception ex)
			{
				_refreshing = false;
				LoggingService.Instance.Register(ex);
			}
		}

		/// <summary>
		/// Whether the normal desktop receives input; false while the secure desktop (Ctrl+Alt+Del, UAC, the lock screen) is shown,
		/// when a capture would not show the user's desktop.
		/// </summary>
		private static bool IsInputDesktopDefault()
		{
			var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
			if (desktop == IntPtr.Zero) return false;

			try
			{
				var name = new System.Text.StringBuilder(256);
				return GetUserObjectInformation(desktop, UOI_NAME, name, name.Capacity * 2, out _)
					&& string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
			}
			finally
			{
				CloseDesktop(desktop);
			}
		}

		private static void StartSettleTimer()
		{
			if (_settleTimer == null)
			{
				_settleTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SettleTime, };
				_settleTimer.Tick += (sender, args) => HideAll();
			}

			_settleTimer.Stop();
			_settleTimer.Start();
		}

		private static void StopSettleTimer()
		{
			_settleTimer?.Stop();
		}

		private static void HideAll()
		{
			StopSettleTimer();
			_settling = false;
			if (_animation != null)
			{
				_animation.Stop();
				_animation = null;
			}

			foreach (var screen in _screens)
			{
				screen.HideAll();
			}
		}

		private static TimeSpan GetDuration()
		{
			return TimeSpan.FromMilliseconds(Math.Max(MinDuration, Math.Min(MaxDuration, Settings.General.SwitchAnimationDuration.Value)));
		}

		private static void EnsureScreens()
		{
			var bounds = Screen.AllScreens.Select(x => x.Bounds).ToArray();
			if (_screens.Count == bounds.Length && _screens.All(x => bounds.Contains(x.Bounds))) return;

			// The display configuration has changed.
			Release();
			foreach (var b in bounds)
			{
				_screens.Add(new ScreenOverlay(b));
			}
		}

		private static void RemoveClosedDesktops()
		{
			var ids = new HashSet<Guid>(VirtualDesktop.AllDesktops.Select(x => x.Id));
			foreach (var screen in _screens)
			{
				screen.RemoveSnapshotsExcept(ids);
			}
		}

		/// <summary>
		/// Waits until WPF has rendered a new frame and DWM has put it on the screen.
		/// </summary>
		/// <remarks>
		/// The render thread presents the frame asynchronously, so one composition pass is not always enough.
		/// </remarks>
		private static void WaitForPresent()
		{
			Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
			DwmFlush();
			DwmFlush();
		}

		private static bool IsFullScreenAppRunning()
		{
			if (SHQueryUserNotificationState(out var state) != 0) return false;

			return state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE;
		}

		/// <summary>
		/// Moves the overlay windows in on every frame.
		/// </summary>
		private sealed class Animation
		{
			private readonly IReadOnlyList<SwitchAnimationWindow> _windows;
			private readonly int _direction;
			private readonly TimeSpan _duration;
			private TimeSpan _startTime;
			private bool _running;

			public event EventHandler Completed;

			public Animation(IReadOnlyList<SwitchAnimationWindow> windows, int direction, TimeSpan duration)
			{
				this._windows = windows;
				this._direction = direction;
				this._duration = duration;
			}

			public void Start()
			{
				if (!_stopwatch.IsRunning) _stopwatch.Start();
				this._startTime = _stopwatch.Elapsed;
				this._running = true;
				CompositionTarget.Rendering += this.OnRendering;
			}

			/// <summary>
			/// Jumps to the end: the windows cover the whole screen.
			/// </summary>
			public void Finish()
			{
				this.Stop();
				this.Place(1.0);
			}

			public void Stop()
			{
				if (!this._running) return;
				this._running = false;
				CompositionTarget.Rendering -= this.OnRendering;
			}

			private void OnRendering(object sender, EventArgs e)
			{
				if (!this._running) return;

				var progress = (_stopwatch.Elapsed - this._startTime).TotalMilliseconds / this._duration.TotalMilliseconds;
				if (progress >= 1.0)
				{
					this.Finish();
					this.Completed?.Invoke(this, EventArgs.Empty);
					return;
				}

				// Quartic ease-out: most of the movement happens at the beginning, so that the switch feels immediate.
				var remaining = 1.0 - progress;
				this.Place(1.0 - remaining * remaining * remaining * remaining);
			}

			private void Place(double coverage)
			{
				foreach (var window in this._windows)
				{
					window.Place(coverage, this._direction);
				}
			}
		}

		/// <summary>
		/// The overlay windows for one screen and the snapshots of each desktop on that screen.
		/// </summary>
		/// <remarks>
		/// Two windows are used in turn, so that a new animation can slide in over the window still covering the screen.
		/// </remarks>
		private sealed class ScreenOverlay : IDisposable
		{
			private readonly SwitchAnimationWindow[] _windows;
			private readonly Dictionary<Guid, ScreenSnapshot> _snapshots = new Dictionary<Guid, ScreenSnapshot>();
			private ScreenSnapshot _spare;
			private int _latest = -1;

			/// <summary>
			/// The snapshot used for background refresh; it replaces the snapshot of the desktop when the capture completes.
			/// </summary>
			public ScreenSnapshot Spare => this._spare ?? (this._spare = new ScreenSnapshot(this.Bounds));

			/// <summary>
			/// Makes the spare snapshot the snapshot of the desktop, and the replaced one the next spare.
			/// </summary>
			public void CommitSpare(Guid desktopId)
			{
				var fresh = this.Spare;
				fresh.Invalidate();
				this._snapshots.TryGetValue(desktopId, out var replaced);
				this._snapshots[desktopId] = fresh;
				this._spare = replaced;
			}

			public System.Drawing.Rectangle Bounds { get; }

			public ScreenOverlay(System.Drawing.Rectangle bounds)
			{
				this.Bounds = bounds;
				this._windows = new[] { new SwitchAnimationWindow(bounds), new SwitchAnimationWindow(bounds), };
				foreach (var window in this._windows)
				{
					window.WarmUp(WaitForPresent);
				}
			}

			public bool HasSnapshot(Guid desktopId)
			{
				return this._snapshots.ContainsKey(desktopId);
			}

			public ScreenSnapshot GetSnapshot(Guid desktopId)
			{
				if (!this._snapshots.TryGetValue(desktopId, out var snapshot))
				{
					snapshot = new ScreenSnapshot(this.Bounds);
					this._snapshots[desktopId] = snapshot;
				}

				return snapshot;
			}

			/// <summary>
			/// Shows the snapshot of the desktop, outside of the screen, in the window not covering the screen.
			/// </summary>
			public SwitchAnimationWindow ShowNext(Guid desktopId)
			{
				// Keep only the latest window (it may be covering the screen) and slide in the other one over it.
				this.HideAllButLatest();

				this._latest = this._latest == 0 ? 1 : 0;
				var window = this._windows[this._latest];
				window.ShowSnapshot(this._snapshots[desktopId].Source);
				return window;
			}

			public void HideAllButLatest()
			{
				for (var i = 0; i < this._windows.Length; i++)
				{
					if (i != this._latest) this._windows[i].Release();
				}
			}

			public void HideAll()
			{
				foreach (var window in this._windows)
				{
					window.Release();
				}
			}

			public void RemoveSnapshotsExcept(HashSet<Guid> desktopIds)
			{
				foreach (var id in this._snapshots.Keys.Where(x => !desktopIds.Contains(x)).ToArray())
				{
					this._snapshots[id].Dispose();
					this._snapshots.Remove(id);
				}
			}

			public void Dispose()
			{
				foreach (var window in this._windows)
				{
					window.Close();
				}

				foreach (var snapshot in this._snapshots.Values)
				{
					snapshot.Dispose();
				}
				this._snapshots.Clear();
				this._spare?.Dispose();
				this._spare = null;
			}
		}

		private const int QUNS_BUSY = 2;
		private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
		private const int QUNS_PRESENTATION_MODE = 4;

		[DllImport("shell32.dll")]
		private static extern int SHQueryUserNotificationState(out int pquns);

		[DllImport("dwmapi.dll")]
		private static extern int DwmFlush();

		private const uint DESKTOP_READOBJECTS = 0x0001;
		private const int UOI_NAME = 2;

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseDesktop(IntPtr hDesktop);
	}
}
