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

		internal bool Wrap { get; private set; }

		internal bool Follow { get; private set; }

		internal static string Recognize(string[] args)
		{
			if (args == null || args.Length < 2) return null;
			var operation = args[0] + " " + args[1];
			return operation == "desktop list" || operation == "desktop switch" || operation == "window list" || operation == "window move" ? operation : null;
		}

		internal static CliCommand Parse(string[] args)
		{
			if (args == null) throw new ArgumentNullException(nameof(args));
			if (args.Length < 2) throw new ArgumentException("Specify desktop list, desktop switch, window list or window move.");
			var command = new CliCommand { Operation = args[0] + " " + args[1] };
			if (command.Operation != "desktop list" && command.Operation != "desktop switch"
				&& command.Operation != "window list" && command.Operation != "window move")
				throw new ArgumentException("Unknown command.");

			var options = new HashSet<string>(StringComparer.Ordinal);
			for (var i = 2; i < args.Length; i++)
			{
				var option = args[i];
				if (!options.Add(option)) throw new ArgumentException("Duplicate option: " + option);
				if (option == "--wrap" && command.Operation == "desktop switch") command.Wrap = true;
				else if (option == "--follow" && command.Operation == "window move") command.Follow = true;
				else if ((option == "--next" || option == "--previous") && command.Operation == "desktop switch")
					command.SetTarget(option.Substring(2), null);
				else if (option == "--id" && command.Operation == "window move")
				{
					command.WindowId = ReadValue(args, ref i);
					RequireId(command.WindowId);
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
			if (command.Wrap && command.TargetKind != "next" && command.TargetKind != "previous")
				throw new ArgumentException("--wrap requires --next or --previous.");
			return command;
		}

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

		private static void RequireId(string value)
		{
			if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new ArgumentException("Specify a nonempty ID returned by the corresponding list command.");
		}
	}
}
