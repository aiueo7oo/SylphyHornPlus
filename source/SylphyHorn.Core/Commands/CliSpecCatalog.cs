using System;
using System.Collections.Generic;
using System.Linq;

namespace SylphyHorn.Commands
{
	internal static class CliSpecCatalog
	{
		private const string DefaultPrerequisite = "Start a compatible SylphyHorn GUI host in the same Windows user session and elevation level. " +
			"Runtime availability and OS capabilities are checked at execution.";

		private const string AssignmentStateFields =
			"assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops closingTargets";

		private static readonly Dictionary<string, CliCommandSpec> Definitions = Build()
			.ToDictionary(item => item.Name, StringComparer.Ordinal);

		// Command words plus every option and its value; the transport must accept the longest valid command.
		internal static readonly int MaximumArgumentCount = Definitions.Values.Max(item => item.Name.Split(' ').Length
			+ item.Arguments.Sum(argument => argument.Type == "flag" ? 1 : 2));

		internal static IEnumerable<CliCommandSpec> All => Definitions.Values.OrderBy(item => item.Name, StringComparer.Ordinal);

		internal static CliCommandSpec Find(string name)
			=> name != null && Definitions.TryGetValue(name, out var value) ? value : null;

		internal static string[] Choices(string command, string option)
			=> Find(command).Arguments.Single(argument => argument.Name == option).Values;

		private static IEnumerable<CliCommandSpec> Build()
			=> HostCommands()
				.Concat(DesktopCommands())
				.Concat(WindowCommands())
				.Concat(UiCommands())
				.Concat(CreationWallpaperCommands())
				.Concat(AppAssignmentCommands())
				.Concat(AutoCloseCommands())
				.Concat(SettingsCommands())
				.Concat(StartupCommands())
				.Concat(ShortcutCommands());

		#region Host

		private static IEnumerable<CliCommandSpec> HostCommands()
		{
			yield return Command("version", "Read CLI and running GUI versions and embedded Git revisions.", "cli host",
				notes: "Always returns local CLI information. Inspect host.status (available or unavailable) and host.errorCode: " +
					"outer success does not imply a reachable host. Missing revisions are null. " +
					"A revision is not proof of a clean working tree or compatibility. " +
					"The host query has a two-second deadline and never starts the GUI. " +
					"--version prints the local CLI version and available nine-character Git revision as text without connecting.",
				prerequisite: "No GUI required for local information; host information requires a compatible running GUI " +
					"in the same user session and elevation level.");

			yield return Command("exit", "Request normal shutdown of the connected SylphyHorn GUI host.", "accepted",
				notes: "Acceptance does not confirm process exit or settings persistence. " +
					"The host attempts to send the response before normal shutdown. " +
					"Once accepted, shutdown proceeds even if the client disconnects. " +
					"Use settings save first if its result must be checked.",
				effects: "exit-host save-settings stop-monitoring");

			yield return Command("logs", "Read recent application logs from the current GUI host.", "logs totalCount omittedCount",
				arguments: new[] { Option("--limit", "integer", omission: "50", minimum: 1, maximum: int.MaxValue) },
				notes: "Returns the latest entries in oldest-first order, with ISO 8601 timestamps, headers and full contents. " +
					"totalCount includes entries omitted by the limit; omittedCount reports that difference. " +
					"Logs are retained only for this host lifetime. " +
					"If the response exceeds the transport size limit, request fewer entries.",
				example: "--limit 50");

			yield return Command("monitor list", "Read current monitor identifiers and geometry.", "monitors",
				queries: Queries("monitor list"));
		}

		#endregion

		#region Desktops

