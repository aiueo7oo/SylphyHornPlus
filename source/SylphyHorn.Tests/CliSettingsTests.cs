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
using Monitor = SylphyHorn.Services.Monitor;

namespace SylphyHorn.Tests
{
	public sealed class CliSettingsTests
	{
		[Fact]
		public async Task SaveRetriesCurrentSettingsWithoutReplayingFailedChanges()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider);
			settings.LoopDesktop.Value = true;
			provider.SaveFailure = new IOException("synthetic");
			Assert.Equal("settings_save_failed", (await Run(service, "settings save")).Error.Code);
			settings.LoopDesktop.Value = false;
			provider.SaveFailure = null;
			var response = await Run(service, "settings save");
			Assert.True(response.Success);
			Assert.True(response.Data.Saved);
			Assert.Null(response.Data.Changed);
			Assert.False(settings.LoopDesktop.Value);
			Assert.False((bool)Assert.Single(provider.SavedDictionaries)["GeneralSettings.LoopDesktop"]);
			Assert.True((await Run(service, "settings save")).Data.Saved);
		}

		[Fact]
		public async Task SaveRejectsBusyOrCancelledRequestsBeforePersistence()
		{
			var available = false;
			var service = CreateService(settings: null, save: () => throw new Exception("Must not save."), available: () => available);
			Assert.Equal("host_busy", (await Run(service, "settings save")).Error.Code);
			available = true;
			var cancelled = await service.ExecuteAsync(CliCommand.Parse(new[] { "settings", "save" }), new CancellationToken(true));
			Assert.Equal("request_cancelled", cancelled.Error.Code);
		}

		[Fact]
		public async Task SaveCancellationAfterSubmissionDoesNotClaimFailureOrSuccess()
		{
			var pending = new TaskCompletionSource<SettingsSaveResult>();
			var service = CreateService(settings: null, save: () => pending.Task);
			using (var cancellation = new CancellationTokenSource())
			{
				var request = service.ExecuteAsync(CliCommand.Parse(new[] { "settings", "save" }), cancellation.Token);
				cancellation.Cancel();
				Assert.Equal("result_unconfirmed", (await request).Error.Code);
			}
			pending.SetCanceled(TestContext.Current.CancellationToken);
		}

		[Theory]
		[InlineData(false, true)]
		[InlineData(true, true)]
		[InlineData(false, false)]
		public async Task BackgroundSettingsRespectCapabilitiesAndDoNotPartiallyApply(bool nativeWallpaper, bool names)
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider, nativeWallpaperSupported: nativeWallpaper, nameSupported: names);
			var changes = 0;
			using (SettingsService.ObserveWallpaperSettings(settings, () => changes++))
			{
				var response = await Run(service, "desktop configure --per-desktop-wallpaper true --override-on-startup true");
				if (nativeWallpaper || !names)
				{
					Assert.Equal("unsupported", response.Error.Code);
					Assert.False(settings.ChangeBackgroundEachDesktop.Value);
					Assert.False(settings.OverrideDesktopsOnStartup.Value);
					Assert.Empty(provider.SavedDictionaries);
					Assert.Equal(0, changes);
				}
				else
				{
					Assert.True(response.Success);
					Assert.True(response.Data.PerDesktopWallpaper);
					Assert.True(response.Data.OverrideOnStartup);
					Assert.True(response.Data.WallpaperEnabled);
					Assert.False(response.Data.NativeWallpaperSupported);
					Assert.False(response.Data.RestartRequired);
					Assert.Equal(1, changes);
					Assert.False((await Run(service, "desktop configure --per-desktop-wallpaper true")).Data.Changed);
					Assert.Equal(1, changes);
					Assert.True((await Run(service, "desktop configure --per-desktop-wallpaper false")).Success);
					Assert.Equal(2, changes);
				}
			}
			var before = changes;
			settings.ChangeBackgroundEachDesktop.Value = !settings.ChangeBackgroundEachDesktop.Value;
			Assert.Equal(before, changes);
		}

		[Fact]
		public async Task GeometryUsesSharedSettingsAndKeepsPinOffsetsSeparate()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var monitors = new[]
			{
				new Monitor("Main",
					new System.Windows.Rect(0, 0, 1920, 1080), new System.Windows.Rect(0, 0, 1920, 1040)),
			};
			var service = CreateService(settings, provider, monitors: () => monitors);
			var changes = 0;
			using (SettingsService.ObserveNotificationAppearance(settings, () => changes++))
			{
				var response = await Run(service, "notification configure --monitor 1 --placement bottom-right "
					+ "--offset-x -10 --offset-y 20 --min-width 450 --simple-min-width 210 --min-height 130 "
					+ "--pin-min-width 310 --pin-offset-x 30 --pin-offset-y -40");
				Assert.True(response.Success);
				Assert.Equal("1", response.Data.Monitor);
				Assert.True(response.Data.MonitorAvailable);
				Assert.Equal("bottom-right", response.Data.Placement);
				var visual = NotificationVisualSettings.Capture(settings);
				Assert.Equal(1u, visual.Display);
				Assert.Equal(SylphyHorn.UI.Bindings.WindowPlacement.BottomRight, visual.Placement);
				Assert.Equal(-10, visual.OffsetX);
				Assert.Equal(20, visual.OffsetY);
				Assert.Equal(450, visual.NotificationMinWidth);
				Assert.Equal(210, visual.SimpleNotificationMinWidth);
				Assert.Equal(130, visual.NotificationMinHeight);
				Assert.Equal(310, visual.PinWindowMinWidth);
				Assert.Equal(30, visual.PinOffsetX);
				Assert.Equal(-40, visual.PinOffsetY);
				Assert.True(changes > 0);
				var observed = changes;
				Assert.False((await Run(service, "notification configure --pin-offset-x 30")).Data.Changed);
				Assert.Equal(observed, changes);
				Assert.NotEmpty(provider.SavedDictionaries);
			}
		}

		[Fact]
		public async Task MissingMonitorRejectsAllChangesAndDisconnectedPreferenceRemainsReadable()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var monitors = new[]
			{
				new Monitor("Left",
					new System.Windows.Rect(-1920, 0, 1920, 1080), new System.Windows.Rect(-1920, 0, 1920, 1040)),
			};
			var service = CreateService(settings, provider, monitors: () => monitors);
			var list = await Run(service, "monitor list");
			var monitor = Assert.Single(list.Data.Monitors);
			Assert.Equal(1, monitor.Number);
			Assert.Equal("Left", monitor.Name);
			Assert.Equal(-1920, monitor.Bounds.X);
			Assert.Equal(1080, monitor.Bounds.Height);
			Assert.Equal(1040, monitor.WorkArea.Height);
			Assert.Equal("monitor_unavailable", (await Run(service, "notification configure --simple true --monitor 2")).Error.Code);
			Assert.False(settings.SimpleNotification.Value);
			Assert.Empty(provider.SavedDictionaries);
			Assert.True((await Run(service, "notification configure --monitor 1")).Success);
			monitors = Array.Empty<Monitor>();
			var saved = await Run(service, "notification settings");
			Assert.Equal("1", saved.Data.Monitor);
			Assert.False(saved.Data.MonitorAvailable);
			Assert.Equal(1u, settings.Display.Value);
			Assert.True((await Run(service, "notification configure --monitor current")).Success);
			Assert.Equal(0u, settings.Display.Value);
			Assert.True((await Run(service, "notification configure --monitor all")).Success);
			Assert.Equal(uint.MaxValue, settings.Display.Value);
		}

		[Theory]
		[InlineData("--monitor 0")]
		[InlineData("--monitor 4294967295")]
		[InlineData("--monitor main")]
		[InlineData("--placement middle")]
		[InlineData("--min-width 0")]
		[InlineData("--pin-min-width -1")]
		[InlineData("--offset-y 2147483648")]
		public void InvalidGeometryIsRejectedBeforeExecution(string options)
		{
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(("notification configure " + options).Split(' ')));
		}

		[Fact]
		public async Task AppearanceSettingsReachNotificationSnapshotsAndRepeatedRequestsDoNotNotify()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider);
			Assert.True((await Run(service, "notification configure --theme " + name)).Success);
			Assert.Equal(GuiEnumValue("BlurWindowThemeMode", value), settings.NotificationWindowStyle.Value);
			Assert.Equal(name, (await Run(service, "notification settings")).Data.Theme);
		}

		[Fact]
		public async Task FontResetPreservesDefaultsAndInvalidRenderingSizesDoNotPartiallyApply()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var inputChanges = 0;
			var trayChanges = 0;
			bool? visible = null;
			var service = CreateService(settings, provider);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var service = CreateService(settings, provider);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var changes = 0;
			using (SettingsService.BindGeneralSettings(settings, () => changes++, _ => { }, () => { }))
			{
				var service = CreateService(settings, provider);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var available = false;
			var service = CreateService(settings, provider, available: () => available);
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
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var pending = new TaskCompletionSource<SettingsSaveResult>();
			var service = CreateService(settings, save: () => pending.Task);
			var request = Run(service, "desktop configure --loop true");
			settings.LoopDesktop.Value = false;
			pending.SetResult(await provider.SaveWithResultAsync());
			Assert.Equal("state_changed", (await request).Error.Code);
		}

		private static async Task<TestDictionaryProvider> CreateProviderAsync()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			return provider;
		}

		private static CliSettingsService CreateService(GeneralSettings settings, TestDictionaryProvider provider,
			Func<bool> available = null, Func<Monitor[]> monitors = null, bool? nativeWallpaperSupported = null, bool? nameSupported = null)
			=> CreateService(settings, () => provider.SaveWithResultAsync(), available, monitors, nativeWallpaperSupported, nameSupported);

		private static CliSettingsService CreateService(GeneralSettings settings, Func<Task<SettingsSaveResult>> save,
			Func<bool> available = null, Func<Monitor[]> monitors = null, bool? nativeWallpaperSupported = null, bool? nameSupported = null)
			=> new CliSettingsService(settings, save, available ?? (() => true), startupCulture: null,
				monitors: monitors ?? (() => Array.Empty<Monitor>()),
				nativeWallpaperSupported: nativeWallpaperSupported, nameSupported: nameSupported);

		private static uint GuiEnumValue(string type, string name)
			=> Convert.ToUInt32(Enum.Parse(
				typeof(SylphyHorn.UI.NotificationWindow).BaseType.Assembly.GetType("MetroRadiance.UI.Controls." + type, true), name));

		private static Task<CliResponse> Run(CliSettingsService service, string args)
			=> service.ExecuteAsync(CliCommand.Parse(args.Split(' ')), CancellationToken.None);
	}
}
#endif
