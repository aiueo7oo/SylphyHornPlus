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
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task CreationImageFailureDoesNotBlockPlacementAndFillersAreExcluded(bool reentrant)
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bmp");
			harness.Settings.WallpapersOnCreation = new[]
			{
				new SylphyHorn.Serialization.DesktopWallpaperOnCreation(null, 2, missing),
				new SylphyHorn.Serialization.DesktopWallpaperOnCreation(null, 3, missing),
			};
			var faults = 0;
			harness.Runtime.Faulted += (_, __) => faults++;
			var intermediate = Batch(1, 3, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "", ""));
			var final = Batch(1, 5, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "", ""), Entry(C, 2, "", ""));
			harness.Operations.Creating = () =>
			{
				var first = harness.Operations.CreateCalls == 1;
				if (reentrant) harness.Provider.PublishStable(first ? intermediate : final);
				return first ? B : C;
			};
			harness.Provider.EnqueueResult(intermediate);
			harness.Provider.EnqueueResult(final);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(3), session.Cancellation.Token);
			harness.Owner.Drain();
			var result = await request;
			Assert.Equal(C, result.Resolution.DesktopId);
			Assert.True(result.Permit.TryStart());
			Assert.Equal(1, faults);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Theory]
		[InlineData("unchanged")]
		[InlineData("wallpaper")]
		[InlineData("position")]
		public async Task UnchangedDestinationsPreservePermitAndDoNotRestartObservation(string change)
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(1), session.Cancellation.Token);
			harness.Owner.Drain();
			var result = await request;
			if (change == "position") harness.Runtime.EditWallpaperPosition(A, SylphyHorn.Services.WallpaperPosition.Center);
			else harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "name", change == "wallpaper" ? "updated" : "wall")));
			Assert.True(result.Permit.TryStart());
			Assert.Equal(0, session.DesktopChanges);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Theory]
		[InlineData("add")]
		[InlineData("name")]
		[InlineData("remove")]
		[InlineData("reorder")]
		[InlineData("current")]
		[InlineData("reset")]
		public async Task DestinationChangesRevokePermit(string change)
		{
			var factory = new Factory();
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "home", ""), Entry(B, 1, "work", "")), factory);
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(2), session.Cancellation.Token);
			harness.Owner.Drain();
			var result = await request;
			var entries = change == "add" ? new[] { Entry(A, 0, "home", ""), Entry(B, 1, "work", ""), Entry(C, 2, "new", "") }
				: change == "remove" ? new[] { Entry(A, 0, "home", "") }
				: change == "reorder" ? new[] { Entry(B, 0, "work", ""), Entry(A, 1, "home", "") }
				: new[] { Entry(A, 0, "home", ""), Entry(B, 1, change == "name" ? "renamed" : "work", "") };
			harness.Provider.PublishStable(Batch(change == "reset" ? 2 : 1, 2, change == "current" ? B : A, entries));
			Assert.False(result.Permit.TryStart());
			Assert.Equal(1, session.DesktopChanges);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

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

		[Theory]
		[InlineData(false, false)]
		[InlineData(false, true)]
		[InlineData(true, false)]
		[InlineData(true, true)]
		public async Task MissingDestinationsAreCreatedOnlyWhenEnabled(bool enabled, bool named)
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var destination = named ? PlacementDestination.ByName("work") : PlacementDestination.ByNumber(3);
			harness.Operations.Creating = () => harness.Operations.CreateCalls == 1 ? B : C;
			harness.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, named ? "work" : "", "")));
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "", ""), Entry(C, 2, "", "")));
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, enabled));
			Assert.Equal(0, harness.Operations.CreateCalls);
			var session = factory.Sessions[0];
			var request = session.Authorize(destination, session.Cancellation.Token);
			harness.Owner.Drain();
			var result = await request;
			Assert.Equal(enabled ? PlacementResolutionStatus.Resolved : PlacementResolutionStatus.Missing, result.Resolution.Status);
			Assert.Equal(enabled ? (named ? 1 : 2) : 0, harness.Operations.CreateCalls);
			Assert.Equal(enabled && named ? 1 : 0, harness.Operations.NameCalls);
			if (enabled) Assert.Equal(named ? B : C, result.Resolution.DesktopId);
			if (enabled && named) Assert.Equal("work", Assert.Single(harness.Operations.NameValues));
			Assert.Empty(harness.Operations.DesktopOperationNames);
			Assert.Equal(A, harness.Runtime.State.CurrentDesktopId);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task CreationReusesLowestNumberedDuplicateWithoutCreatingOrRenaming()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			harness.Provider.PublishStable(Batch(1, 2, A, Entry(B, 0, "name", ""), Entry(A, 1, "name", "")));
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByName("name"), session.Cancellation.Token);
			harness.Owner.Drain();
			Assert.Equal(B, (await request).Resolution.DesktopId);
			Assert.Equal(0, harness.Operations.CreateCalls);
			Assert.Equal(0, harness.Operations.NameCalls);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public async Task CreationOrReconciliationFailureStopsWithoutRollbackOrSwitch(int failure)
		{
			var factory = new Factory();
			var harness = await Create(factory);
			if (failure == 0) harness.Operations.CreateFailure = new InvalidOperationException("synthetic");
			if (failure == 1) harness.Operations.NameFailure = new InvalidOperationException("synthetic");
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var request = session.Authorize(failure == 1 ? PlacementDestination.ByName("work") : PlacementDestination.ByNumber(3), session.Cancellation.Token);
			harness.Owner.Drain();
			await Assert.ThrowsAsync<InvalidOperationException>(() => request);
			Assert.Equal(1, harness.Operations.CreateCalls);
			Assert.Empty(harness.Operations.DesktopOperationNames);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task DisableDuringReconciliationPreventsFurtherCreation()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var refresh = new TaskCompletionSource<WindowsDesktop.VirtualDesktopReconciliationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			harness.Provider.NextRequest = refresh.Task;
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(3), session.Cancellation.Token);
			harness.Owner.Drain();
			Assert.Equal(1, harness.Operations.CreateCalls);
			var disable = harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
			session.Release();
			await disable;
			refresh.SetResult(WindowsDesktop.VirtualDesktopReconciliationResult.Succeeded(Batch(1, 2, A, Entry(A, 0, "name", ""), Entry(B, 1, "", ""))));
			harness.Owner.Drain();
			Assert.Equal(1, harness.Operations.CreateCalls);
			Assert.Empty(harness.Operations.DesktopOperationNames);
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ConcurrentCreationRequestsRecheckStateBeforeCreatingAgain()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			var refresh = new TaskCompletionSource<WindowsDesktop.VirtualDesktopReconciliationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			harness.Provider.NextRequest = refresh.Task;
			harness.Operations.Creating = () => B;
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var first = session.Authorize(PlacementDestination.ByNumber(2), session.Cancellation.Token);
			harness.Owner.Drain();
			var second = session.Authorize(PlacementDestination.ByNumber(2), session.Cancellation.Token);
			harness.Owner.Drain();
			Assert.Equal(1, harness.Operations.CreateCalls);
			refresh.SetResult(WindowsDesktop.VirtualDesktopReconciliationResult.Succeeded(Batch(1, 2, A, Entry(A, 0, "name", ""), Entry(B, 1, "", ""))));
			Assert.Equal(B, (await first).Resolution.DesktopId);
			Assert.Equal(B, (await second).Resolution.DesktopId);
			Assert.Equal(1, harness.Operations.CreateCalls);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task CancelledCreationRequestCannotCreateWhenOwnerResumes()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			using (var cancellation = new CancellationTokenSource())
			{
				var request = session.Authorize(PlacementDestination.ByNumber(2), cancellation.Token);
				cancellation.Cancel();
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
				harness.Owner.Drain();
				Assert.Equal(0, harness.Operations.CreateCalls);
			}
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ExplicitApplicationNeverRecreatesAMissingPreviewDestination()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, PlacementProcessorTests.Configuration().Rules, true));
			var session = factory.Sessions[0];
			var request = session.Authorize(PlacementDestination.ByNumber(2), session.Cancellation.Token, false);
			harness.Owner.Drain();
			Assert.Equal(PlacementResolutionStatus.Missing, (await request).Resolution.Status);
			Assert.Equal(0, harness.Operations.CreateCalls);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Theory]
		[InlineData(false, false)]
		[InlineData(true, false)]
		[InlineData(false, true)]
		[InlineData(true, true)]
		public async Task ClosingUsesManualTargetsOrPersistedCreatedGroupsWithoutChangingRules(bool created, bool current)
		{
			var factory = new Factory();
			long now = 0;
			var harness = Harness.Create(Batch(1, 1, current ? B : A, Entry(A, 0, "home", ""), Entry(B, 1, "work", "")), factory, () => now);
			if (created) harness.Settings.CreatedGroups = new[] { new PlacementCreatedGroup(new[] { B }, true) };
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			var configuration = new AppPlacementConfiguration(true, Array.Empty<AppPlacementRule>(), closeCreatedDesktops: created,
				closingTargets: created ? null : new[] { PlacementDestination.ByName("work") });
			await harness.Runtime.ConfigurePlacementAsync(configuration);
			var session = Assert.Single(factory.Sessions);
			Assert.NotNull(session.Close);
			if (!created) await session.Close(new PlacementOccupancyObservation(true, new[] { B }, () => true), session.Cancellation.Token);
			Assert.True(await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token));
			now = 999;
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			Assert.Empty(harness.Operations.RemovedIds);
			now = 1000;
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			Assert.Equal(B, Assert.Single(harness.Operations.RemovedIds));
			Assert.Same(configuration, session.Configuration);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task IncompleteOrStaleWindowObservationCannotClose(bool incomplete)
		{
			var factory = new Factory();
			long now = 0;
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "home", ""), Entry(B, 1, "work", "")), factory, () => now);
			harness.Settings.CreatedGroups = new[] { new PlacementCreatedGroup(new[] { B }, true) };
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, Array.Empty<AppPlacementRule>(), closeCreatedDesktops: true));
			var session = factory.Sessions[0];
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			now = 1000;
			await session.Close(new PlacementOccupancyObservation(!incomplete, Array.Empty<Guid>(), () => incomplete), session.Cancellation.Token);
			Assert.Empty(harness.Operations.RemovedIds);
			now = 2000;
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			Assert.Empty(harness.Operations.RemovedIds);
			session.Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ClosureOffAddsNoCallbackAndGlobalDisableStartsNoSession()
		{
			var factory = new Factory();
			var harness = await Create(factory);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(false, Array.Empty<AppPlacementRule>(), closeCreatedDesktops: true));
			Assert.Empty(factory.Sessions);
			await harness.Runtime.ConfigurePlacementAsync(PlacementProcessorTests.Configuration());
			Assert.Null(Assert.Single(factory.Sessions).Close);
			factory.Sessions[0].Release();
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task UsingCreatedDestinationArmsItsEmptyFillersAndPersistsTheGroup()
		{
			var factory = new Factory();
			long now = 0;
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "home", ""), Entry(B, 1, "", ""), Entry(C, 2, "work", "")), factory, () => now);
			harness.Settings.CreatedGroups = new[] { new PlacementCreatedGroup(new[] { B, C }, false) };
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, Array.Empty<AppPlacementRule>(), closeCreatedDesktops: true));
			var session = factory.Sessions[0];
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true, new[] { C }), session.Cancellation.Token);
			Assert.True(Assert.Single(harness.Settings.CreatedGroups).Used);
			now = 1000;
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			Assert.Equal(C, Assert.Single(harness.Operations.RemovedIds));
			harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "home", ""), Entry(B, 1, "", "")));
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			now = 2000;
			await session.Close(new PlacementOccupancyObservation(true, Array.Empty<Guid>(), () => true), session.Cancellation.Token);
			Assert.Equal(new[] { C, B }, harness.Operations.RemovedIds);
			Assert.Equal(B, Assert.Single(Assert.Single(harness.Settings.CreatedGroups).Desktops));
			session.Release();
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
				Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
				PlacementHistory history,
				Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null)
			{
				var session = new Session(configuration, authorize) { Close = closeDesktops };
				this.Sessions.Add(session);
				return session;
			}
		}

		private sealed class Session : IPlacementSession
		{
			internal Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> Close;
			internal PlacementDesktopMap PreviewMap;
			internal PlacementPreview AppliedPreview;

			public Task<PlacementRuleApplication> ApplyRulesAsync(PlacementDesktopMap map, PlacementAppIdentity app, bool dryRun, CancellationToken cancellation)
				=> throw new NotSupportedException();

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
			private readonly Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> _authorize;

			internal Task<PlacementAuthorization> Authorize(PlacementDestination destination, CancellationToken cancellation, bool allowCreation = true)
				=> this._authorize(destination, allowCreation, cancellation);
			internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
			internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			internal bool Stopping;

			internal Session(AppPlacementConfiguration configuration, Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize)
			{
				this.Configuration = configuration;
				this._authorize = authorize;
			}

			internal int DesktopChanges;

			public void DesktopChanged() => this.DesktopChanges++;

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
