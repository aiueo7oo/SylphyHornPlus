#if !NETFRAMEWORK
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliCreationWallpaperService
	{
		private readonly GeneralSettings _settings;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<bool> _available;

		internal CliCreationWallpaperService(GeneralSettings settings, Func<Task<SettingsSaveResult>> save, Func<bool> available)
		{
			this._settings = settings;
			this._save = save;
			this._available = available;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var published = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (!this._available()) return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);
				var current = this._settings.DesktopWallpapersOnCreation.Value;
				if (command.Operation.EndsWith(" list", StringComparison.Ordinal)) return Describe(command, current, null);
				var name = command.TargetKind == "name" ? command.TargetValue : null;
				int? number = command.TargetKind == "number" ? int.Parse(command.TargetValue, CultureInfo.InvariantCulture) : (int?)null;
				var entries = current.ToList();
				var previous = entries.SingleOrDefault(item => item.Name == name && item.Number == number);
				var changed = false;
				if (command.Operation.EndsWith(" set", StringComparison.Ordinal))
				{
					var entry = new DesktopWallpaperOnCreation(name, number, command.WallpaperPath);
					WallpaperService.ValidateImage(entry.WallpaperPath);
					changed = previous == null || previous.WallpaperPath != entry.WallpaperPath;
					if (previous == null) entries.Add(entry); else entries[entries.IndexOf(previous)] = entry;
				}
				else changed = entries.Remove(previous);
				var updated = changed ? entries.ToArray() : current;
				if (changed)
				{
					published = true;
					this._settings.DesktopWallpapersOnCreation.Value = updated;
				}
				var saved = await this._save().WaitAsync(cancellation);
				if (!saved.Succeeded) return CliResponse.Fail(command.Operation, "settings_save_failed", "Settings are active in memory but could not be saved.");
				if (!ReferenceEquals(updated, this._settings.DesktopWallpapersOnCreation.Value))
					return CliResponse.Fail(command.Operation, "state_changed", "Settings changed while saving. Read them again.");
				return Describe(command, updated, changed);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "request_cancelled", "Read current settings before retrying.");
			}
			catch (System.Runtime.Serialization.SerializationException) when (!published)
			{
				return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a valid destination and absolute image path.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "operation_failed",
					published ? "Settings persistence could not be confirmed." : "The image or settings could not be read.");
			}
		}

		private static CliResponse Describe(CliCommand command, DesktopWallpaperOnCreation[] entries, bool? changed)
			=> CliResponse.Ok(command.Operation, new CliData
			{
				Changed = changed,
				WallpapersOnCreation = entries.Select(item => new CliCreationWallpaper { Name = item.Name, Number = item.Number, Path = item.WallpaperPath }).ToArray(),
			});
	}
}
#endif
