using System;
using System.Collections.Generic;
using System.Linq;

namespace SylphyHorn.Commands
{
	internal static class CliSpecCatalog
	{

		private static readonly Dictionary<string, CliCommandSpec> Definitions = Build()
			.ToDictionary(item => item.Name, StringComparer.Ordinal);

		internal static IEnumerable<CliCommandSpec> All => Definitions.Values.OrderBy(item => item.Name, StringComparer.Ordinal);

		internal static CliCommandSpec Find(string name)
			=> name != null && Definitions.TryGetValue(name, out var value) ? value : null;

		internal static string[] Choices(string command, string option)
			=> Find(command).Arguments.Single(argument => argument.Name == option).Values;

		private static IEnumerable<CliCommandSpec> Build()
		{
			var version = C("version", "Read CLI and running GUI versions and embedded Git revisions.", "cli host",
				notes: "Always returns local CLI information. Inspect host.status (available or unavailable) and host.errorCode: outer success does not imply " +
					"a reachable host. Missing revisions are null. A revision is not proof of a clean working tree or compatibility. " +
					"The host query has a two-second deadline and never starts the GUI. --version prints the local CLI version and available nine-character Git revision as text without connecting.");
			version.Prerequisites = new[]
			{
				"No GUI required for local information; host information requires a compatible running GUI in the same user session and elevation level.",
			};
			yield return version;
			yield return C("exit", "Request normal shutdown of the connected SylphyHorn GUI host.", "accepted",
				notes: "Acceptance does not confirm process exit or settings persistence. The host attempts to send the response before normal shutdown. " +
					"Once accepted, shutdown proceeds even if the client disconnects. Use settings save first if its result must be checked.",
				effects: "exit-host save-settings stop-monitoring");
			yield return C("logs", "Read recent application logs from the current GUI host.", "logs totalCount omittedCount",
				new[] { A("--limit", "integer", omission: "50", minimum: 1, maximum: int.MaxValue) },
				notes: "Returns the latest entries in oldest-first order, with ISO 8601 timestamps, headers and full contents. " +
					"totalCount includes entries omitted by the limit; omittedCount reports that difference. Logs are retained only for this host lifetime. " +
					"If the response exceeds the transport size limit, request fewer entries.", example: "--limit 50");
			yield return C("desktop list", "Read desktops in current order and their IDs, names and wallpaper state.", "desktops",
				queries: Q("desktop list"));
			yield return C("desktop switch", "Switch the displayed desktop.", "desktop changed",
				new[] { Desktop("--number"), Desktop("--name"), Desktop("--id"), Flag("--next"), Flag("--previous"), Flag("--last-used"), Flag("--wrap") },
				new[] { One("--number --name --id --next --previous --last-used"), Requires("--wrap", "--next --previous") },
				"Desktop names match exactly; duplicate names select the lowest number. Relative destinations use the current desktop. Without --wrap an " +
					"end-of-list move fails.",
				"switch-desktop", "--number 2", Q("desktop list"));
			yield return C("desktop create", "Create one desktop, optionally naming it and switching to it.", "desktop changed",
				new[] { A("--name", "string", omission: "unnamed", description: "A nonempty name requires OS desktop-name support."), Flag("--switch") },
				notes: "After an unconfirmed creation, list desktops before retrying to avoid duplicates.", effects: "create-desktop optional-switch", queries: Q("desktop list"));
			yield return C("desktop rename", "Change or clear a desktop name.", "desktop changed",
				new[] { Desktop("--id", true), A("--name", "string", true, description: "Empty string clears the name; requires OS name support.") },
				effects: "rename-desktop", example: "--id <DESKTOP_ID> --name Development", queries: Q("desktop list"));
			yield return C("desktop reorder", "Move a desktop to an existing position.", "desktop changed",
				new[] { Desktop("--id", true), Desktop("--number", true) },
				notes: "Requires OS desktop-reordering support. Number is the destination position, not a second selector.",
				effects: "reorder-desktops", example: "--id <DESKTOP_ID> --number 2", queries: Q("desktop list"));
			foreach (var name in new[] { "desktop delete", "desktop wallpaper" })
			{
				var wallpaper = name == "desktop wallpaper";
				var arguments = new List<CliSpecArgument> { Desktop("--id"), Desktop("--number") };
				var constraints = new List<CliSpecConstraint> { One("--id --number") };
				if (wallpaper)
				{
					arguments.Add(A("--path", "string", description: "Path to an existing wallpaper image."));
					arguments.Add(A("--position", "string", values: "center tile stretch fit fill span"));
					constraints.Add(One("--path --position"));
				}
				yield return C(name, wallpaper ? "Set wallpaper image or placement for one desktop." : "Close one desktop using the existing GUI removal behavior.",
					wallpaper ? "desktop changed" : "desktops changed", arguments.ToArray(), constraints.ToArray(),
					wallpaper ? "Wallpaper requires OS support or enabled per-desktop wallpaper. Position names are lowercase and case-sensitive."
						: "Windows on the removed desktop move to the fallback selected by the existing removal operation. This is not application termination.",
					wallpaper ? "change-wallpaper" : "remove-desktop relocate-windows possible-switch",
					wallpaper ? "--number 2 --position fill" : "--number 2", Q("desktop list", "desktop settings"));
			}
			yield return C("window list", "List addressable windows and their current desktops and pin state.", "windows complete unavailableCount",
				notes: "Window IDs identify observed window instances, not HWNDs or process IDs. Re-list when an ID becomes stale. Check complete and unavailableCount.", queries: Q("window list"));
			yield return C("window move", "Move one window, optionally following it to the destination.", "desktop window changed",
				new[] { Window(), Desktop("--desktop-number"), Desktop("--desktop-name"), Desktop("--desktop-id"),
					Flag("--desktop-next"), Flag("--desktop-previous"), Flag("--desktop-last-used"), Flag("--desktop-new"), Flag("--wrap"), Flag("--follow") },
				new[] { One("--desktop-number --desktop-name --desktop-id --desktop-next --desktop-previous --desktop-last-used --desktop-new"),
					Requires("--wrap", "--desktop-next --desktop-previous") },
				"Relative next/previous destinations are relative to the window's source desktop. Pinned windows cannot be moved. --follow switches only after " +
					"placement is confirmed.",
				"move-window optional-create optional-switch", "--id <WINDOW_ID> --desktop-number 2", Q("window list", "desktop list"));
			foreach (var verb in new[] { "pin", "unpin" })
				yield return C("window " + verb, verb == "pin" ? "Pin a window or its application across desktops." : "Unpin a window or its application.", "window changed",
					new[] { Window(), A("--scope", "string", true, values: "window app") },
					notes: "Scope is mandatory; app affects every window of the identified application. No implicit scope is selected.",
					effects: "change-pin-state", example: "--id <WINDOW_ID> --scope window", queries: Q("window list"));
			foreach (var page in new[] { "task-view", "window-switch", "settings", "notification-toggle" })
				yield return C("ui " + page, page == "notification-toggle" ? "Toggle the desktop notification display." : "Show the " + page + " interface.",
					"", effects: "change-visible-ui", notes: "Requires an interactive GUI host. Does not automate input inside the displayed interface.");
			foreach (var verb in new[] { "list", "set", "remove" })
			{
				var arguments = new List<CliSpecArgument>();
				if (verb != "list")
				{
					arguments.Add(Desktop("--name"));
					arguments.Add(Desktop("--number"));
				}
				if (verb == "set") arguments.Add(A("--path", "string", true, description: "Absolute readable image path."));
				yield return C("desktop creation wallpaper " + verb, verb + " wallpaper settings applied to newly created desktops.",
					"wallpapersOnCreation changed", arguments.ToArray(), verb == "list" ? null : new[] { One("--name --number") },
					notes: "Names take priority over numbers. Destinations may be absent. Does not change existing wallpapers. " +
						"set replaces the same target; remove of an absent target succeeds unchanged. Import restoration and intermediate filler desktops are excluded.",
					effects: verb == "list" ? "read-settings" : "persist-settings", example: verb == "list" ? null : "--number 3" + (verb == "set" ? " --path C:\\Wallpapers\\work.jpg" : ""),
					queries: Q("desktop creation wallpaper list", "desktop list"));
			}
			yield return C("app list", "List registered applications or applications with open windows.", "apps source",
				new[] { A("--source", "string", values: "registered windows", omission: "registered") },
				notes: "Use canAssign and reason. A launcher or unknown identity is not a safe assignment target.", queries: Q("app list"));
			foreach (var verb in new[] { "list", "status" })
				yield return C("app assignment " + verb, verb == "list" ? "Read saved application assignment rules." : "Read assignment configuration and monitoring status.",
					verb == "list" ? "assignments assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops closingTargets" : "assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops closingTargets",
					queries: Q("app assignment " + verb));
			yield return C("app assignment resume", "Resume paused automatic placement monitoring without changing settings.", "assignmentStatus changed",
				notes: "Paused monitoring starts a fresh session. Active or preparing monitoring is left unchanged. Disabled placement or no rules returns " +
					"assignment_unavailable; temporary suspension returns host_busy. A preparing result is not confirmation of active monitoring. " +
					"Does not bulk-apply rules to existing windows.", effects: "resume-monitoring", queries: Q("app assignment status"));
			yield return C("app assignment configure", "Change automatic assignment, desktop creation and closure settings.",
				"assignments assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops closingTargets changed",
				Bools("--enabled --create-missing-desktops --close-created-desktops --follow-foreground"), new[] { Some("--enabled --create-missing-desktops --close-created-desktops --follow-foreground") },
				"Omitted settings are preserved. Closing created desktops also covers them without an individual closing target; closure still uses the " +
					"application's occupancy conditions. Follow is a default for rules without an override, applies only to foreground windows, and never follows explicit apply.",
				"persist-settings change-monitoring", "--enabled true", Q("app assignment status"));
			foreach (var verb in new[] { "set", "remove", "apply", "enable", "disable" })
			{
				var arguments = new List<CliSpecArgument> { Rule(verb == "enable" || verb == "disable") };
				var constraints = new List<CliSpecConstraint>();
				var queries = new List<string> { "app assignment list", "app assignment status" };
				if (verb == "set" || verb == "remove" || verb == "apply")
				{
					arguments.Add(A("--path", "string", description: "Full executable path; not a process name.", source: "app list: apps[].executablePath; app assignment list: saved application identity"));
					var selectors = "--id --path";
					if (verb == "set")
					{
						arguments.Add(A("--app-id", "string", description: "Package application identity returned for an assignable app.", source: "app list: apps[].appIdentity when appKind=packageAppId"));
						selectors += " --app-id";
						arguments.Add(Desktop("--desktop-name"));
						arguments.Add(Desktop("--desktop-number"));
						arguments.Add(A("--follow-foreground", "string", values: "default true false", omission: "preserve-existing-or-default",
							description: "default inherits the global setting. true/false override it, still only for foreground windows during automatic placement."));
						constraints.Add(new CliSpecConstraint { Kind = "atMostOne", Arguments = Split("--desktop-name --desktop-number") });
						queries.Add("app list");
						queries.Add("desktop list");
					}
					if (verb == "apply")
					{
						arguments.Add(Flag("--all"));
						arguments.Add(Flag("--dry-run"));
						selectors += " --all";
						queries.Add("window list");
						queries.Add("desktop list");
					}
					constraints.Add(One(selectors));
				}
				var sample = verb == "set" ? "--id <RULE_ID> --desktop-number 2" : verb == "apply" ? "--all --dry-run" : "--id <RULE_ID>";
				yield return C("app assignment " + verb,
					verb == "apply" ? "Apply saved rules to existing windows, or inspect the planned moves." : verb + " a saved application assignment rule.",
					verb == "apply" ? "results dryRun changed" : "assignments assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops closingTargets changed",
					arguments.ToArray(), constraints.ToArray(),
					verb == "apply" ? "Requires enabled, active monitoring. --dry-run does not move windows or create desktops. Rechecks identity and destination during actual apply. " +
						"Inspect each result, not only the envelope success."
						: "Rules store a destination name or number, not a desktop ID. Destinations may be absent. set with --id updates that rule; executable paths and " +
							"package identities are distinct. set requires a destination unless both --id and --follow-foreground are supplied; in that case omission preserves the destination.",
					verb == "apply" ? "move-windows-unless-dry-run possible-create" : "persist-rules",
					sample, Q(queries.ToArray()));
			}
			foreach (var verb in new[] { "list", "add", "remove" })
				yield return C("desktop autoclose " + verb, verb == "list" ? "Read automatic desktop closure targets." : verb + " an automatic desktop closure target.",
					"closingTargets assignmentEnabled assignmentStatus createMissingDesktops followForeground closeCreatedDesktops changed",
					verb == "list" ? null : new[] { Desktop("--name"), Desktop("--number") },
					verb == "list" ? null : new[] { One("--name --number") },
					"Configures future automatic closure; does not immediately delete a desktop. Targets can name desktops not currently present.",
					verb == "list" ? "read-only" : "persist-closing-targets", verb == "list" ? "" : "--number 2", Q("desktop autoclose list", "desktop list"));
			yield return C("monitor list", "Read current monitor identifiers and geometry.", "monitors", queries: Q("monitor list"));
			foreach (var area in new[] { "desktop", "notification", "tray", "settings" })
			{
				var read = area == "settings" ? "settings get" : area + " settings";
				var fields = area == "desktop" ? "loop overrideWindowsShortcuts perDesktopWallpaper overrideOnStartup nativeWallpaperSupported wallpaperEnabled"
					: area == "tray" ? "showDesktop currentNumberOnly"
					: area == "settings" ? "language restartRequired"
					: "onSwitch alwaysShow durationMs simple useDesktopName theme corners fontFamily headerFontSize bodyFontSize headerAlign bodyAlign lineSpacing " +
						"cornersSupported monitor monitorAvailable placement offsetX offsetY minWidth simpleMinWidth minHeight pinMinWidth pinOffsetX pinOffsetY";
				yield return C(read, "Read " + area + " settings.", fields, queries: Q(read));
				var arguments = area == "desktop" ? Bools("--loop --override-windows-shortcuts --per-desktop-wallpaper --override-on-startup")
					: area == "tray" ? Bools("--show-desktop --current-number-only")
					: area == "settings" ? new[] { A("--language", "string", values: "auto en ja", omission: "preserve-current") }
					: NotificationArguments();
				yield return C(area + " configure", "Change only the supplied " + area + " settings.", fields + " changed",
					arguments, new[] { Some(string.Join(" ", arguments.Select(argument => argument.Name))) },
					area == "settings" ? "Language changes may require restart; inspect restartRequired."
						: area == "desktop" ? "override-on-startup affects the next launch and can change desktop count. per-desktop-wallpaper is editable only without native OS wallpaper " +
							"support. override-on-startup requires OS name support."
						: area == "notification" ? "Font sizes also require WPF rendering validation. Dimensions and offsets are logical pixels. Unsupported appearance options fail without " +
							"applying the other supplied settings. Empty font-family restores the default."
						: "Omitted settings remain unchanged.",
					"persist-settings", area == "settings" ? "--language en" : area == "desktop" ? "--loop true" : area == "tray" ? "--show-desktop true" : "--duration-ms 1000",
					area == "notification" ? Q(read, "monitor list") : Q(read));
			}
			yield return C("settings export", "Export settings as a GUI-compatible XML backup.", "path",
				new[] { A("--path", "string", true, description: "Relative to the CLI working directory. Cannot be the active settings file."), Flag("--overwrite") },
				notes: "Existing destination files require --overwrite. No automatic backup of an overwritten export.", effects: "write-file", example: "--path backup.xml");
			yield return C("settings import", "Replace settings from a GUI-compatible XML backup.", "path applyDesktops",
				new[] { A("--path", "string", true, description: "Existing XML path, relative to the CLI working directory."), A("--apply-desktops", "boolean", true, values: "true false") },
				notes: "Not a merge. true applies saved desktop count, names and wallpaper paths; false still applies other settings and wallpaper positions. Startup " +
					"registrations are unaffected. Partial failure is not a rollback guarantee.",
				effects: "replace-settings possible-create-remove-desktops", example: "--path backup.xml --apply-desktops false");
			yield return C("settings save", "Save the current in-memory application settings to the normal settings file.", "saved",
				notes: "Saves current committed settings, not the values from a previous failed save. Does not commit unfinished GUI edits, " +
					"change preferences or apply wallpapers. No prior save failure is required. Use settings export for a separate file.", effects: "persist-settings");
			yield return C("settings reset", "Reset application settings using the GUI reset transaction.", "reset",
				new[] { Flag("--yes", true) }, notes: "Clears rules and restores defaults. Preserves desktop count, order, names, wallpaper paths and startup registrations; wallpaper positions become " +
					"Fill. Export first if a backup is needed. Does not restart the app.",
				effects: "reset-settings", example: "--yes");
			yield return C("startup status", "Read startup registrations and their targets without changing them.", "startup", queries: Q("startup status"));
			yield return C("startup configure", "Select disabled, normal shortcut or elevated task startup.", "startup changed",
				new[] { A("--mode", "string", true, values: "disabled normal elevated") },
				notes: "Registers the GUI executable. Task changes require administrator GUI and CLI; no UAC is opened. Mismatched existing targets are not overwritten. " +
					"New registration is confirmed before removing the old one; failure can leave mixed state.",
				effects: "change-startup-registration", example: "--mode normal", queries: Q("startup status"));
			foreach (var verb in new[] { "list", "keys", "set", "clear" })
			{
				var arguments = new List<CliSpecArgument> { A("--device", "string", verb != "list", values: "keyboard mouse", omission: verb == "list" ? "both" : null) };
				if (verb == "set" || verb == "clear")
				{
					arguments.Add(A("--action", "string", true, source: "shortcut list: shortcuts[].action, grouped by device"));
					arguments.Add(A("--number", "integer", minimum: 1, maximum: 1000, description: "Required only when the selected action has numberRequired=true; forbidden for fixed actions."));
					if (verb == "set") arguments.Add(A("--trigger", "string", true, description: "Plus-separated key names; last token is the trigger, preceding tokens are held keys. Left/right modifiers are distinct. Mouse LButton/RButton or " +
						"wheel alone are rejected.", source: "shortcut keys --device keyboard|mouse: keys[].name, canHold, canTrigger"));
				}
				yield return C("shortcut " + verb, verb == "keys" ? "List permitted key names and their trigger/hold roles." : verb + " keyboard or mouse shortcut bindings.",
					verb == "keys" ? "keys" : "shortcuts changed", arguments.ToArray(),
					constraints: verb == "set" || verb == "clear"
						? new[] { new CliSpecConstraint { Kind = "requiredWhenActionNumbered", Arguments = new[] { "--number" }, WhenPresent = "--action" } } : null,
					notes: "New conflicts are rejected without clearing other bindings. Numbered bindings can target future desktops. Clearing a binding does not shift " +
						"subsequent numbers. Listing does not execute any shortcut.",
					effects: verb == "set" || verb == "clear" ? "persist-shortcuts reload-input" : "read-only",
					example: verb == "set" ? "--device keyboard --action desktop-switch-left --trigger LControlKey+LWin+Left"
						: verb == "clear" ? "--device keyboard --action desktop-switch-left" : verb == "keys" ? "--device keyboard" : "",
					queries: verb == "list" ? Q("shortcut list") : Q("shortcut list", "shortcut keys --device keyboard", "shortcut keys --device mouse"));
			}
		}

