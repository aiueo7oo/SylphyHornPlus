using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class PlacementRuntimeLifecycleTests
	{
		[Fact]
		public async Task DisabledAndEmptyConfigurationsAllocateNoSession()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration(false));
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, Array.Empty<AppPlacementRule>()));
			Assert.Empty(factory.Sessions);
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ExplicitRequestsUseActiveSessionAndAreBlockedWhileReconfiguring()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.PreviewExistingPlacementAsync(TestContext.Current.CancellationToken));
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			session.IsReady = true;
			var preview = await harness.Runtime.PreviewExistingPlacementAsync(TestContext.Current.CancellationToken);
			Assert.Equal(A, session.PreviewMap.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			await harness.Runtime.ApplyExistingPlacementAsync(preview, new[] { Guid.NewGuid() }, TestContext.Current.CancellationToken);
			Assert.Same(preview, session.AppliedPreview);
			var stopping = harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
			await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runtime.ApplyExistingPlacementAsync(preview, new[] { Guid.NewGuid() }, TestContext.Current.CancellationToken));
			session.Release();
			await stopping;
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task RapidChangesJoinOldSessionAndStartOnlyLatestConfiguration()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var old = factory.Sessions[0];
			var first = harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var latest = PlacementProcessorTests.Configuration();
			var second = harness.Runtime.ConfigurePlacementAsync(latest);
			Assert.True(old.Stopping);
			Assert.False(first.IsCompleted);
			Assert.False(second.IsCompleted);
			Assert.Single(factory.Sessions);
			old.Release();
			await Task.WhenAll(first, second);
			Assert.Equal(2, factory.Sessions.Count);
			Assert.Same(latest, factory.Sessions[1].Configuration);
			factory.Sessions[1].Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ShutdownCancelsQueuedAuthorizationWithoutPumpingOwnerAndJoinsNativeWork()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			var queued = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			Assert.False(queued.IsCompleted);
			var shutdown = harness.Runtime.ShutdownAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
			Assert.False(shutdown.IsCompleted);
			Assert.False(harness.Provider.Disposed);
			Assert.Equal(1, harness.Provider.ReconciliationRequestCount);
			session.Release();
			await shutdown;
			Assert.True(harness.Provider.Disposed);
			harness.Owner.Drain();
			Assert.Single(factory.Sessions);
		}

		[Fact]
		public async Task ImportWaitsForPlacementAndRestartsFreshWithReloadedConfiguration()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var settings = new Serialization.AppPlacementSettings(harness.Settings.Provider);
			settings.Configuration.Value = PlacementProcessorTests.Configuration();
			using (var subscription = settings.Configuration.Subscribe(configuration =>
			{
				_ = harness.Runtime.ConfigurePlacementAsync(configuration);
			}))
			{
				await harness.Runtime.ConfigurePlacementAsync(settings.Configuration.Value);
				var old = factory.Sessions[0];
				var latest = PlacementProcessorTests.Configuration();
				harness.Settings.Provider.NextImport = new Dictionary<string, object> { ["AppPlacementSettings.Configuration"] = latest };
				var stage = await harness.Settings.PrepareImportAsync("synthetic");
				var import = harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken);
				Assert.True(old.Stopping);
				Assert.Equal(0, harness.Settings.ImportCommitRequests);
				harness.Runtime.EditName(A, "queued");
				harness.Owner.Drain();
				Assert.Equal(0, harness.Operations.NameCalls);
				old.Release();
				Assert.True((await import).Succeeded);
				harness.Owner.Drain();
				Assert.Equal(1, harness.Operations.NameCalls);
				Assert.Equal(2, factory.Sessions.Count);
				Assert.Same(latest, factory.Sessions[1].Configuration);
				factory.Sessions[1].Release();
				await harness.Runtime.ShutdownAsync();
			}
		}

		[Fact]
		public async Task ShutdownDuringImportJoinPreventsImportAndRestart()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			harness.Settings.Provider.NextImport = new Dictionary<string, object>();
			var stage = await harness.Settings.PrepareImportAsync("synthetic");
			var import = harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken);
			var shutdown = harness.Runtime.ShutdownAsync();
			factory.Sessions[0].Release();
			await Task.WhenAll(import, shutdown);
			Assert.Equal(Serialization.SettingsImportCommitStatus.ShuttingDown, (await import).Status);
			Assert.Equal(0, harness.Settings.ImportCommitRequests);
			Assert.Single(factory.Sessions);
			Assert.True(harness.Provider.Disposed);
		}

		[Fact]
		public async Task NewStateRevokesUnstartedPermitAndNextRequestUsesLatestOrder()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			harness.Owner.Drain();
			var old = await request;
			Assert.Equal(A, old.Resolution.DesktopId);
			harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "other", "wall"), Entry(A, 1, "name", "wall")));
			harness.Owner.Drain();
			Assert.False(old.Permit.TryStart());
			request = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			harness.Owner.Drain();
			var next = await request;
			Assert.Equal(B, next.Resolution.DesktopId);
			var disable = harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
			Assert.False(next.Permit.TryStart());
			session.Release();
			await disable;
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task StaleGenerationCannotAuthorizeOrRevokeCurrentPermit()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var old = factory.Sessions[0];
			old.Release();
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[1];
			var request = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			harness.Owner.Drain();
			var current = await request;
			var stale = old.Authorize(PlacementDestination.ByNumber(1), CancellationToken.None);
			harness.Owner.Drain();
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, (await stale).Resolution.Status);
			Assert.Null((await stale).Permit);
			Assert.True(current.Permit.TryStart());
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task UnresponsiveOwnerKeepsOnlyOnePostedAuthorizationAcrossCanceledRequests()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			for (var n = 0; n < 100; n++)
			{
				using (var cancellation = new CancellationTokenSource())
				{
					var request = session.Authorize(PlacementDestination.ByNumber(1), cancellation.Token);
					cancellation.Cancel();
					await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
					Assert.Single(harness.Owner.Posted);
				}
			}
			var latest = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			Assert.Single(harness.Owner.Posted);
			harness.Owner.Drain();
			Assert.Equal(A, (await latest).Resolution.DesktopId);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task RejectedOwnerPostFinishesRequestWithoutLeavingAnAuthorization()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			harness.Owner.RejectPost = true;
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token));
			harness.Owner.RejectPost = false;
			var request = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			harness.Owner.Drain();
			Assert.Equal(A, (await request).Resolution.DesktopId);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task WorkerFailureIsReportedWithoutAutoRestartOrBlockingShutdown()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var faults = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			harness.Runtime.Faulted += (_, __) => faults.TrySetResult(true);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			factory.Sessions[0].Ended.SetException(new InvalidOperationException("synthetic"));
			await faults.Task;
			Assert.Single(factory.Sessions);
			await harness.Runtime.ShutdownAsync();
			Assert.True(harness.Provider.Disposed);
		}

		[Fact]
		public async Task StatusDistinguishesPreparationPauseRestartAndStopping()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			Assert.Equal("Disabled", harness.Runtime.PlacementStatus);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			Assert.Equal("Preparing", harness.Runtime.PlacementStatus);
			factory.Sessions[0].IsReady = true;
			Assert.Equal("Active", harness.Runtime.PlacementStatus);
			factory.Sessions[0].Release();
			Assert.Equal("Paused", harness.Runtime.PlacementStatus);
			await harness.Runtime.RestartPlacementAsync();
			Assert.Equal(2, factory.Sessions.Count);
			Assert.Equal("Preparing", harness.Runtime.PlacementStatus);
			var stopping = harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
			Assert.Equal("Stopping", harness.Runtime.PlacementStatus);
			factory.Sessions[1].Release();
			await stopping;
			Assert.Equal("Disabled", harness.Runtime.PlacementStatus);
			await harness.Runtime.ShutdownAsync();
		}

		private static async Task<Harness> Create(Factory factory)
		{
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "name", "wall")), factory);
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			return harness;
		}

		private sealed class Factory : IPlacementSessionFactory
		{
			internal readonly List<Session> Sessions = new List<Session>();

			public IPlacementSession Start(
				AppPlacementConfiguration configuration,
				Func<PlacementDestination, CancellationToken, Task<PlacementAuthorization>> authorize,
				PlacementHistory history)
			{
				var session = new Session(configuration, authorize);
				this.Sessions.Add(session);
				return session;
			}
		}

		private sealed class Session : IPlacementSession
		{
			internal PlacementDesktopMap PreviewMap;
			internal PlacementPreview AppliedPreview;

			public Task<PlacementPreview> PreviewAsync(PlacementDesktopMap map, CancellationToken cancellation)
			{
				this.PreviewMap = map;
				return Task.FromResult(new PlacementPreview(Array.Empty<PlacementPreviewItem>(), 1000));
			}

			public Task<PlacementResult[]> ApplyAsync(PlacementPreview preview, Guid[] selection, CancellationToken cancellation)
			{
				this.AppliedPreview = preview;
				return Task.FromResult(Array.Empty<PlacementResult>());
			}

			public bool IsReady { get; set; }

			internal readonly AppPlacementConfiguration Configuration;
			internal readonly Func<PlacementDestination, CancellationToken, Task<PlacementAuthorization>> Authorize;
			internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
			internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			internal bool Stopping;

			internal Session(AppPlacementConfiguration configuration, Func<PlacementDestination, CancellationToken, Task<PlacementAuthorization>> authorize)
			{
				this.Configuration = configuration;
				this.Authorize = authorize;
			}

			public Task Completion => this.Ended.Task;

			public Task StopAsync()
			{
				this.Stopping = true;
				this.Cancellation.Cancel();
				return this.Completion;
			}

			internal void Release() => this.Ended.TrySetResult(true);
		}
	}
}
