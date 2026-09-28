using System;
using System.Collections.Generic;
using System.Linq;

namespace SylphyHorn.Services.AppPlacement
{
	internal enum PlacementWindowEventKind
	{
		Create = 0x8000,
		Destroy,
		Show,
		Hide
	}

	internal enum PlacementMonitorState
	{
		Preparing,
		Running,
		Paused,
		Stopped
	}

	internal struct PlacementWindowEvent
	{
		internal PlacementWindowEvent(PlacementWindowEventKind kind, IntPtr window, long occurredAt)
		{
			this.Kind = kind;
			this.Window = window;
			this.OccurredAt = occurredAt;
		}

		internal PlacementWindowEventKind Kind { get; }

		internal IntPtr Window { get; }

		internal long OccurredAt { get; }
	}

	/// <summary>Preserves callback order when an STA lock wait pumps another native event.</summary>
	internal sealed class PlacementWindowEventReceiver
	{
		private readonly Action<PlacementWindowEvent> _receive;
		private readonly Action _overflow;
		private readonly int _limit;
		private readonly Queue<PlacementWindowEvent> _pending = new Queue<PlacementWindowEvent>();
		private bool _receiving;
		private bool _overflowed;

		internal PlacementWindowEventReceiver(Action<PlacementWindowEvent> receive, Action overflow, int limit)
		{
			this._receive = receive;
			this._overflow = overflow;
			this._limit = limit;
		}

		// Called only by the hook thread. Do not acquire the tracker's lock before setting
		// _receiving: an STA lock wait can dispatch a later WinEvent on this same thread.
		internal void Receive(PlacementWindowEvent value)
		{
			if (this._overflowed) return;
			if (this._pending.Count == this._limit)
			{
				this._overflowed = true;
				this._pending.Clear();
				return;
			}
			this._pending.Enqueue(value);
			if (this._receiving) return;
			this._receiving = true;
			try
			{
				while (this._pending.Count != 0 && !this._overflowed)
				{
					this._receive(this._pending.Dequeue());
				}
				if (this._overflowed)
				{
					this._overflow();
				}
			}
			finally
			{
				this._pending.Clear();
				this._receiving = false;
			}
		}
	}

	internal sealed class PlacementCandidate
	{
		internal PlacementCandidate(IntPtr window, Guid epoch, long lifetime, long observedAt)
		{
			this.Window = window;
			this.Epoch = epoch;
			this.Lifetime = lifetime;
			this.ObservedAt = observedAt;
		}

		internal IntPtr Window { get; }

		internal Guid Epoch { get; }

		internal long Lifetime { get; }

		internal long ObservedAt { get; }
	}

	/// <summary>Bounded native ingress and window lifetimes. Never calls COM, UI, or application identification.</summary>
	internal sealed class PlacementWindowEvents
	{
		private readonly object _gate = new object();
		private readonly PlacementWindowEvent[] _events;
		private readonly int _trackingLimit;
		private readonly int _candidateLimit;
		private readonly Dictionary<IntPtr, Lifetime> _windows = new Dictionary<IntPtr, Lifetime>();
		private readonly Queue<PlacementCandidate> _candidates = new Queue<PlacementCandidate>();
		private int _head, _count, _admitted;
		private readonly Guid _epoch = Guid.NewGuid();
		private long _lifetime, _boundary, _lastEventTime;
		private PlacementMonitorState _state = PlacementMonitorState.Preparing;
		private string _pauseReason;
		private long _skippedCandidates;

		internal PlacementWindowEvents(int eventLimit, int trackingLimit, int candidateLimit)
		{
			if (eventLimit <= 0 || trackingLimit <= 0 || candidateLimit <= 0)
			{
				throw new ArgumentOutOfRangeException();
			}
			this._events = new PlacementWindowEvent[eventLimit];
			this._trackingLimit = trackingLimit;
			this._candidateLimit = candidateLimit;
		}