		private static IEnumerable<CliCommandSpec> DesktopCommands()
		{
			yield return Command("desktop list", "Read desktops in current order and their IDs, names and wallpaper state.", "desktops",
				queries: Queries("desktop list"));

			yield return Command("desktop switch", "Switch the displayed desktop.", "desktop changed",
				arguments: new[]
				{
					DesktopOption("--number"), DesktopOption("--name"), DesktopOption("--id"),
					Flag("--next"), Flag("--previous"), Flag("--last-used"), Flag("--wrap"),
				},
				constraints: new[]
				{
					ExactlyOne("--number --name --id --next --previous --last-used"),
					RequiresAny("--wrap", "--next --previous"),
				},
				notes: "Desktop names match exactly; duplicate names select the lowest number. " +
					"Relative destinations use the current desktop. Without --wrap an end-of-list move fails.",
				effects: "switch-desktop",
				example: "--number 2",
				queries: Queries("desktop list"));

			yield return Command("desktop create", "Create one desktop, optionally naming it and switching to it.", "desktop changed",
				arguments: new[]
				{
					Option("--name", "string", omission: "unnamed", description: "A nonempty name requires OS desktop-name support."),
					Flag("--switch"),
				},
				notes: "After an unconfirmed creation, list desktops before retrying to avoid duplicates.",
				effects: "create-desktop optional-switch",
				queries: Queries("desktop list"));

			yield return Command("desktop rename", "Change or clear a desktop name.", "desktop changed",
				arguments: new[]
				{
					DesktopOption("--id", required: true),
					Option("--name", "string", required: true, description: "Empty string clears the name; requires OS name support."),
				},
				effects: "rename-desktop",
				example: "--id <DESKTOP_ID> --name Development",
				queries: Queries("desktop list"));

			yield return Command("desktop reorder", "Move a desktop to an existing position.", "desktop changed",
				arguments: new[] { DesktopOption("--id", required: true), DesktopOption("--number", required: true) },
				notes: "Requires OS desktop-reordering support. Number is the destination position, not a second selector.",
				effects: "reorder-desktops",
				example: "--id <DESKTOP_ID> --number 2",
				queries: Queries("desktop list"));

			yield return Command("desktop delete", "Close one desktop using the existing GUI removal behavior.", "desktops changed",
				arguments: new[]
				{
					DesktopOption("--id"), DesktopOption("--number"),
					DesktopOption("--fallback-id"), DesktopOption("--fallback-number"),
				},
				constraints: new[] { ExactlyOne("--id --number"), AtMostOne("--fallback-id --fallback-number") },
				notes: "Windows move to the specified existing fallback, which must differ from the deleted desktop. " +
					"If the deleted desktop is current, the display also switches there. " +
					"Without a fallback, existing removal behavior is preserved. " +
					"Numbers use the order before deletion. This is not application termination.",
				effects: "remove-desktop relocate-windows possible-switch",
				example: "--number 2",
				queries: Queries("desktop list", "desktop settings"));

			yield return Command("desktop wallpaper", "Set wallpaper image or placement for one desktop.", "desktop changed",
				arguments: new[]
				{
					DesktopOption("--id"), DesktopOption("--number"),
					Option("--path", "string", description: "Absolute path to an existing readable wallpaper image."),
					Option("--position", "string", values: "center tile stretch fit fill span"),
				},
				constraints: new[] { ExactlyOne("--id --number"), ExactlyOne("--path --position") },
				notes: "Wallpaper requires OS support or enabled per-desktop wallpaper. Position names are lowercase and case-sensitive.",
				effects: "change-wallpaper",
				example: "--number 2 --position fill",
				queries: Queries("desktop list", "desktop settings"));
		}

		private static IEnumerable<CliCommandSpec> CreationWallpaperCommands()
		{
			const string notes = "Names take priority over numbers. Destinations may be absent. Does not change existing wallpapers. " +
				"set replaces the same target; remove of an absent target succeeds unchanged. " +
				"Import restoration and intermediate filler desktops are excluded.";
			const string fields = "wallpapersOnCreation changed";
			var queries = Queries("desktop creation wallpaper list", "desktop list");

			yield return Command("desktop creation wallpaper list", "list wallpaper settings applied to newly created desktops.", fields,
				notes: notes,
				effects: "read-settings",
				queries: queries);

			yield return Command("desktop creation wallpaper set", "set wallpaper settings applied to newly created desktops.", fields,
				arguments: new[]
				{
					DesktopOption("--name"), DesktopOption("--number"),
					Option("--path", "string", required: true, description: "Absolute readable image path."),
				},
				constraints: new[] { ExactlyOne("--name --number") },
				notes: notes,
				effects: "persist-settings",
				example: "--number 3 --path C:\\Wallpapers\\work.jpg",
				queries: queries);

			yield return Command("desktop creation wallpaper remove", "remove wallpaper settings applied to newly created desktops.", fields,
				arguments: new[] { DesktopOption("--name"), DesktopOption("--number") },
				constraints: new[] { ExactlyOne("--name --number") },
				notes: notes,
				effects: "persist-settings",
				example: "--number 3",
				queries: queries);
		}

