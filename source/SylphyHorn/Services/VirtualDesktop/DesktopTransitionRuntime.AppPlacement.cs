using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		private readonly IPlacementSessionFactory _placementFactory;
		private readonly PlacementHistory _placementHistory = new PlacementHistory();
		private AppPlacementConfiguration _placementConfiguration = AppPlacementConfiguration.Empty;
		private IPlacementSession _placementSession;
		private PlacementMovePermit _placementPermit;
		private long _placementGeneration;
		private bool _placementSuspended;
		private bool _placementChanging;

		internal PlacementResult[] PlacementResults => this._placementHistory.Snapshot();

		internal string PlacementStatus
		{
			get
			{
				this.EnsureOwnerAccess();
				if (this._placementChanging) return "Stopping";
				if (this._shutdownStarted || this._stopping || this._placementSuspended) return "Suspended";
				if (!this._placementConfiguration.Enabled) return "Disabled";
				if (!this._placementConfiguration.Rules.Any(rule => rule.Enabled) && !this._placementConfiguration.HasClosingTargets) return "NoRules";
				if (this._placementSession == null || this._placementSession.Completion.IsCompleted) return "Paused";
				return this._placementSession.IsReady ? "Active" : "Preparing";
			}
		}

		internal Task RestartPlacementAsync()
		{
			this.EnsureOwnerAccess();
			return this.ReconcilePlacementAsync();
		}

		internal Task<PlacementPreview> PreviewExistingPlacementAsync(CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			this.EnsurePlacementAvailable();
			return this._placementSession.PreviewAsync(this.PlacementDestinations, cancellation);
		}

		internal Task<PlacementResult[]> ApplyExistingPlacementAsync(PlacementPreview preview, Guid[] selection, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			this.EnsurePlacementAvailable();
			return this._placementSession.ApplyAsync(preview, selection, cancellation);
		}

		private void EnsurePlacementAvailable()
		{
			if (this.PlacementStatus != "Active") throw new InvalidOperationException("Placement is not monitoring.");
		}

		private readonly object _placementAuthorizationGate = new object();
		private readonly SemaphoreSlim _placementCreationGate = new SemaphoreSlim(1, 1);
		private PlacementAuthorizationRequest _placementAuthorizationPending;
		private bool _placementAuthorizationPosted;

		internal Task ConfigurePlacementAsync(AppPlacementConfiguration configuration)
		{
			this.EnsureOwnerAccess();
			if (configuration == null) throw new ArgumentNullException(nameof(configuration));
			if (this._shutdownStarted || this._stopping || ReferenceEquals(configuration, this._placementConfiguration)) return Task.CompletedTask;
			this._placementConfiguration = configuration;
			return this.ReconcilePlacementAsync();
		}

		private async Task ReconcilePlacementAsync()
		{
			var generation = ++this._placementGeneration;
			this._placementChanging = true;
			this._desktopClosure.Reset();
			this._closingArmed.Clear();
			try
			{
				this._placementPermit?.Cancel();
				this._placementPermit = null;
				var previous = this._placementSession;
				if (previous != null)
				{
					// Stop cancels queued authorizations synchronously. Awaiting joins any already-started COM call.
					try
					{
						await previous.StopAsync();
					}
					catch { /* ObservePlacementAsync reports the failure once. Completion still joins the worker. */ }
					if (ReferenceEquals(previous, this._placementSession)) this._placementSession = null;
				}
				if (generation != this._placementGeneration || !this._initialized || this._shutdownStarted || this._stopping
					|| this._placementSuspended || !this._placementConfiguration.Enabled
					|| (!this._placementConfiguration.Rules.Any(rule => rule.Enabled) && !this._placementConfiguration.HasClosingTargets)) return;
				try
				{
					Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null;
					if (this._placementConfiguration.HasClosingTargets)
					{
						closeDesktops = (observation, cancellation) => this.ObserveDesktopClosureAsync(generation, observation, cancellation);
					}

					this._placementSession = this._placementFactory.Start(
						this._placementConfiguration,
						(destination, allowCreation, cancellation) => this.AuthorizePlacementAsync(generation, destination, allowCreation, cancellation),
						this._placementHistory,
						closeDesktops);
					_ = this.ObservePlacementAsync(this._placementSession);
				}
				catch (Exception ex)
				{
					this.ReportFault(new DesktopRuntimeFault("AppPlacement.Start", ex.GetType()));
				}
			}
			finally
			{
				if (generation == this._placementGeneration) this._placementChanging = false;
			}
		}

		private async Task ObservePlacementAsync(IPlacementSession session)
		{
			try
			{
				await session.Completion;
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("AppPlacement.Worker", ex.GetType()));
			}
			// Do not auto-restart after loss of event continuity. A later configuration starts a fresh baseline.
		}

		private async Task<PlacementAuthorization> AuthorizePlacementAsync(long generation, PlacementDestination destination, bool allowCreation, CancellationToken cancellation)
		{
			var completion = new TaskCompletionSource<PlacementAuthorization>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellation.Register(() => completion.TrySetCanceled()))
			{
				lock (this._placementAuthorizationGate)
				{
					if (!completion.Task.IsCompleted)
					{
						// One mailbox/posted callback even if an unresponsive owner outlives several deadlines.
						this._placementAuthorizationPending?.Completion.TrySetCanceled();
						this._placementAuthorizationPending = new PlacementAuthorizationRequest(generation, destination, completion, cancellation, allowCreation);
						if (!this._placementAuthorizationPosted)
						{
							this._placementAuthorizationPosted = true;
							try
							{
								if (!this._owner.Post(this.DrainPlacementAuthorization))
								{
									this._placementAuthorizationPosted = false;
									this._placementAuthorizationPending = null;
									completion.TrySetCanceled();
								}
							}
							catch (Exception ex)
							{
								this._placementAuthorizationPosted = false;
								this._placementAuthorizationPending = null;
								completion.TrySetException(ex);
							}
						}
					}
				}
				return await completion.Task.ConfigureAwait(false);
			}
		}

		private void DrainPlacementAuthorization()
		{
			PlacementAuthorizationRequest request;
			lock (this._placementAuthorizationGate)
			{
				request = this._placementAuthorizationPending;
				this._placementAuthorizationPending = null;
				this._placementAuthorizationPosted = false;
			}
			if (request == null || request.Completion.Task.IsCompleted) return;
			try
			{
				if (request.Generation != this._placementGeneration)
				{
					request.Completion.TrySetResult(new PlacementAuthorization(PlacementDesktopMap.Unavailable.Resolve(request.Destination)));
					return;
				}
				var resolution = this.PlacementDestinations.Resolve(request.Destination);
				if (resolution.Status == PlacementResolutionStatus.Missing && request.AllowCreation && this._placementConfiguration.CreateMissingDesktops)
				{
					_ = this.CreatePlacementDestinationAsync(request);
					return;
				}
				this.CompletePlacementAuthorization(request, resolution);
			}
			catch (Exception ex)
			{
				request.Completion.TrySetException(ex);
			}
		}

		private void CompletePlacementAuthorization(PlacementAuthorizationRequest request, PlacementResolution resolution)
		{
			if (resolution.Status == PlacementResolutionStatus.Resolved && request.CreatedGroup != null)
			{
				this._createdGroups.Remove(request.CreatedGroup);
				request.CreatedGroup = new PlacementCreatedGroup(request.CreatedGroup.Desktops, true);
				this._createdGroups.Add(request.CreatedGroup);
				this.SaveCreatedDesktopGroups();
			}
			this._placementPermit?.Cancel();
			var permit = resolution.Status == PlacementResolutionStatus.Resolved ? new PlacementMovePermit() : null;
			this._placementPermit = permit;
			if (!request.Completion.TrySetResult(new PlacementAuthorization(resolution, permit))) permit?.Cancel();
		}

		private async Task CreatePlacementDestinationAsync(PlacementAuthorizationRequest request)
		{
			var entered = false;
			try
			{
				await this._placementCreationGate.WaitAsync(request.Cancellation).ConfigureAwait(false);
				entered = true;
				var remaining = 0;
				await this.OnPlacementOwnerAsync(request, () =>
				{
					remaining = request.Destination.Kind == PlacementDestinationKind.Name
						? 1 : Math.Max(0, request.Destination.Number - this.State.Order.Count);
				}).ConfigureAwait(false);
				while (true)
				{
					Task<VirtualDesktopReconciliationResult> refresh = null;
					Guid created = Guid.Empty;
					await this.OnPlacementOwnerAsync(request, () =>
					{
						var resolution = this.PlacementDestinations.Resolve(request.Destination);
						if (resolution.Status != PlacementResolutionStatus.Missing || remaining == 0)
						{
							this.CompletePlacementAuthorization(request, resolution);
							return;
						}
						// Bound this request to its initial deficit, even if another actor removes desktops.
						remaining--;
						created = this._operations.Create();
						this.RecordPlacementCreatedDesktop(request, created);
						this.CheckPlacementCreation(request);
						if (request.Destination.Kind == PlacementDestinationKind.Name)
							this._operations.SetName(created, request.Destination.Name);
						this.CheckPlacementCreation(request);
						refresh = this.RequestProviderWithBudgetAsync(VirtualDesktopStableReason.ExplicitReconciliation, request.Cancellation);
					}).ConfigureAwait(false);
					if (refresh == null) return;
					var result = await refresh.ConfigureAwait(false);
					await this.OnPlacementOwnerAsync(request, () =>
					{
						if (result.Status != VirtualDesktopReconciliationStatus.Succeeded)
							throw new InvalidOperationException("The created desktop could not be reconciled.");
						this.ApplyStableBatch(result.Batch);
						if (!this.State.Records.ContainsKey(created))
							throw new InvalidOperationException("The created desktop is no longer available.");
					}).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				request.Completion.TrySetCanceled();
			}
			catch (Exception ex)
			{
				request.Completion.TrySetException(ex);
			}
			finally
			{
				if (entered) this._placementCreationGate.Release();
			}
		}

		private void CheckPlacementCreation(PlacementAuthorizationRequest request)
		{
			this.EnsureOwnerAccess();
			request.Cancellation.ThrowIfCancellationRequested();
			if (request.Completion.Task.IsCompleted || request.Generation != this._placementGeneration
				|| this._shutdownStarted || this._stopping || this._placementSuspended || this._placementChanging
				|| !this._placementConfiguration.Enabled || !this._placementConfiguration.CreateMissingDesktops)
				throw new OperationCanceledException();
		}

		private async Task OnPlacementOwnerAsync(PlacementAuthorizationRequest request, Action action)
		{
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (request.Cancellation.Register(() => completion.TrySetCanceled()))
			{
				void Invoke()
				{
					try
					{
						this.CheckPlacementCreation(request);
						action();
						completion.TrySetResult(true);
					}
					catch (Exception ex) { completion.TrySetException(ex); }
				}
				if (this._owner.CheckAccess()) Invoke();
				else if (!this._owner.Post(Invoke)) completion.TrySetCanceled();
				await completion.Task.ConfigureAwait(false);
			}
		}

		private sealed class PlacementAuthorizationRequest
		{
			internal PlacementAuthorizationRequest(long generation, PlacementDestination destination,
				TaskCompletionSource<PlacementAuthorization> completion, CancellationToken cancellation, bool allowCreation)
			{
				this.Generation = generation;
				this.Destination = destination;
				this.Completion = completion;
				this.Cancellation = cancellation;
				this.AllowCreation = allowCreation;
			}

			internal long Generation { get; }

			internal PlacementDestination Destination { get; }

			internal TaskCompletionSource<PlacementAuthorization> Completion { get; }

			internal CancellationToken Cancellation { get; }

			internal bool AllowCreation { get; }

			internal PlacementCreatedGroup CreatedGroup { get; set; }
		}

		private async Task<SettingsImportCommitResult> CommitImportWithPlacementSuspendedAsync(StagedSettingsImport stage,
			bool overrideDesktops, CancellationToken cancellationToken, bool resetPositions)
		{
			this._placementSuspended = true;
			try
			{
				await this.ReconcilePlacementAsync();
				return await this.CommitPreparedImportCoreAsync(stage, overrideDesktops, cancellationToken, resetPositions);
			}
			finally
			{
				this._placementSuspended = false;
				this._createdGroupsLoaded = false;
				await this.ReconcilePlacementAsync();
				this.ScheduleDeferredCommands();
			}
		}
	}
}
