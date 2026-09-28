using System;
using System.Runtime.InteropServices;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class PlacementProcessorTests
	{
		[Theory]
		[InlineData(true, null, true, true)]
		[InlineData(false, null, true, false)]
		[InlineData(false, true, true, true)]
		[InlineData(true, false, true, false)]
		[InlineData(true, true, false, false)]
		public void OnlyAutomaticConfirmedMovesFollowTheEffectivePreference(bool global, bool? rule, bool automatic, bool follows)
		{
			var config = new AppPlacementConfiguration(true, new[] { new AppPlacementRule(Guid.NewGuid(), true,
				Identity(1).App, PlacementDestination.ByNumber(1), followForeground: rule) }, followForeground: global);
			var f = new Fixture(config, automatic);
			f.Windows.ConfirmMove = false;
			f.Step();
			Assert.Equal(0, f.Windows.Follows);
			f.Windows.Location = f.Target;
			f.Step();
			f.Step();
			Assert.Equal(PlacementOutcome.Moved, f.Work.Result.Outcome);
			Assert.Equal(follows ? 1 : 0, f.Windows.Follows);
			Assert.Equal(follows ? 1 : 0, f.Windows.FollowPreparations);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		[InlineData(5)]
		public void FollowGuardDistinguishesMoveFocusChangesFromNewInputAndDesktopChanges(int scenario)
		{
			var identity = Identity(1);
			var source = Guid.NewGuid();
			var target = Guid.NewGuid();
			var foreground = scenario == 1 ? IntPtr.Zero : identity.Window;
			uint? input = 10;
			Guid? desktop = source;
			var current = true;
			var switches = 0;
			var windows = new PlacementWindows(() => foreground, () => input, () => desktop,
				id => { Assert.Equal(target, id); switches++; });
			var follow = windows.PrepareFollow(identity, source, target, () => current);
			foreground = IntPtr.Zero; // Moving the window can change foreground without new input.
			if (scenario == 2)
			{
				input++;
			}
			if (scenario == 3)
			{
				desktop = Guid.NewGuid();
			}
			if (scenario == 4)
			{
				current = false;
			}
			if (scenario == 5)
			{
				input = null;
			}
			follow?.Invoke();
			Assert.Equal(scenario == 0 ? 1 : 0, switches);
		}

		[Fact]
		public void FailedFollowKeepsConfirmedMoveAndDoesNotRetry()
		{
			var f = new Fixture(automatic: true);
			f.Windows.FollowFailure = true;
			f.Step();
			f.Step();
			Assert.Equal(PlacementOutcome.Moved, f.Work.Result.Outcome);
			Assert.StartsWith("FollowFailed:", f.Work.Result.Reason);
			Assert.Equal(1, f.Windows.Moves);
			Assert.Equal(1, f.Windows.Follows);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void HostedPackageLosingChildEvidenceDoesNotMoveOrResend(bool alreadyRequested)
		{
			var host = new PlacementProcessIdentity(10, 100, @"C:\Windows\System32\ApplicationFrameHost.exe", null, null);
			var child = new PlacementProcessIdentity(20, 200, @"C:\Packages\Calculator.exe", "Example_publisher", "Example_publisher!App");
			var app = new PlacementAppIdentity(PlacementAppKind.PackageAppId, child.AppId);
			var configuration = new AppPlacementConfiguration(true, new[] { new AppPlacementRule(Guid.NewGuid(), true, app, PlacementDestination.ByNumber(1)) });
			var fixture = new Fixture(configuration);
			fixture.Windows.Identity = new PlacementWindowIdentity(new IntPtr(42), 7, host, child, app);
			fixture.Windows.ConfirmMove = false;
			fixture.Windows.Status = alreadyRequested ? PlacementInspectionStatus.Ready : PlacementInspectionStatus.NotReady;
			fixture.Step();
			Assert.Null(fixture.Work.Result);
			fixture.Windows.Identity = new PlacementWindowIdentity(new IntPtr(42), 7, host, null, app);
			fixture.Windows.Status = PlacementInspectionStatus.Ready;
			fixture.Now = fixture.Work.NextAt;
			fixture.Step();
			Assert.Equal(alreadyRequested ? PlacementOutcome.Unconfirmed : PlacementOutcome.Changed, fixture.Work.Result.Outcome);
			fixture.Step();
			Assert.Equal(alreadyRequested ? 1 : 0, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void AccessDeniedAtEachNativeStageIsTerminal(int stage)
		{
			var fixture = new Fixture();
			var denied = new COMException("Access denied", unchecked((int)0x80070005));
			if (stage == 0)
			{
				fixture.Windows.InspectFailure = denied;
			}
			if (stage == 1)
			{
				fixture.Windows.LocationFailure = denied;
			}
			if (stage == 2)
			{
				fixture.Windows.BeforeMove = _ => throw denied;
			}
			if (stage == 3)
			{
				fixture.Windows.AfterMove = () => fixture.Windows.LocationFailure = denied;
			}
			fixture.Step();
			Assert.Equal(
				stage == 3 ? PlacementOutcome.Unconfirmed : stage == 2 ? PlacementOutcome.MoveFailed : PlacementOutcome.Unavailable,
				fixture.Work.Result.Outcome);
			Assert.Equal("COMException:80070005", fixture.Work.Result.Reason);
			var inspections = fixture.Windows.Inspections;
			var locations = fixture.Windows.Locations;
			for (var i = 0; i < 10; i++) fixture.Step();
			Assert.Equal(inspections, fixture.Windows.Inspections);
			Assert.Equal(locations, fixture.Windows.Locations);
			Assert.Equal(stage == 3 ? 1 : 0, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public void UnusableReadbackCannotReportSuccessEvenWithRetainedIdentity(int scenario)
		{
			var fixture = new Fixture();
			fixture.Windows.AfterMove = () => fixture.Windows.Status = scenario == 0 ? PlacementInspectionStatus.Unavailable : scenario == 1 ? PlacementInspectionStatus.NotReady : PlacementInspectionStatus.Excluded;
			fixture.Step();
			Assert.Equal(PlacementOutcome.Unconfirmed, fixture.Work.Result.Outcome);
			Assert.Equal(1, fixture.Windows.Locations);
			fixture.Step();
			Assert.Equal(1, fixture.Windows.Moves);
		}

		[Fact]
		public void MoveRequiresReadbackAndIsNeverRepeated()
		{
			var fixture = new Fixture();
			fixture.Windows.ConfirmMove = false;
			fixture.Step();
			Assert.Null(fixture.Work.Result);
			Assert.True(fixture.Work.Requested);
			fixture.Now += 100;
			fixture.Step();
			Assert.Null(fixture.Work.Result);
			fixture.Windows.Location = fixture.Target;
			fixture.Step();
			Assert.Equal(PlacementOutcome.Moved, fixture.Work.Result.Outcome);
			fixture.Step();
			Assert.Equal(1, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void ManualMoveOrProcessReplacementDuringReadinessWaitIsRespected(bool replaceProcess)
		{
			var fixture = new Fixture();
			fixture.Windows.Status = PlacementInspectionStatus.NotReady;
			fixture.Step();
			if (replaceProcess)
			{
				fixture.Windows.Identity = Identity(2);
			}
			else
			{
				fixture.Windows.Location = Guid.NewGuid();
			}
			fixture.Windows.Status = PlacementInspectionStatus.Ready;
			fixture.Step();
			Assert.Equal(PlacementOutcome.Changed, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void NotReadyIsBoundedToFiveAttemptsWithinFiveSeconds()
		{
			var fixture = new Fixture();
			fixture.Windows.Status = PlacementInspectionStatus.NotReady;
			for (var n = 0; n < 5; n++)
			{
				fixture.Step();
				fixture.Now = fixture.Work.NextAt;
			}
			Assert.Equal(PlacementOutcome.TimedOut, fixture.Work.Result.Outcome);
			Assert.Equal(5, fixture.Windows.Inspections);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void MissingLocationRetriesAndMovesOnlyAfterLocationBecomesAvailable()
		{
			var fixture = new Fixture();
			fixture.Windows.LocationAvailable = false;
			fixture.Step();
			Assert.Null(fixture.Work.Result);
			Assert.Equal(0, fixture.Windows.Moves);
			Assert.True(fixture.Work.NextAt > fixture.Now);
			fixture.Now = fixture.Work.NextAt;
			fixture.Windows.LocationAvailable = true;
			fixture.Step();
			Assert.Equal(PlacementOutcome.Moved, fixture.Work.Result.Outcome);
			Assert.Equal(1, fixture.Windows.Moves);
		}

		[Fact]
		public void PersistentlyMissingLocationStopsAfterFiveAttemptsWithoutMoving()
		{
			var fixture = new Fixture();
			fixture.Windows.LocationAvailable = false;
			for (var n = 0; n < 5; n++)
			{
				fixture.Step();
				fixture.Now = fixture.Work.NextAt;
			}
			Assert.Equal(PlacementOutcome.TimedOut, fixture.Work.Result.Outcome);
			Assert.Equal(5, fixture.Windows.Locations);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void ExpiredQueuedCandidateDoesNotEvenInspectTheWindow()
		{
			var fixture = new Fixture();
			fixture.Now = fixture.Work.Deadline;
			fixture.Step();
			Assert.Equal(PlacementOutcome.TimedOut, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Inspections);
		}

		[Fact]
		public void AuthorizationDeadlineIsReportedAsTimeoutWithoutMoving()
		{
			var fixture = new Fixture();
			fixture.BeforeAuthorization = () =>
			{
				fixture.Now = fixture.Work.Deadline;
				throw new OperationCanceledException();
			};
			fixture.Step();
			Assert.Equal(PlacementOutcome.TimedOut, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void NoRuleAvoidsDesktopComReads()
		{
			var fixture = new Fixture(AppPlacementConfiguration.Empty);
			fixture.Step();
			Assert.Equal(PlacementOutcome.NoRule, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Locations);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		public void IneligibleOrUncertainCandidatesNeverMove(int scenario)
		{
			var fixture = new Fixture();
			var expected = PlacementOutcome.Cancelled;
			switch (scenario)
			{
				case 0:
					fixture.Windows.Pinned = true;
					expected = PlacementOutcome.Excluded;
					break;
				case 1:
					fixture.Current = false;
					break;
				case 2:
					fixture.Map = PlacementDesktopMap.Unavailable;
					expected = PlacementOutcome.DestinationUnavailable;
					break;
				case 3:
					fixture.Windows.Status = PlacementInspectionStatus.Unavailable;
					expected = PlacementOutcome.Unavailable;
					break;
				case 4:
					fixture.BeforeAuthorization = () => fixture.Current = false;
					break;
			}
			fixture.Step();
			Assert.Equal(expected, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void CanceledPermitCannotStartAndStartedPermitCannotStartTwice()
		{
			var permit = new PlacementMovePermit();
			permit.Cancel();
			Assert.False(permit.TryStart());
			permit = new PlacementMovePermit();
			Assert.True(permit.TryStart());
			permit.Cancel();
			Assert.False(permit.TryStart());
		}

		[Fact]
		public void CancellationImmediatelyBeforeNativeRequestPreventsMove()
		{
			var fixture = new Fixture();
			fixture.Windows.BeforeMove = permit => permit.Cancel();
			fixture.Step();
			Assert.Equal(PlacementOutcome.Cancelled, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void FailedOrUnconfirmedNativeRequestIsTerminal(bool timeout)
		{
			var fixture = new Fixture();
			fixture.Windows.ConfirmMove = false;
			fixture.Windows.FailMove = !timeout;
			fixture.Step();
			if (timeout)
			{
				fixture.Now = fixture.Work.Deadline;
				fixture.Step();
			}
			Assert.Equal(timeout ? PlacementOutcome.Unconfirmed : PlacementOutcome.MoveFailed, fixture.Work.Result.Outcome);
			fixture.Step();
			Assert.Equal(1, fixture.Windows.Moves);
		}

		[Fact]
		public void DestinationAlreadyReachedDoesNotIssueMove()
		{
			var fixture = new Fixture();
			fixture.Windows.Location = fixture.Target;
			fixture.Step();
			Assert.Equal(PlacementOutcome.AlreadyPlaced, fixture.Work.Result.Outcome);
			Assert.Equal(0, fixture.Windows.Moves);
		}

		[Fact]
		public void ReadbackAfterStopCannotClaimSuccess()
		{
			var fixture = new Fixture();
			fixture.Windows.AfterMove = () => fixture.Current = false;
			fixture.Step();
			Assert.Equal(PlacementOutcome.Unconfirmed, fixture.Work.Result.Outcome);
			Assert.Equal(1, fixture.Windows.Moves);
		}

		[Fact]
		public void HistoryRetainsOnlyLatestTwoHundredResults()
		{
			var history = new PlacementHistory();
			for (var i = 0; i < 10000; i++) history.Add(new PlacementResult(new IntPtr(i), null, PlacementOutcome.Cancelled, null));
			var snapshot = history.Snapshot();
			Assert.Equal(200, snapshot.Length);
			Assert.Equal(new IntPtr(9800), snapshot[0].Window);
			Assert.Equal(new IntPtr(9999), snapshot[199].Window);
		}

		internal static AppPlacementConfiguration Configuration(bool enabled = true)
			=> new AppPlacementConfiguration(enabled, new[] { new AppPlacementRule(Guid.NewGuid(), true, Identity(1).App, PlacementDestination.ByNumber(1)) });

		private static PlacementWindowIdentity Identity(long createdAt)
		{
			var process = new PlacementProcessIdentity(42, createdAt, @"C:\fixture\app.exe", null, null);
			return new PlacementWindowIdentity(new IntPtr(42), 7, process, process, new PlacementAppIdentity(PlacementAppKind.ExecutablePath, process.Path));
		}

		private sealed class Fixture
		{
			internal readonly Guid Target = Guid.NewGuid();
			internal readonly FakeWindows Windows = new FakeWindows();
			internal readonly PlacementWorkItem Work = new PlacementWorkItem(new PlacementCandidate(new IntPtr(42), Guid.NewGuid(), 1, 1000));
			internal readonly PlacementProcessor Processor;
			internal PlacementDesktopMap Map;
			internal long Now = 1000;
			internal bool Current = true;
			internal Action BeforeAuthorization;

			internal Fixture(AppPlacementConfiguration configuration = null, bool automatic = false)
			{
				this.Map = new PlacementDesktopMap(new[] { new PlacementDesktop(this.Target, "target", true) });
				this.Processor = new PlacementProcessor(
					configuration ?? Configuration(),
					this.Windows,
					(destination, deadline) =>
					{
						Assert.Equal(this.Work.Deadline, deadline);
						this.BeforeAuthorization?.Invoke();
						return new PlacementAuthorization(this.Map.Resolve(destination), new PlacementMovePermit());
					},
					_ => this.Current,
					() => this.Now, automatic: automatic);
			}

			internal void Step() => this.Processor.Step(this.Work);
		}

		private sealed class FakeWindows : IPlacementWindows, IPlacementForeground
		{
			internal int Follows, FollowPreparations;
			internal bool FollowFailure;

			public Action PrepareFollow(PlacementWindowIdentity identity, Guid source, Guid target, Func<bool> current)
			{
				this.FollowPreparations++;
				return () =>
				{
					Assert.True(current());
					this.Follows++;
					if (this.FollowFailure)
					{
						throw new InvalidOperationException();
					}
				};
			}

			internal PlacementWindowIdentity Identity = PlacementProcessorTests.Identity(1);
			internal PlacementInspectionStatus Status = PlacementInspectionStatus.Ready;
			internal Guid Location = Guid.NewGuid();
			internal bool Pinned, FailMove;
			internal bool ConfirmMove = true, LocationAvailable = true;
			internal int Moves, Inspections, Locations;
			internal Action<PlacementMovePermit> BeforeMove;
			internal Action AfterMove;
			internal Exception InspectFailure, LocationFailure;

			public PlacementWindowInspection Inspect(IntPtr window)
			{
				this.Inspections++;
				if (this.InspectFailure != null)
				{
					throw this.InspectFailure;
				}
				return new PlacementWindowInspection(this.Status, null, this.Identity);
			}

			public PlacementWindowLocation Locate(IntPtr window)
			{
				this.Locations++;
				if (this.LocationFailure != null)
				{
					throw this.LocationFailure;
				}
				return this.LocationAvailable ? new PlacementWindowLocation(this.Location, this.Pinned) : null;
			}

			public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
				PlacementMovePermit permit, Func<bool> current, Action beforeMove = null)
			{
				this.BeforeMove?.Invoke(permit);
				if (!current() || !permit.TryStart()) return PlacementMoveStatus.Cancelled;
				if (this.Location == target) return PlacementMoveStatus.AlreadyPlaced;
				beforeMove?.Invoke();
				this.Moves++;
				if (this.FailMove)
				{
					throw new COMException("synthetic");
				}
				if (this.ConfirmMove)
				{
					this.Location = target;
				}
				this.AfterMove?.Invoke();
				return PlacementMoveStatus.Requested;
			}
		}
	}
}
