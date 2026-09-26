using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		private readonly HashSet<Guid> _creationWallpaperSkipped = new HashSet<Guid>();
		private Dictionary<Guid, int> _heldCreationWallpapers;

		// Only the synchronous create/name call is held. There is no timer or long-lived name watch.
		private Guid WithCreationWallpapersHeld(Func<Guid> create, string name = null)
		{
			var previous = this._heldCreationWallpapers;
			var held = this._heldCreationWallpapers = new Dictionary<Guid, int>();
			var id = Guid.Empty;
			try
			{
				id = create();
				return id;
			}
			finally
			{
				this._heldCreationWallpapers = previous;
				// A failed create/name operation does not apply wallpaper using its unfinished identity.
				if (id != Guid.Empty)
				{
					foreach (var entry in held)
						this.ApplyCreationWallpaper(entry.Key, entry.Value, entry.Key == id ? name : null);
				}
			}
		}

		private void ApplyCreationWallpapers(DesktopCoordinatorTransition transition)
		{
			var change = transition?.StateChanged;
			if (change == null || change.Kind != DesktopStateChangeKind.Reconciled) return;
			foreach (var id in change.AddedIds)
			{
				var number = change.Snapshot.Order.ToList().IndexOf(id) + 1;
				if (this._heldCreationWallpapers != null) this._heldCreationWallpapers[id] = number;
				else this.ApplyCreationWallpaper(id, number);
			}
			// Unobserved IDs may remain until shutdown; retaining them protects against late notifications.
			// Observed IDs cannot be Added again without an intervening removal/reset.
			if (this._heldCreationWallpapers == null) this._creationWallpaperSkipped.ExceptWith(change.Snapshot.Order);
		}

		private void ApplyCreationWallpaper(Guid id, int number, string assignedName = null)
		{
			if (this._creationWallpaperSkipped.Remove(id)) return;
			if (!this._initialized || this._shutdownStarted || this._stopping || this._activeImportSession != null || this._preparedRuntime != null) return;
			var entries = this._settings.ReadWallpapersOnCreation();
			if (entries.Length == 0 || !this.State.Records.TryGetValue(id, out var record)) return;
			if (record.WallpaperPath.ReadStatus == VirtualDesktopReadStatus.Unsupported && !this._settings.PerDesktopWallpaperEnabled) return;
			if (assignedName == null && record.Name.ReadStatus != VirtualDesktopReadStatus.Unsupported && !record.Name.IsConfirmed) return;
			var name = assignedName ?? (record.Name.HasValue ? record.Name.Value : null);
			var setting = entries.FirstOrDefault(item => name != null && item.Name == name)
				?? entries.FirstOrDefault(item => item.Number == number);
			if (setting == null) return;
			try
			{
				WallpaperService.ValidateImage(setting.WallpaperPath);
				this.SetWallpaperPath(id, setting.WallpaperPath);
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("WallpaperOnCreation", ex.GetType(), id));
			}
		}
	}
}
