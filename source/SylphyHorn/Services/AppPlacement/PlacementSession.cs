using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using static SylphyHorn.Services.AppPlacement.PlacementNativeMethods;

namespace SylphyHorn.Services.AppPlacement
{
	internal sealed class PlacementRequestRejectedException : InvalidOperationException
	{
		internal PlacementRequestRejectedException()
			: base("Placement is unavailable or another request is pending.") { }
	}

	internal interface IPlacementSession
	{
		bool IsReady { get; }

		Task Completion { get; }

		Task StopAsync();

		void DesktopChanged();

		Task<PlacementPreview> PreviewAsync(PlacementDesktopMap map, CancellationToken cancellation);

		Task<PlacementResult[]> ApplyAsync(PlacementPreview preview, Guid[] selection, CancellationToken cancellation);

		Task<PlacementRuleApplication> ApplyRulesAsync(PlacementDesktopMap map, PlacementAppIdentity app, bool dryRun, CancellationToken cancellation);
	}

	internal interface IPlacementSessionFactory
	{
		IPlacementSession Start(
			AppPlacementConfiguration configuration,
			Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
			PlacementHistory history,
			Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null);
	}

	internal sealed class PlacementSessionFactory : IPlacementSessionFactory
	{
		public IPlacementSession Start(
			AppPlacementConfiguration configuration,
			Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
			PlacementHistory history,
			Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null)
			=> new PlacementSession(configuration, authorize, history, closeDesktops);
	}

	/// <summary>One hook and one serial STA worker per enabled configuration. Completion joins both.</summary>
	internal sealed class PlacementSession : IPlacementSession
	{
		private const int CandidateLimit = 256;
		private const int DrainBatchLimit = 32;
		private readonly object _gate = new object();
		private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
		private readonly ManualResetEvent _stop = new ManualResetEvent(false);
		private readonly AutoResetEvent _requestReady = new AutoResetEvent(false);
		private Request _request;
		private bool _requestRunning;
		private PlacementExplicitApplication _explicit;
		private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly AppPlacementConfiguration _configuration;
		private readonly Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> _authorize;
		private readonly PlacementHistory _history;
		private readonly Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> _closeDesktops;
		private PlacementWindowMonitor _monitor;
		private bool _ended;
		private long _closureTopologyVersion;
		private int _ready;

		public bool IsReady => Volatile.Read(ref this._ready) != 0;

		public Task Completion => this._completion.Task;

		internal PlacementSession(
			AppPlacementConfiguration configuration,
			Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
			PlacementHistory history,
			Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null)
		{
			this._configuration = configuration;
			this._authorize = authorize;
			this._history = history;
			this._closeDesktops = closeDesktops;
			var thread = new Thread(this.Run)
			{
				IsBackground = true,
				Name = "App placement worker"
			};
			thread.SetApartmentState(ApartmentState.STA);
			try
			{
				thread.Start();
			}
			catch
			{
				this._cancellation.Dispose();
				this._stop.Dispose();
				this._requestReady.Dispose();
				throw;
			}
		}

		public void DesktopChanged()
		{
			if (this._closeDesktops == null) return;
			lock (this._gate)
			{
				if (this._ended) return;
				Interlocked.Increment(ref this._closureTopologyVersion);
				this._requestReady.Set();
			}
		}

		public Task<PlacementPreview> PreviewAsync(PlacementDesktopMap map, CancellationToken cancellation)
			=> this.Submit(token => this._explicit.Preview(map, token), cancellation);

		public Task<PlacementResult[]> ApplyAsync(PlacementPreview preview, Guid[] selection, CancellationToken cancellation)
		{
			if (selection == null || selection.Length > PlacementExplicitApplication.SelectionLimit)
			{
				throw new ArgumentException("Selection is missing or exceeds the preview item limit.", nameof(selection));
			}
			var selected = (Guid[])selection.Clone();
			return this.Submit(
				token =>
				{
					var results = this._explicit.Apply(preview, selected, (destination, deadline) => this.Authorize(destination, deadline, token, false), token);
					foreach (var result in results)
					{
						this._history.Add(result);
					}
					return results;
				},
				cancellation);
		}

