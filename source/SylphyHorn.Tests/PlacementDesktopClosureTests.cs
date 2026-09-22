using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class PlacementDesktopClosureTests
	{
		private static readonly Guid A = Guid.NewGuid();
		private static readonly Guid B = Guid.NewGuid();
		private static readonly Guid C = Guid.NewGuid();
		private readonly PlacementDesktopClosure _closure = new PlacementDesktopClosure();
		private readonly HashSet<Guid> _armed = new HashSet<Guid> { B, C };
		private readonly HashSet<Guid> _created = new HashSet<Guid>();
		private PlacementDestination[] _targets = { PlacementDestination.ByNumber(2), PlacementDestination.ByNumber(3) };

		private static PlacementClosureDesktop Desktop(Guid id, PlacementDesktopOccupancy occupancy = PlacementDesktopOccupancy.Empty,
			string name = "work", bool nameAvailable = true)
			=> new PlacementClosureDesktop(new PlacementDesktop(id, name, nameAvailable), occupancy);

		private Guid? Observe(long now, PlacementClosureDesktop[] desktops, Guid? current = null, bool available = true, bool closeCreated = false)
			=> this._closure.Observe(desktops, current ?? A, this._targets, this._armed, this._created, closeCreated, available, now);

		[Fact]
		public void NativeMembershipKeepsKnownDesktopsAndExcludesExplicitlyUnassignedWindows()
		{
			Assert.Equal(B, PlacementDesktopOccupancyReader.LocateDesktop(() => B, () => false));
			Assert.Null(PlacementDesktopOccupancyReader.LocateDesktop(() => Guid.Empty, () => false));
		}

		[Theory]
		[InlineData(unchecked((int)0x8002802B))]
		[InlineData(unchecked((int)0x80070490))]
		public void MissingDesktopMembershipRequiresIndependentShellConfirmation(int failure)
		{
			Guid Missing() => throw new COMException("synthetic", failure);
			Assert.Null(PlacementDesktopOccupancyReader.LocateDesktop(Missing, () => throw new COMException("synthetic", failure)));
			Assert.Null(PlacementDesktopOccupancyReader.LocateDesktop(Missing, () => true));
			Assert.Throws<COMException>(() => PlacementDesktopOccupancyReader.LocateDesktop(Missing, () => false));
			Assert.Throws<COMException>(() => PlacementDesktopOccupancyReader.LocateDesktop(Missing,
				() => throw new COMException("denied", unchecked((int)0x80070005))));
		}

		[Fact]
		public void NativeAccessFailureIsNeverInterpretedAsAnEmptyDesktop()
		{
			Assert.Throws<COMException>(() => PlacementDesktopOccupancyReader.LocateDesktop(
				() => throw new COMException("denied", unchecked((int)0x80070005)), () => true));
		}

		[Fact]
		public void WaitsOneSecondThenSelectsOnlyTheLastDesktop()
		{
			var desktops = new[] { Desktop(A), Desktop(B), Desktop(C) };
			Assert.Null(this.Observe(100, desktops));
			Assert.Null(this.Observe(1099, desktops));
			Assert.Equal(C, this.Observe(1100, desktops));
			Assert.Equal(B, this.Observe(1101, new[] { Desktop(A), Desktop(B) }));
		}

		[Theory]
		[InlineData(2)]
		[InlineData(0)]
		public void OccupiedOrUnknownSuffixPreventsRemovingAnEmptyInteriorDesktop(int occupancy)
		{
			var desktops = new[] { Desktop(A), Desktop(B), Desktop(C, (PlacementDesktopOccupancy)occupancy) };
			Assert.Null(this.Observe(0, desktops));
			Assert.Null(this.Observe(1000, desktops));
			Assert.False(this._closure.Waiting);
		}

		[Fact]
		public void CurrentDesktopCanCloseAfterOneSecondWithoutSwitchingAway()
		{
			var desktops = new[] { Desktop(A), Desktop(B) };
			Assert.Null(this.Observe(0, desktops, current: B));
			Assert.Null(this.Observe(999, desktops, current: B));
			Assert.Equal(B, this.Observe(1000, desktops, current: B));
		}

		[Fact]
		public void LastRemainingDesktopIsNeverSelected()
		{
			this._armed.Add(A);
			this._created.Add(A);
			Assert.Null(this.Observe(0, new[] { Desktop(A) }, closeCreated: true));
			Assert.Null(this.Observe(1000, new[] { Desktop(A) }, closeCreated: true));
		}

		[Fact]
		public void ReopeningOrLosingOccupancyEvidenceRestartsTheGracePeriod()
		{
			var empty = new[] { Desktop(A), Desktop(B) };
			this.Observe(0, empty);
			Assert.Null(this.Observe(500, new[] { Desktop(A), Desktop(B, PlacementDesktopOccupancy.Occupied) }));
			Assert.Null(this.Observe(1000, empty));
			Assert.Null(this.Observe(1500, new[] { Desktop(A), Desktop(B, PlacementDesktopOccupancy.Unknown) }));
			Assert.Null(this.Observe(2000, empty));
			Assert.Equal(B, this.Observe(3000, empty));
		}

		[Fact]
		public void AutomaticAndSelectedTargetsCanBeEnabledTogether()
		{
			this._targets = new[] { PlacementDestination.ByName("manual") };
			this._created.Add(C);
			var desktops = new[] { Desktop(A), Desktop(B, name: "manual"), Desktop(C) };
			Assert.Null(this.Observe(0, desktops, closeCreated: true));
			Assert.Equal(C, this.Observe(1000, desktops, closeCreated: true));
			Assert.Equal(B, this.Observe(1001, new[] { Desktop(A), Desktop(B, name: "manual") }, closeCreated: true));
		}

		[Fact]
		public void CreatedDesktopIsNotSelectedWhenAutomaticCleanupIsOff()
		{
			this._targets = Array.Empty<PlacementDestination>();
			this._created.Add(B);
			var desktops = new[] { Desktop(A), Desktop(B) };
			Assert.Null(this.Observe(0, desktops));
			Assert.Null(this.Observe(1000, desktops));
		}

		[Fact]
		public void UnarmedDesktopAndUnselectedSuffixArePreserved()
		{
			this._armed.Clear();
			var desktops = new[] { Desktop(A), Desktop(B) };
			Assert.Null(this.Observe(0, desktops));
			Assert.Null(this.Observe(1000, desktops));
			this._armed.Add(B);
			this._targets = Array.Empty<PlacementDestination>();
			Assert.Null(this.Observe(2000, desktops));
			Assert.Null(this.Observe(3000, desktops));
		}

		[Fact]
		public void DuplicateNameSelectsLowestNumberSoItCannotDeleteAHigherDuplicate()
		{
			this._targets = new[] { PlacementDestination.ByName("work") };
			var desktops = new[] { Desktop(A, name: "home"), Desktop(B), Desktop(C) };
			Assert.Null(this.Observe(0, desktops));
			Assert.Null(this.Observe(1000, desktops));
		}

		[Fact]
		public void NumberSelectionIsReevaluatedAfterReordering()
		{
			this._targets = new[] { PlacementDestination.ByNumber(2) };
			this.Observe(0, new[] { Desktop(A), Desktop(B) });
			Assert.Null(this.Observe(1000, new[] { Desktop(B), Desktop(A) }, current: B));
		}

		[Fact]
		public void LossOfAvailabilityOrExplicitResetDiscardsTheGracePeriod()
		{
			var desktops = new[] { Desktop(A), Desktop(B) };
			this.Observe(0, desktops);
			Assert.Null(this.Observe(1000, desktops, available: false));
			Assert.Null(this.Observe(2000, desktops));
			this._closure.Reset();
			Assert.Null(this.Observe(3000, desktops));
			Assert.Equal(B, this.Observe(4000, desktops));
		}

		[Fact]
		public void UnknownNamesDoNotResolveNamedTargets()
		{
			this._targets = new[] { PlacementDestination.ByName("work") };
			var desktops = new[] { Desktop(A, nameAvailable: false), Desktop(B) };
			Assert.Null(this.Observe(0, desktops));
			Assert.Null(this.Observe(1000, desktops));
		}
	}
}
