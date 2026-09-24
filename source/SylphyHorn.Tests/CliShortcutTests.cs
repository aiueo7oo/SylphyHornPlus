#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;
using SylphyHorn.Services.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliShortcutTests
	{
		[Fact]
		public async Task NumberedBindingsExtendWithoutShiftingAndClearWithoutCreatingDesktops()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var keyboard = new ShortcutKeySettings(provider);
			var mouse = new MouseShortcutSettings(provider);
			var reloads = 0;
			var service = new CliShortcutService(keyboard, mouse, new GeneralSettings(provider),
				() => provider.SaveWithResultAsync(), () => true, () => reloads++, () => 2);
			var list = await Run(service, "shortcut list --device keyboard");
			Assert.Contains(list.Data.Shortcuts, item => item.Action == "desktop-switch-number" && item.Number == 2 && item.Trigger == null);
			Assert.Empty(provider.SavedDictionaries);

			var command = "shortcut set --device keyboard --action desktop-switch-number --number 4 --trigger LControlKey+LWin+D4";
			var result = await Run(service, command);
			Assert.True(result.Success);
			Assert.Equal(4, keyboard.SwitchToIndices.Count);
			Assert.Null(keyboard.SwitchToIndices.Value[2].Value);
			Assert.Equal(new[] { 52, 162, 91 }, keyboard.SwitchToIndices.Value[3].Value);
			Assert.Equal(1, reloads);
			Assert.False((await Run(service, command)).Data.Changed);
			Assert.Equal(1, reloads);
			Assert.True((await Run(service, "shortcut clear --device keyboard --action desktop-switch-number --number 2")).Success);
			Assert.Equal(new[] { 52, 162, 91 }, keyboard.SwitchToIndices.Value[3].Value);
			Assert.True((await Run(service, "shortcut clear --device keyboard --action desktop-switch-number --number 4")).Success);
			Assert.Equal(4, keyboard.SwitchToIndices.Count);
			Assert.Empty(keyboard.SwitchToIndices.Value[3].Value);
			Assert.Equal(2, reloads);
		}

		[Fact]
		public async Task ConflictsRejectChangesButDevicesAndLeftRightModifiersRemainIndependent()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var keyboard = new ShortcutKeySettings(provider);
			var mouse = new MouseShortcutSettings(provider);
			var general = new GeneralSettings(provider);
			var service = new CliShortcutService(keyboard, mouse, general,
				() => provider.SaveWithResultAsync(), () => true, () => { }, () => 2);
			Assert.True((await Run(service, "shortcut set --device keyboard --action settings-show --trigger LControlKey+F8")).Success);
			var saves = provider.SavedDictionaries.Count;
			var conflict = await Run(service, "shortcut set --device keyboard --action task-view-show --trigger LControlKey+F8");
			Assert.Equal("shortcut_conflict", conflict.Error.Code);
			Assert.Equal("settings-show", Assert.Single(conflict.Error.Conflicts).Action);
			Assert.Null(keyboard.ShowTaskView.Value);
			Assert.Equal(saves, provider.SavedDictionaries.Count);
			Assert.True((await Run(service, "shortcut set --device keyboard --action task-view-show --trigger RControlKey+F8")).Success);
			Assert.True((await Run(service, "shortcut set --device mouse --action settings-show --trigger RButton+WheelUp")).Success);
			var mouseList = await Run(service, "shortcut list --device mouse");
			Assert.Equal("RButton+WheelUp", mouseList.Data.Shortcuts.Single(item => item.Action == "settings-show").Trigger);
			general.LoopDesktop.Value = true;
			Assert.Equal("shortcut_conflict", (await Run(service,
				"shortcut set --device keyboard --action desktop-switch-left --trigger LControlKey+LWin+Left")).Error.Code);
		}

		[Fact]
		public async Task RetryRepairsRegistrationAndPersistenceWithoutReapplyingSuccessfulRegistration()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var failReload = true;
			var reloads = 0;
			var available = true;
			var service = new CliShortcutService(new ShortcutKeySettings(provider), new MouseShortcutSettings(provider), new GeneralSettings(provider),
				() => provider.SaveWithResultAsync(), () => available, () =>
				{
					reloads++;
					if (failReload) throw new InvalidOperationException();
				}, () => 1);
			const string command = "shortcut set --device keyboard --action settings-show --trigger F8";
			Assert.Equal("result_unconfirmed", (await Run(service, command)).Error.Code);
			failReload = false;
			provider.SaveFailure = new IOException("synthetic");
			Assert.Equal("settings_save_failed", (await Run(service, command)).Error.Code);
			provider.SaveFailure = null;
			Assert.True((await Run(service, command)).Success);
			Assert.Equal(2, reloads);
			available = false;
			Assert.Equal("host_busy", (await Run(service, command)).Error.Code);
			Assert.True((await Run(service, "shortcut list")).Success);
			var cancelled = await service.ExecuteAsync(CliCommand.Parse(command.Split(' ')), new CancellationToken(true));
			Assert.Equal("request_cancelled", cancelled.Error.Code);
		}

		[Theory]
		[InlineData("keyboard", "Ctrl+F8")]
		[InlineData("keyboard", "LControlKey")]
		[InlineData("keyboard", "LControlKey+LControlKey+F8")]
		[InlineData("keyboard", "F8+F9")]
		[InlineData("mouse", "LButton")]
		[InlineData("mouse", "WheelUp")]
		[InlineData("mouse", "WheelUp+RButton")]
		public void InvalidTriggersCannotEnterTheInputRegistry(string device, string trigger)
		{
			Assert.Throws<ArgumentException>(() => CliShortcutService.ParseTrigger(device, trigger));
		}

		[Fact]
		public async Task DiscoveryKeysRoundTripAndUnsupportedActionsCanStillBeCleared()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var keyboard = new ShortcutKeySettings(provider);
			keyboard.SwapDesktopLeft.Value = new[] { 119 };
			var service = new CliShortcutService(keyboard, new MouseShortcutSettings(provider), new GeneralSettings(provider),
				() => provider.SaveWithResultAsync(), () => true, () => { }, () => 1, reorderSupported: false);
			foreach (var device in new[] { "keyboard", "mouse" })
			{
				var keys = (await Run(service, "shortcut keys --device " + device)).Data.Keys;
				foreach (var key in keys.Where(key => key.CanTrigger))
					CliShortcutService.ParseTrigger(device, device == "mouse" && key.Name != "RButton" ? "RButton+" + key.Name
						: device == "mouse" ? "LButton+RButton" : key.Name);
			}
			Assert.Equal("unsupported", (await Run(service,
				"shortcut set --device keyboard --action desktop-reorder-left --trigger F9")).Error.Code);
			Assert.True((await Run(service, "shortcut clear --device keyboard --action desktop-reorder-left")).Success);
			Assert.Empty(keyboard.SwapDesktopLeft.Value);
			Assert.Equal("invalid_arguments", (await Run(service,
				"shortcut set --device keyboard --action settings-show --number 2 --trigger F8")).Error.Code);
		}

		[Fact]
		public async Task ConcurrentEditDuringSaveReturnsStateChanged()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var keyboard = new ShortcutKeySettings(provider);
			var pending = new TaskCompletionSource<SettingsSaveResult>();
			var service = new CliShortcutService(keyboard, new MouseShortcutSettings(provider), new GeneralSettings(provider),
				() => pending.Task, () => true, () => { }, () => 1);
			var request = Run(service, "shortcut set --device keyboard --action settings-show --trigger F8");
			keyboard.ShowSettings.Value = new[] { 120 };
			pending.SetResult(await provider.SaveWithResultAsync());
			Assert.Equal("state_changed", (await request).Error.Code);
		}

		[Theory]
		[InlineData("shortcut keys")]
		[InlineData("shortcut set --device keyboard --action settings-show")]
		[InlineData("shortcut clear --device keyboard")]
		[InlineData("shortcut list --trigger F8")]
		[InlineData("shortcut set --device keyboard --action desktop-switch-number --number 0 --trigger F8")]
		[InlineData("shortcut set --device keyboard --action desktop-switch-number --number 1001 --trigger F8")]
		[InlineData("shortcut clear --device mouse --action settings-show --trigger MButton")]
		public void InvalidCommandShapesAreRejectedBeforeHostAccess(string command)
		{
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(command.Split(' ')));
		}

		private static Task<CliResponse> Run(CliShortcutService service, string args)
			=> service.ExecuteAsync(CliCommand.Parse(args.Split(' ')), CancellationToken.None);
	}
}
#endif
