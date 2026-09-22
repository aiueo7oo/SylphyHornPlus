using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroTrilithon.Serialization;
using static SylphyHorn.Serialization.SettingsDictionarySnapshot;

namespace SylphyHorn.Serialization
{
	public abstract class DictionaryProvider : ISerializationProvider
	{
		private readonly object _sync = new object();
		private readonly SemaphoreSlim _serializationGate = new SemaphoreSlim(1, 1);
		private readonly object _stageOwnerToken = new object();
		private Dictionary<string, object> _settings = new Dictionary<string, object>(StringComparer.Ordinal);
		private readonly Queue<Action<Dictionary<string, object>>> _deferredMutations = new Queue<Action<Dictionary<string, object>>>();
		private readonly List<SaveWaiter> _saveWaiters = new List<SaveWaiter>();
		private bool _saveLoopRunning;
		private bool _importActive;
		private SettingsImportTransactionPhase _importPhase;
		private StagedSettingsImport _activeStage;
		private object _activeStageClaimToken;
		private bool _importNotificationPending;
		private long _settingsRevision;
		private long _requestedSaveRevision;
		private long _completedSaveRevision;

		public bool IsLoaded { get; private set; }
		public virtual string Filename { get; } = "Settings.xml";
		public virtual Type[] KnownTypes { get; } = { typeof(bool), typeof(int[]), typeof(AppPlacement.AppPlacementConfiguration), typeof(AppPlacement.PlacementCreatedGroup[]), };
		public long SettingsRevision { get { lock (this._sync) return this._settingsRevision; } }
		public bool ImportTransactionActive { get { lock (this._sync) return this._importActive; } }

		public event EventHandler Reloaded;

		public void SetValue<T>(string key, T value)
		{
			if (key == null) throw new ArgumentNullException(nameof(key));
			AppPlacementSettings.ValidateEntry(key, value);
			this.ApplyOrDefer(dic => dic[key] = CloneValue(value));
		}

		public bool TryGetValue<T>(string key, out T value)
		{
			lock (this._sync)
			{
				if (this._settings.TryGetValue(key, out var obj) && obj is T typed)
				{
					value = typed;
					return true;
				}
			}
			value = default(T);
			return false;
		}

		public bool RemoveValue(string key)
		{
			if (key == null) throw new ArgumentNullException(nameof(key));
			lock (this._sync)
			{
				if (this._importActive)
				{
					this._deferredMutations.Enqueue(dic => dic.Remove(key));
					return true;
				}
				var removed = this._settings.Remove(key);
				if (removed) this.AdvanceSettingsRevisionUnderLock();
				return removed;
			}
		}

		public void Clear()
		{
			if (this.ApplyOrDefer(dic => dic.Clear())) this.NotifyReloaded();
		}

		void ISerializationProvider.Save() => this.SaveAsync().GetAwaiter().GetResult();

		public async Task SaveAsync() => await this.SaveWithResultAsync().ConfigureAwait(false);

		public Task<SettingsSaveResult> SaveWithResultAsync(long stateRevision = 0)
		{
			SaveWaiter waiter;
			var startLoop = false;
			lock (this._sync)
			{
				var requested = checked(++this._requestedSaveRevision);
				waiter = new SaveWaiter(requested, stateRevision);
				this._saveWaiters.Add(waiter);
				if (!this._importActive && !this._saveLoopRunning)
				{
					this._saveLoopRunning = true;
					startLoop = true;
				}
			}
			if (startLoop) _ = this.RunSaveLoopAsync();
			return waiter.Completion.Task;
		}

		public async Task ExportAsync(string path)
		{
			if (path == null) throw new ArgumentNullException(nameof(path));
			await this._serializationGate.WaitAsync().ConfigureAwait(false);
			try
			{
				IDictionary<string, object> snapshot;
				lock (this._sync) snapshot = new SortedDictionary<string, object>(CloneDictionary(this._settings));
				await this.SaveAsyncCore(snapshot, path).ConfigureAwait(false);
			}
			finally { this._serializationGate.Release(); }
		}

