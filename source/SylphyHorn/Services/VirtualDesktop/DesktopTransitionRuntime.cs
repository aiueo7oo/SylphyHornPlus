using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed class DesktopPersistenceProtection
	{
		private readonly IReadOnlyDictionary<DesktopProtectionKey, DesktopProtectedValue> _values;
		private DesktopPersistenceProtection(IReadOnlyDictionary<DesktopProtectionKey, DesktopProtectedValue> values) => this._values = values;

		internal static DesktopPersistenceProtection FromSeed(DesktopRuntimeState state, DesktopOperationJournal journal)
		{
			var values = new Dictionary<DesktopProtectionKey, DesktopProtectedValue>();
			foreach (var entry in journal.ProtectionEntries)
			{
				if (!entry.TargetValueSpecified || !state.Records.ContainsKey(entry.DesktopId)) continue;
				values[new DesktopProtectionKey(entry.DesktopId, entry.Property)] = DesktopProtectedValue.Known(entry.TargetValue);
			}
			return values.Count == 0 ? null : new DesktopPersistenceProtection(values);
		}

		internal static DesktopPersistenceProtection FromProjection(DesktopSettingsProjection projection, DesktopRuntimeState state, DesktopOperationJournal journal = null)
		{
			var values = new Dictionary<DesktopProtectionKey, DesktopProtectedValue>();
			var scoped = journal?.ImportProtectionEntries.ToArray();
			for (var index = 0; index < state.Order.Count; index++)
			{
				var id = state.Order[index];
				if (scoped == null || scoped.Any(entry => entry.DesktopId == id && entry.Property == DesktopPropertyKind.Name))
					values[new DesktopProtectionKey(id, DesktopPropertyKind.Name)] = DesktopProtectedValue.FromProjection(projection.Names[index]);
				if (scoped == null || scoped.Any(entry => entry.DesktopId == id && entry.Property == DesktopPropertyKind.WallpaperPath))
					values[new DesktopProtectionKey(id, DesktopPropertyKind.WallpaperPath)] = DesktopProtectedValue.FromProjection(projection.WallpaperPaths[index]);
			}
			return values.Count == 0 ? null : new DesktopPersistenceProtection(values);
		}

		internal DesktopSettingsProjection CreateProjection(DesktopRuntimeState state)
		{
			return new DesktopSettingsProjection(
				state.Order.Select(id => this.GetValue(id, DesktopPropertyKind.Name, state.Records[id].Name)),
				state.Order.Select(id => this.GetValue(id, DesktopPropertyKind.WallpaperPath, state.Records[id].WallpaperPath)),
				state.Order.Select(id => state.Records[id].WallpaperPosition));
		}

		internal DesktopPersistenceProtection Release(Guid desktopId, DesktopPropertyKind property)
		{
			var key = new DesktopProtectionKey(desktopId, property);
			if (!this._values.ContainsKey(key)) return this;
			var values = this._values.ToDictionary(pair => pair.Key, pair => pair.Value);
			values.Remove(key);
			return values.Count == 0 ? null : new DesktopPersistenceProtection(values);
		}

		internal DesktopPersistenceProtection ReleaseNaturalMatches(DesktopRuntimeState state)
		{
			var values = this._values.ToDictionary(pair => pair.Key, pair => pair.Value);
			foreach (var pair in this._values)
			{
				if (!state.Records.TryGetValue(pair.Key.DesktopId, out var record)) { values.Remove(pair.Key); continue; }
				var property = pair.Key.Property == DesktopPropertyKind.Name ? record.Name : record.WallpaperPath;
				if (pair.Value.Matches(property)) values.Remove(pair.Key);
			}
			return values.Count == 0 ? null : new DesktopPersistenceProtection(values);
		}

		private string GetValue(Guid id, DesktopPropertyKind property, DesktopPropertyState runtime)
		{
			return this._values.TryGetValue(new DesktopProtectionKey(id, property), out var value)
				? value.Value
				: runtime.HasValue ? runtime.Value : null;
		}

		private struct DesktopProtectionKey : IEquatable<DesktopProtectionKey>
		{
			internal DesktopProtectionKey(Guid desktopId, DesktopPropertyKind property) { this.DesktopId = desktopId; this.Property = property; }
			internal Guid DesktopId { get; }
			internal DesktopPropertyKind Property { get; }
			public bool Equals(DesktopProtectionKey other) => this.DesktopId == other.DesktopId && this.Property == other.Property;
			public override bool Equals(object obj) => obj is DesktopProtectionKey other && this.Equals(other);
			public override int GetHashCode() => (this.DesktopId.GetHashCode() * 397) ^ (int)this.Property;
		}

		private sealed class DesktopProtectedValue
		{
			private DesktopProtectedValue(bool hasValue, string value) { this.HasValue = hasValue; this.Value = value; }
			internal bool HasValue { get; }
			internal string Value { get; }
			internal static DesktopProtectedValue Known(string value) => new DesktopProtectedValue(true, value);
			internal static DesktopProtectedValue FromProjection(string value) => new DesktopProtectedValue(value != null, value);
			internal bool Matches(DesktopPropertyState runtime) => this.HasValue == runtime.HasValue && (!this.HasValue || this.Value == runtime.Value);
		}
	}

	internal sealed partial class DesktopTransitionRuntime : IDisposable, IDesktopStartupTransactionRuntime, IDesktopImportTransactionRuntime
	{
		// Owner-dispatcher orchestrator for the provider, pure coordinator, settings
		// projection/save transaction, and public StateChanged publication. It consumes
		// only stable batches/current-only transitions; raw capture remains provider-owned.
		//
		// Ordered flow:
		// provider publication -> owner Dispatcher -> coordinator transition -> settings
		// projection/save request -> immutable StateChanged publication.
		//
		// Invariants:
		// - Coordinator state has one owner and is changed only on the owner Dispatcher.
		// - Projection and save ordering follow the accepted transition; consumers do not
		//   reconstruct identity or infer missing provider state.
		// - Startup and import use prepared transactions: active settings/runtime state is
		//   exchanged only after validation and successful commit.
		// - Accepted operations and provider waits have explicit terminal arbitration;
		//   shutdown stops ingress, completes or classifies accepted work, performs its
		//   bounded final reconciliation, then detaches and disposes dependencies.
		private readonly IDesktopProviderClient _provider;
		private readonly IDesktopSettingsTransactions _settings;
		private readonly IDesktopOwnerContext _owner;
		private readonly IDesktopOperations _operations;
		private readonly DesktopStartupSeed _startupSeed;
		private readonly TimeSpan _providerWaitBudget;
		private readonly object _providerWaitGate = new object();
		private readonly List<ProviderWaitRace> _providerWaits = new List<ProviderWaitRace>();
		private DesktopPersistenceProtection _persistenceProtection;
		private readonly Queue<Action> _deferredCommands = new Queue<Action>();
		private DesktopTransitionCoordinator _coordinator;
		private DesktopTransitionCoordinator.DesktopPreparedRuntime _preparedRuntime;
		private DesktopPreparedImportSession _activeImportSession;
		private Task<SettingsImportCommitResult> _activeImportCommit;
		private Task<DesktopRuntimeShutdownResult> _shutdownTask;
		private bool _publishing;
		private bool _deferredDrainScheduled;
		private bool _providerEventsSubscribed;
		private bool _suppressPublication;
		private DesktopCoordinatorTransition _suppressedTransition;
		private bool _initialized;
		private bool _stopping;
		private bool _shutdownStarted;
		private int _disposeSignaled;
		private DesktopRuntimeState _placementProjectionSource;
		private PlacementDesktopMap _placementProjection;

		internal DesktopTransitionRuntime(
			IDesktopProviderClient provider,
			IDesktopSettingsTransactions settings,
			IDesktopOwnerContext owner,
			IDesktopOperations operations,
			TimeSpan? providerWaitBudget = null,
			IPlacementSessionFactory placementFactory = null,
			Func<long> closureClock = null)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
			this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
			this._owner = owner ?? throw new ArgumentNullException(nameof(owner));
			this._operations = operations ?? throw new ArgumentNullException(nameof(operations));
			this._startupSeed = settings.CaptureStartupSeed();
			this._providerWaitBudget = providerWaitBudget ?? TimeSpan.FromSeconds(30);
			this._coordinator = new DesktopTransitionCoordinator(this._startupSeed);
			this._placementFactory = placementFactory ?? new PlacementSessionFactory();
			this._closureClock = closureClock ?? MonotonicMilliseconds;
		}

		internal event EventHandler<DesktopRuntimeStateChanged> StateChanged;
		internal event EventHandler<DesktopRuntimeFault> Faulted;
		internal DesktopRuntimeState State => this._coordinator.State;
		internal bool IsInitialized => this._initialized;

		// Shutdown has started, or the runtime has stopped (also after a failed initialization).
		private bool IsShuttingDown => this._shutdownStarted || this._stopping;

		// A startup override or settings import is preparing desktop state that is not active yet.
		private bool IsTransactionPending => this._preparedRuntime != null || this._activeImportSession != null;

		private bool IsImportCommitActive => this._activeImportCommit != null && !this._activeImportCommit.IsCompleted;

		private bool IsPlacementHalted => this.IsShuttingDown || this._placementSuspended;

		private bool MustDeferDesktopCommands => this._publishing || this.IsTransactionPending || this._placementSuspended;

		private bool HasPendingDeferredCommands => this._deferredDrainScheduled || this._deferredCommands.Count != 0;

		internal PlacementDesktopMap PlacementDestinations
		{
			get
			{
				this.EnsureOwnerAccess();
				if (!this._initialized || this.IsPlacementHalted || this.IsTransactionPending || this.IsImportCommitActive)
				{
					return PlacementDesktopMap.Unavailable;
				}
				var state = this.State;
				if (!ReferenceEquals(state, this._placementProjectionSource))
				{
					this._placementProjection = new PlacementDesktopMap(state.Order.Select(id =>
					{
						var name = state.Records[id].Name;
						return new PlacementDesktop(id, name.Value, IsAvailableName(name));
					}));
					this._placementProjectionSource = state;
				}
				return this._placementProjection;
			}
		}

		// Only a confirmed provider value can identify a desktop by name.
		private static bool IsAvailableName(DesktopPropertyState name)
			=> name.HasValue && name.IsConfirmed && name.ReadStatus == VirtualDesktopReadStatus.Success;

		private static long MonotonicMilliseconds()
			=> (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));

		DesktopRuntimeState IDesktopStartupTransactionRuntime.State => this.State;
		Task<VirtualDesktopReconciliationResult> IDesktopStartupTransactionRuntime.RequestProviderAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken)
			=> this.RequestProviderWithBudgetAsync(reason, cancellationToken);
		DesktopRuntimeInitializationResult IDesktopStartupTransactionRuntime.CommitInitialReconciliation(VirtualDesktopReconciliationResult result, DesktopStartupPublicationMode publicationMode)
			=> this.CompleteInitialization(result, publicationMode);
		void IDesktopStartupTransactionRuntime.ApplyStableBatch(VirtualDesktopStableBatch batch)
			=> this.ApplyStableBatch(batch);
		bool IDesktopStartupTransactionRuntime.ExecuteTopologyPlan(DesktopStartupOverridePlan plan, DesktopOperationJournal journal)
			=> this.ExecuteTopologyPlan(plan, journal);
		DesktopTransitionCoordinator.DesktopPreparedRuntime IDesktopStartupTransactionRuntime.BeginPreparedRuntime()
		{
			this._preparedRuntime = this._coordinator.BeginStagedRuntime();
			return this._preparedRuntime;
		}
		void IDesktopStartupTransactionRuntime.ClearPreparedRuntime() => this._preparedRuntime = null;
		bool IDesktopStartupTransactionRuntime.ApplySeed(DesktopTransitionCoordinator.DesktopPreparedRuntime prepared, DesktopStartupSeed seed, DesktopOperationJournal journal)
			=> this.ApplySeedToPreparedRuntime(prepared, seed, true, journal);
		void IDesktopStartupTransactionRuntime.ApplyStableBatchToPrepared(VirtualDesktopStableBatch batch)
			=> this.ApplyStableBatchToPrepared(batch);
		void IDesktopStartupTransactionRuntime.ReportUnconfirmedOverride(Guid desktopId, DesktopPropertyKind property)
			=> this.ReportUnconfirmedOverride(desktopId, property);

		DesktopRuntimeState IDesktopImportTransactionRuntime.State => this.State;
		DesktopTransitionCoordinator.DesktopPreparedRuntime IDesktopImportTransactionRuntime.BeginPreparedRuntime()
			=> this._coordinator.BeginStagedRuntime();
		Task<VirtualDesktopReconciliationResult> IDesktopImportTransactionRuntime.RequestProviderAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken)
			=> this.RequestProviderWithBudgetAsync(reason, cancellationToken);
		bool IDesktopImportTransactionRuntime.ExecuteTopologyPlan(DesktopStartupOverridePlan plan, DesktopOperationJournal journal)
			=> this.ExecuteTopologyPlan(plan, journal);
		bool IDesktopImportTransactionRuntime.ApplySeed(DesktopTransitionCoordinator.DesktopPreparedRuntime prepared, DesktopStartupSeed seed, bool overrideDesktops, DesktopOperationJournal journal, bool resetPositions)
			=> this.ApplySeedToPreparedRuntime(prepared, seed, overrideDesktops, journal, resetPositions);
		bool IDesktopImportTransactionRuntime.CanCommitPreparedRuntime(DesktopTransitionCoordinator.DesktopPreparedRuntime prepared)
			=> this._coordinator.CanCommitStagedRuntime(prepared);
		void IDesktopImportTransactionRuntime.ReportUnconfirmedOverride(Guid desktopId, DesktopPropertyKind property)
			=> this.ReportUnconfirmedOverride(desktopId, property);
		void IDesktopImportTransactionRuntime.ReportFault(Type exceptionType)
			=> this.ReportFault(new DesktopRuntimeFault("SettingsTransaction", exceptionType));

		internal async Task<DesktopRuntimeInitializationResult> InitializeAsync(bool overrideDesktopsOnStartup = false, CancellationToken cancellationToken = default(CancellationToken))
		{
			this.EnsureOwnerAccess();
			if (this._initialized) return new DesktopRuntimeInitializationResult(DesktopRuntimeInitializationStatus.Completed);
			if (this._shutdownStarted || this._stopping) return new DesktopRuntimeInitializationResult(DesktopRuntimeInitializationStatus.ShuttingDown);

			var overrideEnabled = overrideDesktopsOnStartup && DesktopStartupOverridePlan.GetTargetCount(this._startupSeed) > 0;
			this._suppressPublication = overrideEnabled;
			this.SubscribeProviderEvents();
			try
			{
				var outcome = await new DesktopStartupTransaction(this._startupSeed, overrideEnabled, this)
					.ExecuteAsync(cancellationToken);
				switch (outcome.Kind)
				{
					case DesktopStartupTransactionOutcomeKind.AlreadyPublished:
						return outcome.Result;
					case DesktopStartupTransactionOutcomeKind.CommitPreparedAndPublish:
						this._coordinator.CommitStagedRuntime(outcome.PreparedRuntime, false);
						this._preparedRuntime = null;
						this._persistenceProtection = outcome.PersistenceProtection;
						this._suppressPublication = false;
						this.PublishInitialization();
						return outcome.Result;
					case DesktopStartupTransactionOutcomeKind.PublishRecovered:
						this._persistenceProtection = outcome.PersistenceProtection;
						this._suppressPublication = false;
						this.PublishInitialization();
						return outcome.Result;
					case DesktopStartupTransactionOutcomeKind.Terminate:
						this.TerminateFailedInitialization();
						return outcome.Result;
					default:
						throw new InvalidOperationException("Unknown startup transaction outcome.");
				}
			}
			finally
			{
				this._preparedRuntime = null;
				this._suppressPublication = false;
			}
		}

		private bool ExecuteTopologyPlan(DesktopStartupOverridePlan plan, DesktopOperationJournal journal)
		{
			var failed = false;
			for (var index = 0; index < plan.CreateCount; index++)
			{
				if (failed)
				{
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Create, null, DesktopOverrideOperationStatus.Skipped);
					continue;
				}
				try
				{
					this._creationWallpaperSkipped.Add(this._operations.Create());
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Create, null, DesktopOverrideOperationStatus.Succeeded);
				}
				catch
				{
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Create, null, DesktopOverrideOperationStatus.Failed);
					failed = true;
				}
			}
			foreach (var id in plan.RemoveIds)
			{
				if (failed)
				{
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Remove, id, DesktopOverrideOperationStatus.Skipped);
					continue;
				}
				try
				{
					this._operations.Remove(id);
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Remove, id, DesktopOverrideOperationStatus.Succeeded);
				}
				catch
				{
					journal.RecordTopology(DesktopStartupTopologyMutationKind.Remove, id, DesktopOverrideOperationStatus.Failed);
					failed = true;
				}
			}
			return !failed;
		}

		private async Task<VirtualDesktopReconciliationResult> RequestProviderWithBudgetAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested) return VirtualDesktopReconciliationResult.Cancelled();
			if (this._providerWaitBudget <= TimeSpan.Zero) return VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable);
			using (var providerCancellation = new CancellationTokenSource())
			using (var timeoutCancellation = new CancellationTokenSource())
			{
				var providerState = this._activeImportSession?.PreparedState ?? this._preparedRuntime?.Coordinator.State ?? this.State;
				var race = new ProviderWaitRace(providerCancellation, providerState?.ProviderEpoch ?? 0, providerState?.ProviderSnapshotRevision ?? 0);
				CancellationTokenRegistration callerCancellation = default(CancellationTokenRegistration);
				this.RegisterProviderWait(race);
				try
				{
					callerCancellation = cancellationToken.Register(race.CancelByCaller);
					_ = CompleteProviderWaitTimeoutAsync(race, this._providerWaitBudget, timeoutCancellation.Token);
					try
					{
						var request = this._provider.RequestReconciliationAsync(reason, providerCancellation.Token);
						_ = CompleteProviderWaitRequestAsync(race, request);
					}
					catch (OperationCanceledException)
					{
						race.TryComplete(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable), false);
					}
					catch
					{
						race.TryComplete(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable), false);
					}
					return await race.Completion;
				}
				finally
				{
					timeoutCancellation.Cancel();
					callerCancellation.Dispose();
					this.UnregisterProviderWait(race);
				}
			}
		}

		private static async Task CompleteProviderWaitRequestAsync(ProviderWaitRace race, Task<VirtualDesktopReconciliationResult> request)
		{
			try { race.TryComplete(await request.ConfigureAwait(false), false); }
			catch (OperationCanceledException) { race.TryComplete(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable), false); }
			catch { race.TryComplete(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable), false); }
		}
		private static async Task CompleteProviderWaitTimeoutAsync(ProviderWaitRace race, TimeSpan budget, CancellationToken cancellationToken)
		{
			try
			{
				await Task.Delay(budget, cancellationToken).ConfigureAwait(false);
				race.TryComplete(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable), true);
			}
			catch (OperationCanceledException) { }
		}
		private void RegisterProviderWait(ProviderWaitRace race) { lock (this._providerWaitGate) this._providerWaits.Add(race); }
		private void UnregisterProviderWait(ProviderWaitRace race) { lock (this._providerWaitGate) this._providerWaits.Remove(race); }
		private void ReserveProviderPublication(VirtualDesktopStableBatch batch)
		{
			ProviderWaitRace[] waits;
			lock (this._providerWaitGate) waits = this._providerWaits.ToArray();
			foreach (var wait in waits) wait.TryReservePublishedSuccess(batch);
		}
		private void CompleteReservedProviderPublications()
		{
			ProviderWaitRace[] waits;
			lock (this._providerWaitGate) waits = this._providerWaits.ToArray();
			foreach (var wait in waits) wait.CompleteReservedSuccess();
		}
		internal Task<VirtualDesktopReconciliationResult> RequestReconciliationAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			if (this._shutdownStarted || this._stopping) return Task.FromResult(VirtualDesktopReconciliationResult.ShuttingDown());
			return this._provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation, cancellationToken);
		}

		internal void EditName(Guid desktopId, string value)
			=> this.EnqueueOrRun(() => this.CommitLocalEdit(DesktopLocalEdit.Name(desktopId, value, this.State), () => this._operations.SetName(desktopId, value)));

		internal void EditWallpaperPath(Guid desktopId, string value)
			=> this.EnqueueOrRun(() => this.SetWallpaperPath(desktopId, value));

		private void SetWallpaperPath(Guid desktopId, string value)
		{
			if (!this.State.Records.TryGetValue(desktopId, out var record)) return;
			var unsupported = record.WallpaperPath.ReadStatus == VirtualDesktopReadStatus.Unsupported;
			if (!unsupported && string.IsNullOrEmpty(value)) return;
			this.CommitLocalEdit(
				DesktopLocalEdit.WallpaperPath(desktopId, value, this.State),
				() =>
				{
					if (!unsupported)
					{
						this._operations.SetWallpaperPath(desktopId, value);
					}
					else if (this.State.CurrentDesktopId == desktopId)
					{
						this._operations.ApplyWallpaper(desktopId, value, record.WallpaperPosition);
					}
				});
		}

		internal void EditWallpaperPosition(Guid desktopId, WallpaperPosition value)
			=> this.EnqueueOrRun(() =>
			{
				var record = this.GetRecord(desktopId);
				this.CommitLocalEdit(
					DesktopLocalEdit.WallpaperPosition(desktopId, value, this.State),
					() =>
					{
						if (this.State.CurrentDesktopId == desktopId) this._operations.ApplyWallpaper(desktopId, record.WallpaperPath.HasValue ? record.WallpaperPath.Value : null, value);
					});
			});

		internal void MoveLeft(Guid id) => this.EnqueueOrRun(() => this._operations.MoveLeft(id));
		internal void MoveRight(Guid id) => this.EnqueueOrRun(() => this._operations.MoveRight(id));
		internal void MoveFirst(Guid id) => this.EnqueueOrRun(() => this._operations.MoveFirst(id));
		internal void MoveLast(Guid id) => this.EnqueueOrRun(() => this._operations.MoveLast(id));
		internal void Switch(Guid id) => this.EnqueueOrRun(() => this._operations.Switch(id));
		internal void Remove(Guid id) => this.EnqueueOrRun(() => this._operations.Remove(id));

		internal int IndexOf(Guid desktopId)
		{
			var state = this.State;
			return state == null ? -1 : state.Order.IndexOf(desktopId);
		}

		internal DesktopRecord GetRecord(Guid desktopId)
		{
			var state = this.State;
			if (state == null || !state.Records.TryGetValue(desktopId, out var record)) throw new InvalidOperationException("The desktop is not present in the current Coordinator state.");
			return record;
		}

		internal async Task<SettingsImportCommitResult> ImportAsync(string path, bool overrideDesktops, CancellationToken cancellationToken)
		{
			if (this._shutdownStarted || this._stopping) return SettingsImportCommitResult.ShuttingDown();
			var stage = await this._settings.PrepareImportAsync(path);
			return await this.CommitPreparedImportAsync(stage, overrideDesktops, cancellationToken);
		}

		internal async Task<SettingsImportCommitResult> ResetSettingsAsync(CancellationToken cancellationToken)
		{
			if (this._shutdownStarted || this._stopping) return SettingsImportCommitResult.ShuttingDown();
			var stage = await this._settings.PrepareResetAsync();
			return await this.CommitPreparedImportAsync(stage, false, cancellationToken, true);
		}

		internal Task<SettingsImportCommitResult> CommitPreparedImportAsync(StagedSettingsImport stage, bool overrideDesktops, CancellationToken cancellationToken, bool resetPositions = false)
		{
			this.EnsureOwnerAccess();
			if (this._activeImportCommit != null && !this._activeImportCommit.IsCompleted) throw new InvalidOperationException("A settings import commit is already active.");
			this._activeImportCommit = this.CommitImportWithPlacementSuspendedAsync(stage, overrideDesktops, cancellationToken, resetPositions);
			return this._activeImportCommit;
		}

		private async Task<SettingsImportCommitResult> CommitPreparedImportCoreAsync(StagedSettingsImport stage, bool overrideDesktops, CancellationToken cancellationToken, bool resetPositions)
		{
			this.EnsureOwnerAccess();
			if (stage == null) throw new ArgumentNullException(nameof(stage));
			var claim = this._settings.ClaimImport(stage);
			if (claim == null) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
			if (this.IsShuttingDown)
			{
				this._settings.DiscardImport(claim);
				return SettingsImportCommitResult.ShuttingDown();
			}
			if (cancellationToken.IsCancellationRequested)
			{
				this._settings.DiscardImport(claim);
				return SettingsImportCommitResult.Cancelled();
			}

			var session = new DesktopPreparedImportSession(claim, overrideDesktops, resetPositions, this, this._settings);
			this._activeImportSession = session;
			var requestReconciliation = false;
			try
			{
				var outcome = await session.ExecuteAsync(cancellationToken);
				try
				{
					switch (outcome.Kind)
					{
						case DesktopImportTransactionOutcomeKind.CommitPreparedRuntime:
							return this.CommitPreparedImportOutcome(session, outcome, ref requestReconciliation);
						case DesktopImportTransactionOutcomeKind.ApplyRecoveredState:
							var recoveredIngress = this.DetachImportSession(session);
							this._persistenceProtection = outcome.PersistenceProtection;
							this.ApplyStableBatch(outcome.RecoveryBatch);
							this.ReplayImportIngress(recoveredIngress);
							return outcome.Result;
						case DesktopImportTransactionOutcomeKind.Discarded:
							if (outcome.PersistenceProtection != null) this._persistenceProtection = outcome.PersistenceProtection;
							return outcome.Result;
						case DesktopImportTransactionOutcomeKind.DiscardedAndReconcile:
							if (outcome.PersistenceProtection != null) this._persistenceProtection = outcome.PersistenceProtection;
							requestReconciliation = true;
							return outcome.Result;
						default:
							throw new InvalidOperationException("Unknown prepared import transaction outcome.");
					}
				}
				catch (Exception ex)
				{
					requestReconciliation = true;
					this.ReportFault(new DesktopRuntimeFault("SettingsTransaction", ex.GetType()));
					return SettingsImportCommitResult.Failed();
				}
			}
			finally
			{
				var frozenIngress = ReferenceEquals(this._activeImportSession, session)
					? this.DetachImportSession(session)
					: Array.Empty<DesktopImportProviderIngress>();
				if (requestReconciliation && !this._stopping) _ = this.RequestReconciliationAsync();
				this.ReplayImportIngress(frozenIngress);
				this.ScheduleDeferredCommands();
			}
		}

		private SettingsImportCommitResult CommitPreparedImportOutcome(
			DesktopPreparedImportSession session,
			DesktopImportTransactionOutcome outcome,
			ref bool requestReconciliation)
		{
			try
			{
				this._persistenceProtection = null;
				var transition = this._coordinator.CommitStagedRuntime(outcome.PreparedRuntime, false);
				if (!transition.Accepted)
					throw new InvalidOperationException("The prepared runtime could not be activated after the settings commit.");
				this._settings.PublishImportCommitted();
				this.ApplyTransition(transition, false, false);
				this.ReplayImportIngress(this.DetachImportSession(session));
				return outcome.Result;
			}
			catch (Exception ex)
			{
				requestReconciliation = true;
				this.ReportFault(new DesktopRuntimeFault("SettingsTransaction.PostCommitConsistency", ex.GetType()));
				return SettingsImportCommitResult.CompletedWithFailures(outcome.Result.SaveResult);
			}
		}
		private IReadOnlyList<DesktopImportProviderIngress> DetachImportSession(DesktopPreparedImportSession session)
		{
			var ingress = session.Complete();
			if (ReferenceEquals(this._activeImportSession, session)) this._activeImportSession = null;
			return ingress;
		}

		private void ReplayImportIngress(IEnumerable<DesktopImportProviderIngress> ingress)
		{
			if (ingress == null || this._stopping) return;
			foreach (var item in ingress)
			{
				if (this._stopping) break;
				if (item.StableBatch != null) this.ApplyStableBatch(item.StableBatch);
				else if (item.CurrentTransition != null) this.ApplyCurrentTransition(item.CurrentTransition);
			}
		}

		internal Task<DesktopRuntimeShutdownResult> ShutdownAsync()
		{
			if (this._shutdownTask != null) return this._shutdownTask;
			this._shutdownTask = this.ShutdownCoreAsync();
			return this._shutdownTask;
		}

		private async Task<DesktopRuntimeShutdownResult> ShutdownCoreAsync()
		{
			this._shutdownStarted = true;
			await this.ReconcilePlacementAsync();
			if (this._activeImportCommit != null && !this._activeImportCommit.IsCompleted)
				await this._activeImportCommit;
			if (this._activeImportSession != null)
			{
				var session = this._activeImportSession;
				var discard = session.DiscardForShutdown();
				if (discard.Status != SettingsImportCommitStatus.Publishing)
					this.ReplayImportIngress(this.DetachImportSession(session));
			}
			if (this._deferredCommands.Count != 0) this.DrainDeferredCommands();

			var final = await this.RequestProviderWithBudgetAsync(VirtualDesktopStableReason.Recovery, CancellationToken.None);
			SettingsSaveResult saveResult = null;
			var shutdownStatus = DesktopRuntimeShutdownStatus.ReconciliationUnavailable;
			if (final.Status == VirtualDesktopReconciliationStatus.Succeeded)
			{
				var transition = this._coordinator.ApplyStableBatch(final.Batch);
				this.ApplyTransition(transition, true, false);
				var state = this.State;
				if (state != null)
				{
					saveResult = await this._settings.RequestSaveAsync(state.StateRevision);
					shutdownStatus = saveResult.Succeeded ? DesktopRuntimeShutdownStatus.Completed : DesktopRuntimeShutdownStatus.SaveFailed;
					if (!saveResult.Succeeded) this.ReportFault(new DesktopRuntimeFault("Shutdown.SettingsSave." + saveResult.ErrorCategory, saveResult.ExceptionType));
				}
			}
			else
			{
				this.ReportFault(new DesktopRuntimeFault("Shutdown." + final.Status, typeof(InvalidOperationException)));
			}

			this._stopping = true;
			this.UnsubscribeProviderEvents();
			this._provider.Dispose();
			return new DesktopRuntimeShutdownResult(shutdownStatus, final.Status, saveResult);
		}
		public void Dispose()
		{
			if (Interlocked.Exchange(ref this._disposeSignaled, 1) != 0) return;
			var shutdown = this.ShutdownAsync();
			if (!this._owner.CheckAccess()) shutdown.GetAwaiter().GetResult();
		}
		private void SubscribeProviderEvents()
		{
			if (this._providerEventsSubscribed) return;
			this._provider.StableBatchPublished += this.OnStableBatchPublished;
			this._provider.CurrentTransitioned += this.OnCurrentTransitioned;
			this._provider.Faulted += this.OnProviderFaulted;
			this._providerEventsSubscribed = true;
		}

		private void UnsubscribeProviderEvents()
		{
			if (!this._providerEventsSubscribed) return;
			this._provider.StableBatchPublished -= this.OnStableBatchPublished;
			this._provider.CurrentTransitioned -= this.OnCurrentTransitioned;
			this._provider.Faulted -= this.OnProviderFaulted;
			this._providerEventsSubscribed = false;
		}

		private void TerminateFailedInitialization()
		{
			this._initialized = false;
			this._stopping = true;
			this._preparedRuntime = null;
			this._deferredCommands.Clear();
			this.UnsubscribeProviderEvents();
			this._provider.Dispose();
		}

		private DesktopRuntimeInitializationResult CompleteInitialization(VirtualDesktopReconciliationResult result, DesktopStartupPublicationMode publicationMode)
		{
			this.EnsureOwnerAccess();
			var expectedSuppression = publicationMode == DesktopStartupPublicationMode.DeferredUntilOverrideCompletes;
			if (this._suppressPublication != expectedSuppression)
				throw new InvalidOperationException("The startup publication mode does not match the active startup session.");
			switch (result.Status)
			{
				case VirtualDesktopReconciliationStatus.Succeeded:
					this.ApplyStableBatch(result.Batch);
					this._initialized = this.State != null;
					return new DesktopRuntimeInitializationResult(this._initialized ? DesktopRuntimeInitializationStatus.Completed : DesktopRuntimeInitializationStatus.Unavailable);
				case VirtualDesktopReconciliationStatus.Cancelled:
					return new DesktopRuntimeInitializationResult(DesktopRuntimeInitializationStatus.Cancelled);
				case VirtualDesktopReconciliationStatus.ShuttingDown:
				case VirtualDesktopReconciliationStatus.SupersededByReset:
					return new DesktopRuntimeInitializationResult(DesktopRuntimeInitializationStatus.ShuttingDown);
				default:
					return new DesktopRuntimeInitializationResult(DesktopRuntimeInitializationStatus.Unavailable, result.FailureCategory);
			}
		}

		private void OnStableBatchPublished(object sender, VirtualDesktopStableBatch batch)
		{
			if (this._stopping) return;
			if (this._owner.CheckAccess()) this.ApplyStableBatch(batch, true);
			else if (!this._owner.Post(() => this.ApplyStableBatch(batch, true))) this.ReportFault(new DesktopRuntimeFault("OwnerPostRejected.StableBatch", typeof(InvalidOperationException)));
		}

		private void OnCurrentTransitioned(object sender, VirtualDesktopCurrentTransition transition)
		{
			if (this._stopping) return;
			if (this._owner.CheckAccess()) this.ApplyCurrentTransition(transition);
			else if (!this._owner.Post(() => this.ApplyCurrentTransition(transition))) this.ReportFault(new DesktopRuntimeFault("OwnerPostRejected.CurrentTransition", typeof(InvalidOperationException)));
		}

		private void OnProviderFaulted(object sender, VirtualDesktopProviderFault fault)
			=> this.ReportFault(new DesktopRuntimeFault("Provider." + fault.FailureCategory, fault.ExceptionType, fault.DesktopId, fault.Sequence));

		private void ApplyStableBatch(VirtualDesktopStableBatch batch, bool providerPublication = false)
		{
			this.EnsureOwnerAccess();
			if (this._activeImportSession != null)
			{
				var frozen = this._activeImportSession.Phase == DesktopImportSessionPhase.CommitFrozen;
				if (this._activeImportSession.RouteStable(batch))
				{
					if (providerPublication && !frozen)
					{
						this.ReserveProviderPublication(batch);
						this.CompleteReservedProviderPublications();
					}
					return;
				}
			}
			var state = this.State;
			if (state != null && batch != null && (batch.ProviderEpoch < state.ProviderEpoch || (batch.ProviderEpoch == state.ProviderEpoch && batch.SnapshotRevision <= state.ProviderSnapshotRevision))) return;
			if (this._preparedRuntime != null)
			{
				this.ApplyStableBatchToPrepared(batch);
				if (providerPublication) this.ReserveProviderPublication(batch);
			}
			else
			{
				var applied = this._coordinator.ApplyStableBatch(batch);
				if (this._persistenceProtection != null && applied?.NewState != null) this._persistenceProtection = this._persistenceProtection.ReleaseNaturalMatches(applied.NewState);
				if (this._suppressPublication)
				{
					this._suppressedTransition = applied;
					if (providerPublication && applied?.Accepted == true) this.ReserveProviderPublication(batch);
				}
				else
				{
					this.ApplyTransition(applied, transitionCommitted: providerPublication ? (Action)(() => this.ReserveProviderPublication(batch)) : null);
					this.ApplyCreationWallpapers(applied);
				}
			}
			if (providerPublication) this.CompleteReservedProviderPublications();
		}

		private void ApplyCurrentTransition(VirtualDesktopCurrentTransition transition)
		{
			this.EnsureOwnerAccess();
			if (this._activeImportSession?.RouteCurrent(transition) == true) return;
			if (this._preparedRuntime != null) this._preparedRuntime.Coordinator.ApplyCurrentTransition(transition);
			else this.ApplyTransition(this._coordinator.ApplyCurrentTransition(transition));
		}
		private void PublishInitialization()
		{
			var state = this.State;
			if (state == null) return;
			var projection = DesktopRuntimeProjection.Create(state);
			var changed = new DesktopStateChanged(DesktopStateChangeKind.Initialized, state, state.Order, null, null, null, null, null, null);
			this.ApplyTransition(DesktopCoordinatorTransition.AcceptedChange(state, projection, changed, true, false, DesktopReconciliationReason.None));
			this._suppressedTransition = null;
		}
		private void ApplyStableBatchToPrepared(VirtualDesktopStableBatch batch)
		{
			if (batch == null || this._preparedRuntime == null) return;
			var state = this._preparedRuntime.Coordinator.State;
			if (state != null && (batch.ProviderEpoch < state.ProviderEpoch || (batch.ProviderEpoch == state.ProviderEpoch && batch.SnapshotRevision <= state.ProviderSnapshotRevision))) return;
			this._preparedRuntime.Coordinator.ApplyStableBatch(batch);
		}

		private bool ApplySeedToPreparedRuntime(DesktopTransitionCoordinator.DesktopPreparedRuntime prepared, DesktopStartupSeed seed, bool overrideDesktops, DesktopOperationJournal journal = null, bool resetPositions = false)
		{
			var state = prepared.Coordinator.State;
			journal?.PlanSeedTargets(seed, state);
			for (var index = 0; index < state.Order.Count; index++)
			{
				var id = state.Order[index];
				if (index < seed.Positions.Count || resetPositions)
					this.CommitPreparedEdit(prepared.Coordinator, DesktopLocalEdit.WallpaperPosition(id, index < seed.Positions.Count ? seed.Positions[index] : WallpaperPosition.Fill, prepared.Coordinator.State), null, null);
			}
			if (!overrideDesktops) return true;

			var failed = false;
			for (var index = 0; index < prepared.Coordinator.State.Order.Count; index++)
			{
				var id = prepared.Coordinator.State.Order[index];
				if (index < seed.Names.Count && seed.Names[index] != null)
				{
					if (failed) journal?.Record(id, DesktopPropertyKind.Name, DesktopOverrideOperationStatus.Skipped, seed.Names[index], true);
					else if (!this.CommitPreparedEdit(prepared.Coordinator, DesktopLocalEdit.Name(id, seed.Names[index], prepared.Coordinator.State), () => this._operations.SetName(id, seed.Names[index]), journal)) failed = true;
				}
				if (index < seed.WallpaperPaths.Count && seed.WallpaperPaths[index] != null)
				{
					if (failed)
					{
						journal?.Record(id, DesktopPropertyKind.WallpaperPath, DesktopOverrideOperationStatus.Skipped, seed.WallpaperPaths[index], true);
						continue;
					}
					var path = seed.WallpaperPaths[index];
					var record = prepared.Coordinator.State.Records[id];
					var applicationAuthoritative = record.WallpaperPath.ReadStatus == VirtualDesktopReadStatus.Unsupported;
					Action operation = applicationAuthoritative
						? prepared.Coordinator.State.CurrentDesktopId == id ? (Action)(() => this._operations.ApplyWallpaper(id, path, record.WallpaperPosition)) : null
						: () => this._operations.SetWallpaperPath(id, path);
					if (!this.CommitPreparedEdit(prepared.Coordinator, DesktopLocalEdit.WallpaperPath(id, path, prepared.Coordinator.State), operation, journal, applicationAuthoritative)) failed = true;
				}
			}
			return !failed;
		}

		private bool CommitPreparedEdit(DesktopTransitionCoordinator coordinator, DesktopLocalEdit command, Action operation, DesktopOperationJournal journal, bool applicationAuthoritative = false)
		{
			var edit = coordinator.PrepareLocalEdit(command);
			if (!edit.Transition.Accepted)
			{
				if (command.Property.HasValue) journal?.Record(command.DesktopId, command.Property.Value, DesktopOverrideOperationStatus.Skipped, command.Value, true);
				return true;
			}
			try
			{
				operation?.Invoke();
				if (command.Property.HasValue)
				{
					if (applicationAuthoritative) journal?.RecordApplicationAuthoritative(command.DesktopId, command.Property.Value, command.Value, operation != null);
					else if (operation != null) journal?.Record(command.DesktopId, command.Property.Value, DesktopOverrideOperationStatus.Succeeded, command.Value, true);
				}
			}
			catch
			{
				if (command.Property.HasValue) journal?.Record(command.DesktopId, command.Property.Value, DesktopOverrideOperationStatus.Failed, command.Value, true);
				if (journal == null) throw;
				return false;
			}
			coordinator.CommitLocalEdit(edit);
			return true;
		}

		private void CommitLocalEdit(DesktopLocalEdit command, Action operation)
		{
			this.EnsureOwnerAccess();
			if (this._stopping || command == null) return;
			var prepared = this._coordinator.PrepareLocalEdit(command);
			if (!prepared.Transition.Accepted) return;
			try { operation(); }
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("LocalEdit", ex.GetType(), command.DesktopId));
				return;
			}
			if (command.Property.HasValue && this._persistenceProtection != null) this._persistenceProtection = this._persistenceProtection.Release(command.DesktopId, command.Property.Value);
			this.ApplyTransition(this._coordinator.CommitLocalEdit(prepared));
		}

		private void ApplyTransition(
			DesktopCoordinatorTransition transition,
			bool applyProjection = true,
			bool requestSave = true,
			Action transitionCommitted = null)
		{
			if (transition == null) return;
			if (!transition.Accepted)
			{
				if (transition.RequiresReconciliation) _ = this.RequestReconciliationAsync();
				return;
			}

			if (AffectsPlacementDestinations(transition))
			{
				this._placementPermit?.Cancel();
				this._placementSession?.DesktopChanged();
			}
			if (applyProjection && transition.Projection != null) this._settings.ApplyProjection(this._persistenceProtection?.CreateProjection(transition.NewState) ?? transition.Projection);
			transitionCommitted?.Invoke();
			this._publishing = true;
			try
			{
				if (transition.StateChanged != null)
				{
					var payload = new DesktopRuntimeStateChanged(transition.StateChanged, this._settings.SettingsRevision);
					var handlers = this.StateChanged;
					if (handlers != null)
					{
						foreach (EventHandler<DesktopRuntimeStateChanged> handler in handlers.GetInvocationList())
						{
							try
							{
								handler(this, payload);
							}
							catch (Exception ex)
							{
								this.ReportFault(new DesktopRuntimeFault("StateChangedSubscriber", ex.GetType()));
							}
						}
					}
				}
				if (requestSave && transition.RequiresSave) _ = this.ObserveSaveAsync(this._settings.RequestSaveAsync(transition.NewState.StateRevision));
			}
			finally
			{
				this._publishing = false;
				this.ScheduleDeferredCommands();
			}
			if (transition.RequiresReconciliation) _ = this.RequestReconciliationAsync();
		}

		private void ReportUnconfirmedOverride(Guid desktopId, DesktopPropertyKind property)
		{
			this.ReportFault(new DesktopRuntimeFault("Override.Unconfirmed." + property, typeof(InvalidOperationException), desktopId));
		}
		private async Task ObserveSaveAsync(Task<SettingsSaveResult> task)
		{
			var result = await task.ConfigureAwait(false);
			if (!result.Succeeded) this.ReportFault(new DesktopRuntimeFault("SettingsSave." + result.ErrorCategory, result.ExceptionType == null ? null : typeof(InvalidOperationException)));
		}

		private void EnqueueOrRun(Action command)
		{
			if (command == null || this.IsShuttingDown) return;
			if (!this._owner.CheckAccess()) throw new InvalidOperationException("Desktop commands must be submitted on the owner Dispatcher.");
			if (this.MustDeferDesktopCommands || this.HasPendingDeferredCommands)
			{
				this._deferredCommands.Enqueue(command);
				if (!this._publishing && !this.IsTransactionPending) this.ScheduleDeferredCommands();
				return;
			}
			command();
		}

		private void ScheduleDeferredCommands()
		{
			if (this._deferredDrainScheduled || this._deferredCommands.Count == 0 || this.MustDeferDesktopCommands || this.IsShuttingDown)
			{
				return;
			}
			this._deferredDrainScheduled = true;
			if (!this._owner.Post(this.DrainDeferredCommands))
			{
				this._deferredDrainScheduled = false;
				this._deferredCommands.Clear();
				this.ReportFault(new DesktopRuntimeFault("DeferredCommand.Aborted", typeof(InvalidOperationException)));
			}
		}

		private void DrainDeferredCommands()
		{
			this.EnsureOwnerAccess();
			if (this.MustDeferDesktopCommands)
			{
				this._deferredDrainScheduled = false;
				return;
			}
			try
			{
				var count = this._deferredCommands.Count;
				while (count-- > 0 && this._deferredCommands.Count != 0 && !this._stopping)
					this._deferredCommands.Dequeue()();
			}
			finally
			{
				this._deferredDrainScheduled = false;
				if (this._deferredCommands.Count != 0 && !this._stopping) this.ScheduleDeferredCommands();
			}
		}

		private void ReportFault(DesktopRuntimeFault fault)
		{
			var handlers = this.Faulted;
			if (handlers == null) return;
			foreach (EventHandler<DesktopRuntimeFault> handler in handlers.GetInvocationList())
			{
				try { handler(this, fault); }
				catch { }
			}
		}

		private void EnsureOwnerAccess()
		{
			if (!this._owner.CheckAccess()) throw new InvalidOperationException("The desktop runtime can only be mutated by its owner Dispatcher.");
		}

		internal static string GetCurrentWallpaperPath(DesktopRuntimeState state)
		{
			if (state?.CurrentDesktopId == null || !state.Records.TryGetValue(state.CurrentDesktopId.Value, out var record)) return null;
			return record.WallpaperPath.HasValue && !string.IsNullOrEmpty(record.WallpaperPath.Value)
				? record.WallpaperPath.Value
				: null;
		}
	}
}