		private static CliSpecArgument[] NotificationArguments()
		{
			var result = new List<CliSpecArgument>(Bools("--on-switch --always-show --simple --use-desktop-name"));
			result.Add(A("--duration-ms", "integer", minimum: 1, maximum: int.MaxValue, omission: "preserve-current"));
			result.Add(A("--theme", "string", values: "apps system light dark accent", omission: "preserve-current"));
			result.Add(A("--corners", "string", values: "square rounded small-rounded", omission: "preserve-current"));
			result.Add(A("--font-family", "string", omission: "preserve-current", description: "Installed font-family name, or empty string for default."));
			foreach (var name in new[] { "--header-align", "--body-align" })
				result.Add(A(name, "string", values: "left center right", omission: "preserve-current"));
			foreach (var name in Split("--header-font-size --body-font-size --line-spacing --offset-x --offset-y --min-width --simple-min-width --min-height --pin-min-width " +
				"--pin-offset-x --pin-offset-y"))
				result.Add(A(name, "integer", minimum: name.Contains("min-") || name.Contains("font-size") ? 1 : int.MinValue,
					maximum: int.MaxValue, omission: "preserve-current"));
			result.Add(A("--monitor", "string", omission: "preserve-current", description: "current, all, or an available monitor number (1..4294967294).", source: "monitor list: monitors[].number"));
			result.Add(A("--placement", "string", values: "top-left top-center top-right center-left center center-right bottom-left bottom-center bottom-right", omission: "preserve-current"));
			return result.ToArray();
		}

