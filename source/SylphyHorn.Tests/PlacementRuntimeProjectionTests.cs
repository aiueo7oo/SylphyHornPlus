using System.Collections.Generic;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using WindowsDesktop;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class PlacementRuntimeProjectionTests
	{
		[Fact]
		public async Task ProjectionFollowsProviderOrderAndRecreationButNotPersistedIdentity()
		{
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "work", "wall"), Entry(B, 1, "other", "wall")));
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).Status);
			await harness.Runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
			var first = harness.Runtime.PlacementDestinations;
			Assert.Same(first, harness.Runtime.PlacementDestinations);
			Assert.Equal(A, first.Resolve(PlacementDestination.ByName("work")).DesktopId);
			harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "other", "wall"), Entry(C, 1, "work", "wall")));
			harness.Owner.Drain();
			var next = harness.Runtime.PlacementDestinations;
			Assert.NotSame(first, next);
			Assert.Equal(C, next.Resolve(PlacementDestination.ByName("work")).DesktopId);
			Assert.Equal(B, next.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			Assert.Equal(A, first.Resolve(PlacementDestination.ByName("work")).DesktopId);
		}

		[Fact]
		public async Task UnconfirmedOrFailedNamesCannotResolveButNumbersCan()
		{
			var harness = await Harness.Initialized();
			harness.Runtime.EditName(A, "new");
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByName("new")).Status);
			Assert.Equal(A, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "new", "wall")));
			harness.Owner.Drain();
			Assert.Equal(A, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByName("new")).DesktopId);
			harness.Provider.PublishStable(Batch(1, 3, A, new VirtualDesktopStableEntry(A, 0, null, VirtualDesktopReadStatus.Failed, "wall", VirtualDesktopReadStatus.Success)));
			harness.Owner.Drain();
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByName("new")).Status);
		}

		[Fact]
		public async Task ImportAndShutdownHideEvenCachedProjection()
		{
			var harness = await Harness.Initialized();
			Assert.Equal(A, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			harness.Settings.Provider.NextImport = new Dictionary<string, object>();
			var stage = await harness.Settings.PrepareImportAsync("synthetic");
			harness.Settings.BlockCommit = true;
			var import = harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken);
			Assert.False(import.IsCompleted);
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).Status);
			harness.Settings.CommitRelease.SetResult(true);
			await import;
			Assert.Equal(A, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			var shutdownGate = new TaskCompletionSource<VirtualDesktopReconciliationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			harness.Provider.NextRequest = shutdownGate.Task;
			var shutdown = harness.Runtime.ShutdownAsync();
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).Status);
			shutdownGate.SetResult(VirtualDesktopReconciliationResult.Succeeded(Batch(1, 3, A, Entry(A, 0, "name", "wall"))));
			await shutdown;
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, harness.Runtime.PlacementDestinations.Resolve(PlacementDestination.ByNumber(1)).Status);
		}
	}
}