		public Task<PlacementRuleApplication> ApplyRulesAsync(PlacementDesktopMap map, PlacementAppIdentity app, bool dryRun, CancellationToken cancellation)
			=> this.Submit(token =>
			{
				var application = this._explicit.ApplyRules(map, app, dryRun,
					(destination, deadline) => this.Authorize(destination, deadline, token, false), token);
				foreach (var result in application.Results)
				{
					this._history.Add(result);
				}
				return application;
			}, cancellation);

		private Task<T> Submit<T>(Func<CancellationToken, T> action, CancellationToken cancellation)
		{
			lock (this._gate)
			{
				if (this._ended || !this.IsReady || this._cancellation.IsCancellationRequested || this._request != null || this._requestRunning)
				{
					throw new PlacementRequestRejectedException();
				}
				var request = new Request<T>(action, cancellation);
				this._request = request;
				this._requestReady.Set();
				return request.Completion.Task;
			}
		}

		private void ProcessRequest()
		{
			Request request;
			lock (this._gate)
			{
				request = this._request;
				this._request = null;
				this._requestRunning = request != null;
			}
			try
			{
				request?.Run(this._cancellation.Token);
			}
			finally
			{
				lock (this._gate) this._requestRunning = false;
				request?.Complete();
			}
		}

		private abstract class Request
		{
			internal abstract void Run(CancellationToken session);

			internal abstract void Cancel();

			internal abstract void Complete();
		}

		private sealed class Request<T> : Request
		{
			private readonly Func<CancellationToken, T> _action;
			private readonly CancellationToken _cancellation;
			private T _result;
			private Exception _failure;
			private bool _cancelled;
			internal readonly TaskCompletionSource<T> Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

			internal Request(Func<CancellationToken, T> action, CancellationToken cancellation)
			{
				this._action = action;
				this._cancellation = cancellation;
			}

			internal override void Run(CancellationToken session)
			{
				using (var linked = CancellationTokenSource.CreateLinkedTokenSource(session, this._cancellation))
				{
					try
					{
						linked.Token.ThrowIfCancellationRequested();
						this._result = this._action(linked.Token);
					}
					catch (OperationCanceledException)
					{
						this._cancelled = true;
					}
					catch (Exception ex)
					{
						this._failure = ex;
					}
				}
			}

			internal override void Cancel() => this.Completion.TrySetCanceled();

			internal override void Complete()
			{
				if (this._cancelled)
				{
					this.Cancel();
				}
				else if (this._failure != null)
				{
					this.Completion.TrySetException(this._failure);
				}
				else
				{
					this.Completion.TrySetResult(this._result);
				}
			}
		}

		public Task StopAsync()
		{
			lock (this._gate)
			{
				if (!this._ended)
				{
					this._cancellation.Cancel();
					this._stop.Set();
					this._monitor?.StopAsync();
				}
			}
			return this.Completion;
		}

		private bool IsCurrent(PlacementCandidate candidate)
		{
			this.DrainEvents();
			return !this._cancellation.IsCancellationRequested && this._monitor.Events.IsCurrent(candidate);
		}

		private bool IsExistingCurrent(PlacementCandidate candidate)
		{
			this.DrainEvents();
			return !this._cancellation.IsCancellationRequested && this._monitor.Events.IsExistingCurrent(candidate);
		}

		// Bound ingress work even under sustained activity. If continuity cannot be checked now, skip.
		private void DrainEvents()
		{
			for (var n = 0; n < DrainBatchLimit && this._monitor.Events.BufferedCount != 0; n++)
			{
				this._monitor.Events.ProcessBatch();
			}
		}