		private static IEnumerable<CliCommandSpec> AutoCloseCommands()
		{
			const string fields = "closingTargets assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops changed";
			const string notes = "Configures future automatic closure; does not immediately delete a desktop. " +
				"Targets can name desktops not currently present.";
			var queries = Queries("desktop autoclose list", "desktop list");

			yield return Command("desktop autoclose list", "Read automatic desktop closure targets.", fields,
				notes: notes,
				queries: queries);

			foreach (var verb in new[] { "add", "remove" })
			{
				yield return Command("desktop autoclose " + verb, verb + " an automatic desktop closure target.", fields,
					arguments: new[] { DesktopOption("--name"), DesktopOption("--number") },
					constraints: new[] { ExactlyOne("--name --number") },
					notes: notes,
					effects: "persist-closing-targets",
					example: "--number 2",
					queries: queries);
			}
		}

		#endregion

		#region Windows and UI

		private static IEnumerable<CliCommandSpec> WindowCommands()
		{
			yield return Command("window list", "List addressable windows and their current desktops and pin state.",
				"windows complete unavailableCount",
				notes: "Window IDs identify observed window instances, not HWNDs or process IDs. " +
					"Re-list when an ID becomes stale. Check complete and unavailableCount.",
				queries: Queries("window list"));

			yield return Command("window move", "Move one window, optionally following it to the destination.", "desktop window changed",
				arguments: new[]
				{
					WindowId(),
					DesktopOption("--desktop-number"), DesktopOption("--desktop-name"), DesktopOption("--desktop-id"),
					Flag("--desktop-next"), Flag("--desktop-previous"), Flag("--desktop-last-used"), Flag("--desktop-new"),
					Flag("--wrap"), Flag("--follow"),
				},
				constraints: new[]
				{
					ExactlyOne("--desktop-number --desktop-name --desktop-id --desktop-next --desktop-previous --desktop-last-used --desktop-new"),
					RequiresAny("--wrap", "--desktop-next --desktop-previous"),
				},
				notes: "Relative next/previous destinations are relative to the window's source desktop. " +
					"Pinned windows cannot be moved. --follow switches only after placement is confirmed.",
				effects: "move-window optional-create optional-switch",
				example: "--id <WINDOW_ID> --desktop-number 2",
				queries: Queries("window list", "desktop list"));

			yield return PinCommand("window pin", "Pin a window or its application across desktops.");
			yield return PinCommand("window unpin", "Unpin a window or its application.");
		}

		private static CliCommandSpec PinCommand(string name, string summary)
			=> Command(name, summary, "window changed",
				arguments: new[] { WindowId(), Option("--scope", "string", required: true, values: "window app") },
				notes: "Scope is mandatory; app affects every window of the identified application. No implicit scope is selected.",
				effects: "change-pin-state",
				example: "--id <WINDOW_ID> --scope window",
				queries: Queries("window list"));

		private static IEnumerable<CliCommandSpec> UiCommands()
		{
			const string notes = "Requires an interactive GUI host. Does not automate input inside the displayed interface.";

			foreach (var page in new[] { "task-view", "window-switch", "settings" })
			{
				yield return Command("ui " + page, "Show the " + page + " interface.", "", notes: notes, effects: "change-visible-ui");
			}
			yield return Command("ui notification-toggle", "Toggle the desktop notification display.", "", notes: notes, effects: "change-visible-ui");
		}

		#endregion

		#region Application assignment

