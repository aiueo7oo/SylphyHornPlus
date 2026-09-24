using System;
using System.Collections.Generic;
using System.Threading;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Services.AppPlacement
{
	internal enum PlacementOutcome
	{
		Moved,
		AlreadyPlaced,
		NoRule,
		Excluded,
		Changed,
		Cancelled,
		TimedOut,
		Unavailable,
		DestinationUnavailable,
		MoveFailed,
		Unconfirmed,
		MonitorPaused
	}

	internal sealed class PlacementResult
	{
		internal PlacementResult(IntPtr window, Guid? rule, PlacementOutcome outcome, string reason)
		{
			this.Window = window;
			this.Rule = rule;
			this.Outcome = outcome;
			this.Reason = reason;
			this.Time = DateTimeOffset.Now;
		}

		internal IntPtr Window { get; }

		internal Guid? Rule { get; }

		internal PlacementOutcome Outcome { get; }

		internal string Reason { get; }

		internal DateTimeOffset Time { get; }
	}

	internal sealed class PlacementHistory
	{
		private readonly object _gate = new object();
		private readonly Queue<PlacementResult> _results = new Queue<PlacementResult>();

		internal void Add(PlacementResult result)
		{
			lock (this._gate)
			{
				if (this._results.Count == 200) this._results.Dequeue();
				this._results.Enqueue(result);
			}
		}

		internal PlacementResult[] Snapshot()
		{
			lock (this._gate) return this._results.ToArray();
		}
	}

	// Cancellation never waits for native COM. Once started, the session's completion is the join point.
	internal sealed class PlacementMovePermit
	{
		private int _state;

		internal bool TryStart() => Interlocked.CompareExchange(ref this._state, 1, 0) == 0;

		internal void Cancel() => Interlocked.CompareExchange(ref this._state, 2, 0);
	}

	internal sealed class PlacementAuthorization
	{
		internal PlacementAuthorization(PlacementResolution resolution, PlacementMovePermit permit = null)
		{
			this.Resolution = resolution;
			this.Permit = permit;
		}

		internal PlacementResolution Resolution { get; }

		internal PlacementMovePermit Permit { get; }
	}

	internal sealed class PlacementWindowLocation
	{
		internal PlacementWindowLocation(Guid desktop, bool pinned)
		{
			this.Desktop = desktop;
			this.Pinned = pinned;
		}

		internal Guid Desktop { get; }

		internal bool Pinned { get; }
	}

	internal enum PlacementMoveStatus
	{
		Requested,
		Cancelled,
		Changed,
		Pinned,
		AlreadyPlaced,
		MissingDestination
	}

	internal interface IPlacementWindows
	{
		PlacementWindowInspection Inspect(IntPtr window);

		PlacementWindowLocation Locate(IntPtr window);

		PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
			PlacementMovePermit permit, Func<bool> stillCurrent, Action beforeMove = null);
	}

	internal interface IPlacementForeground
	{
		Action PrepareFollow(PlacementWindowIdentity identity, Guid source, Guid target, Func<bool> current);
	}

	internal sealed class PlacementWorkItem
	{
		internal PlacementWorkItem(PlacementCandidate candidate)
		{
			this.Candidate = candidate;
			this.Deadline = candidate.ObservedAt + 5000;
		}

		internal PlacementCandidate Candidate { get; }

		internal long Deadline { get; }

		internal long NextAt { get; set; }

		internal int Attempts { get; set; }

		internal PlacementWindowIdentity Identity { get; set; }

		internal AppPlacementRule Rule { get; set; }

		internal Guid? Source { get; set; }

		internal Guid? Target { get; set; }

		internal Guid? ExpectedTarget { get; set; }

		internal Action Follow { get; set; }

		internal bool Requested { get; set; }

		internal bool MoveAttempted { get; set; }

		internal PlacementResult Result { get; set; }

		internal void Finish(PlacementOutcome outcome, string reason = null) => this.Result = new PlacementResult(this.Candidate.Window, this.Rule?.Id, outcome, reason);
	}

	/// <summary>One bounded attempt. Scheduling belongs to the session; there are no sleeps or per-event tasks here.</summary>
	internal sealed class PlacementProcessor
	{
		private readonly AppPlacementConfiguration _configuration;
		private readonly IPlacementWindows _windows;
		private readonly Func<PlacementDestination, long, PlacementAuthorization> _authorize;
		private readonly Func<PlacementCandidate, bool> _current;
		private readonly Func<long> _now;
		private readonly CancellationToken _cancellation;
		private readonly bool _automatic;

		internal PlacementProcessor(
			AppPlacementConfiguration configuration,
			IPlacementWindows windows,
			Func<PlacementDestination, long, PlacementAuthorization> authorize,
			Func<PlacementCandidate, bool> current,
			Func<long> now,
			CancellationToken cancellation = default, bool automatic = false)
		{
			this._configuration = configuration;
			this._windows = windows;
			this._authorize = authorize;
			this._current = current;
			this._now = now;
			this._cancellation = cancellation;
			this._automatic = automatic;
		}

		internal void Step(PlacementWorkItem work)
		{
			if (work.Result != null) return;
			try
			{
				if (!this._current(work.Candidate))
				{
					work.Finish(work.Requested ? PlacementOutcome.Unconfirmed : PlacementOutcome.Cancelled);
					return;
				}
				if (this._now() >= work.Deadline)
				{
					work.Finish(work.Requested ? PlacementOutcome.Unconfirmed : PlacementOutcome.TimedOut);
					return;
				}
				if (work.Requested)
				{
					this.Verify(work);
					return;
				}
				work.Attempts++;
				var inspection = this._windows.Inspect(work.Candidate.Window);
				if (inspection.Status == PlacementInspectionStatus.Excluded)
				{
					work.Finish(PlacementOutcome.Excluded, inspection.Reason);
					return;
				}
				if (inspection.Status == PlacementInspectionStatus.Unavailable)
				{
					work.Finish(PlacementOutcome.Unavailable, inspection.Reason);
					return;
				}
				if (inspection.Identity == null)
				{
					this.Retry(work);
					return;
				}
				if (work.Identity != null && !work.Identity.SameInstance(inspection.Identity))
				{
					work.Finish(PlacementOutcome.Changed, "IdentityChanged");
					return;
				}
				work.Identity = inspection.Identity;
				work.Rule = this._configuration.FindEnabledRule(work.Identity.App);
				if (work.Rule == null)
				{
					work.Finish(PlacementOutcome.NoRule);
					return;
				}
				var location = this._windows.Locate(work.Candidate.Window);
				if (location == null)
				{
					this.Retry(work);
					return;
				}
				if (location.Pinned)
				{
					work.Finish(PlacementOutcome.Excluded, "Pinned");
					return;
				}
				if (work.Source.HasValue && work.Source.Value != location.Desktop)
				{
					work.Finish(PlacementOutcome.Changed, "DesktopChanged");
					return;
				}
				work.Source = location.Desktop;
				if (inspection.Status == PlacementInspectionStatus.NotReady)
				{
					this.Retry(work);
					return;
				}
				var authorization = this._authorize(work.Rule.Destination, work.Deadline);
				if (authorization.Resolution.Status != PlacementResolutionStatus.Resolved)
				{
					work.Finish(PlacementOutcome.DestinationUnavailable, authorization.Resolution.Status.ToString());
					return;
				}
				work.Target = authorization.Resolution.DesktopId;
				if (work.ExpectedTarget.HasValue && work.ExpectedTarget != work.Target)
				{
					authorization.Permit?.Cancel();
					work.Finish(PlacementOutcome.Changed, "DestinationChanged");
					return;
				}
				if (!this._current(work.Candidate) || this._now() >= work.Deadline)
				{
					authorization.Permit?.Cancel();
					work.Finish(this._now() >= work.Deadline ? PlacementOutcome.TimedOut : PlacementOutcome.Cancelled);
					return;
				}
				work.MoveAttempted = true;
				PlacementMoveStatus moved;
				using (this._cancellation.Register(() => authorization.Permit?.Cancel()))
					moved = this._windows.Move(
						work.Identity,
						work.Source.Value,
						work.Target.Value,
						authorization.Permit,
						() => !this._cancellation.IsCancellationRequested && this._current(work.Candidate) && this._now() < work.Deadline,
						() =>
						{
							if (this._automatic && (work.Rule.FollowForeground ?? this._configuration.FollowForeground)
								&& this._windows is IPlacementForeground foreground)
								work.Follow = foreground.PrepareFollow(work.Identity, work.Source.Value, work.Target.Value,
									() => !this._cancellation.IsCancellationRequested && this._current(work.Candidate) && this._now() < work.Deadline);
						});
				switch (moved)
				{
					case PlacementMoveStatus.Requested:
						work.Requested = true;
						this.Verify(work);
						break;
					case PlacementMoveStatus.AlreadyPlaced:
						work.Finish(PlacementOutcome.AlreadyPlaced);
						break;
					case PlacementMoveStatus.Pinned:
						work.Finish(PlacementOutcome.Excluded, "Pinned");
						break;
					case PlacementMoveStatus.Changed:
						work.Finish(PlacementOutcome.Changed);
						break;
					case PlacementMoveStatus.MissingDestination:
						work.Finish(PlacementOutcome.DestinationUnavailable, "Missing");
						break;
					default:
						work.Finish(PlacementOutcome.Cancelled);
						break;
				}
			}
			catch (OperationCanceledException)
			{
				work.Finish(work.Requested ? PlacementOutcome.Unconfirmed : this._now() >= work.Deadline ? PlacementOutcome.TimedOut : PlacementOutcome.Cancelled);
			}
			catch (Exception ex)
			{
				work.Finish(
					work.Requested ? PlacementOutcome.Unconfirmed : work.MoveAttempted ? PlacementOutcome.MoveFailed : PlacementOutcome.Unavailable,
					ex.GetType().Name + ":" + ex.HResult.ToString("X8"));
			}
		}

		private void Retry(PlacementWorkItem work)
		{
			if (work.Attempts >= 5 || this._now() >= work.Deadline) work.Finish(PlacementOutcome.TimedOut);
			else work.NextAt = Math.Min(work.Deadline, this._now() + (250L << (work.Attempts - 1)));
		}

		private void Verify(PlacementWorkItem work)
		{
			if (!this._current(work.Candidate) || this._now() >= work.Deadline)
			{
				work.Finish(PlacementOutcome.Unconfirmed);
				return;
			}
			var inspection = this._windows.Inspect(work.Candidate.Window);
			if (inspection.Status != PlacementInspectionStatus.Ready)
			{
				work.Finish(PlacementOutcome.Unconfirmed, inspection.Reason ?? "InspectionUnavailable");
				return;
			}
			if (inspection.Identity == null || !work.Identity.SameInstance(inspection.Identity))
			{
				work.Finish(PlacementOutcome.Unconfirmed, "IdentityChanged");
				return;
			}
			var location = this._windows.Locate(work.Candidate.Window);
			if (location != null && !location.Pinned && location.Desktop == work.Target)
			{
				// A failed follow must not turn a confirmed move into a failed placement or resend it.
				try
				{
					work.Follow?.Invoke();
					work.Finish(PlacementOutcome.Moved);
				}
				catch (Exception ex) { work.Finish(PlacementOutcome.Moved, "FollowFailed:" + ex.GetType().Name); }
			}
			else if (location != null && (location.Pinned || (location.Desktop != work.Source && location.Desktop != work.Target))) work.Finish(PlacementOutcome.Unconfirmed, "DesktopChanged");
			else work.NextAt = Math.Min(work.Deadline, this._now() + 100);
		}
	}
}