		internal PlacementMonitorState State
		{
			get
			{
				lock (this._gate) return this._state;
			}
		}

		internal string PauseReason
		{
			get
			{
				lock (this._gate) return this._pauseReason;
			}
		}

		internal long SkippedCandidates
		{
			get
			{
				lock (this._gate) return this._skippedCandidates;
			}
		}

		internal int TrackedCount
		{
			get
			{
				lock (this._gate) return this._windows.Count;
			}
		}

		internal int BufferedCount
		{
			get
			{
				lock (this._gate) return this._count;
			}
		}

		internal bool Ready(IEnumerable<IntPtr> baseline, long boundary, ISet<IntPtr> initiallyHidden = null)
		{
			if (baseline == null) throw new ArgumentNullException(nameof(baseline));
			lock (this._gate)
			{
				if (this._state != PlacementMonitorState.Preparing) return false;
				foreach (var window in baseline)
				{
					if (window == IntPtr.Zero || this._windows.ContainsKey(window)) continue;
					if (!this.AddLifetime(window, initiallyHidden == null || !initiallyHidden.Contains(window)))
					{
						return false;
					}
				}
				this._boundary = boundary;
				this._lastEventTime = boundary;
				this._state = PlacementMonitorState.Running;
				return true;
			}
		}

		private long _version;

		internal long Version { get { lock (this._gate) return this._version; } }

		internal void Receive(PlacementWindowEvent value)
		{
			lock (this._gate)
			{
				if (this._state != PlacementMonitorState.Preparing && this._state != PlacementMonitorState.Running)
				{
					return;
				}
				if (value.Window == IntPtr.Zero) return;
				this._version++;
				if (value.Kind == PlacementWindowEventKind.Show && this._count > 0)
				{
					var last = this._events[(this._head + this._count - 1) % this._events.Length];
					if (last.Kind == value.Kind && last.Window == value.Window && last.OccurredAt <= value.OccurredAt)
					{
						this._events[(this._head + this._count - 1) % this._events.Length] = value;
						return;
					}
				}
				if (this._count == this._events.Length)
				{
					this.PauseUnderLock("EventCapacity");
					return;
				}
				this._events[(this._head + this._count++) % this._events.Length] = value;
			}
		}

		// Batches bound the time native callbacks can wait for this lock. No caller code runs under it.
		internal void ProcessBatch(int maximum = 64)
		{
			if (maximum <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(maximum));
			}
			lock (this._gate)
			{
				while (maximum-- > 0 && this._count > 0 && this._state == PlacementMonitorState.Running)
				{
					var value = this._events[this._head];
					this._head = (this._head + 1) % this._events.Length;
					this._count--;
					if (value.OccurredAt <= this._boundary) continue;
					if (value.OccurredAt < this._lastEventTime)
					{
						this.PauseUnderLock("EventOrder");
						return;
					}
					this._lastEventTime = value.OccurredAt;
					this._windows.TryGetValue(value.Window, out var lifetime);
					switch (value.Kind)
					{
						case PlacementWindowEventKind.Create:
							if (lifetime != null)
							{
								// A missing DESTROY or duplicate CREATE makes this handle's lifetime ambiguous.
								lifetime.Completed = true;
								lifetime.Ambiguous = true;
								this.Cancel(lifetime);
								this.RemoveCandidate(value.Window);
								break;
							}
							this.AddLifetime(value.Window, false);
							break;
						case PlacementWindowEventKind.Destroy:
							if (lifetime != null)
							{
								this.Cancel(lifetime);
							}
							this._windows.Remove(value.Window);
							this.RemoveCandidate(value.Window);
							break;
						case PlacementWindowEventKind.Show:
							if (lifetime == null || lifetime.Completed) break;
							lifetime.Completed = true; // Once per lifetime, including capacity skips.
							if (this._admitted == this._candidateLimit)
							{
								this._skippedCandidates++;
								break;
							}
							lifetime.Admitted = true;
							this._admitted++;
							this._candidates.Enqueue(new PlacementCandidate(value.Window, this._epoch, lifetime.Id, value.OccurredAt));
							break;
					}
				}
			}
		}

