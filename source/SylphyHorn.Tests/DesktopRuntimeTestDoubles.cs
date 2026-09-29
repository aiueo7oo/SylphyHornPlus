using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.DesktopTransitions;
using WindowsDesktop;
using Xunit;

using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	internal static class DesktopRuntimeTestData
	{
		internal static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
		internal static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
		internal static readonly Guid C = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
		internal static int ExpectedArchitecture() => Environment.Is64BitProcess ? 64 : 32;
		internal static VirtualDesktopStableBatch Batch(long epoch, long revision, Guid current, params VirtualDesktopStableEntry[] entries)
			=> new VirtualDesktopStableBatch(epoch, revision, current, VirtualDesktopReadStatus.Success, entries, VirtualDesktopStableReason.ExplicitReconciliation);
		internal static VirtualDesktopStableEntry Entry(Guid id, int index, string name, string wallpaper)
			=> new VirtualDesktopStableEntry(id, index, name, VirtualDesktopReadStatus.Success, wallpaper, VirtualDesktopReadStatus.Success);
		internal static VirtualDesktopStableEntry WallpaperUnsupported(Guid id, int index, string name)
			=> new VirtualDesktopStableEntry(id, index, name, VirtualDesktopReadStatus.Success, null, VirtualDesktopReadStatus.Unsupported);
	}
	internal sealed class NonSerializableValue { public Action Callback => () => { }; }

	internal sealed class Harness
	{
		private Harness(
			FakeProvider provider,
			FakeSettings settings,
			FakeOwner owner,
			FakeOperations operations,
			IPlacementSessionFactory placementFactory,
			Func<long> closureClock)
		{
			this.Provider = provider;
			this.Settings = settings;
			this.Owner = owner;
			this.Operations = operations;
			this.Runtime = new DesktopTransitionRuntime(provider, settings, owner, operations, placementFactory: placementFactory, closureClock: closureClock);
		}
		internal FakeProvider Provider { get; }
		internal FakeSettings Settings { get; }
		internal FakeOwner Owner { get; }
		internal FakeOperations Operations { get; }
		internal DesktopTransitionRuntime Runtime { get; }
		internal static Harness Create(VirtualDesktopStableBatch batch, IPlacementSessionFactory placementFactory = null, Func<long> closureClock = null)
			=> new Harness(
				new FakeProvider(batch),
				new FakeSettings(DesktopStartupSeed.Empty),
				new FakeOwner(),
				new FakeOperations(),
				placementFactory,
				closureClock);
		internal static async Task<Harness> Initialized()
		{
			var harness = Create(Batch(1, 1, A, Entry(A, 0, "name", "wall")));
			await harness.Runtime.InitializeAsync(false, TestContext.Current.CancellationToken);
			return harness;
		}
	}

	internal sealed class FakeProvider : IDesktopProviderClient
	{
		private readonly Queue<VirtualDesktopReconciliationResult> _results = new Queue<VirtualDesktopReconciliationResult>();
		internal FakeProvider(VirtualDesktopStableBatch initial) => this._results.Enqueue(VirtualDesktopReconciliationResult.Succeeded(initial));
		internal FakeProvider(VirtualDesktopReconciliationResult initial) => this._results.Enqueue(initial);
		public event EventHandler<VirtualDesktopStableBatch> StableBatchPublished;
		public event EventHandler<VirtualDesktopCurrentTransition> CurrentTransitioned;
		public event EventHandler<VirtualDesktopProviderFault> Faulted;
		internal Task<VirtualDesktopReconciliationResult> NextRequest { get; set; }
		internal int NextRequestNumber { get; set; }
		internal TaskCompletionSource<bool> NextRequestStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		internal VirtualDesktopStableBatch PublishSynchronouslyBeforeRequestCompletion { get; set; }
		internal bool Disposed { get; private set; }
		internal int ReconciliationRequestCount { get; private set; }
		internal int StablePublicationCount { get; private set; }
		internal Action<int> RequestObserved { get; set; }
		internal CancellationToken LastRequestCancellationToken { get; private set; }
		internal void EnqueueResult(VirtualDesktopStableBatch batch) => this._results.Enqueue(VirtualDesktopReconciliationResult.Succeeded(batch));
		internal void EnqueueResult(VirtualDesktopReconciliationResult result) => this._results.Enqueue(result);
		internal bool IsPublishingStable { get; private set; }
		internal void PublishStable(VirtualDesktopStableBatch batch)
		{
			var previous = this.IsPublishingStable;
			this.IsPublishingStable = true;
			try
			{
				this.StablePublicationCount++;
				this.StableBatchPublished?.Invoke(this, batch);
			}
			finally
			{
				this.IsPublishingStable = previous;
			}
		}
		internal void PublishCurrent(VirtualDesktopCurrentTransition transition) => this.CurrentTransitioned?.Invoke(this, transition);
		internal void PublishFault(VirtualDesktopProviderFault fault) => this.Faulted?.Invoke(this, fault);
		public Task<VirtualDesktopReconciliationResult> RequestReconciliationAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken)
		{
			this.LastRequestCancellationToken = cancellationToken;
			this.ReconciliationRequestCount++;
			this.RequestObserved?.Invoke(this.ReconciliationRequestCount);
			if (this.Disposed) return Task.FromResult(VirtualDesktopReconciliationResult.ShuttingDown());
			if (this.PublishSynchronouslyBeforeRequestCompletion != null)
			{
				var published = this.PublishSynchronouslyBeforeRequestCompletion;
				this.PublishSynchronouslyBeforeRequestCompletion = null;
				this.PublishStable(published);
				return Task.FromResult(VirtualDesktopReconciliationResult.Succeeded(published));
			}
			if (this.NextRequest != null && (this.NextRequestNumber == 0 || this.NextRequestNumber == this.ReconciliationRequestCount))
			{
				var result = this.NextRequest;
				this.NextRequest = null;
				this.NextRequestStarted.TrySetResult(true);
				return result;
			}
			return Task.FromResult(this._results.Count == 0 ? VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable) : this._results.Dequeue());
		}
		internal Action Disposing { get; set; }
		public void Dispose() { this.Disposing?.Invoke(); this.Disposed = true; }
	}

	internal sealed class FakeSettings : IDesktopSettingsTransactions
	{
		internal PlacementCreatedGroup[] CreatedGroups = Array.Empty<PlacementCreatedGroup>();
		public DesktopWallpaperOnCreation[] WallpapersOnCreation = Array.Empty<DesktopWallpaperOnCreation>();
		public DesktopWallpaperOnCreation[] ReadWallpapersOnCreation() => this.WallpapersOnCreation;
		public bool PerDesktopWallpaperEnabled { get; set; } = true;
		public PlacementCreatedGroup[] ReadCreatedDesktopGroups() => this.CreatedGroups;
		public void WriteCreatedDesktopGroups(PlacementCreatedGroup[] groups) => this.CreatedGroups = groups;
		internal FakeSettings(DesktopStartupSeed seed)
		{
			this.Seed = seed;
			this.Provider = new TestDictionaryProvider();
			this.Provider.InitializeAsync().GetAwaiter().GetResult();
		}
		internal DesktopStartupSeed Seed { get; }
		internal TestDictionaryProvider Provider { get; }
		internal DesktopSettingsProjection LastProjection { get; private set; }
		internal int ProjectionCount { get; private set; }
		internal int SaveRequests { get; private set; }
		internal int ImportCommitRequests { get; private set; }
		internal int ImportDiscardRequests { get; private set; }
		internal int ImportPublishRequests { get; private set; }
		internal long Revision { get; private set; }
		internal Action ProjectionApplied { get; set; }
		internal bool BlockCommit { get; set; }
		internal Exception CommitException { get; set; }
		internal Exception PublishException { get; set; }
		internal TaskCompletionSource<bool> CommitStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource<bool> CommitRelease { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		public DesktopStartupSeed CaptureStartupSeed() => this.Seed;
		public void ApplyProjection(DesktopSettingsProjection projection)
		{
			this.LastProjection = projection;
			this.ProjectionCount++;
			this.Revision++;
			var dictionary = new Dictionary<string, object>();
			SettingsService.ApplyDesktopProjection(dictionary, projection);
			foreach (var pair in dictionary) this.Provider.SetValue(pair.Key, pair.Value);
			this.ProjectionApplied?.Invoke();
		}
		public long SettingsRevision => Math.Max(this.Revision, this.Provider.SettingsRevision);
		public Task<SettingsSaveResult> RequestSaveAsync(long stateRevision) { this.SaveRequests++; return this.Provider.SaveWithResultAsync(stateRevision); }
		public Task<StagedSettingsImport> PrepareImportAsync(string path) => this.Provider.PrepareImportAsync(path);
		public Task<StagedSettingsImport> PrepareResetAsync() => this.Provider.PrepareResetAsync();
		public DesktopSettingsImportClaim ClaimImport(StagedSettingsImport stage) => DesktopSettingsImportClaim.TryCreate(this.Provider, stage);
		public async Task<SettingsImportCommitResult> CommitImportAsync(DesktopSettingsImportClaim claim, IDictionary<string, object> dictionary)
		{
			this.ImportCommitRequests++;
			if (this.BlockCommit)
			{
				this.CommitStarted.TrySetResult(true);
				await this.CommitRelease.Task;
			}
			if (this.CommitException != null) throw this.CommitException;
			return await claim.CommitAsync(this.Provider, dictionary);
		}
		public SettingsImportCommitResult DiscardImport(DesktopSettingsImportClaim claim)
		{
			this.ImportDiscardRequests++;
			return claim.Discard(this.Provider);
		}
		public void PublishImportCommitted()
		{
			this.ImportPublishRequests++;
			if (this.PublishException != null) throw this.PublishException;
			this.Provider.PublishCommittedImport();
		}
	}

	internal sealed class TestDictionaryProvider : DictionaryProvider
	{
		internal IDictionary<string, object> NextImport { get; set; } = new Dictionary<string, object>();
		internal string ContentHash { get; set; }
		internal Exception SaveFailure { get; set; }
		internal List<IDictionary<string, object>> SavedDictionaries { get; } = new List<IDictionary<string, object>>();
		internal Task InitializeAsync() => this.LoadAsync();
		protected override Task SaveAsyncCore(IDictionary<string, object> dic)
		{
			if (this.SaveFailure != null) return Task.FromException(this.SaveFailure);
			this.SavedDictionaries.Add(new Dictionary<string, object>(dic));
			return Task.CompletedTask;
		}
		protected override Task SaveAsyncCore(IDictionary<string, object> dic, string path) => this.SaveAsyncCore(dic);
		protected override Task<IDictionary<string, object>> LoadAsyncCore() => Task.FromResult<IDictionary<string, object>>(new Dictionary<string, object>());
		protected override Task<IDictionary<string, object>> LoadAsyncCore(string path) => Task.FromResult(this.NextImport);
		protected override Task<string> GetContentHashAsyncCore() => Task.FromResult(this.ContentHash);
	}

	internal sealed class ControlledSaveProvider : DictionaryProvider
	{
		private readonly Queue<ControlledWrite> _started = new Queue<ControlledWrite>();
		private readonly Queue<TaskCompletionSource<ControlledWrite>> _waiters = new Queue<TaskCompletionSource<ControlledWrite>>();
		internal List<string> WrittenValues { get; } = new List<string>();
		internal Task<ControlledWrite> NextWriteAsync()
		{
			lock (this._started)
			{
				if (this._started.Count != 0) return Task.FromResult(this._started.Dequeue());
				var waiter = new TaskCompletionSource<ControlledWrite>(TaskCreationOptions.RunContinuationsAsynchronously);
				this._waiters.Enqueue(waiter);
				return waiter.Task;
			}
		}
		protected override Task SaveAsyncCore(IDictionary<string, object> dic)
		{
			var write = new ControlledWrite();
			this.WrittenValues.Add((string)dic["Value"]);
			lock (this._started)
			{
				if (this._waiters.Count != 0) this._waiters.Dequeue().TrySetResult(write);
				else this._started.Enqueue(write);
			}
			return write.Completion.Task;
		}
		protected override Task SaveAsyncCore(IDictionary<string, object> dic, string path) => this.SaveAsyncCore(dic);
		protected override Task<IDictionary<string, object>> LoadAsyncCore() => Task.FromResult<IDictionary<string, object>>(new Dictionary<string, object>());
		protected override Task<IDictionary<string, object>> LoadAsyncCore(string path) => this.LoadAsyncCore();
	}

	internal sealed class ControlledWrite
	{
		internal TaskCompletionSource<bool> Completion { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		internal void Complete() => this.Completion.TrySetResult(true);
	}
	internal sealed class FakeOwner : IDesktopOwnerContext
	{
		internal Queue<Action> Posted { get; } = new Queue<Action>();
		internal bool RejectPost { get; set; }
		public bool CheckAccess() => true;
		public bool Post(Action action) { if (this.RejectPost) return false; this.Posted.Enqueue(action); return true; }
		internal void DrainOne() { if (this.Posted.Count != 0) this.Posted.Dequeue()(); }
		internal void Drain() { while (this.Posted.Count != 0) this.DrainOne(); }
	}

	internal sealed class FakeOperations : IDesktopOperations
	{
		internal bool AllowEmptyRemoval = true;
		public bool TryRemoveEmpty(Guid desktopId, Guid fallbackId, Func<bool> stillCurrent)
		{
			if (!this.AllowEmptyRemoval || !stillCurrent()) return false;
			this.Remove(desktopId);
			return true;
		}
		internal List<string> DesktopOperationNames { get; } = new List<string>();
		internal List<Guid> DesktopOperationIds { get; } = new List<Guid>();
		internal int NameCalls { get; private set; }
		internal int CreateCalls { get; private set; }
		internal Func<Guid> Creating { get; set; }
		internal List<Guid> RemovedIds { get; } = new List<Guid>();
		internal List<Guid?> RemovalFallbackIds { get; } = new List<Guid?>();
		internal Exception NameFailure { get; set; }
		internal Exception CreateFailure { get; set; }
		internal string FailNameValue { get; set; }
		internal string FailWallpaperValue { get; set; }
		internal int WallpaperCalls { get; private set; }
		internal List<Guid> AppliedWallpaperIds { get; } = new List<Guid>();
		internal List<string> AppliedWallpaperValues { get; } = new List<string>();
		internal Action BeforeName { get; set; }
		internal Action BeforeWallpaper { get; set; }
		internal List<string> NameValues { get; } = new List<string>();
		public Guid Create()
		{
			this.CreateCalls++;
			if (this.CreateFailure != null)
			{
				throw this.CreateFailure;
			}
			return this.Creating?.Invoke() ?? Guid.NewGuid();
		}

		public void SetName(Guid desktopId, string value)
		{
			this.NameCalls++;
			this.NameValues.Add(value);
			this.BeforeName?.Invoke();
			if (this.NameFailure != null || this.FailNameValue == value)
			{
				throw this.NameFailure ?? new InvalidOperationException("synthetic");
			}
		}

		public void SetWallpaperPath(Guid desktopId, string value)
		{
			this.BeforeWallpaper?.Invoke();
			this.WallpaperCalls++;
			if (this.FailWallpaperValue == value)
			{
				throw new InvalidOperationException("synthetic");
			}
		}

		public void ApplyWallpaper(Guid desktopId, string value, WallpaperPosition position) { this.AppliedWallpaperIds.Add(desktopId); this.AppliedWallpaperValues.Add(value); }
		public void MoveLeft(Guid desktopId) => this.RecordDesktopOperation(nameof(this.MoveLeft), desktopId);
		public void MoveRight(Guid desktopId) => this.RecordDesktopOperation(nameof(this.MoveRight), desktopId);
		public void MoveFirst(Guid desktopId) => this.RecordDesktopOperation(nameof(this.MoveFirst), desktopId);
		public void MoveLast(Guid desktopId) => this.RecordDesktopOperation(nameof(this.MoveLast), desktopId);
		public void Switch(Guid desktopId) => this.RecordDesktopOperation(nameof(this.Switch), desktopId);
		public void Remove(Guid desktopId, Guid? fallbackId = null)
		{
			this.RemovedIds.Add(desktopId);
			this.RemovalFallbackIds.Add(fallbackId);
			this.RecordDesktopOperation(nameof(this.Remove), desktopId);
		}

		private void RecordDesktopOperation(string name, Guid desktopId)
		{
			this.DesktopOperationNames.Add(name);
			this.DesktopOperationIds.Add(desktopId);
		}
	}
}