		public async Task<StagedSettingsImport> PrepareResetAsync()
		{
			long revision;
			string fingerprint;
			lock (this._sync)
			{
				if (this._importActive) throw new InvalidOperationException("A settings transaction is already active.");
				this._importActive = true;
				this._importPhase = SettingsImportTransactionPhase.Preparing;
				revision = this._settingsRevision;
				fingerprint = ComputeFingerprint(this._settings);
			}
			try
			{
				var diskHash = await this.GetContentHashAsyncCore().ConfigureAwait(false);
				var stage = new StagedSettingsImport(this._stageOwnerToken, new Dictionary<string, object>(StringComparer.Ordinal), revision, fingerprint, diskHash);
				lock (this._sync)
				{
					if (!this._importActive) throw new InvalidOperationException("The settings transaction was terminated during preparation.");
					this._activeStage = stage;
					this._importPhase = SettingsImportTransactionPhase.Prepared;
				}
				return stage;
			}
			catch
			{
				this.EndImportTransaction(null);
				throw;
			}
		}
		public async Task<StagedSettingsImport> PrepareImportAsync(string path)
		{
			if (path == null) throw new ArgumentNullException(nameof(path));
			long revision;
			string fingerprint;
			lock (this._sync)
			{
				if (this._importActive) throw new InvalidOperationException("A settings import transaction is already active.");
				this._importActive = true;
				this._importPhase = SettingsImportTransactionPhase.Preparing;
				revision = this._settingsRevision;
				fingerprint = ComputeFingerprint(this._settings);
			}

			try
			{
				var diskHash = await this.GetContentHashAsyncCore().ConfigureAwait(false);
				var imported = await this.LoadAsyncCore(path).ConfigureAwait(false) ?? new Dictionary<string, object>();
				AppPlacementSettings.ValidateDictionary(imported);
				var stage = new StagedSettingsImport(this._stageOwnerToken, imported, revision, fingerprint, diskHash);
				lock (this._sync)
				{
					if (!this._importActive) throw new InvalidOperationException("The import transaction was terminated during preparation.");
					this._activeStage = stage;
					this._importPhase = SettingsImportTransactionPhase.Prepared;
				}
				return stage;
			}
			catch
			{
				this.EndImportTransaction(null);
				throw;
			}
		}

		internal StagedSettingsImportClaim ClaimStagedImport(StagedSettingsImport stage)
		{
			lock (this._sync)
			{
				if (!this.IsCurrentStageUnderLock(stage) || this._importPhase != SettingsImportTransactionPhase.Prepared) return null;
				this._activeStageClaimToken = new object();
				this._importPhase = SettingsImportTransactionPhase.Claimed;
				return new StagedSettingsImportClaim(stage, this._activeStageClaimToken);
			}
		}

		internal Task<SettingsImportCommitResult> CommitClaimedImportAsync(StagedSettingsImportClaim claim, IDictionary<string, object> commitDictionary)
			=> this.CommitStagedImportCoreAsync(claim, commitDictionary);

		internal SettingsImportCommitResult DiscardClaimedImport(StagedSettingsImportClaim claim)
			=> this.DiscardStagedImportCore(claim);

		public async Task<SettingsImportCommitResult> CommitStagedImportAsync(StagedSettingsImport stage, IDictionary<string, object> commitDictionary)
		{
			if (commitDictionary == null)
			{
				if (!this.IsCurrentStage(stage)) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
				throw new ArgumentNullException(nameof(commitDictionary));
			}
			var claim = this.ClaimStagedImport(stage);
			if (claim == null) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
			return await this.CommitStagedImportCoreAsync(claim, commitDictionary).ConfigureAwait(false);
		}

		private async Task<SettingsImportCommitResult> CommitStagedImportCoreAsync(StagedSettingsImportClaim claim, IDictionary<string, object> commitDictionary)
		{
			if (!this.IsCurrentClaim(claim)) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
			if (commitDictionary == null) throw new ArgumentNullException(nameof(commitDictionary));
			var stage = claim.Stage;

			await this._serializationGate.WaitAsync().ConfigureAwait(false);
			try
			{
				string currentFingerprint;
				long currentRevision;
				lock (this._sync)
				{
					if (!this.IsCurrentClaimUnderLock(claim)) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
					currentFingerprint = ComputeFingerprint(this._settings);
					currentRevision = this._settingsRevision;
				}
				var diskHash = await this.GetContentHashAsyncCore().ConfigureAwait(false);
				if (currentRevision != stage.SettingsRevision || !string.Equals(currentFingerprint, stage.ActiveFingerprint, StringComparison.Ordinal) || !string.Equals(diskHash, stage.DiskHash, StringComparison.Ordinal))
				{
					this.EndImportTransaction(stage);
					return new SettingsImportCommitResult(SettingsImportCommitStatus.Conflict, null);
				}

				lock (this._sync)
				{
					if (!this.IsCurrentClaimUnderLock(claim))
						return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
					this._importPhase = SettingsImportTransactionPhase.Publishing;
				}
				var snapshot = new SortedDictionary<string, object>(CloneDictionary(commitDictionary));
				SettingsSaveResult saveResult;
				try
				{
					AppPlacementSettings.ValidateDictionary(snapshot);
					await this.SaveAsyncCore(snapshot).ConfigureAwait(false);
					lock (this._sync) saveResult = SettingsSaveResult.Success(checked(++this._completedSaveRevision), this._settingsRevision + 1);
				}
				catch (Exception ex)
				{
					saveResult = SettingsSaveResult.Failure(this._completedSaveRevision, currentRevision, Categorize(ex), ex.GetType());
					this.EndImportTransaction(stage);
					return new SettingsImportCommitResult(SettingsImportCommitStatus.PublishFailed, saveResult);
				}

				var startLoop = false;
				lock (this._sync)
				{
					this._settings = new Dictionary<string, object>(snapshot, StringComparer.Ordinal);
					this.AdvanceSettingsRevisionUnderLock();
					this.ApplyDeferredMutationsUnderLock();
					this._activeStage = null;
					this._activeStageClaimToken = null;
					this._importActive = false;
					this._importPhase = SettingsImportTransactionPhase.None;
					this._importNotificationPending = true;
					startLoop = this.StartDeferredSaveLoopUnderLock();
				}
				if (startLoop) _ = this.RunSaveLoopAsync();
				return new SettingsImportCommitResult(SettingsImportCommitStatus.Completed, saveResult);
			}
			finally
			{
				this._serializationGate.Release();
			}
		}

