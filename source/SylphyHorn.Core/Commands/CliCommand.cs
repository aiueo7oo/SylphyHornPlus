using System;
using System.Collections.Generic;
using System.Globalization;

namespace SylphyHorn.Commands
{
	internal sealed class CliCommand
	{
		internal string FilePath { get; private set; }

		internal bool Overwrite { get; private set; }

		internal bool? ApplyDesktops { get; private set; }

		internal string StartupMode { get; private set; }

		internal string Device { get; private set; }

		internal string Action { get; private set; }

		internal string Trigger { get; private set; }

		internal bool? PerDesktopWallpaper { get; private set; }

		internal bool? OverrideOnStartup { get; private set; }

		internal string Monitor { get; private set; }

		internal string Placement { get; private set; }

		internal int? OffsetX { get; private set; }

		internal int? OffsetY { get; private set; }

		internal int? MinWidth { get; private set; }

		internal int? SimpleMinWidth { get; private set; }

		internal int? MinHeight { get; private set; }

		internal int? PinMinWidth { get; private set; }

		internal int? PinOffsetX { get; private set; }

		internal int? PinOffsetY { get; private set; }

		internal bool? Simple { get; private set; }

		internal bool? UseDesktopName { get; private set; }

		internal string Theme { get; private set; }

		internal string Corners { get; private set; }

		internal string FontFamily { get; private set; }

		internal int? HeaderFontSize { get; private set; }

		internal int? BodyFontSize { get; private set; }

		internal string HeaderAlign { get; private set; }

		internal string BodyAlign { get; private set; }

		internal int? LineSpacing { get; private set; }

		internal bool? Loop { get; private set; }

		internal bool? OverrideWindowsShortcuts { get; private set; }

		internal bool? OnSwitch { get; private set; }

		internal bool? AlwaysShow { get; private set; }

		internal int? DurationMs { get; private set; }

		internal bool? ShowDesktop { get; private set; }

		internal bool? CurrentNumberOnly { get; private set; }

		internal bool HasNotificationGeometry
			=> this.Monitor != null || this.Placement != null || this.OffsetX != null || this.OffsetY != null
				|| this.MinWidth != null || this.SimpleMinWidth != null || this.MinHeight != null
				|| this.PinMinWidth != null || this.PinOffsetX != null || this.PinOffsetY != null;

		internal bool HasNotificationAppearance
			=> this.Simple != null || this.UseDesktopName != null || this.Theme != null || this.Corners != null || this.FontFamily != null
			|| this.HeaderFontSize != null || this.BodyFontSize != null || this.HeaderAlign != null || this.BodyAlign != null || this.LineSpacing != null;

		internal string Language { get; private set; }

		internal string Operation { get; private set; }

		internal string TargetKind { get; private set; }

		internal string TargetValue { get; private set; }

		internal string WindowId { get; private set; }

		internal string Name { get; private set; }

		internal int? Number { get; private set; }

		internal string WallpaperPath { get; private set; }

		internal string WallpaperPosition { get; private set; }

		internal string Scope { get; private set; }

		internal string AppPath { get; private set; }

		internal string AppId { get; private set; }

		internal string Source { get; private set; } = "registered";

		internal string RuleId { get; private set; }

		internal bool? AssignmentEnabled { get; private set; }

		internal bool? CreateMissingDesktops { get; private set; }

		internal bool? CloseCreatedDesktops { get; private set; }

		internal bool All { get; private set; }

		internal bool DryRun { get; private set; }

		internal bool Wrap { get; private set; }

		internal bool Follow { get; private set; }

		internal bool SwitchAfterCreate { get; private set; }

		internal static string Recognize(string[] args)
		{
			if (args == null || args.Length < 2) return null;
			var operation = args[0] + " " + args[1];
			if ((operation == "app assignment" || operation == "desktop autoclose") && args.Length >= 3) operation += " " + args[2];
			return IsKnown(operation) ? operation : null;
		}

