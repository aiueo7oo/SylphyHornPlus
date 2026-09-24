#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliSettingsTests
	{
		[Fact]
		public async Task AppearanceSettingsReachNotificationSnapshotsAndRepeatedRequestsDoNotNotify()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
			var changes = 0;
			using (SettingsService.ObserveNotificationAppearance(settings, () => changes++))
			{
				Assert.Equal(0, changes);
				var command = "notification configure --simple true --use-desktop-name true --theme dark --corners rounded "
					+ "--font-family Consolas --header-font-size 20 --body-font-size 30 --header-align right --body-align center --line-spacing -6";
				var result = await Run(service, command);
				Assert.True(result.Success);
				Assert.Equal("dark", result.Data.Theme);
				Assert.Equal("rounded", result.Data.Corners);
				Assert.True(result.Data.UseDesktopName);
				Assert.True(result.Data.Simple);
				Assert.Equal(-6, result.Data.LineSpacing);
				Assert.Equal("right", result.Data.HeaderAlign);
				Assert.Equal("center", result.Data.BodyAlign);
				var visual = NotificationVisualSettings.Capture(settings);
				Assert.True(visual.SimpleNotification);
				Assert.Equal(GuiEnumValue("BlurWindowThemeMode", "Dark"), visual.WindowStyle);
				Assert.Equal(GuiEnumValue("BlurWindowCornerMode", "Rounded"), visual.CornerStyle);
				Assert.Equal(System.Windows.HorizontalAlignment.Right, visual.HeaderAlignment);
				Assert.Equal(System.Windows.HorizontalAlignment.Center, visual.BodyAlignment);
				Assert.Equal(20, visual.HeaderFontSize);
				Assert.Equal(30, visual.BodyFontSize);
				Assert.Equal("0,0,6,-6", visual.HeaderMargin);
				Assert.StartsWith("Consolas, ", visual.FontFamily);
				Assert.Equal(10, changes);
				Assert.False((await Run(service, command)).Data.Changed);
				Assert.Equal(10, changes);
				Assert.False(settings.AlwaysShowDesktopNotification.Value);
				Assert.NotEmpty(provider.SavedDictionaries);
			}
			settings.SimpleNotification.Value = false;
			Assert.Equal(10, changes);
		}

		[Theory]
		[InlineData("apps", "Default")]
		[InlineData("system", "System")]
		[InlineData("light", "Light")]
		[InlineData("dark", "Dark")]
		[InlineData("accent", "Accent")]
		public async Task ThemeNamesMapToTheExistingGuiValues(string name, string value)
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
			Assert.True((await Run(service, "notification configure --theme " + name)).Success);
			Assert.Equal(GuiEnumValue("BlurWindowThemeMode", value), settings.NotificationWindowStyle.Value);
			Assert.Equal(name, (await Run(service, "notification settings")).Data.Theme);
		}

		[Fact]
		public async Task FontResetPreservesDefaultsAndInvalidRenderingSizesDoNotPartiallyApply()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
			settings.NotificationFontFamily.Value = "Consolas";
			var reset = CliCommand.Parse(new[] { "notification", "configure", "--font-family", "" });
			Assert.True((await service.ExecuteAsync(reset, CancellationToken.None)).Success);
			Assert.Equal(GeneralSettings.NotificationFontFamilyDefaultValue, NotificationVisualSettings.Capture(settings).FontFamily);
			Assert.Equal("", (await Run(service, "notification settings")).Data.FontFamily);
			var saves = provider.SavedDictionaries.Count;
			var invalid = await Run(service, "notification configure --simple true --body-font-size 2147483647");
			Assert.Equal("invalid_arguments", invalid.Error.Code);
			Assert.False(settings.SimpleNotification.Value);
			Assert.Equal(saves, provider.SavedDictionaries.Count);
		}

		[Fact]
		public async Task SettingsApplyWithoutASettingsWindowAndSubscriptionsAreDisposed()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var inputChanges = 0;
			var trayChanges = 0;
			bool? visible = null;
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
			using (SettingsService.BindGeneralSettings(settings, () => inputChanges++, value => visible = value, () => trayChanges++))
			{
				Assert.Equal(0, inputChanges);
				var response = await Run(service, "desktop configure --loop true --override-windows-shortcuts true");
				Assert.True(response.Success);
				Assert.True(response.Data.Changed);
				Assert.Equal(2, inputChanges);
				Assert.False((await Run(service, "desktop configure --loop true")).Data.Changed);
				Assert.Equal(2, inputChanges);
				Assert.True((await Run(service, "desktop settings")).Data.OverrideWindowsShortcuts);
				Assert.True((await Run(service, "notification configure --always-show true --duration-ms 1200")).Success);
				Assert.True(visible);
				var notification = await Run(service, "notification settings");
				Assert.Equal(1200, notification.Data.DurationMs);
				Assert.True(notification.Data.OnSwitch);
				Assert.True((await Run(service, "tray configure --current-number-only true")).Success);
				Assert.Equal(1, trayChanges);
				Assert.False((await Run(service, "tray settings")).Data.ShowDesktop);
				Assert.NotEmpty(provider.SavedDictionaries);
			}
			settings.LoopDesktop.Value = false;
			settings.AlwaysShowDesktopNotification.Value = false;
			settings.TrayShowDesktop.Value = true;
			Assert.Equal(2, inputChanges);
			Assert.Equal(1, trayChanges);
			Assert.True(visible);
		}

		[Fact]
		public async Task LanguageReportsRestartUntilStartupPreferenceIsRestored()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
			Assert.Equal("auto", (await Run(service, "settings get")).Data.Language);
			Assert.Empty(provider.SavedDictionaries);
			var changed = await Run(service, "settings configure --language ja");
			Assert.True(changed.Success);
			Assert.True(changed.Data.RestartRequired);
			Assert.Equal("ja", changed.Data.Language);
			Assert.True((await Run(service, "desktop settings")).Data.RestartRequired);
			Assert.False((await Run(service, "settings configure --language auto")).Data.RestartRequired);
			Assert.Null(settings.Culture.Value);
		}

		[Fact]
		public async Task SaveFailureCanBeRetriedWithoutRepeatingRuntimeEffects()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var changes = 0;
			using (SettingsService.BindGeneralSettings(settings, () => changes++, _ => { }, () => { }))
			{
				var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => true, null);
				provider.SaveFailure = new IOException("synthetic");
				Assert.Equal("settings_save_failed", (await Run(service, "desktop configure --loop true")).Error.Code);
				Assert.True(settings.LoopDesktop.Value);
				provider.SaveFailure = null;
				var retry = await Run(service, "desktop configure --loop true");
				Assert.True(retry.Success);
				Assert.False(retry.Data.Changed);
				Assert.Equal(1, changes);
			}
		}

		[Fact]
		public async Task BusyAndCancelledRequestsDoNotMutateSettings()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var available = false;
			var service = new CliSettingsService(settings, () => provider.SaveWithResultAsync(), () => available, null);
			Assert.Equal("host_busy", (await Run(service, "desktop configure --loop true")).Error.Code);
			Assert.True((await Run(service, "desktop settings")).Success);
			available = true;
			var response = await service.ExecuteAsync(CliCommand.Parse(new[] { "desktop", "configure", "--loop", "true" }), new CancellationToken(true));
			Assert.Equal("request_cancelled", response.Error.Code);
			Assert.False(settings.LoopDesktop.Value);
			Assert.Empty(provider.SavedDictionaries);
		}

		[Fact]
		public async Task AConcurrentChangeDuringSaveIsReportedInsteadOfReturningStaleValues()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var pending = new TaskCompletionSource<SettingsSaveResult>();
			var service = new CliSettingsService(settings, () => pending.Task, () => true, null);
			var request = Run(service, "desktop configure --loop true");
			settings.LoopDesktop.Value = false;
			pending.SetResult(await provider.SaveWithResultAsync());
			Assert.Equal("state_changed", (await request).Error.Code);
		}

		private static uint GuiEnumValue(string type, string name)
			=> Convert.ToUInt32(Enum.Parse(
				typeof(SylphyHorn.UI.NotificationWindow).BaseType.Assembly.GetType("MetroRadiance.UI.Controls." + type, true), name));

		private static Task<CliResponse> Run(CliSettingsService service, string args)
			=> service.ExecuteAsync(CliCommand.Parse(args.Split(' ')), CancellationToken.None);
	}
}
#endif