		private static IEnumerable<CliCommandSpec> AppAssignmentCommands()
		{
			const string ruleFields = "assignments " + AssignmentStateFields + " changed";
			const string ruleNotes = "Rules store a destination name or number, not a desktop ID. Destinations may be absent. " +
				"set with --id updates that rule; executable paths and package identities are distinct. " +
				"set requires a destination unless both --id and --follow-foreground are supplied; in that case omission preserves the destination.";

			yield return Command("app list", "List registered applications or applications with open windows.", "apps source",
				arguments: new[] { Option("--source", "string", values: "registered windows", omission: "registered") },
				notes: "Use canAssign and reason. A launcher or unknown identity is not a safe assignment target.",
				queries: Queries("app list"));

			yield return Command("app assignment list", "Read saved application assignment rules.", "assignments " + AssignmentStateFields,
				queries: Queries("app assignment list"));

			yield return Command("app assignment status", "Read assignment configuration and monitoring status.", AssignmentStateFields,
				queries: Queries("app assignment status"));

			yield return Command("app assignment resume", "Resume paused automatic placement monitoring without changing settings.",
				"assignmentStatus changed",
				notes: "Paused monitoring starts a fresh session. Active or preparing monitoring is left unchanged. " +
					"Disabled placement or no rules returns assignment_unavailable; temporary suspension returns host_busy. " +
					"A preparing result is not confirmation of active monitoring. Does not bulk-apply rules to existing windows.",
				effects: "resume-monitoring",
				queries: Queries("app assignment status"));

			const string configureOptions = "--enabled --create-missing-desktops --close-created-desktops --follow-foreground";
			yield return Command("app assignment configure", "Change automatic assignment, desktop creation and closure settings.", ruleFields,
				arguments: Booleans(configureOptions),
				constraints: new[] { AtLeastOne(configureOptions) },
				notes: "Omitted settings are preserved. Closing created desktops also covers them without an individual closing target; " +
					"closure still uses the application's occupancy conditions. Follow is a default for rules without an override, " +
					"applies only to foreground windows, and never follows explicit apply.",
				effects: "persist-settings change-monitoring",
				example: "--enabled true",
				queries: Queries("app assignment status"));

			yield return Command("app assignment set", "set a saved application assignment rule.", ruleFields,
				arguments: new[]
				{
					RuleId(required: false),
					ExecutablePath(),
					Option("--app-id", "string",
						description: "Package application identity returned for an assignable app.",
						source: "app list: apps[].appIdentity when appKind=packageAppId"),
					DesktopOption("--desktop-name"),
					DesktopOption("--desktop-number"),
					Option("--follow-foreground", "string", values: "default true false", omission: "preserve-existing-or-default",
						description: "default inherits the global setting. true/false override it, " +
							"still only for foreground windows during automatic placement."),
				},
				constraints: new[] { AtMostOne("--desktop-name --desktop-number"), ExactlyOne("--id --path --app-id") },
				notes: ruleNotes,
				effects: "persist-rules",
				example: "--id <RULE_ID> --desktop-number 2",
				queries: Queries("app assignment list", "app assignment status", "app list", "desktop list"));

			yield return Command("app assignment remove", "remove a saved application assignment rule.", ruleFields,
				arguments: new[] { RuleId(required: false), ExecutablePath() },
				constraints: new[] { ExactlyOne("--id --path") },
				notes: ruleNotes,
				effects: "persist-rules",
				example: "--id <RULE_ID>",
				queries: Queries("app assignment list", "app assignment status"));

			yield return Command("app assignment apply", "Apply saved rules to existing windows, or inspect the planned moves.",
				"results dryRun changed",
				arguments: new[] { RuleId(required: false), ExecutablePath(), Flag("--all"), Flag("--dry-run") },
				constraints: new[] { ExactlyOne("--id --path --all") },
				notes: "Requires enabled, active monitoring. --dry-run does not move windows or create desktops. " +
					"Rechecks identity and destination during actual apply. Inspect each result, not only the envelope success.",
				effects: "move-windows-unless-dry-run",
				example: "--all --dry-run",
				queries: Queries("app assignment list", "app assignment status", "window list", "desktop list"));

			foreach (var verb in new[] { "enable", "disable" })
			{
				yield return Command("app assignment " + verb, verb + " a saved application assignment rule.", ruleFields,
					arguments: new[] { RuleId(required: true) },
					notes: ruleNotes,
					effects: "persist-rules",
					example: "--id <RULE_ID>",
					queries: Queries("app assignment list", "app assignment status"));
			}
		}

		private static CliSpecArgument RuleId(bool required)
			=> Option("--id", "uuid", required,
				description: "Persisted rule ID, shared with GUI settings.",
				source: "app assignment list: assignments[].id");

		private static CliSpecArgument ExecutablePath()
			=> Option("--path", "string",
				description: "Full executable path; not a process name.",
				source: "app list: apps[].executablePath; app assignment list: saved application identity");