		internal PlacementCandidate TakeCandidate()
		{
			lock (this._gate)
			{
				// Drain ingress before handing out work; queued DESTROY must invalidate it first.
				if (this._state != PlacementMonitorState.Running || this._count != 0) return null;
				while (this._candidates.Count > 0)
				{
					var value = this._candidates.Dequeue();
					if (this.IsCurrentUnderLock(value)) return value;
				}
				return null;
			}
		}

		internal bool IsCurrent(PlacementCandidate candidate)
		{
			lock (this._gate) return this._count == 0 && this.IsCurrentUnderLock(candidate);
		}

		internal PlacementCandidate[] Existing(long now)
		{
			lock (this._gate)
			{
				if (this._state != PlacementMonitorState.Running || this._count != 0)
				{
					throw new InvalidOperationException("Monitor is not current.");
				}
				return this._windows.Where(pair => pair.Value.Completed && !pair.Value.Admitted && !pair.Value.Ambiguous)
					.Select(pair => new PlacementCandidate(pair.Key, this._epoch, pair.Value.Id, now)).ToArray();
			}
		}

		internal bool IsExistingCurrent(PlacementCandidate candidate)
		{
			lock (this._gate)
				return candidate != null && this._state == PlacementMonitorState.Running && this._count == 0 && candidate.Epoch == this._epoch
					&& this._windows.TryGetValue(candidate.Window, out var current) && current.Id == candidate.Lifetime
					&& current.Completed && !current.Admitted && !current.Ambiguous;
		}

		internal void Complete(PlacementCandidate candidate)
		{
			lock (this._gate)
				if (this.IsCurrentUnderLock(candidate))
				{
					this.Cancel(this._windows[candidate.Window]);
				}
		}

		internal void Pause(string reason)
		{
			lock (this._gate)
				if (this._state == PlacementMonitorState.Preparing || this._state == PlacementMonitorState.Running)
				{
					this.PauseUnderLock(reason);
				}
		}

		internal void Stop()
		{
			lock (this._gate)
			{
				this.ClearUnderLock();
				this._state = PlacementMonitorState.Stopped;
			}
		}

		private bool IsCurrentUnderLock(PlacementCandidate candidate)
			=> candidate != null && this._state == PlacementMonitorState.Running && candidate.Epoch == this._epoch
				&& this._windows.TryGetValue(candidate.Window, out var current) && current.Id == candidate.Lifetime && current.Admitted;

		private bool AddLifetime(IntPtr window, bool existing)
		{
			if (this._windows.Count == this._trackingLimit)
			{
				this.PauseUnderLock("TrackingCapacity");
				return false;
			}
			this._windows.Add(window, new Lifetime
			{
				Id = checked(++this._lifetime),
				Completed = existing
			});
			return true;
		}

		private void RemoveCandidate(IntPtr window)
		{
			var count = this._candidates.Count;
			while (count-- > 0)
			{
				var candidate = this._candidates.Dequeue();
				if (candidate.Window != window)
				{
					this._candidates.Enqueue(candidate);
				}
			}
		}

		private void Cancel(Lifetime lifetime)
		{
			if (lifetime.Admitted)
			{
				lifetime.Admitted = false;
				this._admitted--;
			}
		}

		private void PauseUnderLock(string reason)
		{
			this.ClearUnderLock();
			this._pauseReason = reason;
			this._state = PlacementMonitorState.Paused;
		}

		private void ClearUnderLock()
		{
			this._head = this._count = this._admitted = 0;
			this._windows.Clear();
			this._candidates.Clear();
		}

		private sealed class Lifetime
		{
			internal long Id;
			internal bool Completed, Admitted, Ambiguous;
		}
	}
}