		internal static CliCommand Parse(string[] args)
		{
			if (args == null) throw new ArgumentNullException(nameof(args));
			if (args.Length < 2) throw new ArgumentException("Specify a command.");
			var command = new CliCommand { Operation = Recognize(args) };
			if (command.Operation == null) throw new ArgumentException("Unknown command.");
			var assignment = command.Operation.StartsWith("app assignment ", StringComparison.Ordinal);
			var autoclose = command.Operation.StartsWith("desktop autoclose ", StringComparison.Ordinal);

			var options = new HashSet<string>(StringComparer.Ordinal);
			for (var i = assignment || autoclose ? 3 : 2; i < args.Length; i++)
			{
				var option = args[i];
				if (!options.Add(option)) throw new ArgumentException("Duplicate option: " + option);
				if (command.Operation == "app assignment configure"
					&& (option == "--enabled" || option == "--create-missing-desktops" || option == "--close-created-desktops"))
				{
					var value = ReadValue(args, ref i);
					if (value != "true" && value != "false") throw new ArgumentException(option + " requires true or false.");
					if (option == "--enabled") command.AssignmentEnabled = value == "true";
					else if (option == "--create-missing-desktops") command.CreateMissingDesktops = value == "true";
					else command.CloseCreatedDesktops = value == "true";
				}
				else if (option == "--id" && (command.Operation == "app assignment enable" || command.Operation == "app assignment disable"
					|| command.Operation == "app assignment set" || command.Operation == "app assignment remove" || command.Operation == "app assignment apply"))
				{
					command.RuleId = ReadValue(args, ref i);
					RequireId(command.RuleId);
				}
				else if ((command.Operation == "settings export" || command.Operation == "settings import") && option == "--path")
					command.FilePath = ReadTextValue(args, ref i, "Specify a settings file path.");
				else if (command.Operation == "settings export" && option == "--overwrite")
					command.Overwrite = true;
				else if (command.Operation == "settings import" && option == "--apply-desktops")
					command.ApplyDesktops = ReadBoolean(args, ref i);
				else if (command.Operation == "startup configure" && option == "--mode")
					command.StartupMode = ReadChoice(args, ref i, "disabled", "normal", "elevated");
				else if (command.Operation.StartsWith("shortcut ", StringComparison.Ordinal) && option == "--device")
					command.Device = ReadChoice(args, ref i, "keyboard", "mouse");
				else if ((command.Operation == "shortcut set" || command.Operation == "shortcut clear") && option == "--action")
					command.Action = ReadTextValue(args, ref i, "Specify an action returned by shortcut list.");
				else if (command.Operation == "shortcut set" && option == "--trigger")
					command.Trigger = ReadTextValue(args, ref i, "Specify a trigger using names from shortcut keys.");
				else if ((command.Operation == "shortcut set" || command.Operation == "shortcut clear") && option == "--number")
				{
					if (!int.TryParse(ReadValue(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
						|| number < 1 || number > 1000)
						throw new ArgumentException("Shortcut desktop numbers must be between 1 and 1000.");
					command.Number = number;
				}
				else if (command.Operation == "desktop configure" && option == "--per-desktop-wallpaper")
					command.PerDesktopWallpaper = ReadBoolean(args, ref i);
				else if (command.Operation == "desktop configure" && option == "--override-on-startup")
					command.OverrideOnStartup = ReadBoolean(args, ref i);
				else if (command.Operation == "desktop configure" && option == "--loop")
					command.Loop = ReadBoolean(args, ref i);
				else if (command.Operation == "desktop configure" && option == "--override-windows-shortcuts")
					command.OverrideWindowsShortcuts = ReadBoolean(args, ref i);
				else if (command.Operation == "notification configure" && option == "--on-switch")
					command.OnSwitch = ReadBoolean(args, ref i);
				else if (command.Operation == "notification configure" && option == "--always-show")
					command.AlwaysShow = ReadBoolean(args, ref i);
				else if (command.Operation == "tray configure" && option == "--show-desktop")
					command.ShowDesktop = ReadBoolean(args, ref i);
				else if (command.Operation == "tray configure" && option == "--current-number-only")
					command.CurrentNumberOnly = ReadBoolean(args, ref i);
				else if (command.Operation == "notification configure" && option == "--monitor")
				{
					command.Monitor = ReadValue(args, ref i);
					if (command.Monitor != "current" && command.Monitor != "all"
						&& (!uint.TryParse(command.Monitor, NumberStyles.None, CultureInfo.InvariantCulture, out var monitor)
							|| monitor == 0 || monitor == uint.MaxValue))
						throw new ArgumentException("--monitor requires current, all or a positive monitor number.");
				}
				else if (command.Operation == "notification configure" && option == "--placement")
					command.Placement = ReadChoice(args, ref i,
						"top-left", "top-center", "top-right", "center-left", "center", "center-right",
						"bottom-left", "bottom-center", "bottom-right");
				else if (command.Operation == "notification configure"
					&& (option == "--offset-x"
						|| option == "--offset-y"
						|| option == "--min-width"
						|| option == "--simple-min-width"
						|| option == "--min-height"
						|| option == "--pin-min-width"
						|| option == "--pin-offset-x"
						|| option == "--pin-offset-y"))
				{
					if (!int.TryParse(ReadValue(args, ref i), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
						|| (option.Contains("min-") && value < 1))
						throw new ArgumentException("Minimum sizes must be positive integers; offsets must be signed integers.");
					if (option == "--offset-x") command.OffsetX = value;
					else if (option == "--offset-y") command.OffsetY = value;
					else if (option == "--min-width") command.MinWidth = value;
					else if (option == "--simple-min-width") command.SimpleMinWidth = value;
					else if (option == "--min-height") command.MinHeight = value;
					else if (option == "--pin-min-width") command.PinMinWidth = value;
					else if (option == "--pin-offset-x") command.PinOffsetX = value;
					else if (option == "--pin-offset-y") command.PinOffsetY = value;
				}
				else if (command.Operation == "notification configure" && option == "--simple")
					command.Simple = ReadBoolean(args, ref i);
				else if (command.Operation == "notification configure" && option == "--use-desktop-name")
					command.UseDesktopName = ReadBoolean(args, ref i);
				else if (command.Operation == "notification configure" && option == "--theme")
					command.Theme = ReadChoice(args, ref i, "apps", "system", "light", "dark", "accent");
				else if (command.Operation == "notification configure" && option == "--corners")
					command.Corners = ReadChoice(args, ref i, "square", "rounded", "small-rounded");
				else if (command.Operation == "notification configure" && option == "--header-align")
					command.HeaderAlign = ReadChoice(args, ref i, "left", "center", "right");
				else if (command.Operation == "notification configure" && option == "--body-align")
					command.BodyAlign = ReadChoice(args, ref i, "left", "center", "right");
				else if (command.Operation == "notification configure" && option == "--font-family")
					command.FontFamily = ReadTextValue(args, ref i, "A font family is missing; use an empty string to restore the default.");
				else if (command.Operation == "notification configure"
					&& (option == "--header-font-size" || option == "--body-font-size" || option == "--line-spacing"))
				{
					if (!int.TryParse(ReadValue(args, ref i), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
						|| (option != "--line-spacing" && number < 1))
						throw new ArgumentException("Font sizes must be positive integers; line spacing must be a signed integer.");
					if (option == "--header-font-size") command.HeaderFontSize = number;
					else if (option == "--body-font-size") command.BodyFontSize = number;
					else command.LineSpacing = number;
				}
				else if (command.Operation == "notification configure" && option == "--duration-ms")
				{
					if (!int.TryParse(ReadValue(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out var duration) || duration < 1)
						throw new ArgumentException("--duration-ms requires a positive integer in milliseconds.");
					command.DurationMs = duration;
				}
				else if (command.Operation == "settings configure" && option == "--language")
				{
					command.Language = ReadValue(args, ref i);
					if (command.Language != "auto" && command.Language != "en" && command.Language != "ja")
						throw new ArgumentException("--language must be auto, en or ja.");
				}
				else if (option == "--source" && command.Operation == "app list")
				{
					command.Source = ReadValue(args, ref i);
					if (command.Source != "registered" && command.Source != "windows")
						throw new ArgumentException("--source must be registered or windows.");
				}
				else if (option == "--app-id" && command.Operation == "app assignment set")
					command.AppId = ReadValue(args, ref i);
				else if (option == "--all" && command.Operation == "app assignment apply") command.All = true;
				else if (option == "--dry-run" && command.Operation == "app assignment apply") command.DryRun = true;
				else if (option == "--path" && (command.Operation == "app assignment set" || command.Operation == "app assignment remove"
					|| command.Operation == "app assignment apply"))
					command.AppPath = ReadValue(args, ref i);
				else if ((command.Operation == "app assignment set" && (option == "--desktop-name" || option == "--desktop-number"))
					|| ((command.Operation == "desktop autoclose add" || command.Operation == "desktop autoclose remove")
						&& (option == "--name" || option == "--number")))
				{
					var kind = option.Substring(autoclose ? "--".Length : "--desktop-".Length);
					var value = ReadValue(args, ref i);
					if (kind == "number" && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1))
						throw new ArgumentException("Desktop numbers must be positive integers starting at 1.");
					command.SetTarget(kind, value);
				}
				else if (option == "--wrap" && (command.Operation == "desktop switch" || command.Operation == "window move"))
					command.Wrap = true;
				else if (option == "--switch" && command.Operation == "desktop create") command.SwitchAfterCreate = true;
				else if (option == "--follow" && command.Operation == "window move") command.Follow = true;
				else if ((option == "--next" || option == "--previous") && command.Operation == "desktop switch")
					command.SetTarget(option.Substring(2), null);
				else if (option == "--last-used" && command.Operation == "desktop switch")
					command.SetTarget("last-used", null);
				else if (command.Operation == "window move" && (option == "--desktop-next" || option == "--desktop-previous"
					|| option == "--desktop-last-used" || option == "--desktop-new"))
					command.SetTarget(option.Substring("--desktop-".Length), null);
				else if (option == "--id" && (command.Operation == "window move" || command.Operation == "window pin"
					|| command.Operation == "window unpin"))
				{
					command.WindowId = ReadValue(args, ref i);
					RequireId(command.WindowId);
				}
				else if (option == "--id" && (command.Operation == "desktop rename" || command.Operation == "desktop reorder"
					|| command.Operation == "desktop delete" || command.Operation == "desktop wallpaper"))
				{
					var id = ReadValue(args, ref i);
					RequireId(id);
					command.SetTarget("id", id);
				}
				else if (option == "--name" && (command.Operation == "desktop create" || command.Operation == "desktop rename"))
					command.Name = command.Operation == "desktop rename" ? ReadName(args, ref i) : ReadValue(args, ref i);
				else if (option == "--number" && command.Operation == "desktop reorder")
				{
					var value = ReadValue(args, ref i);
					if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
						throw new ArgumentException("Desktop numbers must be positive integers starting at 1.");
					command.Number = number;
				}
				else if (option == "--number" && (command.Operation == "desktop delete" || command.Operation == "desktop wallpaper"))
				{
					var value = ReadValue(args, ref i);
					if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
						throw new ArgumentException("Desktop numbers must be positive integers starting at 1.");
					command.SetTarget("number", value);
				}
				else if (option == "--path" && command.Operation == "desktop wallpaper")
					command.WallpaperPath = ReadTextValue(args, ref i, "A wallpaper path is missing.");
				else if (option == "--position" && command.Operation == "desktop wallpaper")
				{
					command.WallpaperPosition = ReadValue(args, ref i);
					if (command.WallpaperPosition != "center" && command.WallpaperPosition != "tile"
						&& command.WallpaperPosition != "stretch" && command.WallpaperPosition != "fit"
						&& command.WallpaperPosition != "fill" && command.WallpaperPosition != "span")
						throw new ArgumentException("--position must be center, tile, stretch, fit, fill, or span.");
				}
				else if (option == "--scope" && (command.Operation == "window pin" || command.Operation == "window unpin"))
				{
					command.Scope = ReadValue(args, ref i);
					if (command.Scope != "window" && command.Scope != "app") throw new ArgumentException("--scope must be window or app.");
				}
				else
				{
					var prefix = command.Operation == "window move" ? "--desktop-" : "--";
					if ((command.Operation != "desktop switch" && command.Operation != "window move")
						|| (option != prefix + "number" && option != prefix + "name" && option != prefix + "id"))
						throw new ArgumentException("Unknown option: " + option);
					var kind = option.Substring(prefix.Length);
					var value = ReadValue(args, ref i);
					if (kind == "number" && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1))
						throw new ArgumentException("Desktop numbers must be positive integers starting at 1.");
					if (kind == "id") RequireId(value);
					command.SetTarget(kind, value);
				}
			}

			if ((command.Operation == "desktop switch" || command.Operation == "window move") && command.TargetKind == null)
				throw new ArgumentException("Specify exactly one destination.");
			if (command.Operation == "window move" && command.WindowId == null)
				throw new ArgumentException("Specify --id using a window ID returned by window list.");
			if ((command.Operation == "desktop rename" && command.Name == null)
				|| (command.Operation == "desktop reorder" && command.Number == null)
				|| ((command.Operation == "desktop rename" || command.Operation == "desktop reorder"
					|| command.Operation == "desktop delete" || command.Operation == "desktop wallpaper") && command.TargetKind == null))
				throw new ArgumentException("Specify a desktop selector and required value.");
			if (command.Operation == "desktop wallpaper" && (command.WallpaperPath == null) == (command.WallpaperPosition == null))
				throw new ArgumentException("Specify exactly one of --path or --position.");
			if ((command.Operation == "window pin" || command.Operation == "window unpin") && (command.WindowId == null || command.Scope == null))
				throw new ArgumentException("Specify --id from window list and --scope window or app.");
			if (command.Wrap && command.TargetKind != "next" && command.TargetKind != "previous")
				throw new ArgumentException("--wrap requires a next or previous destination.");
			var assignmentSelectors = (command.AppPath != null ? 1 : 0) + (command.RuleId != null ? 1 : 0) + (command.All ? 1 : 0) + (command.AppId != null ? 1 : 0);
			if ((command.Operation == "app assignment set" || command.Operation == "app assignment remove") && assignmentSelectors != 1)
				throw new ArgumentException(command.Operation == "app assignment set"
					? "Specify exactly one of --path, --app-id or --id." : "Specify exactly one of --path or --id.");
			if (command.Operation == "app assignment apply" && assignmentSelectors != 1)
				throw new ArgumentException("Specify exactly one of --path, --id or --all.");
			if ((command.Operation == "desktop autoclose add" || command.Operation == "desktop autoclose remove") && command.TargetKind == null)
				throw new ArgumentException("Specify exactly one of --name or --number.");
			if (command.Operation == "app assignment set" && command.TargetKind == null)
				throw new ArgumentException("Specify exactly one of --desktop-name or --desktop-number.");
			if (command.Operation == "app assignment configure" && command.AssignmentEnabled == null
				&& command.CreateMissingDesktops == null && command.CloseCreatedDesktops == null)
				throw new ArgumentException("Specify at least one assignment setting.");
			if ((command.Operation == "app assignment enable" || command.Operation == "app assignment disable") && command.RuleId == null)
				throw new ArgumentException("Specify --id using a saved rule ID returned by app assignment list.");
			if ((command.Operation == "desktop configure" && command.Loop == null && command.OverrideWindowsShortcuts == null
					&& command.PerDesktopWallpaper == null && command.OverrideOnStartup == null)
				|| (command.Operation == "notification configure" && command.OnSwitch == null && command.AlwaysShow == null
					&& command.DurationMs == null && !command.HasNotificationAppearance && !command.HasNotificationGeometry)
				|| (command.Operation == "tray configure" && command.ShowDesktop == null && command.CurrentNumberOnly == null)
				|| (command.Operation == "settings configure" && command.Language == null))
				throw new ArgumentException("Specify at least one setting. Read current values with desktop/notification/tray settings or settings get.");
			if (command.Operation.StartsWith("shortcut ", StringComparison.Ordinal))
			{
				if (command.Operation != "shortcut list" && command.Device == null)
					throw new ArgumentException("Specify --device keyboard or mouse.");
				if ((command.Operation == "shortcut set" || command.Operation == "shortcut clear") && command.Action == null)
					throw new ArgumentException("Specify --action using shortcut list.");
				if (command.Operation == "shortcut set" && command.Trigger == null)
					throw new ArgumentException("Specify --trigger using shortcut keys.");
			}
			if ((command.Operation == "settings export" || command.Operation == "settings import") && command.FilePath == null)
				throw new ArgumentException("Specify --path.");
			if (command.Operation == "settings import" && command.ApplyDesktops == null)
				throw new ArgumentException("Specify --apply-desktops true or false.");
			if (command.Operation == "startup configure" && command.StartupMode == null)
				throw new ArgumentException("Specify --mode disabled, normal or elevated.");
			return command;
		}

		private static bool IsKnown(string operation)
			=> operation == "settings export" || operation == "settings import" || operation == "startup status" || operation == "startup configure"
				|| operation == "shortcut list" || operation == "shortcut keys" || operation == "shortcut set" || operation == "shortcut clear"
				|| operation == "monitor list" || operation == "desktop settings" || operation == "desktop configure"
				|| operation == "notification settings" || operation == "notification configure"
				|| operation == "tray settings" || operation == "tray configure"
				|| operation == "settings get" || operation == "settings configure" || operation == "app list"
				|| operation == "desktop list" || operation == "desktop switch" || operation == "desktop create"
				|| operation == "desktop rename" || operation == "desktop reorder" || operation == "desktop delete"
				|| operation == "desktop wallpaper" || operation == "window list"
				|| operation == "window move" || operation == "window pin" || operation == "window unpin"
				|| operation == "ui task-view" || operation == "ui window-switch" || operation == "ui settings"
				|| operation == "ui notification-toggle" || operation == "app assignment list"
				|| operation == "app assignment set" || operation == "app assignment remove" || operation == "app assignment apply"
				|| operation == "app assignment status" || operation == "app assignment configure"
				|| operation == "app assignment enable" || operation == "app assignment disable"
				|| operation == "desktop autoclose list" || operation == "desktop autoclose add" || operation == "desktop autoclose remove";

		private void SetTarget(string kind, string value)
		{
			if (this.TargetKind != null) throw new ArgumentException("Destination options are mutually exclusive.");
			this.TargetKind = kind;
			this.TargetValue = value;
		}

		private static string ReadChoice(string[] args, ref int index, params string[] choices)
		{
			var value = ReadValue(args, ref index);
			if (Array.IndexOf(choices, value) < 0) throw new ArgumentException("Expected one of: " + string.Join(", ", choices) + ".");
			return value;
		}

		private static bool ReadBoolean(string[] args, ref int index)
		{
			var value = ReadValue(args, ref index);
			if (value != "true" && value != "false") throw new ArgumentException("Boolean settings require true or false.");
			return value == "true";
		}

		private static string ReadValue(string[] args, ref int index)
		{
			if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
				throw new ArgumentException("An option value is missing.");
			return args[index];
		}

		private static string ReadName(string[] args, ref int index)
			=> ReadTextValue(args, ref index, "A desktop name is missing.");

		private static string ReadTextValue(string[] args, ref int index, string error)
		{
			if (++index >= args.Length || args[index] == null || args[index].StartsWith("--", StringComparison.Ordinal))
				throw new ArgumentException(error);
			return args[index];
		}

		private static void RequireId(string value)
		{
			if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new ArgumentException("Specify a nonempty ID returned by the corresponding list command.");
		}
	}
}
