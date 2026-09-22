using System;
using System.Collections.Generic;
using System.Linq;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Services.AppPlacement
{
	internal enum PlacementDesktopOccupancy
	{
		Unknown,
		Empty,
		Occupied
	}

	/// <summary>One confirmed observation, including windows excluded from automatic placement.</summary>
	internal sealed class PlacementClosureDesktop
	{
		internal PlacementClosureDesktop(PlacementDesktop desktop, PlacementDesktopOccupancy occupancy)
		{
			this.Desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
			this.Occupancy = occupancy;
		}

		internal PlacementDesktop Desktop { get; }

		internal PlacementDesktopOccupancy Occupancy { get; }
	}

	/// <summary>Chooses a single suffix desktop; the runtime must revalidate before removing it.</summary>
	internal sealed class PlacementDesktopClosure
	{
		internal const long GraceMilliseconds = 1000;
		private readonly Dictionary<Guid, long> _emptySince = new Dictionary<Guid, long>();
		private long _lastObservation;

		internal bool Waiting { get; private set; }

		internal void Reset()
		{
			this._emptySince.Clear();
			this.Waiting = false;
			this._lastObservation = 0;
		}

		internal Guid? Observe(
			IReadOnlyList<PlacementClosureDesktop> desktops,
			Guid currentDesktop,
			IEnumerable<PlacementDestination> selectedTargets,
			ISet<Guid> armedDesktops,
			ISet<Guid> automaticallyCreatedDesktops,
			bool closeAutomaticallyCreated,
			bool available,
			long now)
		{
			if (desktops == null) throw new ArgumentNullException(nameof(desktops));
			if (selectedTargets == null) throw new ArgumentNullException(nameof(selectedTargets));
			if (armedDesktops == null) throw new ArgumentNullException(nameof(armedDesktops));
			if (automaticallyCreatedDesktops == null) throw new ArgumentNullException(nameof(automaticallyCreatedDesktops));
			if (now < 0) throw new ArgumentOutOfRangeException(nameof(now));
			if (!available || desktops.Count < 2 || !desktops.Any(item => item.Desktop.Id == currentDesktop))
			{
				this.Reset();
				return null;
			}
			if (now < this._lastObservation) this.Reset();
			this._lastObservation = now;
			var map = new PlacementDesktopMap(desktops.Select(item => item.Desktop));
			var targets = new HashSet<Guid>();
			foreach (var target in selectedTargets)
			{
				var resolution = map.Resolve(target);
				if (resolution.Status == PlacementResolutionStatus.Resolved) targets.Add(resolution.DesktopId.Value);
			}
			if (closeAutomaticallyCreated) targets.UnionWith(automaticallyCreatedDesktops);

			var stillEmpty = new HashSet<Guid>();
			foreach (var item in desktops)
			{
				var id = item.Desktop.Id;
				if (!targets.Contains(id) || !armedDesktops.Contains(id)
					|| item.Occupancy != PlacementDesktopOccupancy.Empty) continue;
				stillEmpty.Add(id);
				if (!this._emptySince.ContainsKey(id)) this._emptySince.Add(id, now);
			}
			foreach (var id in this._emptySince.Keys.Where(id => !stillEmpty.Contains(id)).ToArray())
				this._emptySince.Remove(id);

			// Do not skip a blocked last desktop: removing an interior desktop would renumber successors.
			var last = desktops[desktops.Count - 1].Desktop.Id;
			this.Waiting = this._emptySince.ContainsKey(last);
			return this._emptySince.TryGetValue(last, out var since) && now - since >= GraceMilliseconds
				? last : (Guid?)null;
		}
	}
}
