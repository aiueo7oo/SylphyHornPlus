using System;
using System.Linq;
using System.Threading;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class PlacementExplicitApplicationTests
	{
		[Fact]
		public void CancellationDuringReadbackRetainsSubmittedAndNotStartedOutcomes()
		{
			using (var cancellation = new CancellationTokenSource())
			{
				var f = new Fixture { Count = 2 };
				f.Windows.ConfirmMove = false;
				f.Waiting = () =>
				{
					cancellation.Cancel();
					cancellation.Token.ThrowIfCancellationRequested();
				};
				var application = f.Engine.ApplyRules(f.Map, null, false, f.Authorize, cancellation.Token);
				Assert.Equal(PlacementOutcome.Unconfirmed, application.Results[0].Outcome);
				Assert.Equal(PlacementOutcome.Cancelled, application.Results[1].Outcome);
				Assert.Equal(1, f.Windows.Moves);
			}
		}

		[Fact]
		public void DryRunDoesNotMoveOrInvalidateTheGuiPreview()
		{
			var f = new Fixture();
			var gui = f.Preview();
			var query = f.Engine.ApplyRules(f.Map, null, true, f.Authorize, CancellationToken.None);
			Assert.Single(query.Preview.Items);
			Assert.Empty(query.Results);
			Assert.Equal(0, f.Windows.Moves);
			Assert.Equal(PlacementOutcome.Moved, Assert.Single(f.Apply(gui, TestContext.Current.CancellationToken)).Outcome);
		}

		[Fact]
		public void DirectApplyReenumeratesAndFiltersByApplication()
		{
			var f = new Fixture();
			f.Engine.ApplyRules(f.Map, null, true, f.Authorize, CancellationToken.None);
			f.Count = 2;
			var other = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\other.exe");
			Assert.Empty(f.Engine.ApplyRules(f.Map, other, false, f.Authorize, CancellationToken.None).Preview.Items);
			Assert.Equal(0, f.Windows.Moves);
			var result = f.Engine.ApplyRules(f.Map, null, false, f.Authorize, CancellationToken.None);
			Assert.Equal(2, result.Preview.Items.Count);
			Assert.Equal(2, result.Results.Length);
			Assert.Equal(PlacementOutcome.Moved, result.Results[0].Outcome);
			// This fixture shares location across windows, so the first move changes the second candidate's source.
			Assert.Equal(PlacementOutcome.Changed, result.Results[1].Outcome);
		}

		[Fact]
		public void PreviewDoesNotMoveAndOnlyTheSelectedSnapshotCanBeUsedOnce()
		{
			var f = new Fixture();
			var preview = f.Preview();
			Assert.True(Assert.Single(preview.Items).CanApply);
			Assert.Equal(0, f.Windows.Moves);
			Assert.Equal(PlacementOutcome.Moved, Assert.Single(f.Apply(preview, TestContext.Current.CancellationToken)).Outcome);
			Assert.Equal(1, f.Windows.Moves);
			Assert.Throws<InvalidOperationException>(() => f.Apply(preview, TestContext.Current.CancellationToken));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public void ExpiredReplacedOrForeignPreviewCannotMove(int scenario)
		{
			var f = new Fixture();
			var preview = f.Preview();
			if (scenario == 0) f.Now = preview.ExpiresAt;
			if (scenario == 1) f.Preview();
			if (scenario == 2) preview = new Fixture().Preview();
			Assert.Throws<InvalidOperationException>(() => f.Apply(preview, TestContext.Current.CancellationToken));
			Assert.Equal(0, f.Windows.Moves);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public void InvalidSelectionIsRejectedBeforeAnyMove(int scenario)
		{
			var f = new Fixture();
			var preview = f.Preview();
			var id = preview.Items[0].Id;
			var ids = scenario == 0 ? Array.Empty<Guid>() : scenario == 1 ? new[] { id, id } : new[] { id, Guid.NewGuid() };
			Assert.Throws<ArgumentException>(() => f.Engine.Apply(preview, ids, f.Authorize, CancellationToken.None));
			Assert.Equal(0, f.Windows.Moves);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		public void ChangedOrCancelledTargetsAreNotMoved(int scenario)
		{
			var f = new Fixture();
			var preview = f.Preview();
			using (var cancellation = new CancellationTokenSource())
			{
				if (scenario == 0) f.Windows.Location = Guid.NewGuid();
				if (scenario == 1) f.Windows.ProcessVersion++;
				if (scenario == 2) f.Target = Guid.NewGuid();
				if (scenario == 3) f.Current = false;
				if (scenario == 4) cancellation.Cancel();
				var result = Assert.Single(f.Apply(preview, cancellation.Token));
				Assert.Equal(scenario < 3 ? PlacementOutcome.Changed : PlacementOutcome.Cancelled, result.Outcome);
				Assert.Equal(0, f.Windows.Moves);
			}
		}

		[Fact]
		public void CancellationBetweenValidationAndNativeAcceptanceCancelsThePermit()
		{
			var f = new Fixture();
			var preview = f.Preview();
			using (var cancellation = new CancellationTokenSource())
			{
				f.Windows.BeforePermit = () => cancellation.Cancel();
				Assert.Equal(PlacementOutcome.Cancelled, Assert.Single(f.Apply(preview, cancellation.Token)).Outcome);
				Assert.Equal(0, f.Windows.Moves);
			}
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public void PinnedAlreadyPlacedAndUnavailableDestinationsAreNotSelectable(int scenario)
		{
			var f = new Fixture();
			if (scenario == 0) f.Windows.Pinned = true;
			if (scenario == 1) f.Windows.Location = f.Target;
			var preview = scenario == 2 ? f.Engine.Preview(PlacementDesktopMap.Unavailable, CancellationToken.None) : f.Preview();
			Assert.False(Assert.Single(preview.Items).CanApply);
			Assert.Throws<ArgumentException>(() => f.Apply(preview, TestContext.Current.CancellationToken));
			Assert.Equal(0, f.Windows.Moves);
		}

		[Fact]
		public void MissingReadbackIsBoundedAndNeverRetriesTheMove()
		{
			var f = new Fixture();
			var preview = f.Preview();
			f.Windows.ConfirmMove = false;
			Assert.Equal(PlacementOutcome.Unconfirmed, Assert.Single(f.Apply(preview, TestContext.Current.CancellationToken)).Outcome);
			Assert.Equal(1, f.Windows.Moves);
			Assert.Equal(6000, f.Now);
		}

		[Fact]
		public void PreviewCapacityAndDeadlineNeverReturnAPartialSelection()
		{
			var f = new Fixture();
			f.Count = 257;
			Assert.Throws<InvalidOperationException>(() => f.Preview());
			f.Count = 1;
			f.Windows.AfterInspect = () => f.Now += 5000;
			Assert.Throws<TimeoutException>(() => f.Preview());
			Assert.Equal(0, f.Windows.Moves);
		}

		[Fact]
		public void BatchStopsAfterThirtySecondsAndDoesNotMoveRemainingWindows()
		{
			var f = new Fixture();
			f.Count = 10;
			var preview = f.Preview();
			f.Windows.ConfirmMove = false;
			var results = f.Apply(preview, TestContext.Current.CancellationToken);
			Assert.Equal(10, results.Length);
			Assert.Equal(6, f.Windows.Moves);
			Assert.Equal(31000, f.Now);
			Assert.All(results.Skip(6), result => Assert.Equal(PlacementOutcome.Cancelled, result.Outcome));
		}

		private sealed class Fixture
		{
			internal long Now = 1000;
			internal int Count = 1;
			internal bool Current = true;
			internal Action Waiting;
			internal Guid Target = Guid.NewGuid();
			internal readonly Windows Windows = new Windows();
			internal readonly PlacementExplicitApplication Engine;

			internal Fixture()
			{
				this.Engine = new PlacementExplicitApplication(
					PlacementProcessorTests.Configuration(),
					this.Windows,
					() => Enumerable.Range(1, this.Count).Select(n => new PlacementCandidate(new IntPtr(n), Guid.Empty, n, this.Now)).ToArray(),
					_ => this.Current,
					_ => "Fixture",
					() => this.Now,
					(delay, _) =>
					{
						this.Waiting?.Invoke();
						this.Now += delay;
					});
			}

			internal PlacementDesktopMap Map => new PlacementDesktopMap(new[] { new PlacementDesktop(this.Target, "target", true) });

			internal PlacementPreview Preview() => this.Engine.Preview(this.Map, CancellationToken.None);

			internal PlacementAuthorization Authorize(PlacementDestination destination, long deadline) => new PlacementAuthorization(this.Map.Resolve(destination), new PlacementMovePermit());

			internal PlacementResult[] Apply(PlacementPreview preview, CancellationToken cancellation = default)
				=> this.Engine.Apply(preview, preview.Items.Select(item => item.Id).ToArray(), this.Authorize, cancellation);
		}

		private sealed class Windows : IPlacementWindows
		{
			internal long ProcessVersion = 1;
			internal Guid Location = Guid.NewGuid();
			internal bool Pinned, ConfirmMove = true;
			internal int Moves;
			internal Action BeforePermit, AfterInspect;

			public PlacementWindowInspection Inspect(IntPtr window)
			{
				this.AfterInspect?.Invoke();
				var process = new PlacementProcessIdentity(42, this.ProcessVersion, @"C:\fixture\app.exe", null, null);
				return new PlacementWindowInspection(
					PlacementInspectionStatus.Ready,
					null,
					new PlacementWindowIdentity(window, 7, process, process,
						new PlacementAppIdentity(PlacementAppKind.ExecutablePath, process.Path)));
			}

			public PlacementWindowLocation Locate(IntPtr window) => new PlacementWindowLocation(this.Location, this.Pinned);

			public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target, PlacementMovePermit permit, Func<bool> current)
			{
				if (!current()) return PlacementMoveStatus.Cancelled;
				this.BeforePermit?.Invoke();
				if (!permit.TryStart()) return PlacementMoveStatus.Cancelled;
				this.Moves++;
				if (this.ConfirmMove) this.Location = target;
				return PlacementMoveStatus.Requested;
			}
		}
	}
}