		private static CliCommandSpec C(string name, string summary, string fields, CliSpecArgument[] arguments = null,
			CliSpecConstraint[] constraints = null, string notes = null, string effects = "read-only", string example = "", string[][] queries = null)
			=> new CliCommandSpec
			{
				Name = name, Summary = summary, Arguments = arguments ?? Array.Empty<CliSpecArgument>(),
				Constraints = constraints ?? Array.Empty<CliSpecConstraint>(), Notes = notes == null ? Array.Empty<string>() : new[] { notes },
				Prerequisites = new[] { "Start a compatible SylphyHorn GUI host in the same Windows user session and elevation level. Runtime availability and OS capabilities are " +
					"checked at execution." },
				Effects = Split(effects), ResultFields = Split(fields), Examples = new[] { Split(name + " " + example) },
				Queries = queries ?? Array.Empty<string[]>(),
			};

		private static CliSpecArgument A(string name, string type, bool required = false, string values = null,
			string omission = null, long? minimum = null, long? maximum = null, string description = null, string source = null)
			=> new CliSpecArgument
			{
				Name = name, Type = type, Required = required, Values = values == null ? null : Split(values),
				Omission = required ? "error" : omission ?? "not-selected", Minimum = minimum, Maximum = maximum,
				Description = description, ValuesFrom = source,
			};