		private void Run()
		{
			Exception failure = null;
			var pending = new List<PlacementWorkItem>();
			try
			{
				if (!this.StartMonitor()) return;
				Volatile.Write(ref this._ready, 1);
				var windows = new PlacementWindows();
				this._explicit = new PlacementExplicitApplication(
					this._configuration,
					windows,
					() => this._monitor.Events.Existing(Now()),
					this.IsExistingCurrent,
					GetWindowTitle,
					Now,
					(delay, token) => WaitHandle.WaitAny(new[] { this._stop, this._monitor.Changed, token.WaitHandle }, delay));
				var processor = new PlacementProcessor(this._configuration, windows,
					this.Authorize, this.IsCurrent, Now, this._cancellation.Token, automatic: true);
				var handles = new[] { this._stop, this._monitor.Changed, this._requestReady };
				var closure = this._closeDesktops == null ? null : new DesktopClosureCheck(this);
				while (!this._cancellation.IsCancellationRequested)
				{
					this.DrainEvents();
					if (this._monitor.Events.State != PlacementMonitorState.Running) break;
					this.ProcessRequest();
					var capacityReached = this.TakeCandidates(pending);
					var delay = this.StepPending(pending, processor, closure);
					if (this._monitor.Events.State != PlacementMonitorState.Running) break;
					if (this._monitor.Events.BufferedCount != 0 || (capacityReached && pending.Count < CandidateLimit))
					{
						delay = 0;
					}
					if (closure != null)
					{
						delay = MergeDelay(delay, closure.Check(pending.Count == 0));
					}
					WaitHandle.WaitAny(handles, delay);
				}
			}
			catch (OperationCanceledException) when (this._cancellation.IsCancellationRequested) { }
			catch (Exception ex)
			{
				failure = ex;
			}
			finally
			{
				foreach (var work in pending)
				{
					work.FinishIncomplete(PlacementOutcome.Cancelled);
					this._history.Add(work.Result);
				}
				var stopFailure = this.StopMonitor();
				failure = failure ?? stopFailure;
				lock (this._gate)
				{
					this._ended = true;
					this._request?.Cancel();
					this._request = null;
					this._cancellation.Dispose();
					this._stop.Dispose();
					this._requestReady.Dispose();
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

		// False when the session stopped first or the monitor could not capture its baseline.
		private bool StartMonitor()
		{
			lock (this._gate)
			{
				if (this._cancellation.IsCancellationRequested) return false;
				this._monitor = PlacementWindowMonitor.Start(candidateLimit: CandidateLimit);
			}
			return this._monitor.Ready.GetAwaiter().GetResult();
		}

		// Returns whether the worker list was full, so freed slots can be refilled without waiting.
		private bool TakeCandidates(List<PlacementWorkItem> pending)
		{
			PlacementCandidate candidate;
			// Destroyed, already handed-out candidates may still be awaiting their next retry.
			// Cap the worker list separately from the tracker's live admission count.
			while (pending.Count < CandidateLimit && (candidate = this._monitor.Events.TakeCandidate()) != null)
			{
				pending.Add(new PlacementWorkItem(candidate));
			}
			return pending.Count == CandidateLimit;
		}

		// Steps every due or invalidated item and returns the delay until the earliest remaining retry.
		private int StepPending(List<PlacementWorkItem> pending, PlacementProcessor processor, DesktopClosureCheck closure)
		{
			var delay = Timeout.Infinite;
			for (var i = pending.Count - 1; i >= 0 && !this._cancellation.IsCancellationRequested; i--)
			{
				var work = pending[i];
				if (work.NextAt <= Now() || !this._monitor.Events.IsCurrent(work.Candidate))
				{
					processor.Step(work);
				}
				if (work.Result != null)
				{
					this.Complete(work, closure);
					pending.RemoveAt(i);
				}
				else
				{
					delay = MergeDelay(delay, (int)Math.Max(0, work.NextAt - Now()));
				}
			}
			return delay;
		}

		private void Complete(PlacementWorkItem work, DesktopClosureCheck closure)
		{
			if (work.Target.HasValue && (work.Result.Outcome == PlacementOutcome.Moved || work.Result.Outcome == PlacementOutcome.AlreadyPlaced))
			{
				closure?.MarkUsed(work.Target.Value);
			}
			if (work.Result.Outcome != PlacementOutcome.NoRule)
			{
				this._history.Add(work.Result);
			}
			this._monitor.Events.Complete(work.Candidate);
		}

		// Timeout.Infinite (-1) means no wake-up is scheduled. Otherwise keep the earliest wake-up.
		private static int MergeDelay(int delay, int next)
		{
			if (next < 0) return delay;
			return delay < 0 ? next : Math.Min(delay, next);
		}

		private Exception StopMonitor()
		{
			if (this._monitor == null) return null;
			if (this._monitor.Events.State == PlacementMonitorState.Paused)
			{
				this._history.Add(new PlacementResult(IntPtr.Zero, null, PlacementOutcome.MonitorPaused, this._monitor.Events.PauseReason));
			}
			Exception failure = null;
			try
			{
				this._monitor.StopAsync().GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			this._monitor.Dispose();
			return failure;
		}

		private static long Now() => checked((long)GetTickCount64());

		private PlacementAuthorization Authorize(PlacementDestination destination, long deadline)
			=> this.Authorize(destination, deadline, this._cancellation.Token);

		private PlacementAuthorization Authorize(PlacementDestination destination, long deadline, CancellationToken request, bool allowCreation = true)
		{
			using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this._cancellation.Token, request))
			{
				cancellation.CancelAfter((int)Math.Max(1, deadline - Now()));
				return this._authorize(destination, allowCreation, cancellation.Token).GetAwaiter().GetResult();
			}
		}

		/// <summary>Worker-thread state for offering desktops that stayed empty to the closure callback.</summary>
		private sealed class DesktopClosureCheck
		{
			// Coalesce a burst of native events before inspecting occupancy.
			private const long SettleMilliseconds = 100;
			private const long MinimumRecheckMilliseconds = 50;

			private readonly PlacementSession _session;
			private readonly PlacementDesktopOccupancyReader _occupancy = new PlacementDesktopOccupancyReader();
			// Targets that received, or already held, an automatically placed window.
			private readonly HashSet<Guid> _usedDesktops = new HashSet<Guid>();
			private long _observedVersion = -1;
			private long _observedTopology = -1;
			private long _nextCheck = long.MaxValue;

			internal DesktopClosureCheck(PlacementSession session)
			{
				this._session = session;
			}

			internal void MarkUsed(Guid desktop) => this._usedDesktops.Add(desktop);

			// Offers occupancy to the callback when due and the worker is idle; returns the delay until the next check.
			internal int Check(bool idle)
			{
				var session = this._session;
				var version = session._monitor.Events.Version;
				var topology = Interlocked.Read(ref session._closureTopologyVersion);
				if (version != this._observedVersion || topology != this._observedTopology)
				{
					this._observedVersion = version;
					this._observedTopology = topology;
					this._nextCheck = Now() + SettleMilliseconds;
				}
				if (idle && this._nextCheck <= Now())
				{
					bool Current() => !session._cancellation.IsCancellationRequested
						&& session._monitor.Events.State == PlacementMonitorState.Running && session._monitor.Events.Version == version
						&& Interlocked.Read(ref session._closureTopologyVersion) == topology;
					var observed = this._occupancy.Read(session._cancellation.Token, Current);
					var observation = new PlacementOccupancyObservation(observed.Complete, observed.Occupied, Current, this._usedDesktops);
					var retry = session._closeDesktops(observation, session._cancellation.Token).GetAwaiter().GetResult();
					this._nextCheck = retry ? Now() + PlacementDesktopClosure.GraceMilliseconds : long.MaxValue;
				}
				if (this._nextCheck == long.MaxValue) return Timeout.Infinite;
				return (int)Math.Max(MinimumRecheckMilliseconds, this._nextCheck - Now());
			}
		}
	}
}