		#endregion

		#region Settings and startup

		private static IEnumerable<CliCommandSpec> SettingsCommands()
		{
			var desktop = SettingsArea("desktop", "desktop settings",
				"loop overrideWindowsShortcuts perDesktopWallpaper overrideOnStartup nativeWallpaperSupported wallpaperEnabled",
				Booleans("--loop --override-windows-shortcuts --per-desktop-wallpaper --override-on-startup"),
				"override-on-startup affects the next launch and can change desktop count. " +
					"per-desktop-wallpaper is editable only without native OS wallpaper support. override-on-startup requires OS name support.",
				"--loop true");
			var notification = SettingsArea("notification", "notification settings",
				"onSwitch alwaysShow durationMs simple useDesktopName theme corners fontFamily headerFontSize bodyFontSize headerAlign bodyAlign " +
					"lineSpacing cornersSupported monitor monitorAvailable placement offsetX offsetY minWidth simpleMinWidth minHeight " +
					"pinMinWidth pinOffsetX pinOffsetY",
				NotificationArguments(),
				"Font sizes also require WPF rendering validation. Dimensions and offsets are logical pixels. " +
					"Corner styles are saved on every build; cornersSupported reports whether Windows can display them. " +
					"An invalid font family or font size, or an unavailable monitor, fails without applying the other supplied settings. " +
					"Empty font-family restores the default.",
				"--duration-ms 1000",
				"monitor list");
			var tray = SettingsArea("tray", "tray settings", "showDesktop currentNumberOnly",
				Booleans("--show-desktop --current-number-only"),
				"Omitted settings remain unchanged.",
				"--show-desktop true");
			var application = SettingsArea("settings", "settings get", "language restartRequired",
				new[] { Option("--language", "string", values: "auto en ja", omission: "preserve-current") },
				"Language changes may require restart; inspect restartRequired.",
				"--language en");

			foreach (var command in desktop.Concat(notification).Concat(tray).Concat(application))
			{
				yield return command;
			}

			yield return Command("settings export", "Export settings as a GUI-compatible XML backup.", "path",
				arguments: new[]
				{
					Option("--path", "string", required: true,
						description: "Relative to the CLI working directory. Cannot be the active settings file."),
					Flag("--overwrite"),
				},
				notes: "Existing destination files require --overwrite. No automatic backup of an overwritten export.",
				effects: "write-file",
				example: "--path backup.xml");

			yield return Command("settings import", "Replace settings from a GUI-compatible XML backup.", "path applyDesktops",
				arguments: new[]
				{
					Option("--path", "string", required: true, description: "Existing XML path, relative to the CLI working directory."),
					Option("--apply-desktops", "boolean", required: true, values: "true false"),
				},
				notes: "Not a merge. true applies saved desktop count, names and wallpaper paths; " +
					"false still applies other settings and wallpaper positions. " +
					"Startup registrations are unaffected. Partial failure is not a rollback guarantee.",
				effects: "replace-settings possible-create-remove-desktops",
				example: "--path backup.xml --apply-desktops false");

			yield return Command("settings save", "Save the current in-memory application settings to the normal settings file.", "saved",
				notes: "Saves current committed settings, not the values from a previous failed save. " +
					"Does not commit unfinished GUI edits, change preferences or apply wallpapers. " +
					"No prior save failure is required. Use settings export for a separate file.",
				effects: "persist-settings");

			yield return Command("settings reset", "Reset application settings using the GUI reset transaction.", "reset",
				arguments: new[] { Flag("--yes", required: true) },
				notes: "Clears rules and restores defaults. Preserves desktop count, order, names, wallpaper paths and startup registrations; " +
					"wallpaper positions become Fill. Export first if a backup is needed. Does not restart the app.",
				effects: "reset-settings",
				example: "--yes");
		}

		// Each settings area has a read command and a configure command that changes only the supplied options.
		private static IEnumerable<CliCommandSpec> SettingsArea(string area, string readCommand, string fields,
			CliSpecArgument[] arguments, string notes, string example, params string[] extraQueries)
		{
			var queries = Queries(new[] { readCommand }.Concat(extraQueries).ToArray());

			yield return Command(readCommand, "Read " + area + " settings.", fields, queries: Queries(readCommand));

			yield return Command(area + " configure", "Change only the supplied " + area + " settings.", fields + " changed",
				arguments: arguments,
				constraints: new[] { AtLeastOne(string.Join(" ", arguments.Select(argument => argument.Name))) },
				notes: notes,
				effects: "persist-settings",
				example: example,
				queries: queries);
		}

