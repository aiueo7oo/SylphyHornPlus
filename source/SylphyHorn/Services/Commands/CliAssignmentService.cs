#if !NETFRAMEWORK
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
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
		private const string FollowForegroundDefault = "default";
		private const string NoRulesStatus = "NoRules";
		private const string LauncherOnlyProblem = "LauncherOnly";

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
				{
					return CliResponse.Ok(command.Operation, this.Describe(this._settings.Configuration.Value, includeRules: false));
				}
				if (!this._available())
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);
				}
				if (command.Operation == "app list")
				{
					return await this.ListAppsAsync(command, cancellation);
				}

				var current = this._settings.Configuration.Value;
				if (command.Operation == "app assignment list")
				{
					return CliResponse.Ok(command.Operation, this.Describe(current));
				}
				if (command.Operation == "desktop autoclose list")
				{
					return CliResponse.Ok(command.Operation, this.Describe(current, includeRules: false));
				}

				var edit = await this.EditAsync(command, current, cancellation);
				if (edit.Failure != null)
				{
					return edit.Failure;
				}
				var updated = edit.Configuration;
				var configurationChanged = !ReferenceEquals(updated, current);
				if (configurationChanged)
				{
					published = true;
					this._settings.Configuration.Value = updated;
				}
				return await this.SaveAndDescribeAsync(command, updated, configurationChanged, cancellation);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "request_cancelled",
					published ? "Settings may have changed. Query assignments before retrying." : "The request was cancelled before changing assignments.");
			}
			catch (SerializationException) when (!published)
			{
				return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a valid absolute executable path or package app ID.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, published ? "result_unconfirmed" : "operation_failed",
					published ? "Settings persistence could not be confirmed. Query assignments before retrying."
						: "The application or settings could not be read.");
			}
		}

		private async Task<CliResponse> ListAppsAsync(CliCommand command, CancellationToken cancellation)
		{
			var apps = await this._catalog.ReadAsync(command.Source == "windows", cancellation, false).WaitAsync(cancellation);
			return CliResponse.Ok(command.Operation, new CliData
			{
				Source = command.Source,
				Apps = apps.Select(app => new CliApp
				{
					DisplayName = app.Name,
					ExecutablePath = app.Path,
					AppKind = app.Identity == null ? null : FormatAppKind(app.Identity.Kind),
					AppIdentity = app.Identity?.Value,
					CanAssign = app.Identity != null,
					Reason = DescribeUnassignableReason(app),
				}).ToArray(),
			});
		}

		private async Task<ConfigurationEdit> EditAsync(CliCommand command, AppPlacementConfiguration current, CancellationToken cancellation)
		{
			switch (command.Operation)
			{
				case "desktop autoclose add":
					return ConfigurationEdit.Accepted(AddClosingTarget(current, ReadDestination(command)));
				case "desktop autoclose remove":
					return ConfigurationEdit.Accepted(RemoveClosingTarget(current, ReadDestination(command)));
				case "app assignment configure":
					return ConfigurationEdit.Accepted(Configure(current, command));
				case "app assignment enable":
					return SetRuleEnabled(command, current, enabled: true);
				case "app assignment disable":
					return SetRuleEnabled(command, current, enabled: false);
				case "app assignment set":
				case "app assignment remove":
				default:
					return await this.EditRuleAsync(command, current, cancellation);
			}
		}

		private async Task<CliResponse> SaveAndDescribeAsync(CliCommand command, AppPlacementConfiguration updated,
			bool configurationChanged, CancellationToken cancellation)
		{
			// Also retry persistence for an unchanged request after an earlier save failure.
			var saved = await this._save().WaitAsync(cancellation);
			if (!saved.Succeeded)
			{
				return CliResponse.Fail(command.Operation, "settings_save_failed", "Settings are active in memory but could not be saved.");
			}
			if (!ReferenceEquals(updated, this._settings.Configuration.Value))
			{
				return CliResponse.Fail(command.Operation, "state_changed", "Settings changed while saving. Query current assignments.");
			}
			var includeRules = !command.Operation.StartsWith("desktop autoclose ", StringComparison.Ordinal);
			var data = this.Describe(updated, includeRules);
			data.Changed = configurationChanged;
			return CliResponse.Ok(command.Operation, data);
		}

		private static AppPlacementConfiguration AddClosingTarget(AppPlacementConfiguration current, PlacementDestination target)
		{
			if (current.ClosingTargets.Any(item => SameDestination(item, target))) return current;
			return current.WithClosingTargets(current.ClosingTargets.Concat(new[] { target }));
		}

		private static AppPlacementConfiguration RemoveClosingTarget(AppPlacementConfiguration current, PlacementDestination target)
		{
			var remaining = current.ClosingTargets.Where(item => !SameDestination(item, target)).ToArray();
			return remaining.Length == current.ClosingTargets.Count ? current : current.WithClosingTargets(remaining);
		}

		private static AppPlacementConfiguration Configure(AppPlacementConfiguration current, CliCommand command)
		{
			var enabled = command.AssignmentEnabled ?? current.Enabled;
			var createMissingDesktops = command.CreateMissingDesktops ?? current.CreateMissingDesktops;
			var closeCreatedDesktops = command.CloseCreatedDesktops ?? current.CloseCreatedDesktops;
			var followForeground = command.FollowForeground == null ? current.FollowForeground : ParseFollowForeground(command.FollowForeground);
			var unchanged = enabled == current.Enabled
				&& createMissingDesktops == current.CreateMissingDesktops
				&& closeCreatedDesktops == current.CloseCreatedDesktops
				&& followForeground == current.FollowForeground;
			if (unchanged) return current;
			return current.WithOptions(enabled, createMissingDesktops, closeCreatedDesktops, followForeground);
		}

		private static ConfigurationEdit SetRuleEnabled(CliCommand command, AppPlacementConfiguration current, bool enabled)
		{
			var id = Guid.Parse(command.RuleId);
			var rule = current.Rules.SingleOrDefault(item => item.Id == id);
			if (rule == null)
			{
				return ConfigurationEdit.Rejected(RuleNotFound(command));
			}
			if (enabled && current.Rules.Any(item => item.Id != id && item.Enabled && item.App.Equals(rule.App)))
			{
				return ConfigurationEdit.Rejected(CliResponse.Fail(command.Operation, "assignment_conflict",
					"Another rule for this application is enabled. Disable it first."));
			}
			if (rule.Enabled == enabled)
			{
				return ConfigurationEdit.Accepted(current);
			}
			var toggled = rule.WithEnabled(enabled);
			return ConfigurationEdit.Accepted(current.WithRules(current.Rules.Select(item => item.Id == id ? toggled : item)));
		}

		private async Task<ConfigurationEdit> EditRuleAsync(CliCommand command, AppPlacementConfiguration current, CancellationToken cancellation)
		{
			if (command.RuleId != null)
			{
				var id = Guid.Parse(command.RuleId);
				var saved = current.Rules.SingleOrDefault(rule => rule.Id == id);
				if (saved == null)
				{
					return ConfigurationEdit.Rejected(RuleNotFound(command));
				}
				return ConfigurationEdit.Accepted(ApplyRuleEdit(command, current, saved, saved.App, choice: null));
			}

			PlacementAppIdentity identity;
			PlacementAppChoice choice = null;
			if (command.AppId != null)
			{
				identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, command.AppId);
				var apps = await this._catalog.ReadAsync(false, cancellation, false).WaitAsync(cancellation);
				choice = apps.FirstOrDefault(app => identity.Equals(app.Identity));
				if (choice == null)
				{
					return ConfigurationEdit.Rejected(CliResponse.Fail(command.Operation, "app_unavailable",
						"The registered package application could not be identified."));
				}
			}
			else
			{
				identity = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, command.AppPath);
				// Removal matches the saved path and must not require the executable to exist.
				if (command.Operation == "app assignment set")
				{
					choice = await this._catalog.ReadExecutableAsync(identity.Value, cancellation).WaitAsync(cancellation);
					if (choice?.Identity == null)
					{
						return ConfigurationEdit.Rejected(CliResponse.Fail(command.Operation, "app_unavailable",
							"The executable could not be identified."));
					}
					identity = choice.Identity;
				}
			}

			cancellation.ThrowIfCancellationRequested();
			if (!this._available() || !ReferenceEquals(current, this._settings.Configuration.Value))
			{
				return ConfigurationEdit.Rejected(CliResponse.Fail(command.Operation, "state_changed",
					"Settings changed while identifying the application.", true));
			}
			var matches = current.Rules.Where(rule => rule.App.Equals(identity)).ToArray();
			if (matches.Length > 1)
			{
				return ConfigurationEdit.Rejected(CliResponse.Fail(command.Operation, "ambiguous_assignment",
					"Multiple rules match this application. Select a saved rule ID."));
			}
			return ConfigurationEdit.Accepted(ApplyRuleEdit(command, current, matches.SingleOrDefault(), identity, choice));
		}

		private static AppPlacementConfiguration ApplyRuleEdit(CliCommand command, AppPlacementConfiguration current,
			AppPlacementRule previous, PlacementAppIdentity identity, PlacementAppChoice choice)
		{
			if (command.Operation == "app assignment remove")
			{
				if (previous == null) return current;
				return current.WithRules(current.Rules.Where(rule => rule != previous));
			}

			var destination = command.TargetKind == null ? previous.Destination : ReadDestination(command);
			var followForeground = ResolveRuleFollowForeground(command.FollowForeground, previous);
			var unchanged = previous != null
				&& SameDestination(previous.Destination, destination)
				&& previous.FollowForeground == followForeground;
			if (unchanged) return current;

			var rule = new AppPlacementRule(
				id: previous?.Id ?? Guid.NewGuid(),
				enabled: previous?.Enabled ?? true,
				app: identity,
				destination: destination,
				displayName: choice?.Name ?? previous?.DisplayName,
				displayExecutablePath: choice?.Path ?? previous?.DisplayExecutablePath,
				followForeground: followForeground);
			if (previous == null)
			{
				return current.WithRules(current.Rules.Concat(new[] { rule }));
			}
			return current.WithRules(current.Rules.Select(item => item == previous ? rule : item));
		}

		private static CliResponse RuleNotFound(CliCommand command)
			=> CliResponse.Fail(command.Operation, "assignment_not_found", "The saved rule no longer exists.");

		private static PlacementDestination ReadDestination(CliCommand command)
			=> command.TargetKind == "name" ? PlacementDestination.ByName(command.TargetValue)
				: PlacementDestination.ByNumber(int.Parse(command.TargetValue, CultureInfo.InvariantCulture));

		private static bool SameDestination(PlacementDestination first, PlacementDestination second)
			=> first.Kind == second.Kind && first.Name == second.Name && first.Number == second.Number;

		private static bool ParseFollowForeground(string value) => value == "true";

		private static bool? ResolveRuleFollowForeground(string value, AppPlacementRule previous)
		{
			if (value == null) return previous?.FollowForeground;
			if (value == FollowForegroundDefault) return null;
			return ParseFollowForeground(value);
		}

		private static string FormatFollowForeground(bool? value)
		{
			if (!value.HasValue) return FollowForegroundDefault;
			return value.Value ? "true" : "false";
		}

		private static string FormatAppKind(PlacementAppKind kind)
			=> kind == PlacementAppKind.ExecutablePath ? "executablePath" : "packageAppId";

		private static string DescribeUnassignableReason(PlacementAppChoice app)
		{
			if (app.Identity != null) return null;
			return app.Problem == LauncherOnlyProblem ? "launcher_only" : "identity_unavailable";
		}

		private static string DestinationName(PlacementDestination destination)
			=> destination.Kind == PlacementDestinationKind.Name ? destination.Name : null;

		private static int? DestinationNumber(PlacementDestination destination)
			=> destination.Kind == PlacementDestinationKind.Number ? (int?)destination.Number : null;

		private string DescribeStatus()
		{
			var status = this._status();
			return status == NoRulesStatus ? "no_rules" : status.ToLowerInvariant();
		}

		private CliData Describe(AppPlacementConfiguration configuration, bool includeRules = true) => new CliData
		{
			AssignmentEnabled = configuration.Enabled,
			FollowForeground = configuration.FollowForeground,
			AssignmentStatus = this.DescribeStatus(),
			CreateMissingDesktops = configuration.CreateMissingDesktops,
			CloseCreatedDesktops = configuration.CloseCreatedDesktops,
			ClosingTargets = configuration.ClosingTargets.Select(target => new CliAssignmentTarget
			{
				DesktopName = DestinationName(target),
				DesktopNumber = DestinationNumber(target),
			}).ToArray(),
			Assignments = !includeRules ? null : configuration.Rules.Select(rule => new CliAssignment
			{
				Id = rule.Id.ToString(),
				Enabled = rule.Enabled,
				FollowForeground = FormatFollowForeground(rule.FollowForeground),
				EffectiveFollowForeground = rule.FollowForeground ?? configuration.FollowForeground,
				AppKind = FormatAppKind(rule.App.Kind),
				AppIdentity = rule.App.Value,
				ExecutablePath = rule.App.Kind == PlacementAppKind.ExecutablePath ? rule.App.Value : rule.DisplayExecutablePath,
				DisplayName = rule.DisplayName,
				DesktopName = DestinationName(rule.Destination),
				DesktopNumber = DestinationNumber(rule.Destination),
			}).ToArray(),
		};

		private sealed class ConfigurationEdit
		{
			private ConfigurationEdit(AppPlacementConfiguration configuration, CliResponse failure)
			{
				this.Configuration = configuration;
				this.Failure = failure;
			}

			internal AppPlacementConfiguration Configuration { get; }

			internal CliResponse Failure { get; }

			internal static ConfigurationEdit Accepted(AppPlacementConfiguration configuration) => new ConfigurationEdit(configuration, null);

			internal static ConfigurationEdit Rejected(CliResponse failure) => new ConfigurationEdit(null, failure);
		}
	}
}
#endif
