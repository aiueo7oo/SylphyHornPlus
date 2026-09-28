using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		private readonly PlacementDesktopClosure _desktopClosure = new PlacementDesktopClosure();
		private readonly HashSet<Guid> _closingArmed = new HashSet<Guid>();
		private readonly List<PlacementCreatedGroup> _createdGroups = new List<PlacementCreatedGroup>();
		private bool _createdGroupsLoaded;
		private readonly Func<long> _closureClock;

		private void LoadCreatedDesktopGroups()
		{
			if (this._createdGroupsLoaded) return;
			this._createdGroups.Clear();
			this._createdGroups.AddRange(this._settings.ReadCreatedDesktopGroups());
			this._createdGroupsLoaded = true;
		}

		private void RecordPlacementCreatedDesktop(PlacementAuthorizationRequest request, Guid id)
		{
			this.LoadCreatedDesktopGroups();
			var previous = request.CreatedGroup;
			request.CreatedGroup = new PlacementCreatedGroup((previous?.Desktops ?? Array.Empty<Guid>()).Concat(new[] { id }), false);
			if (previous != null)
			{
				this._createdGroups.Remove(previous);
			}
			this._createdGroups.Add(request.CreatedGroup);
			this.SaveCreatedDesktopGroups();
		}

		private async void SaveCreatedDesktopGroups()
		{
			try
			{
				this._settings.WriteCreatedDesktopGroups(this._createdGroups.ToArray());
				var result = await this._settings.RequestSaveAsync(this._settings.SettingsRevision);
				if (!result.Succeeded)
				{
					this.ReportFault(new DesktopRuntimeFault("AppPlacement.CreatedDesktops.Save", typeof(InvalidOperationException)));
				}
			}
			catch (Exception ex) { this.ReportFault(new DesktopRuntimeFault("AppPlacement.CreatedDesktops.Save", ex.GetType())); }
		}

		private async Task<bool> ObserveDesktopClosureAsync(long generation, PlacementOccupancyObservation observation, CancellationToken cancellation)
		{
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellation.Register(() => completion.TrySetCanceled()))
			{
				void Invoke()
				{
					try
					{
						if (completion.Task.IsCompleted || cancellation.IsCancellationRequested || generation != this._placementGeneration)
						{
							completion.TrySetResult(false);
							return;
						}
						completion.TrySetResult(this.ObserveDesktopClosure(observation));
					}
					catch (Exception ex)
					{
						this._desktopClosure.Reset();
						this.ReportFault(new DesktopRuntimeFault("AppPlacement.CloseDesktop", ex.GetType()));
						completion.TrySetResult(false);
					}
				}
				if (this._owner.CheckAccess())
				{
					Invoke();
				}
				else if (!this._owner.Post(Invoke))
				{
					completion.TrySetCanceled();
				}
				return await completion.Task.ConfigureAwait(false);
			}
		}

		private bool ObserveDesktopClosure(PlacementOccupancyObservation observation)
		{
			this.EnsureOwnerAccess();
			if (!this._initialized || this._shutdownStarted || this._stopping || this._placementSuspended || this._placementChanging
				|| !this._placementConfiguration.Enabled || !this._placementConfiguration.HasClosingTargets
				|| !observation.Complete || !observation.StillCurrent() || ReferenceEquals(this.PlacementDestinations, PlacementDesktopMap.Unavailable))
			{
				this._desktopClosure.Reset();
				return false;
			}
			if (this._placementCreationGate.CurrentCount == 0) { this._desktopClosure.Reset(); return true; }
			var state = this.State;
			var generation = this._placementGeneration;
			if (!state.CurrentDesktopId.HasValue || observation.Occupied.Any(id => !state.Records.ContainsKey(id)))
			{
				this._desktopClosure.Reset();
				return false;
			}
			this.LoadCreatedDesktopGroups();
			var changed = false;
			for (var i = this._createdGroups.Count - 1; i >= 0; i--)
			{
				var group = this._createdGroups[i];
				var live = group.Desktops.Where(id => state.Records.ContainsKey(id)).ToArray();
				var used = group.Used || live.Any(id => observation.Occupied.Contains(id) || observation.Used.Contains(id));
				if (live.Length == 0) { this._createdGroups.RemoveAt(i); changed = true; }
				else if (live.Length != group.Desktops.Count || used != group.Used)
				{
					this._createdGroups[i] = new PlacementCreatedGroup(live, used);
					changed = true;
				}
			}
			if (changed)
			{
				this.SaveCreatedDesktopGroups();
			}
			this._closingArmed.IntersectWith(state.Order);
			this._closingArmed.UnionWith(observation.Occupied);
			this._closingArmed.UnionWith(observation.Used.Where(state.Records.ContainsKey));
			foreach (var group in this._createdGroups.Where(group => group.Used)) this._closingArmed.UnionWith(group.Desktops);
			var created = new HashSet<Guid>(this._createdGroups.SelectMany(group => group.Desktops));
			var desktops = state.Order.Select(id =>
			{
				var name = state.Records[id].Name;
				return new PlacementClosureDesktop(new PlacementDesktop(id, name.Value, name.HasValue && name.ReadStatus == WindowsDesktop.VirtualDesktopReadStatus.Success),
					observation.Occupied.Contains(id) ? PlacementDesktopOccupancy.Occupied : PlacementDesktopOccupancy.Empty);
			}).ToArray();
			var candidate = this._desktopClosure.Observe(desktops, state.CurrentDesktopId.Value,
				this._placementConfiguration.ClosingTargets, this._closingArmed, created,
				this._placementConfiguration.CloseCreatedDesktops, true, this._closureClock());
			if (candidate.HasValue && observation.StillCurrent() && ReferenceEquals(state, this.State))
			{
				if (this._operations.TryRemoveEmpty(candidate.Value, state.Order[state.Order.Count - 2],
					() => generation == this._placementGeneration && ReferenceEquals(state, this.State)
						&& !this._shutdownStarted && !this._stopping && !this._placementSuspended && observation.StillCurrent()))
				{
					this._desktopClosure.Reset();
					_ = this.RequestReconciliationAsync();
					return true;
				}
				this._desktopClosure.Reset();
				return false;
			}
			return this._desktopClosure.Waiting;
		}
	}
}