		private static CliSpecArgument[] NotificationArguments()
		{
			var result = new List<CliSpecArgument>(Booleans("--on-switch --always-show --simple --use-desktop-name"))
			{
				Option("--duration-ms", "integer", minimum: 1, maximum: int.MaxValue, omission: "preserve-current"),
				Option("--theme", "string", values: "apps system light dark accent", omission: "preserve-current"),
				Option("--corners", "string", values: "square rounded small-rounded", omission: "preserve-current"),
				Option("--font-family", "string", omission: "preserve-current",
					description: "Installed font-family name, or empty string for default."),
				Option("--header-align", "string", values: "left center right", omission: "preserve-current"),
				Option("--body-align", "string", values: "left center right", omission: "preserve-current"),
			};

			// Sizes and minimum dimensions must be positive; spacing and offsets may be negative.
			foreach (var name in Words("--header-font-size --body-font-size --line-spacing --offset-x --offset-y --min-width " +
				"--simple-min-width --min-height --pin-min-width --pin-offset-x --pin-offset-y"))
			{
				var positive = name.Contains("min-") || name.Contains("font-size");
				result.Add(Option(name, "integer", minimum: positive ? 1 : int.MinValue, maximum: int.MaxValue, omission: "preserve-current"));
			}

			result.Add(Option("--monitor", "string", omission: "preserve-current",
				description: "current, all, or an available monitor number (1..4294967294).",
				source: "monitor list: monitors[].number"));
			result.Add(Option("--placement", "string", omission: "preserve-current",
				values: "top-left top-center top-right center-left center center-right bottom-left bottom-center bottom-right"));
			return result.ToArray();
		}

		private static IEnumerable<CliCommandSpec> StartupCommands()
		{
			yield return Command("startup status", "Read startup registrations and their targets without changing them.", "startup",
				queries: Queries("startup status"));

			yield return Command("startup configure", "Select disabled, normal shortcut or elevated task startup.", "startup changed",
				arguments: new[] { Option("--mode", "string", required: true, values: "disabled normal elevated") },
				notes: "Registers the GUI executable. Task changes require administrator GUI and CLI; no UAC is opened. " +
					"Mismatched existing targets are not overwritten. " +
					"New registration is confirmed before removing the old one; failure can leave mixed state.",
				effects: "change-startup-registration",
				example: "--mode normal",
				queries: Queries("startup status"));
		}

		#endregion

		#region Shortcuts

		private static IEnumerable<CliCommandSpec> ShortcutCommands()
		{
			const string notes = "New conflicts are rejected without clearing other bindings. Numbered bindings can target future desktops. " +
				"Clearing a binding does not shift subsequent numbers. Listing does not execute any shortcut.";
			var bindingQueries = Queries("shortcut list", "shortcut keys --device keyboard", "shortcut keys --device mouse");
			var numberedAction = new CliSpecConstraint
			{
				Kind = "requiredWhenActionNumbered",
				Arguments = new[] { "--number" },
				WhenPresent = "--action",
			};

			yield return Command("shortcut list", "list keyboard or mouse shortcut bindings.", "shortcuts changed",
				arguments: new[] { Option("--device", "string", values: "keyboard mouse", omission: "both") },
				notes: notes,
				queries: Queries("shortcut list"));

			yield return Command("shortcut keys", "List permitted key names and their trigger/hold roles.", "keys",
				arguments: new[] { Device() },
				notes: notes,
				example: "--device keyboard",
				queries: bindingQueries);

			yield return Command("shortcut set", "set keyboard or mouse shortcut bindings.", "shortcuts changed",
				arguments: new[]
				{
					Device(),
					ShortcutAction(),
					ShortcutNumber(),
					Option("--trigger", "string", required: true,
						description: "Plus-separated key names; last token is the trigger, preceding tokens are held keys. " +
							"Left/right modifiers are distinct. Mouse LButton/RButton or wheel alone are rejected.",
						source: "shortcut keys --device keyboard|mouse: keys[].name, canHold, canTrigger"),
				},
				constraints: new[] { numberedAction },
				notes: notes,
				effects: "persist-shortcuts reload-input",
				example: "--device keyboard --action desktop-switch-left --trigger LControlKey+LWin+Left",
				queries: bindingQueries);

			yield return Command("shortcut clear", "clear keyboard or mouse shortcut bindings.", "shortcuts changed",
				arguments: new[] { Device(), ShortcutAction(), ShortcutNumber() },
				constraints: new[] { numberedAction },
				notes: notes,
				effects: "persist-shortcuts reload-input",
				example: "--device keyboard --action desktop-switch-left",
				queries: bindingQueries);
		}

