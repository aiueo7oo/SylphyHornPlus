#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Properties;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		private readonly Dictionary<Guid, CliWindowEntry> _cliWindows = new Dictionary<Guid, CliWindowEntry>();

		internal async Task<CliResponse> ExecuteCliAsync(CliCommand command, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			var submitted = false;
			try
			{
				await this.RefreshCliStateAsync(cancellation);
				if (command.Operation == "desktop list")
					return CliResponse.Ok(command.Operation, new CliData { Desktops = this.State.Order.Select(this.CliDesktopInfo).ToArray() });
				if (command.Operation == "window list") return CliResponse.Ok(command.Operation, this.ListCliWindows(cancellation));
				if (command.Operation == "desktop create")
				{
					if (command.Name != null && !ProductInfo.IsNameSupportBuild)
						throw new CliFailure("unsupported", "Desktop names are unavailable on this Windows build.");
					this.EnsureCliAvailable(cancellation);
					submitted = true;
					var created = this._operations.Create();
					if (command.Name != null) this._operations.SetName(created, command.Name);
					await this.ConfirmCliDesktopAsync(() => this.State.Records.ContainsKey(created)
						&& (command.Name == null || this.CliDesktopInfo(created).Name == command.Name), cancellation);
					return CliResponse.Ok(command.Operation, new CliData { Changed = true, Desktop = this.CliDesktopInfo(created) });
				}

				var target = command.Operation == "window pin" || command.Operation == "window unpin"
					? Guid.Empty : this.ResolveCliTarget(command);
				if (command.Operation == "desktop rename")
				{
					if (!ProductInfo.IsNameSupportBuild) throw new CliFailure("unsupported", "Desktop names are unavailable on this Windows build.");
					var changed = this.CliDesktopInfo(target).Name != command.Name;
					if (changed)
					{
						this.EnsureCliAvailable(cancellation);
						submitted = true;
						this._operations.SetName(target, command.Name);
						await this.ConfirmCliDesktopAsync(() => this.CliDesktopInfo(target).Name == command.Name, cancellation);
					}
					return CliResponse.Ok(command.Operation, new CliData { Changed = changed, Desktop = this.CliDesktopInfo(target) });
				}
				if (command.Operation == "desktop reorder")
				{
					if (!ProductInfo.IsReorderingSupportBuild)
						throw new CliFailure("unsupported", "Desktop reordering is unavailable on this Windows build.");
					var count = this.State.Order.Count;
					if (command.Number > count) throw new CliFailure("desktop_not_found", "The destination position does not exist.");
					var original = this.CliDesktopInfo(target).Number;
					for (var number = original; number != command.Number;)
					{
						this.EnsureCliAvailable(cancellation);
						if (this.State.Order.Count != count) throw new CliFailure("state_changed", "The desktop order changed during reordering.");
						var next = number + (command.Number > number ? 1 : -1);
						submitted = true;
						if (next > number) this._operations.MoveRight(target);
						else this._operations.MoveLeft(target);
						await this.ConfirmCliDesktopAsync(() => this.CliDesktopInfo(target).Number == next, cancellation);
						number = next;
					}
					return CliResponse.Ok(command.Operation, new CliData { Changed = original != command.Number, Desktop = this.CliDesktopInfo(target) });
				}
				if (command.Operation == "desktop delete")
				{
					this.EnsureCliAvailable(cancellation);
					submitted = true;
					this._operations.Remove(target);
					await this.ConfirmCliDesktopAsync(() => !this.State.Records.ContainsKey(target), cancellation);
					return CliResponse.Ok(command.Operation, new CliData
					{
						Changed = true,
						Desktops = this.State.Order.Select(this.CliDesktopInfo).ToArray()
					});
				}
				if (command.Operation == "desktop switch")
				{
					var changed = this.State.CurrentDesktopId != target;
					if (changed)
					{
						this.EnsureCliAvailable(cancellation);
						submitted = true;
						this._operations.Switch(target);
						await this.ConfirmCliSwitchAsync(target, cancellation);
					}
					return CliResponse.Ok(command.Operation, new CliData { Changed = changed, Desktop = this.CliDesktopInfo(target) });
				}

				var key = Guid.Parse(command.WindowId);
				var entry = this.ResolveCliWindow(key);
				var windows = new PlacementWindows();
				var inspection = windows.Inspect(entry.Identity.Window);
				if (inspection.Status != PlacementInspectionStatus.Ready || !entry.Identity.SameInstance(inspection.Identity))
					throw new CliFailure("window_changed", "The window identity changed. Run window list again.");
				if (command.Operation == "window pin" || command.Operation == "window unpin")
				{
					var pin = command.Operation == "window pin";
					var appId = command.Scope == "app" ? ApplicationHelper.GetAppId(entry.Identity.Window) : null;
					if (command.Scope == "app" && string.IsNullOrEmpty(appId))
						throw new CliFailure("app_id_unavailable", "The window's application ID is unavailable.");
					var current = command.Scope == "app" ? VirtualDesktop.IsPinnedApplication(appId) : VirtualDesktop.IsPinnedWindow(entry.Identity.Window);
					var changed = current != pin;
					if (changed)
					{
						this.EnsureCliAvailable(cancellation);
						inspection = windows.Inspect(entry.Identity.Window);
						if (inspection.Status != PlacementInspectionStatus.Ready || !entry.Identity.SameInstance(inspection.Identity)
							|| (command.Scope == "app" && ApplicationHelper.GetAppId(entry.Identity.Window) != appId))
							throw new CliFailure("window_changed", "The window identity changed. Run window list again.");
						submitted = true;
						if (command.Scope == "app")
						{
							if (pin) VirtualDesktop.PinApplication(appId);
							else VirtualDesktop.UnpinApplication(appId);
						}
						else if (pin) VirtualDesktop.PinWindow(entry.Identity.Window);
						else VirtualDesktop.UnpinWindow(entry.Identity.Window);
						while (true)
						{
							this.EnsureCliAvailable(cancellation);
							if (command.Scope == "window")
							{
								inspection = windows.Inspect(entry.Identity.Window);
								if (inspection.Status != PlacementInspectionStatus.Ready || !entry.Identity.SameInstance(inspection.Identity))
									throw new CliFailure("result_unconfirmed", "The window changed before its pin state could be confirmed.");
							}
							current = command.Scope == "app" ? VirtualDesktop.IsPinnedApplication(appId) : VirtualDesktop.IsPinnedWindow(entry.Identity.Window);
							if (current == pin) break;
							await Task.Delay(100, cancellation);
						}
					}
					var result = new CliData { Changed = changed };
					inspection = windows.Inspect(entry.Identity.Window);
					if (inspection.Status == PlacementInspectionStatus.Ready && entry.Identity.SameInstance(inspection.Identity))
					{
						try { result.Window = this.CliWindowInfo(key, entry.Identity, windows.Locate(entry.Identity.Window)); }
						catch { /* The pin state was confirmed; optional window details may no longer be available. */ }
					}
					return CliResponse.Ok(command.Operation, result);
				}
				var location = windows.Locate(entry.Identity.Window);
				if (location == null) throw new CliFailure("state_unavailable", "The window location could not be read.", true);
				if (location.Pinned) throw new CliFailure("window_pinned", "A pinned window cannot be assigned to one desktop.");
				var moved = location.Desktop != target;
				if (moved)
				{
					this.EnsureCliAvailable(cancellation);
					var permit = new PlacementMovePermit();
					using (cancellation.Register(permit.Cancel))
					{
						submitted = true;
						var result = windows.Move(entry.Identity, location.Desktop, target, permit,
							() => !cancellation.IsCancellationRequested && this.CliAvailable && this.State.Records.ContainsKey(target));
						if (result != PlacementMoveStatus.Requested && result != PlacementMoveStatus.AlreadyPlaced)
						{
							submitted = false;
							throw new CliFailure("window_changed", "The window could not be moved because its state changed.");
						}
					}
					while (true)
					{
						this.EnsureCliAvailable(cancellation);
						inspection = windows.Inspect(entry.Identity.Window);
						if (inspection.Status != PlacementInspectionStatus.Ready || !entry.Identity.SameInstance(inspection.Identity))
							throw new CliFailure("result_unconfirmed", "The window changed before its destination could be confirmed.");
						location = windows.Locate(entry.Identity.Window);
						if (location != null && !location.Pinned && location.Desktop == target) break;
						await Task.Delay(100, cancellation);
					}
				}
				var switched = command.Follow && this.State.CurrentDesktopId != target;
				if (switched)
				{
					this.EnsureCliAvailable(cancellation);
					submitted = true;
					this._operations.Switch(target);
					await this.ConfirmCliSwitchAsync(target, cancellation);
				}
				return CliResponse.Ok(command.Operation, new CliData
				{
					Changed = moved || switched,
					Desktop = this.CliDesktopInfo(target),
					Window = this.CliWindowInfo(key, entry.Identity, location)
				});
			}
			catch (CliFailure ex)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : ex.Code, ex.Message, !submitted && ex.Retryable);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled",
					submitted ? "The operation may have completed. Query current state before retrying." : "The request expired before any operation was submitted.");
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("Cli.Execute", ex.GetType()));
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "operation_failed", "The desktop service could not complete this request.");
			}
		}

		private bool CliAvailable => this._initialized && !this._shutdownStarted && !this._stopping && !this._publishing
			&& this._preparedRuntime == null && this._activeImportSession == null && !this._placementSuspended
			&& (this._activeImportCommit == null || this._activeImportCommit.IsCompleted)
			&& !this._deferredDrainScheduled && this._deferredCommands.Count == 0;

		private void EnsureCliAvailable(CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!this.CliAvailable) throw new CliFailure("host_busy", "SylphyHorn is changing settings or shutting down.", true);
		}

		private async Task RefreshCliStateAsync(CancellationToken cancellation)
		{
			this.EnsureCliAvailable(cancellation);
			var result = await this.RequestReconciliationAsync(cancellation).WaitAsync(cancellation);
			this.EnsureCliAvailable(cancellation);
			if (result.Status != VirtualDesktopReconciliationStatus.Succeeded) throw new CliFailure("state_unavailable", "Desktop state could not be refreshed.", true);
			this.ApplyStableBatch(result.Batch);
			this.EnsureCliAvailable(cancellation);
			if (!this.State.CurrentDesktopId.HasValue) throw new CliFailure("state_unavailable", "The current desktop is unknown.", true);
		}

		private async Task ConfirmCliSwitchAsync(Guid target, CancellationToken cancellation)
		{
			do
			{
				await this.RefreshCliStateAsync(cancellation);
				if (this.State.CurrentDesktopId == target) return;
				await Task.Delay(100, cancellation);
			} while (true);
		}

		private async Task ConfirmCliDesktopAsync(Func<bool> confirmed, CancellationToken cancellation)
		{
			while (true)
			{
				await this.RefreshCliStateAsync(cancellation);
				if (confirmed()) return;
				await Task.Delay(100, cancellation);
			}
		}

		private Guid ResolveCliTarget(CliCommand command)
		{
			var order = this.State.Order.ToArray();
			if (command.TargetKind == "id")
			{
				var id = Guid.Parse(command.TargetValue);
				if (this.State.Records.ContainsKey(id)) return id;
			}
			else if (command.TargetKind == "next" || command.TargetKind == "previous")
			{
				var index = Array.IndexOf(order, this.State.CurrentDesktopId.Value) + (command.TargetKind == "next" ? 1 : -1);
				if (command.Wrap) index = (index + order.Length) % order.Length;
				if (index >= 0 && index < order.Length) return order[index];
				throw new CliFailure(command.TargetKind == "next" ? "no_next_desktop" : "no_previous_desktop", "There is no desktop in that direction.");
			}
			else
			{
				var selector = command.TargetKind == "name" ? PlacementDestination.ByName(command.TargetValue)
					: PlacementDestination.ByNumber(int.Parse(command.TargetValue, System.Globalization.CultureInfo.InvariantCulture));
				var resolution = this.PlacementDestinations.Resolve(selector);
				if (resolution.Status == PlacementResolutionStatus.Resolved) return resolution.DesktopId.Value;
				if (resolution.Status == PlacementResolutionStatus.StateUnavailable) throw new CliFailure("state_unavailable", "Desktop names could not be resolved.", true);
			}
			throw new CliFailure("desktop_not_found", "The specified desktop does not exist.");
		}

		private CliWindowEntry ResolveCliWindow(Guid key)
		{
			if (!this._cliWindows.TryGetValue(key, out var entry) || entry.ExpiresAt < DateTime.UtcNow)
				throw new CliFailure("window_not_found", "The window ID is unknown or expired. Run window list again.");
			return entry;
		}

		private CliDesktop CliDesktopInfo(Guid id)
		{
			if (!this.State.Records.TryGetValue(id, out var record)) throw new CliFailure("state_unavailable", "The destination no longer exists.", true);
			var available = record.Name.HasValue && record.Name.IsConfirmed && record.Name.ReadStatus == VirtualDesktopReadStatus.Success;
			return new CliDesktop
			{
				Id = id.ToString(),
				Number = Array.IndexOf(this.State.Order.ToArray(), id) + 1,
				Name = available ? record.Name.Value : null,
				NameAvailable = available,
				Current = this.State.CurrentDesktopId == id
			};
		}

		private CliData ListCliWindows(CancellationToken cancellation)
		{
			foreach (var key in this._cliWindows.Where(item => item.Value.ExpiresAt < DateTime.UtcNow).Select(item => item.Key).ToArray()) this._cliWindows.Remove(key);
			var windows = new PlacementWindows();
			var result = new List<CliWindow>();
			var unavailable = 0;
			var count = 0;
			var complete = true;
			var enumerated = CliEnumWindows((window, _) =>
			{
				if (cancellation.IsCancellationRequested || ++count > 4096) { complete = false; return false; }
				try
				{
					var inspection = windows.Inspect(window);
					if (inspection.Status == PlacementInspectionStatus.Unavailable) { unavailable++; return true; }
					if (inspection.Status != PlacementInspectionStatus.Ready) return true;
					var identity = inspection.Identity;
					var previous = this._cliWindows.FirstOrDefault(item => item.Value.Identity.SameInstance(identity));
					var key = previous.Value == null ? Guid.NewGuid() : previous.Key;
					if (previous.Value == null && this._cliWindows.Count >= 4096) { complete = false; return false; }
					this._cliWindows[key] = new CliWindowEntry { Identity = identity, ExpiresAt = DateTime.UtcNow.AddMinutes(5) };
					var location = windows.Locate(window);
					if (location == null) unavailable++;
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
			return new CliWindow
			{
				Id = key.ToString(),
				Title = title.ToString(),
				ProcessId = process.Id,
				ProcessName = Path.GetFileNameWithoutExtension(process.Path),
				ExecutablePath = process.Path,
				AppId = appId,
				DesktopId = location != null && !pinned ? location.Desktop.ToString() : null,
				Pinned = pinned,
				WindowPinned = windowPinned,
				AppPinned = appPinned,
				Movable = location != null && !pinned
			};
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
