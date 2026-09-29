using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.DesktopTransitions;
using WindowsDesktop;
using Xunit;
using static SylphyHorn.WindowsIntegrationTests.PlacementTestEnvironment;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementRuntimeIntegrationTests
	{
		private readonly ITestOutputHelper _output;

		public PlacementRuntimeIntegrationTests(ITestOutputHelper output) => this._output = output;

		[WpfFact(Timeout = 90000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task RealRuntimeCancelsPendingMovesAcrossDisableImportAndShutdown()
		{
			using (var environment = await PlacementTestEnvironment.Create(this._output))
			{
				var settings = new FileSettingsTransactions(Path.Combine(environment.Root, "settings.xml"));
				await settings.Provider.LoadAsync();
				var placement = new AppPlacementSettings(settings.Provider);
				var sessions = new ObservedSessionFactory();
				var owner = new GatedOwner(Dispatcher.CurrentDispatcher);
				var operations = new ForbiddenDesktopOperations();
				var provider = new ObservedProvider(
					environment.Provider,
					() =>
					{
						Assert.All(sessions.Sessions, session => Assert.Equal(TaskStatus.RanToCompletion, session.Completion.Status));
						environment.AssertDesktopUnchanged();
					});
				var runtime = new DesktopTransitionRuntime(provider, settings, owner, operations, placementFactory: sessions);
				var faults = new List<string>();
				runtime.Faulted += (_, fault) => faults.Add(fault.Category + ": " + fault.ExceptionType);
				IDisposable subscription = null;
				Task configurationChange = Task.CompletedTask;
				try
				{
					Assert.Equal(DesktopRuntimeInitializationStatus.Completed, (await runtime.InitializeAsync()).Status);
					// Same SerializableProperty subscription used by ApplicationPreparation, scoped to a temporary file.
					subscription = placement.Configuration.Subscribe(configuration =>
					{
						configurationChange = runtime.ConfigurePlacementAsync(configuration);
					});
					await runtime.ConfigurePlacementAsync(placement.Configuration.Value);
					Assert.Equal("Disabled", runtime.PlacementStatus);
					Assert.Empty(sessions.Sessions);
					placement.Configuration.Value = environment.Configuration();
					await configurationChange;
					await Until(() => runtime.PlacementStatus == "Active", "enabled runtime");
					var automatic = await environment.Show();
					await Until(() => runtime.PlacementResults.Any(item => item.Window == automatic), "runtime automatic move");
					Assert.Equal(PlacementOutcome.Moved, runtime.PlacementResults.Last(item => item.Window == automatic).Outcome);
					Assert.Equal(environment.Target, environment.Location(automatic));

					owner.Hold();
					var disabling = await environment.Show();
					await Until(() => owner.HasPending, "queued authorization before disable");
					placement.Configuration.Value = AppPlacementConfiguration.Empty;
					await configurationChange;
					Assert.Equal("Disabled", runtime.PlacementStatus);
					Assert.All(sessions.Sessions, session => Assert.Equal(TaskStatus.RanToCompletion, session.Completion.Status));
					owner.Release();
					Assert.Equal(environment.Source, environment.Location(disabling));
					Assert.Equal(PlacementOutcome.Cancelled, runtime.PlacementResults.Last(item => item.Window == disabling).Outcome);
					await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PreviewExistingPlacementAsync(CancellationToken.None));
					var disabled = await environment.Show();
					placement.Configuration.Value = environment.Configuration();
					await configurationChange;
					await Until(() => runtime.PlacementStatus == "Active", "re-enabled runtime");
					Assert.Equal(environment.Source, environment.Location(disabled));
					var oldPreview = await runtime.PreviewExistingPlacementAsync(CancellationToken.None);
					var oldItem = oldPreview.Items.Single(item => item.Candidate.Window == disabled);
					Assert.True(oldItem.CanApply);
					this._output.WriteLine("Runtime setting subscription: auto move, pending authorization cancellation, disabled requests and fresh baseline verified.");

					var imported = environment.Configuration();
					var importFile = new FileInfo(Path.Combine(environment.Root, "import.xml"));
					await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["AppPlacementSettings.Configuration"] = imported }, importFile, settings.Provider.KnownTypes);
					settings.HoldCommit = true;
					var committing = runtime.ImportAsync(importFile.FullName, false, CancellationToken.None);
					await Bounded(settings.CommitEntered.Task, "import reaches disk commit");
					Assert.Equal("Suspended", runtime.PlacementStatus);
					Assert.All(sessions.Sessions, session => Assert.Equal(TaskStatus.RanToCompletion, session.Completion.Status));
					await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ApplyExistingPlacementAsync(oldPreview, new[] { oldItem.Id }, CancellationToken.None));
					var duringImport = await environment.Show();
					Assert.Equal(environment.Source, environment.Location(duringImport));
					settings.CommitRelease.TrySetResult(true);
					Assert.True((await Bounded(committing, "settings import")).Succeeded);
					await configurationChange;
					await Until(() => runtime.PlacementStatus == "Active", "imported configuration resumes monitoring");
					// Open immediately after readiness, before XML/preview checks add incidental delay.
					var afterImport = await environment.Show();
					Assert.True(placement.Configuration.Value.Enabled);
					Assert.NotNull(placement.Configuration.Value.FindEnabledRule(new PlacementAppIdentity(PlacementAppKind.ExecutablePath, environment.HostPath)));
					Assert.Equal(imported.Rules.Single().Id, Assert.Single(placement.Configuration.Value.Rules).Id);
					var saved = await AtomicSettingsFile.ReadAsync(new FileInfo(settings.Provider.Path), settings.Provider.KnownTypes);
					var savedPlacement = (AppPlacementConfiguration)saved["AppPlacementSettings.Configuration"];
					Assert.True(savedPlacement.Enabled);
					Assert.Equal(imported.Rules.Single().Id, Assert.Single(savedPlacement.Rules).Id);
					Assert.Equal(environment.HostPath, savedPlacement.Rules[0].App.Value, ignoreCase: true);
					Assert.Equal(imported.Rules[0].Destination.Number, savedPlacement.Rules[0].Destination.Number);
					await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ApplyExistingPlacementAsync(oldPreview, new[] { oldItem.Id }, CancellationToken.None));
					Assert.Equal(environment.Source, environment.Location(duringImport));
					try
					{
						await Until(() => runtime.PlacementResults.Any(value => value.Window == afterImport), "post-import automatic move");
					}
					catch
					{
						this._output.WriteLine(
							"Post-import failure: status={0}; sessions={1}; latest completion={2}; window={3}",
							runtime.PlacementStatus,
							sessions.Sessions.Count,
							sessions.Sessions.Last().Completion.Status,
							afterImport);
						foreach (var result in runtime.PlacementResults)
						{
							this._output.WriteLine("Window={0}; outcome={1}; reason={2}", result.Window, result.Outcome, result.Reason);
						}
						foreach (var fault in faults)
						{
							this._output.WriteLine(fault);
						}
						throw;
					}
					var importedResult = runtime.PlacementResults.Last(item => item.Window == afterImport);
					Assert.Equal(PlacementOutcome.Moved, importedResult.Outcome);
					Assert.Equal(imported.Rules.Single().Id, importedResult.Rule);
					Assert.Equal(environment.Target, environment.Location(afterImport));
					this._output.WriteLine("Import: old workers joined before disk commit; XML and reload publish new rule; "
						+ "paused windows stay; old preview rejected; new windows move under imported rule.");

					var preview = await runtime.PreviewExistingPlacementAsync(CancellationToken.None);
					var shutdownItem = preview.Items.Single(value => value.Candidate.Window == duringImport);
					Assert.True(shutdownItem.CanApply);
					owner.Hold();
					var applying = runtime.ApplyExistingPlacementAsync(preview, new[] { shutdownItem.Id }, CancellationToken.None);
					await Until(() => owner.HasPending, "queued explicit authorization before shutdown");
					environment.AssertDesktopUnchanged();
					var shutdown = await Bounded(runtime.ShutdownAsync(), "runtime shutdown");
					Assert.Equal(DesktopRuntimeShutdownStatus.Completed, shutdown.Status);
					Assert.Equal(PlacementOutcome.Cancelled, Assert.Single(await Bounded(applying, "cancelled application during shutdown")).Outcome);
					Assert.True(provider.Disposed);
					owner.Release();
					Assert.Equal(environment.Source, environment.Location(duringImport));
					await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PreviewExistingPlacementAsync(CancellationToken.None));
					Assert.Equal(VirtualDesktopReconciliationStatus.ShuttingDown, (await runtime.RequestReconciliationAsync()).Status);
					Assert.Empty(faults);
					Assert.Equal(0, operations.Calls);
					this._output.WriteLine(
						"Shutdown: pending explicit move cancelled; all {0} native sessions joined before provider disposal; disk save completed; no desktop mutations.",
						sessions.Sessions.Count);
				}
				finally
				{
					settings.CommitRelease.TrySetResult(true);
					subscription?.Dispose();
					var stopping = runtime.ShutdownAsync();
					owner.Release();
					await Bounded(stopping, "cleanup shutdown");
				}
			}
		}

		private sealed class ObservedSessionFactory : IPlacementSessionFactory
		{
			internal readonly List<IPlacementSession> Sessions = new List<IPlacementSession>();

			public IPlacementSession Start(
				AppPlacementConfiguration configuration,
				Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
				PlacementHistory history,
				Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null)
			{
				var session = new PlacementSessionFactory().Start(configuration, authorize, history, closeDesktops);
				this.Sessions.Add(session);
				return session;
			}
		}

		private sealed class GatedOwner : IDesktopOwnerContext
		{
			private readonly DispatcherDesktopOwnerContext _owner;
			private readonly object _gate = new object();
			private readonly Queue<Action> _held = new Queue<Action>();
			private bool _holding;

			internal GatedOwner(Dispatcher dispatcher) => this._owner = new DispatcherDesktopOwnerContext(dispatcher);

			public bool CheckAccess() => this._owner.CheckAccess();

			public bool Post(Action action)
			{
				lock (this._gate)
				{
					if (!this._holding)
					{
						return this._owner.Post(action);
					}
					this._held.Enqueue(action);
					return true;
				}
			}

			internal bool HasPending
			{
				get
				{
					lock (this._gate) return this._held.Count != 0;
				}
			}

			internal void Hold()
			{
				lock (this._gate) this._holding = true;
			}

			internal void Release()
			{
				Action[] actions;
				lock (this._gate)
				{
					this._holding = false;
					actions = this._held.ToArray();
					this._held.Clear();
				}
				foreach (var action in actions)
				{
					action();
				}
			}
		}

		private sealed class ObservedProvider : IDesktopProviderClient
		{
			private readonly VirtualDesktopProviderClient _inner;
			private readonly Action _beforeDispose;
			internal bool Disposed;

			internal ObservedProvider(VirtualDesktopProvider provider, Action beforeDispose)
			{
				this._inner = new VirtualDesktopProviderClient(provider);
				this._beforeDispose = beforeDispose;
			}

			public event EventHandler<VirtualDesktopStableBatch> StableBatchPublished
			{
				add => this._inner.StableBatchPublished += value;
				remove => this._inner.StableBatchPublished -= value;
			}

			public event EventHandler<VirtualDesktopCurrentTransition> CurrentTransitioned
			{
				add => this._inner.CurrentTransitioned += value;
				remove => this._inner.CurrentTransitioned -= value;
			}

			public event EventHandler<VirtualDesktopProviderFault> Faulted
			{
				add => this._inner.Faulted += value;
				remove => this._inner.Faulted -= value;
			}

			public Task<VirtualDesktopReconciliationResult> RequestReconciliationAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken)
				=> this._inner.RequestReconciliationAsync(reason, cancellationToken);

			public void Dispose()
			{
				this._beforeDispose();
				this._inner.Dispose();
				this.Disposed = true;
			}
		}

		private sealed class FileSettingsTransactions : IDesktopSettingsTransactions
		{
			internal readonly FileProvider Provider;
			internal bool HoldCommit;
			internal readonly TaskCompletionSource<bool> CommitEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			internal readonly TaskCompletionSource<bool> CommitRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			internal FileSettingsTransactions(string path) => this.Provider = new FileProvider(path);

			public DesktopStartupSeed CaptureStartupSeed() => DesktopStartupSeed.Empty;

			public DesktopWallpaperOnCreation[] ReadWallpapersOnCreation() => new GeneralSettings(this.Provider).DesktopWallpapersOnCreation.Value;
			public bool PerDesktopWallpaperEnabled => new GeneralSettings(this.Provider).ChangeBackgroundEachDesktop.Value;
			public PlacementCreatedGroup[] ReadCreatedDesktopGroups() => new AppPlacementSettings(this.Provider).CreatedDesktopGroups.Value;

			public void WriteCreatedDesktopGroups(PlacementCreatedGroup[] groups) => new AppPlacementSettings(this.Provider).CreatedDesktopGroups.Value = groups;

			public void ApplyProjection(DesktopSettingsProjection projection)
			{
				var values = new Dictionary<string, object>();
				SettingsService.ApplyDesktopProjection(values, projection);
				foreach (var pair in values)
				{
					this.Provider.SetValue(pair.Key, pair.Value);
				}
			}

			public long SettingsRevision => this.Provider.SettingsRevision;

			public Task<SettingsSaveResult> RequestSaveAsync(long stateRevision) => this.Provider.SaveWithResultAsync(stateRevision);

			public Task<StagedSettingsImport> PrepareImportAsync(string path) => this.Provider.PrepareImportAsync(path);

			public Task<StagedSettingsImport> PrepareResetAsync() => this.Provider.PrepareResetAsync();

			public DesktopSettingsImportClaim ClaimImport(StagedSettingsImport stage) => DesktopSettingsImportClaim.TryCreate(this.Provider, stage);

			public async Task<SettingsImportCommitResult> CommitImportAsync(DesktopSettingsImportClaim claim, IDictionary<string, object> dictionary)
			{
				if (this.HoldCommit)
				{
					this.CommitEntered.TrySetResult(true);
					await this.CommitRelease.Task;
				}
				return await claim.CommitAsync(this.Provider, dictionary);
			}

			public SettingsImportCommitResult DiscardImport(DesktopSettingsImportClaim claim) => claim.Discard(this.Provider);

			public void PublishImportCommitted() => this.Provider.PublishCommittedImport();
		}

		private sealed class FileProvider : DictionaryProvider
		{
			internal string Path { get; }

			internal FileProvider(string path) => this.Path = path;

			protected override Task SaveAsyncCore(IDictionary<string, object> dictionary) => AtomicSettingsFile.WriteAsync(dictionary, new FileInfo(this.Path), this.KnownTypes);

			protected override Task SaveAsyncCore(IDictionary<string, object> dictionary, string path) => AtomicSettingsFile.WriteAsync(dictionary, new FileInfo(path), this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore() => AtomicSettingsFile.ReadAsync(new FileInfo(this.Path), this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore(string path) => AtomicSettingsFile.ReadAsync(new FileInfo(path), this.KnownTypes);

			protected override Task<string> GetContentHashAsyncCore() => AtomicSettingsFile.HashAsync(new FileInfo(this.Path));
		}

		private sealed class ForbiddenDesktopOperations : IDesktopOperations
		{
			internal int Calls;

			private void Reject()
			{
				this.Calls++;
				throw new InvalidOperationException("Desktop mutation is outside the placement fixture.");
			}

			public Guid Create() { this.Reject(); return Guid.Empty; }

			public bool TryRemoveEmpty(Guid desktopId, Guid fallbackId, Func<bool> stillCurrent) { this.Reject(); return false; }

			public void SetName(Guid desktopId, string value) => this.Reject();

			public void SetWallpaperPath(Guid desktopId, string value) => this.Reject();

			public void ApplyWallpaper(Guid desktopId, string value, WallpaperPosition position) => this.Reject();

			public void MoveLeft(Guid desktopId) => this.Reject();

			public void MoveRight(Guid desktopId) => this.Reject();

			public void MoveFirst(Guid desktopId) => this.Reject();

			public void MoveLast(Guid desktopId) => this.Reject();

			public void Switch(Guid desktopId) => this.Reject();

			public void Remove(Guid desktopId, Guid? fallbackId = null) => this.Reject();
		}
	}
}