		private static CliSpecArgument Device()
			=> Option("--device", "string", required: true, values: "keyboard mouse");

		private static CliSpecArgument ShortcutAction()
			=> Option("--action", "string", required: true, source: "shortcut list: shortcuts[].action, grouped by device");

		private static CliSpecArgument ShortcutNumber()
			=> Option("--number", "integer", minimum: 1, maximum: 1000,
				description: "Required only when the selected action has numberRequired=true; forbidden for fixed actions.");

		#endregion

		#region Builders

		private static CliCommandSpec Command(string name, string summary, string resultFields,
			CliSpecArgument[] arguments = null, CliSpecConstraint[] constraints = null, string notes = null,
			string effects = "read-only", string example = "", string[][] queries = null, string prerequisite = DefaultPrerequisite)
			=> new CliCommandSpec
			{
				Name = name,
				Summary = summary,
				Arguments = arguments ?? Array.Empty<CliSpecArgument>(),
				Constraints = constraints ?? Array.Empty<CliSpecConstraint>(),
				Notes = notes == null ? Array.Empty<string>() : new[] { notes },
				Prerequisites = new[] { prerequisite },
				Effects = Words(effects),
				ResultFields = Words(resultFields),
				Examples = new[] { Words(name + " " + example) },
				Queries = queries ?? Array.Empty<string[]>(),
			};

		private static CliSpecArgument Option(string name, string type, bool required = false, string values = null,
			string omission = null, long? minimum = null, long? maximum = null, string description = null, string source = null)
			=> new CliSpecArgument
			{
				Name = name,
				Type = type,
				Required = required,
				Values = values == null ? null : Words(values),
				Omission = required ? "error" : omission ?? "not-selected",
				Minimum = minimum,
				Maximum = maximum,
				Description = description,
				ValuesFrom = source,
			};

		private static CliSpecArgument Flag(string name, bool required = false)
			=> Option(name, "flag", required, omission: "false");

		private static CliSpecArgument[] Booleans(string names)
			=> Words(names).Select(name => Option(name, "boolean", values: "true false", omission: "preserve-current")).ToArray();

		private static CliSpecArgument WindowId()
			=> Option("--id", "uuid", required: true,
				description: "Opaque window-instance ID; not PID or HWND.",
				source: "window list: windows[].id");

		// Desktop selectors share their type and value source with the desktop list field named by the option suffix.
		private static CliSpecArgument DesktopOption(string name, bool required = false)
		{
			var field = name.Substring(name.LastIndexOf('-') + 1);
			var source = "desktop list: desktops[]." + field;
			if (field == "number")
			{
				return Option(name, "integer", required, minimum: 1, maximum: int.MaxValue, source: source);
			}
			return Option(name, field == "id" ? "uuid" : "string", required, source: source);
		}

		private static CliSpecConstraint ExactlyOne(string options) => Constraint("exactlyOne", options);

		private static CliSpecConstraint AtLeastOne(string options) => Constraint("atLeastOne", options);

		private static CliSpecConstraint AtMostOne(string options) => Constraint("atMostOne", options);

		private static CliSpecConstraint RequiresAny(string whenPresent, string options)
		{
			var constraint = Constraint("requiresAny", options);
			constraint.WhenPresent = whenPresent;
			return constraint;
		}

		private static CliSpecConstraint Constraint(string kind, string options)
			=> new CliSpecConstraint { Kind = kind, Arguments = Words(options) };

		private static string[][] Queries(params string[] commands) => commands.Select(Words).ToArray();

		private static string[] Words(string text) => text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

		#endregion
	}
}
