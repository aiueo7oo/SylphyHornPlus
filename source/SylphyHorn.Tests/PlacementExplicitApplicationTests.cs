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
				var fixture = new Fixture { Count = 2 };
				fixture.Windows.ConfirmMove = false;
				fixture.Waiting = () =>
				{
					cancellation.Cancel();
					cancellation.Token.ThrowIfCancellationRequested();
				};
				var application = fixture.Engine.ApplyRules(fixture.Map, null, false, fixture.Authorize, cancellation.Token);
				Assert.Equal(PlacementOutcome.Unconfirmed, application.Results[0].Outcome);
				Assert.Equal(PlacementOutcome.Cancelled, application.Results[1].Outcome);
				Assert.Equal(1, fixture.Windows.Moves);
			}
		}

		[Fact]
		public void DryRunDoesNotMoveOrInvalidateTheGuiPreview()
		{
			var fixture = new Fixture();
			var gui = fixture.Preview();
			var query = fixture.Engine.ApplyRules(fixture.Map, null, true, fixture.Authorize, CancellationToken.None);
			Assert.Single(query.Preview.Items);
			Assert.Empty(query.Results);
			Assert.Equal(0, fixture.Windows.Moves);
			Assert.Equal(PlacementOutcome.Moved, Assert.Single(fixture.Apply(gui, TestContext.Current.CancellationToken)).Outcome);
		}

		[Fact]
		public void DirectApplyReenumeratesAndFiltersByApplication()
		{
			var fixture = new Fixture();
			fixture.Engine.ApplyRules(fixture.Map, null, true, fixture.Authorize, CancellationToken.None);
			fixture.Count = 2;
			var other = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\other.exe");
			Assert.Empty(fixture.Engine.ApplyRules(fixture.Map, other, false, fixture.Authorize, CancellationToken.None).Preview.Items);
			Assert.Equal(0, fixture.Windows.Moves);
			var result = fixture.Engine.ApplyRules(fixture.Map, null, false, fixture.Authorize, CancellationToken.None);
			Assert.Equal(2, result.Preview.Items.Count);
			Assert.Equal(2, result.Results.Length);
			Assert.Equal(PlacementOutcome.Moved, result.Results[0].Outcome);
			// This fixture shares location across windows, so the first move changes the second candidate's source.
			Assert.Equal(PlacementOutcome.Changed, result.Results[1].Outcome);
		}

		[Fact]
		public void PreviewDoesNotMoveAndOnlyTheSelectedSnapshotCanBeUsedOnce()
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			Assert.True(Assert.Single(preview.Items).CanApply);
			Assert.Equal(0, fixture.Windows.Moves);
			Assert.Equal(PlacementOutcome.Moved, Assert.Single(fixture.Apply(preview, TestContext.Current.CancellationToken)).Outcome);
			Assert.Equal(1, fixture.Windows.Moves);
			Assert.Throws<InvalidOperationException>(() => fixture.Apply(preview, TestContext.Current.CancellationToken));
		}

		[Theory]
		[InlineData("Expired")]
		[InlineData("Replaced")]
		[InlineData("Foreign")]
		public void ExpiredReplacedOrForeignPreviewCannotMove(string scenario)
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			switch (scenario)
			{
				case "Expired":
					fixture.Now = preview.ExpiresAt;
					break;
				case "Replaced":
					fixture.Preview();
					break;
				case "Foreign":
					preview = new Fixture().Preview();
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(scenario));
			}
			Assert.Throws<InvalidOperationException>(() => fixture.Apply(preview, TestContext.Current.CancellationToken));
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData("Empty")]
		[InlineData("Duplicate")]
		[InlineData("NotInPreview")]
		public void InvalidSelectionIsRejectedBeforeAnyMove(string selection)
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			var id = preview.Items[0].Id;
			Guid[] ids;
			switch (selection)
			{
				case "Empty":
					ids = Array.Empty<Guid>();
					break;
				case "Duplicate":
					ids = new[] { id, id };
					break;
				case "NotInPreview":
					ids = new[] { id, Guid.NewGuid() };
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(selection));
			}
			Assert.Throws<ArgumentException>(() => fixture.Engine.Apply(preview, ids, fixture.Authorize, CancellationToken.None));
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData("SourceDesktopChanged", nameof(PlacementOutcome.Changed))]
		[InlineData("ProcessReplaced", nameof(PlacementOutcome.Changed))]
		[InlineData("TargetDesktopReplaced", nameof(PlacementOutcome.Changed))]
		[InlineData("NoLongerCurrent", nameof(PlacementOutcome.Cancelled))]
		[InlineData("Cancelled", nameof(PlacementOutcome.Cancelled))]
		public void ChangedOrCancelledTargetsAreNotMoved(string change, string expectedOutcome)
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			using (var cancellation = new CancellationTokenSource())
			{
				switch (change)
				{
					case "SourceDesktopChanged":
						fixture.Windows.Location = Guid.NewGuid();
						break;
					case "ProcessReplaced":
						fixture.Windows.ProcessVersion++;
						break;
					case "TargetDesktopReplaced":
						fixture.Target = Guid.NewGuid();
						break;
					case "NoLongerCurrent":
						fixture.Current = false;
						break;
					case "Cancelled":
						cancellation.Cancel();
						break;
					default:
						throw new ArgumentOutOfRangeException(nameof(change));
				}
				var result = Assert.Single(fixture.Apply(preview, cancellation.Token));
				Assert.Equal(expectedOutcome, result.Outcome.ToString());
				Assert.Equal(0, fixture.Windows.Moves);
			}
		}

		[Fact]
		public void CancellationBetweenValidationAndNativeAcceptanceCancelsThePermit()
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			using (var cancellation = new CancellationTokenSource())
			{
				fixture.Windows.BeforePermit = () => cancellation.Cancel();
				Assert.Equal(PlacementOutcome.Cancelled, Assert.Single(fixture.Apply(preview, cancellation.Token)).Outcome);
				Assert.Equal(0, fixture.Windows.Moves);
			}
		}

		[Theory]
		[InlineData("Pinned")]
		[InlineData("AlreadyPlaced")]
		[InlineData("DestinationUnavailable")]
		public void PinnedAlreadyPlacedAndUnavailableDestinationsAreNotSelectable(string scenario)
		{
			var fixture = new Fixture();
			var map = fixture.Map;
			switch (scenario)
			{
				case "Pinned":
					fixture.Windows.Pinned = true;
					break;
				case "AlreadyPlaced":
					fixture.Windows.Location = fixture.Target;
					break;
				case "DestinationUnavailable":
					map = PlacementDesktopMap.Unavailable;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(scenario));
			}
			var preview = fixture.Engine.Preview(map, CancellationToken.None);
			Assert.False(Assert.Single(preview.Items).CanApply);
			Assert.Throws<ArgumentException>(() => fixture.Apply(preview, TestContext.Current.CancellationToken));
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void MissingReadbackIsBoundedAndNeverRetriesTheMove()
		{
			var fixture = new Fixture();
			var preview = fixture.Preview();
			fixture.Windows.ConfirmMove = false;
			Assert.Equal(PlacementOutcome.Unconfirmed, Assert.Single(fixture.Apply(preview, TestContext.Current.CancellationToken)).Outcome);
			Assert.Equal(1, fixture.Windows.Moves);
			Assert.Equal(6000, fixture.Now);
		}

		[Fact]
		public void PreviewCapacityAndDeadlineNeverReturnAPartialSelection()
		{
			var fixture = new Fixture();
			fixture.Count = 257;
			Assert.Throws<InvalidOperationException>(() => fixture.Preview());
			fixture.Count = 1;
			fixture.Windows.AfterInspect = () => fixture.Now += 5000;
			Assert.Throws<TimeoutException>(() => fixture.Preview());
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void BatchStopsAfterThirtySecondsAndDoesNotMoveRemainingWindows()
		{
			var fixture = new Fixture();
			fixture.Count = 10;
			var preview = fixture.Preview();
			fixture.Windows.ConfirmMove = false;
			var results = fixture.Apply(preview, TestContext.Current.CancellationToken);
			Assert.Equal(10, results.Length);
			Assert.Equal(6, fixture.Windows.Moves);
			Assert.Equal(31000, fixture.Now);
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

			public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
				PlacementMovePermit permit, Func<bool> current, Action beforeMove = null)
			{
				if (!current()) return PlacementMoveStatus.Cancelled;
				this.BeforePermit?.Invoke();
				if (!permit.TryStart()) return PlacementMoveStatus.Cancelled;
				beforeMove?.Invoke();
				this.Moves++;
				if (this.ConfirmMove)
				{
					this.Location = target;
				}
				return PlacementMoveStatus.Requested;
			}
		}
	}
}
