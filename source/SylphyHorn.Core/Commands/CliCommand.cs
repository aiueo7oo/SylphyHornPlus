using System;
using System.Collections.Generic;
using System.Globalization;

namespace SylphyHorn.Commands
{
	internal sealed class CliCommand
	{
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
			var assignmentSelectors = (command.AppPath != null ? 1 : 0) + (command.RuleId != null ? 1 : 0) + (command.All ? 1 : 0);
			if ((command.Operation == "app assignment set" || command.Operation == "app assignment remove") && assignmentSelectors != 1)
				throw new ArgumentException("Specify exactly one of --path or --id.");
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
			return command;
		}

		private static bool IsKnown(string operation)
			=> operation == "desktop list" || operation == "desktop switch" || operation == "desktop create"
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
