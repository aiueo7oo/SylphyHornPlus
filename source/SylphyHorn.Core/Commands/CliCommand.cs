using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SylphyHorn.Commands
{
	internal sealed class CliCommand
	{
		private const string DesktopNumberError = "Desktop numbers must be positive integers starting at 1.";

		private const string DesktopSelectorError = "Specify a desktop selector and required value.";

		private const string NoSettingError =
			"Specify at least one setting. Read current values with desktop/notification/tray settings or settings get.";

		private const string GeometryError = "Minimum sizes must be positive integers; offsets must be signed integers.";

		private const string FontSizeError = "Font sizes must be positive integers; line spacing must be a signed integer.";

		internal int Limit { get; private set; } = 50;

		internal bool ConfirmReset { get; private set; }

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

		internal Guid? FallbackId { get; private set; }

		internal int? FallbackNumber { get; private set; }

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

		internal string FollowForeground { get; private set; }

		internal bool All { get; private set; }

		internal bool DryRun { get; private set; }

		internal bool Wrap { get; private set; }

		internal bool Follow { get; private set; }

		internal bool SwitchAfterCreate { get; private set; }

		// Mirrors the command areas of CliSpecCatalog.
		private enum CommandArea
		{
			Host,
			Desktop,
			Window,
			Ui,
			CreationWallpaper,
			AppAssignment,
			AutoClose,
			Settings,
			Startup,
			Shortcut,
		}

		internal static string Recognize(string[] args)
		{
			if (args != null && args.Length > 0 && (args[0] == "logs" || args[0] == "version" || args[0] == "exit"))
			{
				return args[0];
			}
			if (args == null || args.Length < 2) return null;
			var operation = args[0] + " " + args[1];
			if ((operation == "app assignment" || operation == "desktop autoclose") && args.Length >= 3)
			{
				operation += " " + args[2];
			}
			if (operation == "desktop creation" && args.Length >= 4 && args[2] == "wallpaper")
			{
				operation += " wallpaper " + args[3];
			}
			return CliSpecCatalog.Find(operation) != null ? operation : null;
		}

		internal static CliCommand Parse(string[] args)
		{
			if (args == null)
			{
				throw new ArgumentNullException(nameof(args));
			}
			if (args.Length == 0)
			{
				throw new ArgumentException("Specify a command.");
			}
			var operation = Recognize(args);
			if (operation == null)
			{
				throw new ArgumentException("Unknown command.");
			}

			var command = new CliCommand { Operation = operation };
			var area = AreaOf(operation);
			var options = new OptionReader(args, operation.Split(' ').Length);
			while (options.MoveNext())
			{
				if (!command.ReadOption(area, options))
				{
					throw new ArgumentException("Unknown option: " + options.Option);
				}
			}
			command.Validate(area);
			return command;
		}

		private static CommandArea AreaOf(string operation)
		{
			var words = operation.Split(' ');
			switch (words[0])
			{
				case "desktop": return DesktopAreaOf(words[1]);
				case "window": return CommandArea.Window;
				case "ui": return CommandArea.Ui;
				case "app": return CommandArea.AppAssignment;
				case "notification":
				case "tray":
				case "settings":
					return CommandArea.Settings;
				case "startup": return CommandArea.Startup;
				case "shortcut": return CommandArea.Shortcut;
				default: return CommandArea.Host;
			}
		}

		// Desktop settings commands belong to the settings area, as in CliSpecCatalog.
		private static CommandArea DesktopAreaOf(string subcommand)
		{
			switch (subcommand)
			{
				case "creation": return CommandArea.CreationWallpaper;
				case "autoclose": return CommandArea.AutoClose;
				case "settings":
				case "configure":
					return CommandArea.Settings;
				default: return CommandArea.Desktop;
			}
		}

		private bool ReadOption(CommandArea area, OptionReader options)
		{
			switch (area)
			{
				case CommandArea.Host: return this.ReadHostOption(options);
				case CommandArea.Desktop: return this.ReadDesktopOption(options);
				case CommandArea.Window: return this.ReadWindowOption(options);
				case CommandArea.CreationWallpaper: return this.ReadCreationWallpaperOption(options);
				case CommandArea.AppAssignment: return this.ReadAppAssignmentOption(options);
				case CommandArea.AutoClose: return this.ReadAutoCloseOption(options);
				case CommandArea.Settings: return this.ReadSettingsOption(options);
				case CommandArea.Startup: return this.ReadStartupOption(options);
				case CommandArea.Shortcut: return this.ReadShortcutOption(options);
				// UI commands take no options.
				default: return false;
			}
		}

		private void Validate(CommandArea area)
		{
			switch (area)
			{
				case CommandArea.Desktop:
					this.ValidateDesktop();
					break;
				case CommandArea.Window:
					this.ValidateWindow();
					break;
				case CommandArea.CreationWallpaper:
					this.ValidateCreationWallpaper();
					break;
				case CommandArea.AppAssignment:
					this.ValidateAppAssignment();
					break;
				case CommandArea.AutoClose:
					this.ValidateAutoClose();
					break;
				case CommandArea.Settings:
					this.ValidateSettings();
					break;
				case CommandArea.Startup:
					this.ValidateStartup();
					break;
				case CommandArea.Shortcut:
					this.ValidateShortcut();
					break;
			}
		}

		#region Host

		private bool ReadHostOption(OptionReader options)
		{
			if (this.Operation != "logs" || options.Option != "--limit") return false;
			this.Limit = options.ReadPositiveInteger("--limit requires a positive integer.");
			return true;
		}

		#endregion

		#region Desktops

		private bool ReadDesktopOption(OptionReader options)
		{
			switch (this.Operation)
			{
				case "desktop switch": return this.ReadSwitchOption(options);
				case "desktop create": return this.ReadCreateOption(options);
				case "desktop rename": return this.ReadRenameOption(options);
				case "desktop reorder": return this.ReadReorderOption(options);
				case "desktop delete": return this.ReadDeleteOption(options);
				case "desktop wallpaper": return this.ReadWallpaperOption(options);
				default: return false;
			}
		}

		private bool ReadSwitchOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--number":
					this.ReadDesktopSelector(options, "number");
					return true;
				case "--name":
					this.ReadDesktopSelector(options, "name");
					return true;
				case "--id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--next":
				case "--previous":
				case "--last-used":
					this.SetTarget(options.Option.Substring("--".Length), null);
					return true;
				case "--wrap":
					this.Wrap = true;
					return true;
				default:
					return false;
			}
		}

		private bool ReadCreateOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--name":
					this.Name = options.ReadValue();
					return true;
				case "--switch":
					this.SwitchAfterCreate = true;
					return true;
				default:
					return false;
			}
		}

		private bool ReadRenameOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--name":
					this.Name = options.ReadText("A desktop name is missing.");
					return true;
				default:
					return false;
			}
		}

		private bool ReadReorderOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--number":
					// The destination position, not a second selector.
					this.Number = options.ReadPositiveInteger(DesktopNumberError);
					return true;
				default:
					return false;
			}
		}

		private bool ReadDeleteOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--number":
					this.ReadDesktopSelector(options, "number");
					return true;
				case "--fallback-id":
					this.FallbackId = Guid.Parse(options.ReadId());
					return true;
				case "--fallback-number":
					this.FallbackNumber = options.ReadPositiveInteger(DesktopNumberError);
					return true;
				default:
					return false;
			}
		}

		private bool ReadWallpaperOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--number":
					this.ReadDesktopSelector(options, "number");
					return true;
				case "--path":
					this.WallpaperPath = options.ReadText("A wallpaper path is missing.");
					return true;
				case "--position":
					this.WallpaperPosition = this.ReadCatalogChoice(options, "--position must be center, tile, stretch, fit, fill, or span.");
					return true;
				default:
					return false;
			}
		}

		private void ValidateDesktop()
		{
			switch (this.Operation)
			{
				case "desktop switch":
					Require(this.TargetKind != null, "Specify exactly one destination.");
					this.ValidateWrap();
					break;
				case "desktop rename":
					Require(this.TargetKind != null && this.Name != null, DesktopSelectorError);
					break;
				case "desktop reorder":
					Require(this.TargetKind != null && this.Number != null, DesktopSelectorError);
					break;
				case "desktop delete":
					Require(this.TargetKind != null, DesktopSelectorError);
					Require(this.FallbackId == null || this.FallbackNumber == null, "--fallback-id and --fallback-number are mutually exclusive.");
					break;
				case "desktop wallpaper":
					Require(this.TargetKind != null, DesktopSelectorError);
					Require((this.WallpaperPath == null) != (this.WallpaperPosition == null), "Specify exactly one of --path or --position.");
					break;
			}
		}

		private bool ReadCreationWallpaperOption(OptionReader options)
		{
			if (this.Operation == "desktop creation wallpaper list") return false;
			switch (options.Option)
			{
				case "--name":
					this.SetTarget("name", options.ReadValue());
					return true;
				case "--number":
					this.SetTarget("number", options.ReadPositiveIntegerText("Desktop numbers must be positive integers."));
					return true;
				case "--path" when this.Operation == "desktop creation wallpaper set":
					this.WallpaperPath = options.ReadValue();
					return true;
				default:
					return false;
			}
		}

		private void ValidateCreationWallpaper()
		{
			if (this.Operation == "desktop creation wallpaper list") return;
			Require(this.TargetKind != null, "Specify --name or --number.");
			if (this.Operation == "desktop creation wallpaper set")
			{
				Require(this.WallpaperPath != null, "Specify --path.");
			}
		}

		private bool ReadAutoCloseOption(OptionReader options)
		{
			if (this.Operation == "desktop autoclose list") return false;
			switch (options.Option)
			{
				case "--name":
					this.ReadDesktopSelector(options, "name");
					return true;
				case "--number":
					this.ReadDesktopSelector(options, "number");
					return true;
				default:
					return false;
			}
		}

		private void ValidateAutoClose()
		{
			if (this.Operation == "desktop autoclose list") return;
			Require(this.TargetKind != null, "Specify exactly one of --name or --number.");
		}

		// Selectors keep the value as typed; the host resolves it against current desktops.
		private void ReadDesktopSelector(OptionReader options, string kind)
		{
			switch (kind)
			{
				case "number":
					this.SetTarget(kind, options.ReadPositiveIntegerText(DesktopNumberError));
					break;
				case "id":
					this.SetTarget(kind, options.ReadId());
					break;
				default:
					this.SetTarget(kind, options.ReadValue());
					break;
			}
		}

		private void SetTarget(string kind, string value)
		{
			if (this.TargetKind != null)
			{
				throw new ArgumentException("Destination options are mutually exclusive.");
			}
			this.TargetKind = kind;
			this.TargetValue = value;
		}

		private void ValidateWrap()
		{
			var relative = this.TargetKind == "next" || this.TargetKind == "previous";
			Require(!this.Wrap || relative, "--wrap requires a next or previous destination.");
		}

		#endregion

		#region Windows

		private bool ReadWindowOption(OptionReader options)
		{
			switch (this.Operation)
			{
				case "window move": return this.ReadMoveOption(options);
				case "window pin":
				case "window unpin":
					return this.ReadPinOption(options);
				default: return false;
			}
		}

		private bool ReadMoveOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.WindowId = options.ReadId();
					return true;
				case "--desktop-number":
					this.ReadDesktopSelector(options, "number");
					return true;
				case "--desktop-name":
					this.ReadDesktopSelector(options, "name");
					return true;
				case "--desktop-id":
					this.ReadDesktopSelector(options, "id");
					return true;
				case "--desktop-next":
				case "--desktop-previous":
				case "--desktop-last-used":
				case "--desktop-new":
					this.SetTarget(options.Option.Substring("--desktop-".Length), null);
					return true;
				case "--wrap":
					this.Wrap = true;
					return true;
				case "--follow":
					this.Follow = true;
					return true;
				default:
					return false;
			}
		}

		private bool ReadPinOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--id":
					this.WindowId = options.ReadId();
					return true;
				case "--scope":
					this.Scope = this.ReadCatalogChoice(options, "--scope must be window or app.");
					return true;
				default:
					return false;
			}
		}

		private void ValidateWindow()
		{
			switch (this.Operation)
			{
				case "window move":
					Require(this.TargetKind != null, "Specify exactly one destination.");
					Require(this.WindowId != null, "Specify --id using a window ID returned by window list.");
					this.ValidateWrap();
					break;
				case "window pin":
				case "window unpin":
					Require(this.WindowId != null && this.Scope != null, "Specify --id from window list and --scope window or app.");
					break;
			}
		}

		#endregion

		#region Application assignment

		// set, remove and apply each take exactly one of these selectors.
		private int RuleSelectorCount
			=> new[] { this.AppPath != null, this.AppId != null, this.RuleId != null, this.All }.Count(selected => selected);

		private bool ReadAppAssignmentOption(OptionReader options)
		{
			switch (this.Operation)
			{
				case "app list": return this.ReadAppListOption(options);
				case "app assignment configure": return this.ReadAssignmentConfigureOption(options);
				case "app assignment set": return this.ReadAssignmentSetOption(options);
				case "app assignment remove": return this.ReadRuleSelector(options);
				case "app assignment apply": return this.ReadRuleSelector(options) || this.ReadApplyOption(options);
				case "app assignment enable":
				case "app assignment disable":
					return this.ReadRuleId(options);
				default: return false;
			}
		}

		private bool ReadAppListOption(OptionReader options)
		{
			if (options.Option != "--source") return false;
			this.Source = this.ReadCatalogChoice(options, "--source must be registered or windows.");
			return true;
		}

		private bool ReadAssignmentConfigureOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--enabled":
					this.AssignmentEnabled = options.ReadBoolean("--enabled requires true or false.");
					return true;
				case "--create-missing-desktops":
					this.CreateMissingDesktops = options.ReadBoolean("--create-missing-desktops requires true or false.");
					return true;
				case "--close-created-desktops":
					this.CloseCreatedDesktops = options.ReadBoolean("--close-created-desktops requires true or false.");
					return true;
				case "--follow-foreground":
					this.FollowForeground = this.ReadCatalogChoice(options);
					return true;
				default:
					return false;
			}
		}

		private bool ReadAssignmentSetOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--app-id":
					this.AppId = options.ReadValue();
					return true;
				case "--desktop-name":
					this.ReadDesktopSelector(options, "name");
					return true;
				case "--desktop-number":
					this.ReadDesktopSelector(options, "number");
					return true;
				case "--follow-foreground":
					this.FollowForeground = this.ReadCatalogChoice(options);
					return true;
				default:
					return this.ReadRuleSelector(options);
			}
		}

		private bool ReadApplyOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--all":
					this.All = true;
					return true;
				case "--dry-run":
					this.DryRun = true;
					return true;
				default:
					return false;
			}
		}

		private bool ReadRuleSelector(OptionReader options)
		{
			if (this.ReadRuleId(options)) return true;
			if (options.Option != "--path") return false;
			this.AppPath = options.ReadValue();
			return true;
		}

		private bool ReadRuleId(OptionReader options)
		{
			if (options.Option != "--id") return false;
			this.RuleId = options.ReadId();
			return true;
		}

		private void ValidateAppAssignment()
		{
			switch (this.Operation)
			{
				case "app assignment configure":
					var anySetting = this.AssignmentEnabled != null || this.CreateMissingDesktops != null
						|| this.CloseCreatedDesktops != null || this.FollowForeground != null;
					Require(anySetting, "Specify at least one assignment setting.");
					break;
				case "app assignment set":
					Require(this.RuleSelectorCount == 1, "Specify exactly one of --path, --app-id or --id.");
					// Changing only --follow-foreground of a saved rule preserves its destination.
					var keepsDestination = this.RuleId != null && this.FollowForeground != null;
					Require(this.TargetKind != null || keepsDestination, "Specify exactly one of --desktop-name or --desktop-number.");
					break;
				case "app assignment remove":
					Require(this.RuleSelectorCount == 1, "Specify exactly one of --path or --id.");
					break;
				case "app assignment apply":
					Require(this.RuleSelectorCount == 1, "Specify exactly one of --path, --id or --all.");
					break;
				case "app assignment enable":
				case "app assignment disable":
					Require(this.RuleId != null, "Specify --id using a saved rule ID returned by app assignment list.");
					break;
			}
		}

		#endregion

		#region Settings and startup

		private bool ReadSettingsOption(OptionReader options)
		{
			switch (this.Operation)
			{
				case "desktop configure": return this.ReadDesktopSettingsOption(options);
				case "notification configure": return this.ReadNotificationSettingsOption(options);
				case "tray configure": return this.ReadTraySettingsOption(options);
				case "settings configure": return this.ReadGeneralSettingsOption(options);
				case "settings export": return this.ReadExportOption(options);
				case "settings import": return this.ReadImportOption(options);
				case "settings reset": return this.ReadResetOption(options);
				default: return false;
			}
		}

		private bool ReadDesktopSettingsOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--loop":
					this.Loop = options.ReadBoolean();
					return true;
				case "--override-windows-shortcuts":
					this.OverrideWindowsShortcuts = options.ReadBoolean();
					return true;
				case "--per-desktop-wallpaper":
					this.PerDesktopWallpaper = options.ReadBoolean();
					return true;
				case "--override-on-startup":
					this.OverrideOnStartup = options.ReadBoolean();
					return true;
				default:
					return false;
			}
		}

		private bool ReadNotificationSettingsOption(OptionReader options)
			=> this.ReadNotificationBehaviorOption(options)
				|| this.ReadNotificationAppearanceOption(options)
				|| this.ReadNotificationGeometryOption(options);

		private bool ReadNotificationBehaviorOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--on-switch":
					this.OnSwitch = options.ReadBoolean();
					return true;
				case "--always-show":
					this.AlwaysShow = options.ReadBoolean();
					return true;
				case "--duration-ms":
					this.DurationMs = options.ReadPositiveInteger("--duration-ms requires a positive integer in milliseconds.");
					return true;
				default:
					return false;
			}
		}

		private bool ReadNotificationAppearanceOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--simple":
					this.Simple = options.ReadBoolean();
					return true;
				case "--use-desktop-name":
					this.UseDesktopName = options.ReadBoolean();
					return true;
				case "--theme":
					this.Theme = this.ReadCatalogChoice(options);
					return true;
				case "--corners":
					this.Corners = this.ReadCatalogChoice(options);
					return true;
				case "--font-family":
					this.FontFamily = options.ReadText("A font family is missing; use an empty string to restore the default.");
					return true;
				case "--header-font-size":
					this.HeaderFontSize = ReadFontSize(options);
					return true;
				case "--body-font-size":
					this.BodyFontSize = ReadFontSize(options);
					return true;
				case "--header-align":
					this.HeaderAlign = this.ReadCatalogChoice(options);
					return true;
				case "--body-align":
					this.BodyAlign = this.ReadCatalogChoice(options);
					return true;
				case "--line-spacing":
					this.LineSpacing = options.ReadInteger(NumberStyles.AllowLeadingSign, int.MinValue, int.MaxValue, FontSizeError);
					return true;
				default:
					return false;
			}
		}

		private bool ReadNotificationGeometryOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--monitor":
					this.Monitor = ReadMonitor(options);
					return true;
				case "--placement":
					this.Placement = this.ReadCatalogChoice(options);
					return true;
				case "--offset-x":
					this.OffsetX = ReadPixelOffset(options);
					return true;
				case "--offset-y":
					this.OffsetY = ReadPixelOffset(options);
					return true;
				case "--min-width":
					this.MinWidth = ReadPixelSize(options);
					return true;
				case "--simple-min-width":
					this.SimpleMinWidth = ReadPixelSize(options);
					return true;
				case "--min-height":
					this.MinHeight = ReadPixelSize(options);
					return true;
				case "--pin-min-width":
					this.PinMinWidth = ReadPixelSize(options);
					return true;
				case "--pin-offset-x":
					this.PinOffsetX = ReadPixelOffset(options);
					return true;
				case "--pin-offset-y":
					this.PinOffsetY = ReadPixelOffset(options);
					return true;
				default:
					return false;
			}
		}

		private static int ReadFontSize(OptionReader options) => options.ReadInteger(NumberStyles.AllowLeadingSign, 1, int.MaxValue, FontSizeError);

		private static int ReadPixelSize(OptionReader options) => options.ReadInteger(NumberStyles.AllowLeadingSign, 1, int.MaxValue, GeometryError);

		private static int ReadPixelOffset(OptionReader options) => options.ReadInteger(NumberStyles.AllowLeadingSign, int.MinValue, int.MaxValue, GeometryError);

		private static string ReadMonitor(OptionReader options)
		{
			var value = options.ReadValue();
			if (value == "current" || value == "all") return value;
			if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0 || number == uint.MaxValue)
			{
				throw new ArgumentException("--monitor requires current, all or a positive monitor number.");
			}
			return value;
		}

		private bool ReadTraySettingsOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--show-desktop":
					this.ShowDesktop = options.ReadBoolean();
					return true;
				case "--current-number-only":
					this.CurrentNumberOnly = options.ReadBoolean();
					return true;
				default:
					return false;
			}
		}

		private bool ReadGeneralSettingsOption(OptionReader options)
		{
			if (options.Option != "--language") return false;
			this.Language = this.ReadCatalogChoice(options, "--language must be auto, en or ja.");
			return true;
		}

		private bool ReadExportOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--path":
					this.FilePath = options.ReadText("Specify a settings file path.");
					return true;
				case "--overwrite":
					this.Overwrite = true;
					return true;
				default:
					return false;
			}
		}

		private bool ReadImportOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--path":
					this.FilePath = options.ReadText("Specify a settings file path.");
					return true;
				case "--apply-desktops":
					this.ApplyDesktops = options.ReadBoolean();
					return true;
				default:
					return false;
			}
		}

		private bool ReadResetOption(OptionReader options)
		{
			if (options.Option != "--yes") return false;
			this.ConfirmReset = true;
			return true;
		}

		private void ValidateSettings()
		{
			switch (this.Operation)
			{
				case "desktop configure":
					var anyDesktopSetting = this.Loop != null || this.OverrideWindowsShortcuts != null
						|| this.PerDesktopWallpaper != null || this.OverrideOnStartup != null;
					Require(anyDesktopSetting, NoSettingError);
					break;
				case "notification configure":
					var anyNotificationSetting = this.OnSwitch != null || this.AlwaysShow != null || this.DurationMs != null
						|| this.HasNotificationAppearance || this.HasNotificationGeometry;
					Require(anyNotificationSetting, NoSettingError);
					break;
				case "tray configure":
					Require(this.ShowDesktop != null || this.CurrentNumberOnly != null, NoSettingError);
					break;
				case "settings configure":
					Require(this.Language != null, NoSettingError);
					break;
				case "settings export":
					Require(this.FilePath != null, "Specify --path.");
					break;
				case "settings import":
					Require(this.FilePath != null, "Specify --path.");
					Require(this.ApplyDesktops != null, "Specify --apply-desktops true or false.");
					break;
				case "settings reset":
					Require(this.ConfirmReset, "Specify --yes to reset application settings.");
					break;
			}
		}

		private bool ReadStartupOption(OptionReader options)
		{
			if (this.Operation != "startup configure" || options.Option != "--mode") return false;
			this.StartupMode = this.ReadCatalogChoice(options);
			return true;
		}

		private void ValidateStartup()
		{
			if (this.Operation != "startup configure") return;
			Require(this.StartupMode != null, "Specify --mode disabled, normal or elevated.");
		}

		#endregion

		#region Shortcuts

		private bool ChangesBinding => this.Operation == "shortcut set" || this.Operation == "shortcut clear";

		private bool ReadShortcutOption(OptionReader options)
		{
			switch (options.Option)
			{
				case "--device":
					this.Device = this.ReadCatalogChoice(options);
					return true;
				case "--action" when this.ChangesBinding:
					this.Action = options.ReadText("Specify an action returned by shortcut list.");
					return true;
				case "--number" when this.ChangesBinding:
					this.Number = options.ReadInteger(NumberStyles.None, 1, 1000, "Shortcut desktop numbers must be between 1 and 1000.");
					return true;
				case "--trigger" when this.Operation == "shortcut set":
					this.Trigger = options.ReadText("Specify a trigger using names from shortcut keys.");
					return true;
				default:
					return false;
			}
		}

		private void ValidateShortcut()
		{
			if (this.Operation != "shortcut list")
			{
				Require(this.Device != null, "Specify --device keyboard or mouse.");
			}
			if (this.ChangesBinding)
			{
				Require(this.Action != null, "Specify --action using shortcut list.");
			}
			if (this.Operation == "shortcut set")
			{
				Require(this.Trigger != null, "Specify --trigger using shortcut keys.");
			}
		}

		#endregion

		#region Option reading

		// The accepted values are the ones CliSpecCatalog publishes for this command and option.
		private string ReadCatalogChoice(OptionReader options)
			=> options.ReadChoice(CliSpecCatalog.Choices(this.Operation, options.Option));

		private string ReadCatalogChoice(OptionReader options, string error)
			=> options.ReadChoice(CliSpecCatalog.Choices(this.Operation, options.Option), error);

		private static void Require(bool condition, string error)
		{
			if (!condition)
			{
				throw new ArgumentException(error);
			}
		}

		// Walks the options after the command words; each option may appear once and consumes its value, if any.
		private sealed class OptionReader
		{
			private readonly string[] _args;
			private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
			private int _index;

			internal OptionReader(string[] args, int firstOptionIndex)
			{
				this._args = args;
				this._index = firstOptionIndex - 1;
			}

			internal string Option { get; private set; }

			internal bool MoveNext()
			{
				if (++this._index >= this._args.Length) return false;
				this.Option = this._args[this._index];
				if (!this._seen.Add(this.Option))
				{
					throw new ArgumentException("Duplicate option: " + this.Option);
				}
				return true;
			}

			// Accepts empty or blank text, such as an empty name that clears the current one.
			internal string ReadText(string missingError)
			{
				if (++this._index >= this._args.Length || this._args[this._index] == null
					|| this._args[this._index].StartsWith("--", StringComparison.Ordinal))
				{
					throw new ArgumentException(missingError);
				}
				return this._args[this._index];
			}

			internal string ReadValue()
			{
				const string missingError = "An option value is missing.";
				var value = this.ReadText(missingError);
				if (string.IsNullOrWhiteSpace(value))
				{
					throw new ArgumentException(missingError);
				}
				return value;
			}

			internal string ReadChoice(string[] choices)
				=> this.ReadChoice(choices, "Expected one of: " + string.Join(", ", choices) + ".");

			internal string ReadChoice(string[] choices, string error)
			{
				var value = this.ReadValue();
				if (Array.IndexOf(choices, value) < 0)
				{
					throw new ArgumentException(error);
				}
				return value;
			}

			internal bool ReadBoolean() => this.ReadBoolean("Boolean settings require true or false.");

			internal bool ReadBoolean(string error) => this.ReadChoice(new[] { "true", "false" }, error) == "true";

			internal string ReadId()
			{
				var value = this.ReadValue();
				if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
				{
					throw new ArgumentException("Specify a nonempty ID returned by the corresponding list command.");
				}
				return value;
			}

			internal int ReadInteger(NumberStyles styles, int minimum, int maximum, string error)
			{
				if (!TryParseInteger(this.ReadValue(), styles, minimum, maximum, out var value))
				{
					throw new ArgumentException(error);
				}
				return value;
			}

			// Digits only; a sign or surrounding white space is rejected.
			internal int ReadPositiveInteger(string error) => this.ReadInteger(NumberStyles.None, 1, int.MaxValue, error);

			// Validated like ReadPositiveInteger but returned as typed.
			internal string ReadPositiveIntegerText(string error)
			{
				var value = this.ReadValue();
				if (!TryParseInteger(value, NumberStyles.None, 1, int.MaxValue, out _))
				{
					throw new ArgumentException(error);
				}
				return value;
			}

			private static bool TryParseInteger(string text, NumberStyles styles, int minimum, int maximum, out int value)
				=> int.TryParse(text, styles, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
		}

		#endregion
	}
}
