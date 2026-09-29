using System;
using System.Collections.Generic;
using System.Linq;

namespace SylphyHorn.AppPlacement
{
	public enum PlacementResolutionStatus
	{
		Resolved,
		Missing,
		StateUnavailable
	}

	public sealed class PlacementResolution
	{
		internal PlacementResolution(PlacementResolutionStatus status, Guid? desktopId = null)
		{
			this.Status = status;
			this.DesktopId = desktopId;
		}

		public PlacementResolutionStatus Status { get; }

		public Guid? DesktopId { get; }
	}

	public sealed class PlacementDesktop
	{
		public PlacementDesktop(Guid id, string name, bool nameAvailable)
		{
			if (id == Guid.Empty)
			{
				throw new ArgumentException("A desktop ID is required.", nameof(id));
			}
			this.Id = id;
			this.Name = name;
			this.NameAvailable = nameAvailable;
		}

		public Guid Id { get; }

		public string Name { get; }

		public bool NameAvailable { get; }
	}

	/// <summary>An immutable lookup projection of one current runtime state; it never owns or persists desktop state.</summary>
	public sealed class PlacementDesktopMap
	{
		private readonly Guid[] _order;
		private readonly Dictionary<string, PlacementResolution> _names = new Dictionary<string, PlacementResolution>(StringComparer.Ordinal);
		private readonly bool _namesAvailable;

		public static PlacementDesktopMap Unavailable { get; } = new PlacementDesktopMap();

		private PlacementDesktopMap() { }

		public PlacementDesktopMap(IEnumerable<PlacementDesktop> desktops)
		{
			if (desktops == null) throw new ArgumentNullException(nameof(desktops));
			var entries = desktops.ToArray();
			if (entries.Any(d => d == null) || entries.Select(d => d.Id).Distinct().Count() != entries.Length)
			{
				throw new ArgumentException("Desktop entries must have unique IDs.", nameof(desktops));
			}
			this._order = entries.Select(d => d.Id).ToArray();
			this._namesAvailable = entries.All(d => d.NameAvailable);
			foreach (var entry in entries.Where(d => d.NameAvailable && !string.IsNullOrEmpty(d.Name)))
			{
				// Entries follow the current desktop order; keep the lowest-numbered match.
				if (!this._names.ContainsKey(entry.Name))
				{
					this._names.Add(entry.Name, new PlacementResolution(PlacementResolutionStatus.Resolved, entry.Id));
				}
			}
		}

		public PlacementResolution Resolve(PlacementDestination destination)
		{
			if (destination == null) throw new ArgumentNullException(nameof(destination));
			if (this._order == null)
			{
				return new PlacementResolution(PlacementResolutionStatus.StateUnavailable);
			}
			if (destination.Kind == PlacementDestinationKind.Number)
			{
				return destination.Number <= this._order.Length
					? new PlacementResolution(PlacementResolutionStatus.Resolved, this._order[destination.Number - 1])
					: new PlacementResolution(PlacementResolutionStatus.Missing);
			}
			if (!this._namesAvailable)
			{
				return new PlacementResolution(PlacementResolutionStatus.StateUnavailable);
			}
			return this._names.TryGetValue(destination.Name, out var result) ? result : new PlacementResolution(PlacementResolutionStatus.Missing);
		}
	}
}
