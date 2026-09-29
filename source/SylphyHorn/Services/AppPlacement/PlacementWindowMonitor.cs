using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static SylphyHorn.Services.AppPlacement.PlacementNativeMethods;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>Opt-in native observation. Owns one hook thread and never activates, moves or closes a window.</summary>
	internal sealed class PlacementWindowMonitor : IDisposable
	{
		private const uint MaximumEventAgeMilliseconds = 5000;
		private const int MessagesPerPump = 64;

		private const uint WINEVENT_OUTOFCONTEXT = 0;
		private const int OBJID_WINDOW = 0;
		private const int CHILDID_SELF = 0;
		private const uint PM_NOREMOVE = 0;
		private const uint PM_REMOVE = 1;
		// QS_ALLINPUT as defined before Windows 8, without the QS_TOUCH and QS_POINTER bits.
		private const uint QS_ALLINPUT_WIN7 = 0x04FF;
		private const uint MWMO_INPUTAVAILABLE = 0x0004;
		private const uint INFINITE = uint.MaxValue;
		private const uint WAIT_FAILED = uint.MaxValue;

		private readonly object _gate = new object();
		private readonly ManualResetEvent _stop = new ManualResetEvent(false);
		private readonly AutoResetEvent _changed = new AutoResetEvent(false);
		private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly int _trackingLimit;
		private readonly PlacementWindowEventReceiver _receiver;
		private bool _ended;

		internal PlacementWindowEvents Events { get; }

		internal Task<bool> Ready => this._ready.Task;

		internal Task Completion => this._completion.Task;

		internal WaitHandle Changed => this._changed;

		private PlacementWindowMonitor(int eventLimit, int trackingLimit, int candidateLimit)
		{
			this._trackingLimit = trackingLimit;
			this.Events = new PlacementWindowEvents(eventLimit, trackingLimit, candidateLimit);
			this._receiver = new PlacementWindowEventReceiver(this.ReceiveOrdered, () => this.Events.Pause("EventCapacity"), eventLimit);
		}

		internal static PlacementWindowMonitor Start(int eventLimit = 2048, int trackingLimit = 4096, int candidateLimit = 256)
		{
			if (eventLimit <= 0 || trackingLimit <= 0 || candidateLimit <= 0)
			{
				throw new ArgumentOutOfRangeException();
			}
			var monitor = new PlacementWindowMonitor(eventLimit, trackingLimit, candidateLimit);
			var thread = new Thread(monitor.Run)
			{
				IsBackground = true,
				Name = "App placement window events"
			};
			thread.SetApartmentState(ApartmentState.STA);
			try
			{
				thread.Start();
			}
			catch
			{
				monitor._stop.Dispose();
				monitor._changed.Dispose();
				throw;
			}
			return monitor;
		}

		internal Task StopAsync()
		{
			this.Events.Stop();
			lock (this._gate)
			{
				if (!this._ended)
				{
					this._stop.Set();
				}
			}
			return this.Completion;
		}

		private void Run()
		{
			IntPtr hook = IntPtr.Zero;
			WinEvent callback = this.Receive;
			Exception failure = null;
			try
			{
				Message message;
				// Create this thread's message queue before the hook can post to it.
				PeekMessage(out message, IntPtr.Zero, 0, 0, PM_NOREMOVE);
				if (this._stop.WaitOne(0)) return;
				hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT);
				if (hook == IntPtr.Zero)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				var baseline = new List<IntPtr>();
				var initiallyHidden = new HashSet<IntPtr>();
				var overflow = false;
				var enumerated = EnumWindows(
					(window, state) =>
					{
						if (this._stop.WaitOne(0)) return false;
						if (baseline.Count == this._trackingLimit)
						{
							overflow = true;
							return false;
						}
						baseline.Add(window);
						// Minimized and Shell-cloaked windows still have WS_VISIBLE; preserve their placement.
						if (!IsWindowVisible(window))
						{
							initiallyHidden.Add(window);
						}
						return true;
					},
					IntPtr.Zero);
				if (!enumerated)
				{
					if (!this._stop.WaitOne(0))
					{
						this.Events.Pause(overflow ? "BaselineCapacity" : "BaselineUnavailable");
					}
					this._ready.TrySetResult(false);
					return;
				}
				// Freeze the cutoff when baseline capture finishes. Events at that tick remain
				// ambiguous and are excluded, but Ready must not let a caller create a new
				// window in the same excluded tick. Wait only on this hook thread, cancellably.
				var boundary = GetTickCount64();
				while (GetTickCount64() <= boundary)
				{
					if (this._stop.WaitOne(1)) return;
				}
				if (this._stop.WaitOne(0)) return;
				this._ready.TrySetResult(this.Events.Ready(baseline, checked((long)boundary), initiallyHidden));
				var handles = new[] { this._stop.SafeWaitHandle.DangerousGetHandle() };
				while (!this._stop.WaitOne(0) && this.Events.State != PlacementMonitorState.Paused)
				{
					for (var n = 0; n < MessagesPerPump && PeekMessage(out message, IntPtr.Zero, 0, 0, PM_REMOVE); n++)
					{
						TranslateMessage(ref message);
						DispatchMessage(ref message);
					}
					if (this.Events.State == PlacementMonitorState.Paused) break;
					var result = MsgWaitForMultipleObjectsEx(1, handles, INFINITE, QS_ALLINPUT_WIN7, MWMO_INPUTAVAILABLE);
					if (result == WAIT_FAILED)
					{
						throw new Win32Exception(Marshal.GetLastWin32Error());
					}
				}
			}
			catch (Exception ex)
			{
				failure = ex;
				this.Events.Pause("NativeFailure");
			}
			finally
			{
				if (hook != IntPtr.Zero && !UnhookWinEvent(hook))
				{
					failure = new Win32Exception(Marshal.GetLastWin32Error());
					this.Events.Pause("UnhookFailed");
				}
				GC.KeepAlive(callback);
				this._ready.TrySetResult(false);
				lock (this._gate)
				{
					this._ended = true;
					this._stop.Dispose();
					// Completion is the terminal signal. Consumers must stop using Changed before closing it.
					this._changed.Set();
				}
				if (failure == null)
				{
					this._completion.TrySetResult(true);
				}
				else
				{
					this._completion.TrySetException(failure);
				}
			}
		}

		public void Dispose()
		{
			if (!this.Completion.IsCompleted)
			{
				throw new InvalidOperationException("Stop the monitor before releasing its consumer signal.");
			}
			this._changed.Dispose();
		}

		private void Receive(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
		{
			if (window == IntPtr.Zero || objectId != OBJID_WINDOW || childId != CHILDID_SELF) return;
			try
			{
				// Keep the native 32-bit timestamp until this callback reaches the ordered drain.
				this._receiver.Receive(new PlacementWindowEvent((PlacementWindowEventKind)kind, window, time));
				this._changed.Set();
			}
			catch
			{
				this.Events.Pause("CallbackFailure");
			}
		}

		private void ReceiveOrdered(PlacementWindowEvent value)
		{
			// OBJID_WINDOW is also raised for child HWNDs. Keep them out of the lifetime budget.
			// A destroyed HWND may no longer be queryable, so DESTROY always reaches the tracker.
			if (value.Kind != PlacementWindowEventKind.Destroy && GetAncestor(value.Window, GA_ROOT) != value.Window) return;
			var occurred = NormalizeRecentTime(unchecked((uint)value.OccurredAt), checked((long)GetTickCount64()), MaximumEventAgeMilliseconds);
			if (!occurred.HasValue)
			{
				this.Events.Pause("EventTimeUnavailable");
			}
			else
			{
				this.Events.Receive(new PlacementWindowEvent(value.Kind, value.Window, occurred.Value));
			}
		}

		internal static long? NormalizeRecentTime(uint value, long now, uint maximumAge)
		{
			var age = unchecked((uint)now - value);
			return age <= maximumAge && now >= age ? now - age : (long?)null;
		}

		private delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);

		[StructLayout(LayoutKind.Sequential)]
		private struct Message
		{
			internal IntPtr Hwnd;
			internal uint Value;
			internal UIntPtr WParam;
			internal IntPtr LParam;
			internal uint Time;
			internal int X, Y;
			internal uint Private;
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool UnhookWinEvent(IntPtr hook);

		[DllImport("user32.dll")]
		private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);

		[DllImport("user32.dll")]
		private static extern bool TranslateMessage(ref Message message);

		[DllImport("user32.dll")]
		private static extern IntPtr DispatchMessage(ref Message message);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint timeout, uint mask, uint flags);
	}
}
