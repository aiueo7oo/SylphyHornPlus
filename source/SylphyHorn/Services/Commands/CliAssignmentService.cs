#if !NETFRAMEWORK
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;
using SylphyHorn.Services.AppPlacement;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliAssignmentService
	{
		private readonly AppPlacementSettings _settings;
		private readonly IPlacementAppCatalog _catalog;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<bool> _available;

		internal CliAssignmentService(AppPlacementSettings settings, IPlacementAppCatalog catalog,
			Func<Task<SettingsSaveResult>> save, Func<bool> available)
		{
			this._settings = settings;
			this._catalog = catalog;
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
				var current = this._settings.Configuration.Value;
				if (command.Operation == "app assignment list") return CliResponse.Ok(command.Operation, Describe(current));
				var identity = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, command.AppPath);
				PlacementAppChoice choice = null;
				if (command.Operation == "app assignment set")
				{
					choice = await this._catalog.ReadExecutableAsync(identity.Value, cancellation).WaitAsync(cancellation);
					if (choice?.Identity == null)
						return CliResponse.Fail(command.Operation, "app_unavailable", "The executable could not be identified.");
					identity = choice.Identity;
				}
				cancellation.ThrowIfCancellationRequested();
				if (!this._available() || !ReferenceEquals(current, this._settings.Configuration.Value))
					return CliResponse.Fail(command.Operation, "state_changed", "Settings changed while identifying the application.", true);
				var matches = current.Rules.Where(rule => rule.App.Equals(identity)).ToArray();
				if (matches.Length > 1)
					return CliResponse.Fail(command.Operation, "ambiguous_assignment", "Multiple rules match this application. Resolve them in settings first.");
				var rules = current.Rules.ToList();
				var previous = matches.SingleOrDefault();
				var changed = false;
				if (command.Operation == "app assignment remove")
				{
					if (previous != null) changed = rules.Remove(previous);
				}
				else
				{
					var destination = command.TargetKind == "name" ? PlacementDestination.ByName(command.TargetValue)
						: PlacementDestination.ByNumber(int.Parse(command.TargetValue, CultureInfo.InvariantCulture));
					changed = previous == null || previous.Destination.Kind != destination.Kind
						|| previous.Destination.Name != destination.Name || previous.Destination.Number != destination.Number;
					if (changed)
					{
						var rule = new AppPlacementRule(previous?.Id ?? Guid.NewGuid(), previous?.Enabled ?? true,
							identity, destination, choice.Name, choice.Path);
						if (previous == null) rules.Add(rule); else rules[rules.IndexOf(previous)] = rule;
					}
				}
				var updated = changed ? new AppPlacementConfiguration(current.Enabled, rules, current.CreateMissingDesktops,
					current.CloseCreatedDesktops, current.ClosingTargets) : current;
				if (changed)
				{
					published = true;
					this._settings.Configuration.Value = updated;
				}
				// Also retry persistence for an unchanged request after an earlier save failure.
				var saved = await this._save().WaitAsync(cancellation);
				if (!saved.Succeeded)
					return CliResponse.Fail(command.Operation, "settings_save_failed", "Settings are active in memory but could not be saved.");
				if (!ReferenceEquals(updated, this._settings.Configuration.Value))
					return CliResponse.Fail(command.Operation, "state_changed", "Settings changed while saving. Query current assignments.");
				var data = Describe(updated);
				data.Changed = changed;
				return CliResponse.Ok(command.Operation, data);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "request_cancelled",
					published ? "Settings may have changed. Query assignments before retrying." : "The request was cancelled before changing assignments.");
			}
			catch (System.Runtime.Serialization.SerializationException) when (!published)
			{
				return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a valid absolute executable path.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "operation_failed",
					published ? "Settings persistence could not be confirmed. Query assignments before retrying."
						: "The application or settings could not be read.");
			}
		}

		private static CliData Describe(AppPlacementConfiguration configuration) => new CliData
		{
			AssignmentEnabled = configuration.Enabled,
			Assignments = configuration.Rules.Select(rule => new CliAssignment
			{
				Id = rule.Id.ToString(),
				Enabled = rule.Enabled,
				AppKind = rule.App.Kind == PlacementAppKind.ExecutablePath ? "executablePath" : "packageAppId",
				AppIdentity = rule.App.Value,
				ExecutablePath = rule.App.Kind == PlacementAppKind.ExecutablePath ? rule.App.Value : rule.DisplayExecutablePath,
				DisplayName = rule.DisplayName,
				DesktopName = rule.Destination.Kind == PlacementDestinationKind.Name ? rule.Destination.Name : null,
				DesktopNumber = rule.Destination.Kind == PlacementDestinationKind.Number ? (int?)rule.Destination.Number : null,
			}).ToArray(),
		};
	}
}
#endif