		public void PublishCommittedImport()
		{
			lock (this._sync)
			{
				if (!this._importNotificationPending) return;
				this._importNotificationPending = false;
			}
			this.NotifyReloaded();
		}
		public SettingsImportCommitResult DiscardStagedImport(StagedSettingsImport stage)
		{
			var claim = this.ClaimStagedImport(stage);
			if (claim == null)
			{
				lock (this._sync)
				{
					if (this.IsCurrentStageUnderLock(stage) && (this._importPhase == SettingsImportTransactionPhase.Claimed || this._importPhase == SettingsImportTransactionPhase.Publishing))
						return new SettingsImportCommitResult(SettingsImportCommitStatus.Publishing, null);
				}
				return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
			}
			return this.DiscardStagedImportCore(claim);
		}

		private SettingsImportCommitResult DiscardStagedImportCore(StagedSettingsImportClaim claim)
		{
			var startLoop = false;
			lock (this._sync)
			{
				if (!this.IsCurrentClaimUnderLock(claim)) return new SettingsImportCommitResult(SettingsImportCommitStatus.InvalidStage, null);
				this._activeStage = null;
				this._activeStageClaimToken = null;
				this._importActive = false;
				this._importPhase = SettingsImportTransactionPhase.None;
				this.ApplyDeferredMutationsUnderLock();
				startLoop = this.StartDeferredSaveLoopUnderLock();
			}
			if (startLoop) _ = this.RunSaveLoopAsync();
			return new SettingsImportCommitResult(SettingsImportCommitStatus.Discarded, null);
		}

		void ISerializationProvider.Load() => this.LoadAsync().GetAwaiter().GetResult();

		public async Task LoadAsync()
		{
			var dic = await this.LoadAsyncCore().ConfigureAwait(false);
			AppPlacementSettings.ValidateDictionary(dic);
			lock (this._sync)
			{
				this._settings = dic != null ? CloneDictionary(dic) : new Dictionary<string, object>(StringComparer.Ordinal);
				this.AdvanceSettingsRevisionUnderLock();
			}
			this.IsLoaded = true;
		}

		public async Task ImportAsync(string path)
		{
			var stage = await this.PrepareImportAsync(path).ConfigureAwait(false);
			var result = await this.CommitStagedImportAsync(stage, stage.CreateCommitDictionary()).ConfigureAwait(false);
			if (!result.Succeeded) throw new InvalidOperationException("The staged settings import could not be committed.");
			this.PublishCommittedImport();
			this.IsLoaded = true;
		}

		protected abstract Task SaveAsyncCore(IDictionary<string, object> dic);
		protected abstract Task SaveAsyncCore(IDictionary<string, object> dic, string path);
		protected abstract Task<IDictionary<string, object>> LoadAsyncCore();
		protected abstract Task<IDictionary<string, object>> LoadAsyncCore(string path);
		protected virtual Task<string> GetContentHashAsyncCore() => Task.FromResult<string>(null);
		protected void OnReloaded() => this.NotifyReloaded();

