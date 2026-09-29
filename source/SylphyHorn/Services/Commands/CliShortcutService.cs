#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SylphyHorn.Commands;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliShortcutService
	{
		private sealed class Definition
		{
			internal Definition(string action, Func<ShortcutKeySettings, ShortcutkeyProperty> property, bool reorder = false)
			{
				this.Action = action;
				this.Property = property;
				this.Reorder = reorder;
			}

			internal Definition(string action, Func<ShortcutKeySettings, ShortcutkeyPropertyList> list, bool reorder = false)
			{
				this.Action = action;
				this.List = list;
				this.Reorder = reorder;
			}

			internal string Action { get; }
			internal Func<ShortcutKeySettings, ShortcutkeyProperty> Property { get; }
			internal Func<ShortcutKeySettings, ShortcutkeyPropertyList> List { get; }
			internal bool Reorder { get; }
		}

		private static readonly Definition[] Definitions =
		{
			new Definition("window-move-left", settings => settings.MoveLeft),
			new Definition("window-move-left-and-switch", settings => settings.MoveLeftAndSwitch),
			new Definition("window-move-right", settings => settings.MoveRight),
			new Definition("window-move-right-and-switch", settings => settings.MoveRightAndSwitch),
			new Definition("window-move-new", settings => settings.MoveNew),
			new Definition("window-move-new-and-switch", settings => settings.MoveNewAndSwitch),
			new Definition("window-move-previous", settings => settings.MoveToPrevious),
			new Definition("window-move-previous-and-switch", settings => settings.MoveToPreviousAndSwitch),
			new Definition("desktop-switch-left", settings => settings.SwitchToLeft),
			new Definition("desktop-switch-right", settings => settings.SwitchToRight),
			new Definition("desktop-switch-previous", settings => settings.SwitchToPrevious),
			new Definition("desktop-reorder-left", settings => settings.SwapDesktopLeft, reorder: true),
			new Definition("desktop-reorder-right", settings => settings.SwapDesktopRight, reorder: true),
			new Definition("desktop-reorder-first", settings => settings.SwapDesktopFirst, reorder: true),
			new Definition("desktop-reorder-last", settings => settings.SwapDesktopLast, reorder: true),
			new Definition("desktop-close-left", settings => settings.CloseAndSwitchLeft),
			new Definition("desktop-close-right", settings => settings.CloseAndSwitchRight),
			new Definition("task-view-show", settings => settings.ShowTaskView),
			new Definition("window-switch-show", settings => settings.ShowWindowSwitch),
			new Definition("window-pin", settings => settings.Pin),
			new Definition("window-unpin", settings => settings.Unpin),
			new Definition("window-toggle-pin", settings => settings.TogglePin),
			new Definition("app-pin", settings => settings.PinApp),
			new Definition("app-unpin", settings => settings.UnpinApp),
			new Definition("app-toggle-pin", settings => settings.TogglePinApp),
			new Definition("settings-show", settings => settings.ShowSettings),
			new Definition("notification-toggle", settings => settings.ToggleDesktopNotification),
			new Definition("desktop-switch-number", settings => settings.SwitchToIndices),
			new Definition("window-move-number", settings => settings.MoveToIndices),
			new Definition("window-move-number-and-switch", settings => settings.MoveToIndicesAndSwitch),
			new Definition("desktop-reorder-number", settings => settings.SwapDesktopIndices, reorder: true),
		};

		private const int WheelUp = (int)Mouse.Stroke.WheelUp;
		private const int WheelDown = (int)Mouse.Stroke.WheelDown;
		private static readonly IReadOnlyDictionary<string, int> KeyboardKeys = Enum.GetValues(typeof(Keys)).Cast<Keys>()
			.Where(key => (int)key > 6 && (int)key < 256 && key != Keys.Back
				&& key != Keys.ShiftKey && key != Keys.ControlKey && key != Keys.Menu)
			.Distinct().ToDictionary(key => key.ToString(), key => (int)key, StringComparer.Ordinal);
		private static readonly IReadOnlyDictionary<string, int> MouseKeys = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			{ "LButton", (int)Keys.LButton },
			{ "RButton", (int)Keys.RButton },
			{ "MButton", (int)Keys.MButton },
			{ "XButton1", (int)Keys.XButton1 },
			{ "XButton2", (int)Keys.XButton2 },
			{ "WheelUp", WheelUp },
			{ "WheelDown", WheelDown },
		};

		private readonly ShortcutKeySettings _keyboard;
		private readonly MouseShortcutSettings _mouse;
		private readonly GeneralSettings _general;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<bool> _available;
		private readonly Action _reload;
		private readonly Func<int> _desktopCount;
		private readonly bool _reorderSupported;
		private bool _registrationPending;

		internal CliShortcutService(ShortcutKeySettings keyboard, MouseShortcutSettings mouse, GeneralSettings general,
			Func<Task<SettingsSaveResult>> save, Func<bool> available, Action reload, Func<int> desktopCount,
			bool? reorderSupported = null)
		{
			this._keyboard = keyboard;
			this._mouse = mouse;
			this._general = general;
			this._save = save;
			this._available = available;
			this._reload = reload;
			this._desktopCount = desktopCount;
			this._reorderSupported = reorderSupported ?? ProductInfo.IsReorderingSupportBuild;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var changed = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (command.Operation == "shortcut keys")
				{
					return CliResponse.Ok(command.Operation, new CliData { Keys = DescribeKeys(command.Device) });
				}
				if (command.Operation == "shortcut list")
				{
					return CliResponse.Ok(command.Operation, new CliData { Shortcuts = this.Describe(command.Device).ToArray() });
				}
				if (command.Operation != "shortcut set" && command.Operation != "shortcut clear")
				{
					return CliResponse.Fail(command.Operation, "invalid_arguments", "Unknown shortcut command.");
				}
				if (!this._available())
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Settings are changing or input is being edited.", true);
				}

				var definition = Definitions.FirstOrDefault(item => item.Action == command.Action);
				if (definition == null || (definition.List != null) != command.Number.HasValue)
				{
					return CliResponse.Fail(command.Operation, "invalid_arguments", "Use an action from shortcut list; numbered actions require --number.");
				}
				if (command.Operation == "shortcut set" && definition.Reorder && !this._reorderSupported)
				{
					return CliResponse.Fail(command.Operation, "unsupported", "Desktop reordering is unavailable on this Windows build.");
				}
				var desired = command.Operation == "shortcut clear" ? ShortcutKey.None : ParseTrigger(command.Device, command.Trigger);
				var settings = command.Device == "keyboard" ? this._keyboard : this._mouse;
				var property = ResolveProperty(definition, settings, command.Number);
				var previous = property.ToShortcutKey();
				if (previous != desired && desired != ShortcutKey.None)
				{
					var conflicts = this.Describe(command.Device)
						.Where(item => !(item.Action == command.Action && item.Number == command.Number)
							&& item.Trigger != null && item.Trigger == FormatTrigger(command.Device, desired)).ToList();
					if (command.Device == "keyboard"
						&& (this._general.OverrideWindowsDefaultKeyCombination.Value || this._general.LoopDesktop.Value))
					{
						AddDefaultConflict(conflicts, desired, this._keyboard.SwitchToLeftWithDefault, "windows-switch-left");
						AddDefaultConflict(conflicts, desired, this._keyboard.SwitchToRightWithDefault, "windows-switch-right");
					}
					if (conflicts.Count != 0)
					{
						var failure = CliResponse.Fail(command.Operation, "shortcut_conflict",
							"This input is already assigned. Inspect conflicts before clearing an assignment.");
						failure.Error.Conflicts = conflicts.ToArray();
						return failure;
					}
				}

				if (previous != desired)
				{
					changed = true;
					this._registrationPending = true;
					if (property == null)
					{
						var list = definition.List(settings);
						list.StretchTo(command.Number.Value);
						property = list.Value[command.Number.Value - 1];
					}
					property.Value = desired.ToSerializable();
				}

				// Retry registration as well as saving after a previous partial failure.
				if (this._registrationPending)
				{
					this._reload();
					this._registrationPending = false;
				}
				var saved = await this._save().WaitAsync(cancellation);
				if (!saved.Succeeded)
				{
					return CliResponse.Fail(command.Operation, "settings_save_failed", "The shortcut is active in memory but could not be saved.");
				}
				var currentProperty = ResolveProperty(definition, settings, command.Number);
				if (currentProperty.ToShortcutKey() != desired)
				{
					return CliResponse.Fail(command.Operation, "state_changed", "The shortcut changed while saving. Read shortcuts before retrying.");
				}
				return CliResponse.Ok(command.Operation, new CliData
				{
					Changed = changed,
					Shortcuts = new[] { this.DescribeOne(command.Device, definition, currentProperty, command.Number) },
				});
			}
			catch (ArgumentException) when (!changed)
			{
				return CliResponse.Fail(command.Operation, "invalid_arguments", "Invalid trigger. Use shortcut keys and put the main key or wheel last.");
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, changed ? "result_unconfirmed" : "request_cancelled",
					"Read shortcuts before retrying a cancelled change.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, changed ? "result_unconfirmed" : "operation_failed",
					"Shortcut registration or persistence could not be confirmed. Read shortcuts before retrying.");
			}
		}

		private static void AddDefaultConflict(List<CliShortcut> conflicts, ShortcutKey desired, ShortcutkeyProperty property, string action)
		{
			if (property.ToShortcutKey() == desired)
			{
				conflicts.Add(new CliShortcut
				{
					Device = "keyboard", Action = action,
					Trigger = FormatTrigger("keyboard", desired), Supported = true,
				});
			}
		}

		private IEnumerable<CliShortcut> Describe(string device)
		{
			foreach (var selected in device == null ? new[] { "keyboard", "mouse" } : new[] { device })
			{
				var settings = selected == "keyboard" ? this._keyboard : this._mouse;
				foreach (var definition in Definitions)
				{
					if (definition.List == null)
					{
						yield return this.DescribeOne(selected, definition, definition.Property(settings), null);
						continue;
					}
					var list = definition.List(settings);
					var count = Math.Max(list.Count, this._desktopCount());
					if (count == 0)
					{
						yield return this.DescribeOne(selected, definition, null, null);
					}
					for (var index = 0; index < count; index++)
					{
						yield return this.DescribeOne(selected, definition, index < list.Count ? list.Value[index] : null, index + 1);
					}
				}
			}
		}

		/// <summary>Returns null for a numbered shortcut that the stored list does not reach yet.</summary>
		private static ShortcutkeyProperty ResolveProperty(Definition definition, ShortcutKeySettings settings, int? number)
		{
			if (definition.Property != null) return definition.Property(settings);
			var list = definition.List(settings);
			return number.Value <= list.Count ? list.Value[number.Value - 1] : null;
		}

		private CliShortcut DescribeOne(string device, Definition definition, ShortcutkeyProperty property, int? number)
			=> new CliShortcut
			{
				Device = device, Action = definition.Action, Number = number,
				NumberRequired = definition.List != null,
				Trigger = FormatTrigger(device, property.ToShortcutKey()),
				Supported = !definition.Reorder || this._reorderSupported,
			};

		internal static ShortcutKey ParseTrigger(string device, string trigger)
		{
			var names = trigger.Split('+');
			var vocabulary = device == "keyboard" ? KeyboardKeys : MouseKeys;
			if (names.Length == 0 || names.Any(name => !vocabulary.ContainsKey(name)))
			{
				throw new ArgumentException();
			}
			var codes = names.Select(name => vocabulary[name]).ToArray();
			if (codes.Distinct().Count() != codes.Length)
			{
				throw new ArgumentException();
			}
			var main = codes[codes.Length - 1];
			var held = codes.Take(codes.Length - 1).ToArray();
			if (device == "keyboard")
			{
				if (((Keys)main).IsModifyKey() || held.Any(code => !((Keys)code).IsModifyKey()))
				{
					throw new ArgumentException();
				}
			}
			else if (held.Any(code => code == WheelUp || code == WheelDown)
				|| (held.Length == 0 && (main == (int)Keys.LButton || main == (int)Keys.RButton || main == WheelUp || main == WheelDown)))
			{
				throw new ArgumentException();
			}
			return new ShortcutKey((Keys)main, held.Select(code => (Keys)code).ToArray());
		}

		private static string FormatTrigger(string device, ShortcutKey shortcut)
		{
			if (shortcut == ShortcutKey.None) return null;
			var vocabulary = device == "keyboard" ? KeyboardKeys : MouseKeys;
			string Name(Keys key) => vocabulary.FirstOrDefault(pair => pair.Value == (int)key).Key
				?? ((int)key).ToString(CultureInfo.InvariantCulture);
			return string.Join("+", (shortcut.Modifiers ?? Array.Empty<Keys>()).OrderBy(key => (int)key)
				.Select(Name).Concat(new[] { Name(shortcut.Key) }));
		}

		private static CliInputKey[] DescribeKeys(string device)
		{
			var vocabulary = device == "keyboard" ? KeyboardKeys : MouseKeys;
			return vocabulary.Select(pair => new CliInputKey
			{
				Name = pair.Key,
				CanTrigger = device == "mouse" || !((Keys)pair.Value).IsModifyKey(),
				CanHold = device == "keyboard" ? ((Keys)pair.Value).IsModifyKey() : pair.Value != WheelUp && pair.Value != WheelDown,
			}).OrderBy(key => key.Name, StringComparer.Ordinal).ToArray();
		}
	}
}
#endif
