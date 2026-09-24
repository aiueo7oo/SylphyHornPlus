#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using MetroRadiance.UI.Controls;
using SylphyHorn.Commands;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliSettingsService
	{
		private static readonly IReadOnlyDictionary<string, uint> Themes = new Dictionary<string, uint>(StringComparer.Ordinal)
		{
			{ "apps", (uint)BlurWindowThemeMode.Default },
			{ "system", (uint)BlurWindowThemeMode.System },
			{ "light", (uint)BlurWindowThemeMode.Light },
			{ "dark", (uint)BlurWindowThemeMode.Dark },
			{ "accent", (uint)BlurWindowThemeMode.Accent },
		};

		private static readonly IReadOnlyDictionary<string, uint> Corners = new Dictionary<string, uint>(StringComparer.Ordinal)
		{
			{ "square", (uint)BlurWindowCornerMode.NotRounded },
			{ "rounded", (uint)BlurWindowCornerMode.Rounded },
			{ "small-rounded", (uint)BlurWindowCornerMode.SmallRounded },
		};

		private static readonly IReadOnlyDictionary<string, uint> Alignments = new Dictionary<string, uint>(StringComparer.Ordinal)
		{
			{ "left", (uint)HorizontalAlignment.Left },
			{ "center", (uint)HorizontalAlignment.Center },
			{ "right", (uint)HorizontalAlignment.Right },
		};

		private static readonly IReadOnlyDictionary<string, uint> Placements = new Dictionary<string, uint>(StringComparer.Ordinal)
		{
			{ "top-left", (uint)WindowPlacement.TopLeft },
			{ "top-center", (uint)WindowPlacement.TopCenter },
			{ "top-right", (uint)WindowPlacement.TopRight },
			{ "center-left", (uint)WindowPlacement.CenterLeft },
			{ "center", (uint)WindowPlacement.Center },
			{ "center-right", (uint)WindowPlacement.CenterRight },
			{ "bottom-left", (uint)WindowPlacement.BottomLeft },
			{ "bottom-center", (uint)WindowPlacement.BottomCenter },
			{ "bottom-right", (uint)WindowPlacement.BottomRight },
		};

		private readonly Func<Monitor[]> _monitors;
		private readonly GeneralSettings _settings;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<bool> _available;
		private readonly string _startupCulture;

		internal CliSettingsService(GeneralSettings settings, Func<Task<SettingsSaveResult>> save, Func<bool> available, string startupCulture,
			Func<Monitor[]> monitors = null)
		{
			this._monitors = monitors ?? MonitorService.GetMonitors;
			this._settings = settings;
			this._save = save;
			this._available = available;
			this._startupCulture = startupCulture;
		}

		internal static bool Handles(string operation)
			=> operation == "monitor list" || operation == "desktop settings" || operation == "desktop configure"
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
				var monitors = command.Operation == "monitor list" || command.Operation.StartsWith("notification ", StringComparison.Ordinal)
					? this._monitors() : Array.Empty<Monitor>();
				if (command.Operation == "monitor list")
					return CliResponse.Ok(command.Operation, new CliData
					{
						Monitors = monitors.Select((monitor, index) => new CliMonitor
						{
							Number = index + 1, Name = monitor.Name,
							Bounds = DescribeRectangle(monitor.MonitorArea), WorkArea = DescribeRectangle(monitor.WorkArea),
						}).ToArray(),
					});
				if (!command.Operation.EndsWith(" configure", StringComparison.Ordinal))
					return CliResponse.Ok(command.Operation, this.Describe(command.Operation, monitors.Length));
				if (!this._available()) return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);

				uint? display = command.Monitor == null ? (uint?)null : command.Monitor == "current" ? 0u
					: command.Monitor == "all" ? uint.MaxValue : uint.Parse(command.Monitor, CultureInfo.InvariantCulture);
				if (display.HasValue && display != 0 && display != uint.MaxValue && display > monitors.Length)
					return CliResponse.Fail(command.Operation, "monitor_unavailable", "The specified monitor number is not currently available.");

				if (!ValidFontSize(command.HeaderFontSize) || !ValidFontSize(command.BodyFontSize))
					return CliResponse.Fail(command.Operation, "invalid_arguments", "The font size is outside the supported rendering range.");
				if (command.FontFamily != null && command.FontFamily.Length != 0)
				{
					try
					{
						if (string.IsNullOrWhiteSpace(command.FontFamily)) throw new ArgumentException();
						_ = new FontFamily(command.FontFamily);
					}
					catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
					{
						return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a font family or an empty string for the default.");
					}
				}

				Set(this._settings.Display, display, ref changed);
				Set(this._settings.Placement, command.Placement == null ? (uint?)null : Placements[command.Placement], ref changed);
				Set(this._settings.NotificationOffsetX, command.OffsetX, ref changed);
				Set(this._settings.NotificationOffsetY, command.OffsetY, ref changed);
				Set(this._settings.NotificationMinWidth, command.MinWidth, ref changed);
				Set(this._settings.SimpleNotificationMinWidth, command.SimpleMinWidth, ref changed);
				Set(this._settings.NotificationMinHeight, command.MinHeight, ref changed);
				Set(this._settings.PinWindowMinWidth, command.PinMinWidth, ref changed);
				Set(this._settings.PinWindowOffsetX, command.PinOffsetX, ref changed);
				Set(this._settings.PinWindowOffsetY, command.PinOffsetY, ref changed);
				Set(this._settings.LoopDesktop, command.Loop, ref changed);
				Set(this._settings.OverrideWindowsDefaultKeyCombination, command.OverrideWindowsShortcuts, ref changed);
				Set(this._settings.NotificationWhenSwitchedDesktop, command.OnSwitch, ref changed);
				Set(this._settings.NotificationDuration, command.DurationMs, ref changed);
				Set(this._settings.SimpleNotification, command.Simple, ref changed);
				Set(this._settings.UseDesktopName, command.UseDesktopName, ref changed);
				Set(this._settings.NotificationHeaderFontSize, command.HeaderFontSize, ref changed);
				Set(this._settings.NotificationBodyFontSize, command.BodyFontSize, ref changed);
				Set(this._settings.NotificationLineSpacing, command.LineSpacing, ref changed);
				Set(this._settings.NotificationWindowStyle, command.Theme == null ? (uint?)null : Themes[command.Theme], ref changed);
				Set(this._settings.NotificationCornerStyle, command.Corners == null ? (uint?)null : Corners[command.Corners], ref changed);
				Set(this._settings.NotificationHeaderAlignment, command.HeaderAlign == null ? (uint?)null : Alignments[command.HeaderAlign], ref changed);
				Set(this._settings.NotificationBodyAlignment, command.BodyAlign == null ? (uint?)null : Alignments[command.BodyAlign], ref changed);
				if (command.FontFamily != null && command.FontFamily != (this._settings.NotificationFontFamily.Value ?? ""))
				{
					changed = true;
					this._settings.NotificationFontFamily.Value = command.FontFamily;
				}
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

				var data = this.Describe(command.Operation, monitors.Length);
				// An unchanged request also retries persistence after an earlier save failure.
				var saved = await this._save().WaitAsync(cancellation);
				if (!saved.Succeeded)
					return CliResponse.Fail(command.Operation, "settings_save_failed", "Settings are active in memory but could not be saved.");
				if (!CliProtocol.Serialize(data).SequenceEqual(CliProtocol.Serialize(this.Describe(command.Operation, monitors.Length))))
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

		private static CliRectangle DescribeRectangle(Rect rectangle)
			=> new CliRectangle { X = rectangle.X, Y = rectangle.Y, Width = rectangle.Width, Height = rectangle.Height };

		private static bool ValidFontSize(int? value)
			=> !value.HasValue || TextElement.FontSizeProperty.IsValidValue((double)value.Value);

		private static string SettingName(IReadOnlyDictionary<string, uint> values, uint value)
			=> values.FirstOrDefault(item => item.Value == value).Key ?? "unknown";

		private static void Set<T>(SerializableProperty<T> property, T? value, ref bool changed) where T : struct
		{
			if (!value.HasValue || EqualityComparer<T>.Default.Equals(property.Value, value.Value)) return;
			changed = true;
			property.Value = value.Value;
		}

		private CliData Describe(string operation, int monitorCount)
		{
			var data = new CliData { RestartRequired = this._settings.Culture.Value != this._startupCulture };
			if (operation.StartsWith("desktop ", StringComparison.Ordinal))
			{
				data.Loop = this._settings.LoopDesktop.Value;
				data.OverrideWindowsShortcuts = this._settings.OverrideWindowsDefaultKeyCombination.Value;
			}
			else if (operation.StartsWith("notification ", StringComparison.Ordinal))
			{
				var display = this._settings.Display.Value;
				data.Monitor = display == 0 ? "current" : display == uint.MaxValue ? "all" : display.ToString(CultureInfo.InvariantCulture);
				data.MonitorAvailable = monitorCount > 0 && (display == 0 || display == uint.MaxValue || display <= monitorCount);
				data.Placement = SettingName(Placements, this._settings.Placement.Value);
				data.OffsetX = this._settings.NotificationOffsetX.Value;
				data.OffsetY = this._settings.NotificationOffsetY.Value;
				data.MinWidth = this._settings.NotificationMinWidth.Value;
				data.SimpleMinWidth = this._settings.SimpleNotificationMinWidth.Value;
				data.MinHeight = this._settings.NotificationMinHeight.Value;
				data.PinMinWidth = this._settings.PinWindowMinWidth.Value;
				data.PinOffsetX = this._settings.PinWindowOffsetX.Value;
				data.PinOffsetY = this._settings.PinWindowOffsetY.Value;
				data.OnSwitch = this._settings.NotificationWhenSwitchedDesktop.Value;
				data.AlwaysShow = this._settings.AlwaysShowDesktopNotification.Value;
				data.DurationMs = this._settings.NotificationDuration.Value;
				data.Simple = this._settings.SimpleNotification.Value;
				data.UseDesktopName = this._settings.UseDesktopName.Value;
				data.HeaderFontSize = this._settings.NotificationHeaderFontSize.Value;
				data.BodyFontSize = this._settings.NotificationBodyFontSize.Value;
				data.LineSpacing = this._settings.NotificationLineSpacing.Value;
				data.Theme = SettingName(Themes, this._settings.NotificationWindowStyle.Value);
				data.Corners = SettingName(Corners, this._settings.NotificationCornerStyle.Value);
				data.HeaderAlign = SettingName(Alignments, this._settings.NotificationHeaderAlignment.Value);
				data.BodyAlign = SettingName(Alignments, this._settings.NotificationBodyAlignment.Value);
				data.FontFamily = this._settings.NotificationFontFamily.Value ?? "";
				data.CornersSupported = ProductInfo.IsWindows11OrLater;
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