		private async Task RunSaveLoopAsync()
		{
			while (true)
			{
				await this._serializationGate.WaitAsync().ConfigureAwait(false);
				long targetRevision;
				SettingsSaveResult result;
				try
				{
					IDictionary<string, object> snapshot;
					long settingsRevision;
					lock (this._sync)
					{
						targetRevision = this._requestedSaveRevision;
						settingsRevision = this._settingsRevision;
						snapshot = new SortedDictionary<string, object>(CloneDictionary(this._settings));
					}
					try
					{
						await this.SaveAsyncCore(snapshot).ConfigureAwait(false);
						lock (this._sync)
						{
							this._completedSaveRevision = Math.Max(this._completedSaveRevision, targetRevision);
							this.AdvanceSettingsRevisionUnderLock();
							result = SettingsSaveResult.Success(targetRevision, this._settingsRevision);
						}
					}
					catch (Exception ex)
					{
						result = SettingsSaveResult.Failure(targetRevision, settingsRevision, Categorize(ex), ex.GetType());
					}
				}
				finally { this._serializationGate.Release(); }

				List<SaveWaiter> completed;
				var continueLoop = false;
				lock (this._sync)
				{
					completed = this._saveWaiters.Where(waiter => waiter.RequestRevision <= targetRevision).ToList();
					this._saveWaiters.RemoveAll(waiter => waiter.RequestRevision <= targetRevision);
					continueLoop = !this._importActive && this._requestedSaveRevision > targetRevision;
					if (!continueLoop) this._saveLoopRunning = false;
				}
				foreach (var waiter in completed) waiter.Completion.TrySetResult(result);
				if (!continueLoop) return;
			}
		}

		private bool ApplyOrDefer(Action<Dictionary<string, object>> mutation)
		{
			lock (this._sync)
			{
				if (this._importActive)
				{
					this._deferredMutations.Enqueue(mutation);
					return false;
				}
				mutation(this._settings);
				this.AdvanceSettingsRevisionUnderLock();
				return true;
			}
		}

		private void EndImportTransaction(StagedSettingsImport stage)
		{
			var startLoop = false;
			lock (this._sync)
			{
				if (stage != null && !this.IsCurrentStageUnderLock(stage)) return;
				this._activeStage = null;
				this._activeStageClaimToken = null;
				this._importActive = false;
				this._importPhase = SettingsImportTransactionPhase.None;
				this.ApplyDeferredMutationsUnderLock();
				startLoop = this.StartDeferredSaveLoopUnderLock();
			}
			if (startLoop) _ = this.RunSaveLoopAsync();
		}

		private bool StartDeferredSaveLoopUnderLock()
		{
			if (this._saveLoopRunning || this._saveWaiters.Count == 0) return false;
			this._saveLoopRunning = true;
			return true;
		}

		private void ApplyDeferredMutationsUnderLock()
		{
			while (this._deferredMutations.Count != 0)
			{
				this._deferredMutations.Dequeue()(this._settings);
				this.AdvanceSettingsRevisionUnderLock();
			}
		}

		private bool IsCurrentStage(StagedSettingsImport stage)
		{
			lock (this._sync) return this.IsCurrentStageUnderLock(stage);
		}

		private bool IsCurrentStageUnderLock(StagedSettingsImport stage)
			=> stage != null && stage.IsOwnedBy(this._stageOwnerToken) && this._importActive && ReferenceEquals(this._activeStage, stage);

		private bool IsCurrentClaim(StagedSettingsImportClaim claim)
		{
			lock (this._sync) return this.IsCurrentClaimUnderLock(claim);
		}

		private bool IsCurrentClaimUnderLock(StagedSettingsImportClaim claim)
			=> claim != null
				&& this.IsCurrentStageUnderLock(claim.Stage)
				&& this._importPhase == SettingsImportTransactionPhase.Claimed
				&& ReferenceEquals(this._activeStageClaimToken, claim.ClaimToken);

		private void AdvanceSettingsRevisionUnderLock()
		{
			if (this._settingsRevision == long.MaxValue) throw new InvalidOperationException("The settings revision is exhausted.");
			this._settingsRevision++;
		}

		private void NotifyReloaded()
		{
			var handlers = this.Reloaded;
			if (handlers == null) return;
			foreach (EventHandler handler in handlers.GetInvocationList())
			{
				try { handler(this, EventArgs.Empty); }
				catch { }
			}
		}

		private static SettingsSaveErrorCategory Categorize(Exception exception)
		{
			if (exception is System.IO.IOException || exception is UnauthorizedAccessException) return SettingsSaveErrorCategory.Io;
			if (exception is System.Runtime.Serialization.SerializationException || exception is System.Xml.XmlException) return SettingsSaveErrorCategory.Serialization;
			return SettingsSaveErrorCategory.Unknown;
		}

		private sealed class SaveWaiter
		{
			internal SaveWaiter(long requestRevision, long stateRevision)
			{
				this.RequestRevision = requestRevision;
				this.StateRevision = stateRevision;
				this.Completion = new TaskCompletionSource<SettingsSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			internal long RequestRevision { get; }
			internal long StateRevision { get; }
			internal TaskCompletionSource<SettingsSaveResult> Completion { get; }
		}
	}
}
