#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		// Bounds both one window enumeration and the number of unexpired window IDs.
		private const int CliWindowLimit = 4096;
		private const string CliWindowChangedMessage = "The window identity changed. Run window list again.";
		private static readonly TimeSpan CliWindowLifetime = TimeSpan.FromMinutes(5);
		private static readonly TimeSpan CliConfirmationInterval = TimeSpan.FromMilliseconds(100);

		private readonly Dictionary<Guid, CliWindowEntry> _cliWindows = new Dictionary<Guid, CliWindowEntry>();

		internal async Task<CliResponse> ExecuteCliAsync(CliCommand command, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			var progress = new CliRequestProgress();
			try
			{
				await this.RefreshCliStateAsync(cancellation);
				var data = await this.ExecuteCliOperationAsync(command, progress, cancellation);
				return CliResponse.Ok(command.Operation, data);
			}
			catch (CliFailure ex)
			{
				return CliResponse.Fail(command.Operation, progress.Submitted ? "result_unconfirmed" : ex.Code, ex.Message, !progress.Submitted && ex.Retryable);
			}
			catch (OperationCanceledException)
			{
				if (progress.Submitted)
				{
					return CliResponse.Fail(command.Operation, "result_unconfirmed", "The operation may have completed. Query current state before retrying.");
				}
				return CliResponse.Fail(command.Operation, "request_cancelled", "The request expired before any operation was submitted.");
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("Cli.Execute", ex.GetType()));
				return CliResponse.Fail(command.Operation, progress.Submitted ? "result_unconfirmed" : "operation_failed",
					"The desktop service could not complete this request.");
			}
		}

		private Task<CliData> ExecuteCliOperationAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			switch (command.Operation)
			{
				case "desktop list":
					return Task.FromResult(new CliData { Desktops = this.CliDesktopInfos() });
				case "window list":
					return Task.FromResult(this.ListCliWindows(cancellation));
				case "desktop create":
					return this.CreateCliDesktopAsync(command, progress, cancellation);
				case "desktop rename":
					return this.RenameCliDesktopAsync(command, progress, cancellation);
				case "desktop reorder":
					return this.ReorderCliDesktopAsync(command, progress, cancellation);
				case "desktop delete":
					return this.DeleteCliDesktopAsync(command, progress, cancellation);
				case "desktop wallpaper":
					return this.ChangeCliWallpaperAsync(command, progress, cancellation);
				case "desktop switch":
					return this.SwitchCliDesktopAsync(command, progress, cancellation);
				case "window pin":
					return this.SetCliWindowPinnedAsync(command, pinned: true, progress, cancellation);
				case "window unpin":
					return this.SetCliWindowPinnedAsync(command, pinned: false, progress, cancellation);
				case "window move":
				default:
					return this.MoveCliWindowAsync(command, progress, cancellation);
			}
		}

		private async Task<CliData> CreateCliDesktopAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			if (command.Name != null && !ProductInfo.IsNameSupportBuild)
			{
				throw DesktopNamesUnsupported();
			}
			this.EnsureCliAvailable(cancellation);
			progress.Submitted = true;
			var created = this.WithCreationWallpapersHeld(() =>
			{
				var id = this._operations.Create();
				if (command.Name != null)
				{
					this._operations.SetName(id, command.Name);
				}
				return id;
			}, command.Name);
			await this.ConfirmCliDesktopAsync(() => this.State.Records.ContainsKey(created)
				&& (command.Name == null || this.CliDesktopInfo(created).Name == command.Name), cancellation);
			if (command.SwitchAfterCreate)
			{
				await this.SubmitCliSwitchAsync(created, progress, cancellation);
			}
			return new CliData { Changed = true, Desktop = this.CliDesktopInfo(created) };
		}

		private async Task<CliData> RenameCliDesktopAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var target = this.ResolveCliTarget(command);
			if (!ProductInfo.IsNameSupportBuild)
			{
				throw DesktopNamesUnsupported();
			}
			var changed = this.CliDesktopInfo(target).Name != command.Name;
			if (changed)
			{
				this.EnsureCliAvailable(cancellation);
				progress.Submitted = true;
				this._operations.SetName(target, command.Name);
				await this.ConfirmCliDesktopAsync(() => this.CliDesktopInfo(target).Name == command.Name, cancellation);
			}
			return new CliData { Changed = changed, Desktop = this.CliDesktopInfo(target) };
		}

		private async Task<CliData> ReorderCliDesktopAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var target = this.ResolveCliTarget(command);
			if (!ProductInfo.IsReorderingSupportBuild)
			{
				throw new CliFailure("unsupported", "Desktop reordering is unavailable on this Windows build.");
			}
			var count = this.State.Order.Count;
			if (command.Number > count)
			{
				throw new CliFailure("desktop_not_found", "The destination position does not exist.");
			}
			var original = this.CliDesktopInfo(target).Number;
			// Move one position at a time and confirm each step, so an unconfirmed step stops the sequence.
			var number = original;
			while (number != command.Number)
			{
				this.EnsureCliAvailable(cancellation);
				if (this.State.Order.Count != count)
				{
					throw new CliFailure("state_changed", "The desktop order changed during reordering.");
				}
				var movingRight = command.Number > number;
				var next = movingRight ? number + 1 : number - 1;
				progress.Submitted = true;
				if (movingRight)
				{
					this._operations.MoveRight(target);
				}
				else
				{
					this._operations.MoveLeft(target);
				}
				await this.ConfirmCliDesktopAsync(() => this.CliDesktopInfo(target).Number == next, cancellation);
				number = next;
			}
			return new CliData { Changed = original != command.Number, Desktop = this.CliDesktopInfo(target) };
		}

		private async Task<CliData> DeleteCliDesktopAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var target = this.ResolveCliTarget(command);
			var fallback = this.ResolveCliRemovalFallback(command, target);
			var removingCurrent = this.State.CurrentDesktopId == target;
			this.EnsureCliAvailable(cancellation);
			progress.Submitted = true;
			this._operations.Remove(target, fallback);
			await this.ConfirmCliDesktopAsync(() => !this.State.Records.ContainsKey(target)
				&& (!removingCurrent || !fallback.HasValue || this.State.CurrentDesktopId == fallback), cancellation);
			return new CliData { Changed = true, Desktops = this.CliDesktopInfos() };
		}

		private async Task<CliData> ChangeCliWallpaperAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var target = this.ResolveCliTarget(command);
			if (!ProductInfo.IsWallpaperSupportBuild && !Settings.General.ChangeBackgroundEachDesktop)
			{
				throw new CliFailure("unsupported", "Per-desktop wallpaper is disabled on this Windows build.");
			}
			var record = this.State.Records[target];
			bool changed;
			if (command.WallpaperPath != null)
			{
				changed = await this.ChangeCliWallpaperPathAsync(target, record, command.WallpaperPath, progress, cancellation);
			}
			else
			{
				changed = this.ChangeCliWallpaperPosition(target, record, command.WallpaperPosition, progress, cancellation);
			}
			return new CliData { Changed = changed, Desktop = this.CliDesktopInfo(target) };
		}

		private async Task<bool> ChangeCliWallpaperPathAsync(Guid target, DesktopRecord record, string path,
			CliRequestProgress progress, CancellationToken cancellation)
		{
			// Windows builds with per-desktop wallpaper report the path back; older builds keep it in SylphyHorn only.
			var providerConfirmsPath = record.WallpaperPath.ReadStatus != VirtualDesktopReadStatus.Unsupported;
			if (providerConfirmsPath && string.IsNullOrEmpty(path))
			{
				throw new CliFailure("invalid_arguments", "The wallpaper path cannot be empty on this Windows build.");
			}
			if (!string.IsNullOrEmpty(path))
			{
				ValidateCliWallpaperImage(path);
			}
			var changed = !record.WallpaperPath.HasValue || record.WallpaperPath.Value != path
				|| (providerConfirmsPath && !record.WallpaperPath.IsConfirmed);
			if (!changed) return false;

			this.EnsureCliAvailable(cancellation);
			progress.Submitted = true;
			this.EditWallpaperPath(target, path);
			var applied = this.State.Records[target].WallpaperPath;
			if (!applied.HasValue || applied.Value != path)
			{
				throw new CliFailure("result_unconfirmed", "The wallpaper path could not be applied.");
			}
			if (providerConfirmsPath)
			{
				await this.ConfirmCliDesktopAsync(() => this.State.Records[target].WallpaperPath.IsConfirmed
					&& this.State.Records[target].WallpaperPath.Value == path, cancellation);
			}
			return true;
		}

		private bool ChangeCliWallpaperPosition(Guid target, DesktopRecord record, string value, CliRequestProgress progress, CancellationToken cancellation)
		{
			var position = (WallpaperPosition)Enum.Parse(typeof(WallpaperPosition), value, true);
			if (record.WallpaperPosition == position) return false;

			this.EnsureCliAvailable(cancellation);
			progress.Submitted = true;
			this.EditWallpaperPosition(target, position);
			if (this.State.Records[target].WallpaperPosition != position)
			{
				throw new CliFailure("result_unconfirmed", "The wallpaper position could not be applied.");
			}
			return true;
		}

		private static void ValidateCliWallpaperImage(string path)
		{
			if (!Path.IsPathFullyQualified(path))
			{
				throw new CliFailure("invalid_arguments", "Specify an absolute image path.");
			}
			try
			{
				WallpaperService.ValidateImage(path);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
				|| ex is ArgumentException || ex is NotSupportedException)
			{
				throw new CliFailure("invalid_arguments", "The image file does not exist or cannot be read.");
			}
		}

		private async Task<CliData> SwitchCliDesktopAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var target = this.ResolveCliTarget(command);
			var changed = this.State.CurrentDesktopId != target;
			if (changed)
			{
				await this.SubmitCliSwitchAsync(target, progress, cancellation);
			}
			return new CliData { Changed = changed, Desktop = this.CliDesktopInfo(target) };
		}

		private async Task SubmitCliSwitchAsync(Guid target, CliRequestProgress progress, CancellationToken cancellation)
		{
			this.EnsureCliAvailable(cancellation);
			progress.Submitted = true;
			this._operations.Switch(target);
			await this.ConfirmCliSwitchAsync(target, cancellation);
		}

		private async Task<CliData> SetCliWindowPinnedAsync(CliCommand command, bool pinned, CliRequestProgress progress, CancellationToken cancellation)
		{
			var key = Guid.Parse(command.WindowId);
			var identity = this.ResolveCliWindow(key).Identity;
			var window = identity.Window;
			var windows = new PlacementWindows();
			EnsureSameCliWindow(windows, identity);
			var appScope = command.Scope == "app";
			var appId = appScope ? ApplicationHelper.GetAppId(window) : null;
			if (appScope && string.IsNullOrEmpty(appId))
			{
				throw new CliFailure("app_id_unavailable", "The window's application ID is unavailable.");
			}
			bool IsPinned() => appScope ? VirtualDesktop.IsPinnedApplication(appId) : VirtualDesktop.IsPinnedWindow(window);

			var changed = IsPinned() != pinned;
			if (changed)
			{
				this.EnsureCliAvailable(cancellation);
				if (!IsSameCliWindow(windows, identity) || (appScope && ApplicationHelper.GetAppId(window) != appId))
				{
					throw new CliFailure("window_changed", CliWindowChangedMessage);
				}
				progress.Submitted = true;
				if (appScope)
				{
					SetApplicationPinned(appId, pinned);
				}
				else
				{
					SetWindowPinned(window, pinned);
				}
				while (true)
				{
					this.EnsureCliAvailable(cancellation);
					// An application pin does not depend on this window staying open.
					if (!appScope && !IsSameCliWindow(windows, identity))
					{
						throw new CliFailure("result_unconfirmed", "The window changed before its pin state could be confirmed.");
					}
					if (IsPinned() == pinned) break;
					await Task.Delay(CliConfirmationInterval, cancellation);
				}
			}
			var result = new CliData { Changed = changed };
			if (IsSameCliWindow(windows, identity))
			{
				try
				{
					result.Window = this.CliWindowInfo(key, identity, windows.Locate(window));
				}
				catch
				{
					// The pin state was confirmed; optional window details may no longer be available.
				}
			}
			return result;
		}

		private static void SetApplicationPinned(string appId, bool pinned)
		{
			if (pinned)
			{
				VirtualDesktop.PinApplication(appId);
			}
			else
			{
				VirtualDesktop.UnpinApplication(appId);
			}
		}

		private static void SetWindowPinned(IntPtr window, bool pinned)
		{
			if (pinned)
			{
				VirtualDesktop.PinWindow(window);
			}
			else
			{
				VirtualDesktop.UnpinWindow(window);
			}
		}

		private async Task<CliData> MoveCliWindowAsync(CliCommand command, CliRequestProgress progress, CancellationToken cancellation)
		{
			var key = Guid.Parse(command.WindowId);
			var identity = this.ResolveCliWindow(key).Identity;
			var windows = new PlacementWindows();
			EnsureSameCliWindow(windows, identity);
			var location = windows.Locate(identity.Window);
			if (location == null)
			{
				throw new CliFailure("state_unavailable", "The window location could not be read.", retryable: true);
			}
			if (location.Pinned)
			{
				throw new CliFailure("window_pinned", "A pinned window cannot be assigned to one desktop.");
			}
			var createsDesktop = command.TargetKind == "new";
			var target = await this.ResolveCliMoveTargetAsync(command, location.Desktop, progress, cancellation);
			var moved = location.Desktop != target;
			if (moved)
			{
				this.EnsureCliAvailable(cancellation);
				var permit = new PlacementMovePermit();
				using (cancellation.Register(permit.Cancel))
				{
					progress.Submitted = true;
					var status = windows.Move(identity, location.Desktop, target, permit,
						() => !cancellation.IsCancellationRequested && this.CliAvailable && this.State.Records.ContainsKey(target));
					if (status != PlacementMoveStatus.Requested && status != PlacementMoveStatus.AlreadyPlaced)
					{
						// A rejected move changed nothing, unless this request has already created the destination.
						if (!createsDesktop)
						{
							progress.Submitted = false;
						}
						throw new CliFailure("window_changed", "The window could not be moved because its state changed.");
					}
				}
				location = await this.ConfirmCliWindowLocationAsync(windows, identity, target, cancellation);
			}
			var switched = command.Follow && this.State.CurrentDesktopId != target;
			if (switched)
			{
				await this.SubmitCliSwitchAsync(target, progress, cancellation);
			}
			return new CliData
			{
				Changed = moved || switched,
				Desktop = this.CliDesktopInfo(target),
				Window = this.CliWindowInfo(key, identity, location)
			};
		}

		private async Task<Guid> ResolveCliMoveTargetAsync(CliCommand command, Guid source, CliRequestProgress progress, CancellationToken cancellation)
		{
			switch (command.TargetKind)
			{
				case "new":
					this.EnsureCliAvailable(cancellation);
					progress.Submitted = true;
					var created = this._operations.Create();
					await this.ConfirmCliDesktopAsync(() => this.State.Records.ContainsKey(created), cancellation);
					return created;
				case "next":
				case "previous":
					// Relative destinations of a window move start from the window's desktop, not the current one.
					return this.ResolveCliRelativeTarget(command, source);
				default:
					return this.ResolveCliTarget(command);
			}
		}

		private async Task<PlacementWindowLocation> ConfirmCliWindowLocationAsync(PlacementWindows windows, PlacementWindowIdentity identity,
			Guid target, CancellationToken cancellation)
		{
			while (true)
			{
				this.EnsureCliAvailable(cancellation);
				if (!IsSameCliWindow(windows, identity))
				{
					throw new CliFailure("result_unconfirmed", "The window changed before its destination could be confirmed.");
				}
				var location = windows.Locate(identity.Window);
				if (location != null && !location.Pinned && location.Desktop == target) return location;
				await Task.Delay(CliConfirmationInterval, cancellation);
			}
		}

		private static bool IsSameCliWindow(PlacementWindows windows, PlacementWindowIdentity identity)
		{
			var inspection = windows.Inspect(identity.Window);
			return inspection.Status == PlacementInspectionStatus.Ready && identity.SameInstance(inspection.Identity);
		}

		private static void EnsureSameCliWindow(PlacementWindows windows, PlacementWindowIdentity identity)
		{
			if (!IsSameCliWindow(windows, identity))
			{
				throw new CliFailure("window_changed", CliWindowChangedMessage);
			}
		}

		private static CliFailure DesktopNamesUnsupported()
			=> new CliFailure("unsupported", "Desktop names are unavailable on this Windows build.");

		internal bool CliAvailable => this._initialized && !this.IsShuttingDown && !this.MustDeferDesktopCommands
			&& !this.IsImportCommitActive && !this.HasPendingDeferredCommands;

		private void EnsureCliAvailable(CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!this.CliAvailable)
			{
				throw new CliFailure("host_busy", "SylphyHorn is changing settings or shutting down.", retryable: true);
			}
		}

		private async Task RefreshCliStateAsync(CancellationToken cancellation)
		{
			this.EnsureCliAvailable(cancellation);
			var result = await this.RequestReconciliationAsync(cancellation).WaitAsync(cancellation);
			this.EnsureCliAvailable(cancellation);
			if (result.Status != VirtualDesktopReconciliationStatus.Succeeded)
			{
				throw new CliFailure("state_unavailable", "Desktop state could not be refreshed.", retryable: true);
			}
			this.ApplyStableBatch(result.Batch);
			this.EnsureCliAvailable(cancellation);
			if (!this.State.CurrentDesktopId.HasValue)
			{
				throw new CliFailure("state_unavailable", "The current desktop is unknown.", retryable: true);
			}
		}

		private async Task ConfirmCliSwitchAsync(Guid target, CancellationToken cancellation)
		{
			while (true)
			{
				await this.RefreshCliStateAsync(cancellation);
				if (this.State.CurrentDesktopId == target) return;
				await Task.Delay(CliConfirmationInterval, cancellation);
			}
		}

		private async Task ConfirmCliDesktopAsync(Func<bool> confirmed, CancellationToken cancellation)
		{
			while (true)
			{
				await this.RefreshCliStateAsync(cancellation);
				if (confirmed()) return;
				await Task.Delay(CliConfirmationInterval, cancellation);
			}
		}

		private Guid? ResolveCliRemovalFallback(CliCommand command, Guid target)
		{
			var fallback = command.FallbackId;
			if (command.FallbackNumber.HasValue)
			{
				var index = command.FallbackNumber.Value - 1;
				if (index >= this.State.Order.Count)
				{
					throw new CliFailure("desktop_not_found", "The fallback desktop does not exist.");
				}
				fallback = this.State.Order[index];
			}
			if (fallback.HasValue && !this.State.Records.ContainsKey(fallback.Value))
			{
				throw new CliFailure("desktop_not_found", "The fallback desktop does not exist.");
			}
			if (fallback == target)
			{
				throw new CliFailure("invalid_arguments", "The fallback desktop must differ from the desktop being deleted.");
			}
			return fallback;
		}

		private Guid ResolveCliTarget(CliCommand command)
		{
			if (command.TargetKind == "id")
			{
				var id = Guid.Parse(command.TargetValue);
				if (this.State.Records.ContainsKey(id)) return id;
			}
			else if (command.TargetKind == "next" || command.TargetKind == "previous")
			{
				return this.ResolveCliRelativeTarget(command, this.State.CurrentDesktopId.Value);
			}
			else if (command.TargetKind == "last-used")
			{
				var previous = VirtualDesktop.History.Previous;
				if (previous != null && previous.Id != this.State.CurrentDesktopId
					&& this.State.Records.ContainsKey(previous.Id))
				{
					return previous.Id;
				}
				throw new CliFailure("no_last_used_desktop", "No previously used desktop is available.");
			}
			else
			{
				var selector = command.TargetKind == "name" ? PlacementDestination.ByName(command.TargetValue)
					: PlacementDestination.ByNumber(int.Parse(command.TargetValue, CultureInfo.InvariantCulture));
				var resolution = this.PlacementDestinations.Resolve(selector);
				if (resolution.Status == PlacementResolutionStatus.Resolved)
				{
					return resolution.DesktopId.Value;
				}
				if (resolution.Status == PlacementResolutionStatus.StateUnavailable)
				{
					throw new CliFailure("state_unavailable", "Desktop names could not be resolved.", retryable: true);
				}
			}
			throw new CliFailure("desktop_not_found", "The specified desktop does not exist.");
		}

		private Guid ResolveCliRelativeTarget(CliCommand command, Guid source)
		{
			var order = this.State.Order.ToArray();
			var sourceIndex = Array.IndexOf(order, source);
			if (sourceIndex < 0)
			{
				throw new CliFailure("state_unavailable", "The source desktop is no longer available.", retryable: true);
			}
			var forward = command.TargetKind == "next";
			var index = forward ? sourceIndex + 1 : sourceIndex - 1;
			if (command.Wrap)
			{
				index = (index + order.Length) % order.Length;
			}
			if (index >= 0 && index < order.Length)
			{
				return order[index];
			}
			throw new CliFailure(forward ? "no_next_desktop" : "no_previous_desktop", "There is no desktop in that direction.");
		}

		private CliWindowEntry ResolveCliWindow(Guid key)
		{
			if (!this._cliWindows.TryGetValue(key, out var entry) || entry.ExpiresAt < DateTime.UtcNow)
			{
				throw new CliFailure("window_not_found", "The window ID is unknown or expired. Run window list again.");
			}
			return entry;
		}

		private void RemoveExpiredCliWindows()
		{
			var expired = this._cliWindows.Where(item => item.Value.ExpiresAt < DateTime.UtcNow).Select(item => item.Key).ToArray();
			foreach (var key in expired)
			{
				this._cliWindows.Remove(key);
			}
		}

		// A window instance keeps its ID across listings while the ID has not expired.
		private Guid? FindCliWindowKey(PlacementWindowIdentity identity)
		{
			var known = this._cliWindows.FirstOrDefault(item => item.Value.Identity.SameInstance(identity));
			return known.Value == null ? (Guid?)null : known.Key;
		}

		private Guid RegisterCliWindow(PlacementWindowIdentity identity, Guid? knownKey)
		{
			var key = knownKey ?? Guid.NewGuid();
			this._cliWindows[key] = new CliWindowEntry { Identity = identity, ExpiresAt = DateTime.UtcNow.Add(CliWindowLifetime) };
			return key;
		}

		private CliDesktop[] CliDesktopInfos() => this.State.Order.Select(this.CliDesktopInfo).ToArray();

		private CliDesktop CliDesktopInfo(Guid id)
		{
			if (!this.State.Records.TryGetValue(id, out var record))
			{
				throw new CliFailure("state_unavailable", "The destination no longer exists.", retryable: true);
			}
			var nameAvailable = IsAvailableName(record.Name);
			return new CliDesktop
			{
				Id = id.ToString(),
				Number = Array.IndexOf(this.State.Order.ToArray(), id) + 1,
				Name = nameAvailable ? record.Name.Value : null,
				NameAvailable = nameAvailable,
				WallpaperPath = record.WallpaperPath.HasValue ? record.WallpaperPath.Value : null,
				WallpaperPathAvailable = record.WallpaperPath.HasValue,
				WallpaperPathConfirmed = record.WallpaperPath.IsConfirmed,
				WallpaperPosition = record.WallpaperPosition.ToString().ToLowerInvariant(),
				Current = this.State.CurrentDesktopId == id
			};
		}

		private CliData ListCliWindows(CancellationToken cancellation)
		{
			this.RemoveExpiredCliWindows();
			var windows = new PlacementWindows();
			var result = new List<CliWindow>();
			var unavailable = 0;
			var visited = 0;
			var complete = true;
			var enumerated = CliEnumWindows((window, _) =>
			{
				if (cancellation.IsCancellationRequested || ++visited > CliWindowLimit)
				{
					complete = false;
					return false;
				}
				try
				{
					var inspection = windows.Inspect(window);
					if (inspection.Status == PlacementInspectionStatus.Unavailable)
					{
						unavailable++;
						return true;
					}
					if (inspection.Status != PlacementInspectionStatus.Ready) return true;
					var identity = inspection.Identity;
					var knownKey = this.FindCliWindowKey(identity);
					if (!knownKey.HasValue && this._cliWindows.Count >= CliWindowLimit)
					{
						complete = false;
						return false;
					}
					var key = this.RegisterCliWindow(identity, knownKey);
					var location = windows.Locate(window);
					if (location == null)
					{
						unavailable++;
					}
					result.Add(this.CliWindowInfo(key, identity, location));
				}
				catch
				{
					unavailable++;
				}
				return true;
			}, IntPtr.Zero);
			this.EnsureCliAvailable(cancellation);
			return new CliData { Windows = result.ToArray(), Complete = complete && enumerated && unavailable == 0, UnavailableCount = unavailable };
		}

		private CliWindow CliWindowInfo(Guid key, PlacementWindowIdentity identity, PlacementWindowLocation location)
		{
			var title = new StringBuilder(1024);
			CliGetWindowText(identity.Window, title, title.Capacity);
			var process = identity.AppProcess ?? identity.Owner;
			var appId = ApplicationHelper.GetAppId(identity.Window);
			// The Shell may report a pinned view when the application is pinned.
			var windowPinned = VirtualDesktop.IsPinnedWindow(identity.Window);
			var appPinned = string.IsNullOrEmpty(appId) ? (bool?)null : VirtualDesktop.IsPinnedApplication(appId);
			var pinned = windowPinned || appPinned == true || (location != null && location.Pinned);
			var movable = location != null && !pinned;
			return new CliWindow
			{
				Id = key.ToString(),
				Title = title.ToString(),
				ProcessId = process.Id,
				ProcessName = Path.GetFileNameWithoutExtension(process.Path),
				ExecutablePath = process.Path,
				AppId = appId,
				DesktopId = movable ? location.Desktop.ToString() : null,
				Pinned = pinned,
				WindowPinned = windowPinned,
				AppPinned = appPinned,
				Movable = movable
			};
		}

		// Once a request has reached Windows, a failure no longer proves that nothing changed.
		private sealed class CliRequestProgress
		{
			internal bool Submitted;
		}

		private sealed class CliWindowEntry
		{
			internal PlacementWindowIdentity Identity;
			internal DateTime ExpiresAt;
		}

		private sealed class CliFailure : Exception
		{
			internal CliFailure(string code, string message, bool retryable = false) : base(message)
			{
				this.Code = code;
				this.Retryable = retryable;
			}

			internal string Code { get; }
			internal bool Retryable { get; }
		}

		private delegate bool CliEnumWindow(IntPtr window, IntPtr state);

		[DllImport("user32.dll", EntryPoint = "EnumWindows")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CliEnumWindows(CliEnumWindow callback, IntPtr state);

		[DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
		private static extern int CliGetWindowText(IntPtr window, StringBuilder title, int maximum);
	}
}
#endif
