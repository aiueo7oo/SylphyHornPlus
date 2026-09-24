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
		private readonly Func<string> _status;

		internal CliAssignmentService(AppPlacementSettings settings, IPlacementAppCatalog catalog,
			Func<Task<SettingsSaveResult>> save, Func<bool> available, Func<string> status)
		{
			this._settings = settings;
			this._catalog = catalog;
			this._save = save;
			this._available = available;
			this._status = status;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var published = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (command.Operation == "app assignment status")
					return CliResponse.Ok(command.Operation, this.Describe(this._settings.Configuration.Value, false));
				if (!this._available()) return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);
				var current = this._settings.Configuration.Value;
				if (command.Operation == "app assignment list") return CliResponse.Ok(command.Operation, this.Describe(current));
				if (command.Operation == "desktop autoclose list") return CliResponse.Ok(command.Operation, this.Describe(current, false));
				AppPlacementConfiguration updated;
				if (command.Operation == "desktop autoclose add" || command.Operation == "desktop autoclose remove")
				{
					var target = ReadDestination(command);
					var targets = current.ClosingTargets.ToList();
					var changed = false;
					if (command.Operation == "desktop autoclose add")
					{
						if (!targets.Any(item => SameDestination(item, target)))
						{
							targets.Add(target);
							changed = true;
						}
					}
					else changed = targets.RemoveAll(item => SameDestination(item, target)) != 0;
					updated = changed ? new AppPlacementConfiguration(current.Enabled, current.Rules,
						current.CreateMissingDesktops, current.CloseCreatedDesktops, targets) : current;
				}
				else if (command.Operation == "app assignment configure")
				{
					var enabled = command.AssignmentEnabled ?? current.Enabled;
					var create = command.CreateMissingDesktops ?? current.CreateMissingDesktops;
					var close = command.CloseCreatedDesktops ?? current.CloseCreatedDesktops;
					updated = enabled == current.Enabled && create == current.CreateMissingDesktops && close == current.CloseCreatedDesktops
						? current : new AppPlacementConfiguration(enabled, current.Rules, create, close, current.ClosingTargets);
				}
				else if (command.Operation == "app assignment enable" || command.Operation == "app assignment disable")
				{
					var id = Guid.Parse(command.RuleId);
					var rule = current.Rules.SingleOrDefault(item => item.Id == id);
					if (rule == null) return CliResponse.Fail(command.Operation, "assignment_not_found", "The saved rule no longer exists.");
					var enabled = command.Operation == "app assignment enable";
					if (enabled && current.Rules.Any(item => item.Id != id && item.Enabled && item.App.Equals(rule.App)))
						return CliResponse.Fail(command.Operation, "assignment_conflict", "Another rule for this application is enabled. Disable it first.");
					var rules = current.Rules.Select(item => item.Id != id ? item : new AppPlacementRule(item.Id, enabled,
						item.App, item.Destination, item.DisplayName, item.DisplayExecutablePath));
					updated = rule.Enabled == enabled ? current : new AppPlacementConfiguration(current.Enabled, rules,
						current.CreateMissingDesktops, current.CloseCreatedDesktops, current.ClosingTargets);
				}
				else
				{
					PlacementAppIdentity identity;
					PlacementAppChoice choice = null;
					AppPlacementRule previous;
					if (command.RuleId != null)
					{
						var id = Guid.Parse(command.RuleId);
						previous = current.Rules.SingleOrDefault(rule => rule.Id == id);
						if (previous == null) return CliResponse.Fail(command.Operation, "assignment_not_found", "The saved rule no longer exists.");
						identity = previous.App;
					}
					else
					{
						identity = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, command.AppPath);
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
							return CliResponse.Fail(command.Operation, "ambiguous_assignment", "Multiple rules match this application. Select a saved rule ID.");
						previous = matches.SingleOrDefault();
					}
					var rules = current.Rules.ToList();
					var changed = false;
					if (command.Operation == "app assignment remove")
					{
						if (previous != null) changed = rules.Remove(previous);
					}
					else
					{
						var destination = ReadDestination(command);
						changed = previous == null || !SameDestination(previous.Destination, destination);
						if (changed)
						{
							var rule = new AppPlacementRule(previous?.Id ?? Guid.NewGuid(), previous?.Enabled ?? true,
								identity, destination, choice?.Name ?? previous?.DisplayName, choice?.Path ?? previous?.DisplayExecutablePath);
							if (previous == null) rules.Add(rule); else rules[rules.IndexOf(previous)] = rule;
						}
					}
					updated = changed ? new AppPlacementConfiguration(current.Enabled, rules, current.CreateMissingDesktops,
						current.CloseCreatedDesktops, current.ClosingTargets) : current;
				}
				var configurationChanged = !ReferenceEquals(updated, current);
				if (configurationChanged)
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
				var data = this.Describe(updated, !command.Operation.StartsWith("desktop autoclose ", StringComparison.Ordinal));
				data.Changed = configurationChanged;
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

		private static PlacementDestination ReadDestination(CliCommand command)
			=> command.TargetKind == "name" ? PlacementDestination.ByName(command.TargetValue)
				: PlacementDestination.ByNumber(int.Parse(command.TargetValue, CultureInfo.InvariantCulture));

		private static bool SameDestination(PlacementDestination first, PlacementDestination second)
			=> first.Kind == second.Kind && first.Name == second.Name && first.Number == second.Number;

		private CliData Describe(AppPlacementConfiguration configuration, bool includeRules = true) => new CliData
		{
			AssignmentEnabled = configuration.Enabled,
			AssignmentStatus = this._status() == "NoRules" ? "no_rules" : this._status().ToLowerInvariant(),
			CreateMissingDesktops = configuration.CreateMissingDesktops,
			CloseCreatedDesktops = configuration.CloseCreatedDesktops,
			ClosingTargets = configuration.ClosingTargets.Select(target => new CliAssignmentTarget
			{
				DesktopName = target.Kind == PlacementDestinationKind.Name ? target.Name : null,
				DesktopNumber = target.Kind == PlacementDestinationKind.Number ? (int?)target.Number : null,
			}).ToArray(),
			Assignments = !includeRules ? null : configuration.Rules.Select(rule => new CliAssignment
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