		private static CliSpecArgument Flag(string name, bool required = false) => A(name, "flag", required, omission: "false");

		private static CliSpecArgument[] Bools(string names) => Split(names).Select(name => A(name, "boolean", values: "true false", omission: "preserve-current")).ToArray();

		private static CliSpecArgument Window() => A("--id", "uuid", true, source: "window list: windows[].id", description: "Opaque window-instance ID; not PID or HWND.");

		private static CliSpecArgument Rule(bool required) => A("--id", "uuid", required, source: "app assignment list: assignments[].id", description: "Persisted rule ID, shared with GUI settings.");

		private static CliSpecArgument Desktop(string name, bool required = false)
			=> A(name, name.EndsWith("number", StringComparison.Ordinal) ? "integer" : name.EndsWith("id", StringComparison.Ordinal) ? "uuid" : "string",
				required, minimum: name.EndsWith("number", StringComparison.Ordinal) ? (long?)1 : null,
				maximum: name.EndsWith("number", StringComparison.Ordinal) ? (long?)int.MaxValue : null,
				source: "desktop list: desktops[]." + name.Substring(name.LastIndexOf('-') + 1));

		private static CliSpecConstraint One(string args) => new CliSpecConstraint { Kind = "exactlyOne", Arguments = Split(args) };

		private static CliSpecConstraint Some(string args) => new CliSpecConstraint { Kind = "atLeastOne", Arguments = Split(args) };

		private static CliSpecConstraint Requires(string when, string args) => new CliSpecConstraint { Kind = "requiresAny", WhenPresent = when, Arguments = Split(args) };

		private static string[][] Q(params string[] commands) => commands.Select(Split).ToArray();

		private static string[] Split(string text) => text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
	}
}
