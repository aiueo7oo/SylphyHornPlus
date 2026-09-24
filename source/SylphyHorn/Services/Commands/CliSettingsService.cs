#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliSettingsService
	{
		private readonly GeneralSettings _settings;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<bool> _available;
		private readonly string _startupCulture;

		internal CliSettingsService(GeneralSettings settings, Func<Task<SettingsSaveResult>> save, Func<bool> available, string startupCulture)
		{
			this._settings = settings;
			this._save = save;
			this._available = available;
			this._startupCulture = startupCulture;
		}

		internal static bool Handles(string operation)
			=> operation == "desktop settings" || operation == "desktop configure"
				|| operation == "notification settings" || operation == "notification configure"
				|| operation == "tray settings" || operation == "tray configure"
				|| operation == "settings get" || operation == "settings configure";

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var changed = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (!Handles(command.Operation)) return CliResponse.Fail(command.Operation, "invalid_arguments", "Unknown settings command.");
				if (!command.Operation.EndsWith(" configure", StringComparison.Ordinal))
					return CliResponse.Ok(command.Operation, this.Describe(command.Operation));
				if (!this._available()) return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);

				Set(this._settings.LoopDesktop, command.Loop, ref changed);
				Set(this._settings.OverrideWindowsDefaultKeyCombination, command.OverrideWindowsShortcuts, ref changed);
				Set(this._settings.NotificationWhenSwitchedDesktop, command.OnSwitch, ref changed);
				Set(this._settings.NotificationDuration, command.DurationMs, ref changed);
				Set(this._settings.AlwaysShowDesktopNotification, command.AlwaysShow, ref changed);
				Set(this._settings.TrayShowOnlyCurrentNumber, command.CurrentNumberOnly, ref changed);
				Set(this._settings.TrayShowDesktop, command.ShowDesktop, ref changed);
				if (command.Language != null)
				{
					var culture = command.Language == "auto" ? null : command.Language;
					if (this._settings.Culture.Value != culture)
					{
						changed = true;
						this._settings.Culture.Value = culture;
					}
				}

				var data = this.Describe(command.Operation);
				// An unchanged request also retries persistence after an earlier save failure.
				var saved = await this._save().WaitAsync(cancellation);
				if (!saved.Succeeded)
					return CliResponse.Fail(command.Operation, "settings_save_failed", "Settings are active in memory but could not be saved.");
				if (!CliProtocol.Serialize(data).SequenceEqual(CliProtocol.Serialize(this.Describe(command.Operation))))
					return CliResponse.Fail(command.Operation, "state_changed", "Settings changed while saving. Read current settings before retrying.");
				data.Changed = changed;
				return CliResponse.Ok(command.Operation, data);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, changed ? "result_unconfirmed" : "request_cancelled",
					changed ? "Settings may have changed. Read current settings before retrying." : "The request was cancelled.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, changed ? "result_unconfirmed" : "operation_failed",
					"Settings application or persistence could not be confirmed. Read current settings before retrying.");
			}
		}

		private static void Set<T>(SerializableProperty<T> property, T? value, ref bool changed) where T : struct
		{
			if (!value.HasValue || EqualityComparer<T>.Default.Equals(property.Value, value.Value)) return;
			changed = true;
			property.Value = value.Value;
		}

		private CliData Describe(string operation)
		{
			var data = new CliData { RestartRequired = this._settings.Culture.Value != this._startupCulture };
			if (operation.StartsWith("desktop ", StringComparison.Ordinal))
			{
				data.Loop = this._settings.LoopDesktop.Value;
				data.OverrideWindowsShortcuts = this._settings.OverrideWindowsDefaultKeyCombination.Value;
			}
			else if (operation.StartsWith("notification ", StringComparison.Ordinal))
			{
				data.OnSwitch = this._settings.NotificationWhenSwitchedDesktop.Value;
				data.AlwaysShow = this._settings.AlwaysShowDesktopNotification.Value;
				data.DurationMs = this._settings.NotificationDuration.Value;
			}
			else if (operation.StartsWith("tray ", StringComparison.Ordinal))
			{
				data.ShowDesktop = this._settings.TrayShowDesktop.Value;
				data.CurrentNumberOnly = this._settings.TrayShowOnlyCurrentNumber.Value;
			}
			else data.Language = this._settings.Culture.Value ?? "auto";
			return data;
		}
	}
}
#endif
