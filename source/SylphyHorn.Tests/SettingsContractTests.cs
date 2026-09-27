using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SylphyHorn.Serialization;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class SettingsContractTests
	{
		[Fact]
		public void ConcurrentHostRegistrationPreservesTypedLookupAndLastRegistration()
		{
			Parallel.For(0, 2000, index =>
			{
				new RegistryHost<int>();
				new RegistryHost<string>();
				Assert.NotNull(SettingsHost.Instance<RegistryHost<int>>());
				Assert.NotNull(SettingsHost.Instance<RegistryHost<string>>());
			});
			var lastInteger = new RegistryHost<int>();
			var lastString = new RegistryHost<string>();
			Assert.Same(lastInteger, SettingsHost.Instance<RegistryHost<int>>());
			Assert.Same(lastString, SettingsHost.Instance<RegistryHost<string>>());
			Assert.Null(SettingsHost.Instance<RegistryHost<Guid>>());
		}

		[Fact]
		public async Task DesktopSettingsAdapterReadsSharedUpdatesWithoutAddingSubscriptions()
		{
			var provider = await CreateProviderAsync();
			var general = new GeneralSettings(provider);
			var placement = new AppPlacementSettings(provider);
			var first = new DesktopWallpaperOnCreation("Work", null, @"C:\Images\first.bmp");
			var second = new DesktopWallpaperOnCreation(null, 2, @"C:\Images\second.bmp");
			general.DesktopWallpapersOnCreation.Value = new[] { first };
			general.ChangeBackgroundEachDesktop.Value = true;
			placement.CreatedDesktopGroups.Value = new[] { new AppPlacement.PlacementCreatedGroup(new[] { Guid.NewGuid() }, false) };
			var adapter = new Services.DesktopTransitions.ApplicationDesktopSettingsTransactions(provider, general, placement);
			Assert.Same(first, Assert.Single(adapter.ReadWallpapersOnCreation()));
			Assert.True(adapter.PerDesktopWallpaperEnabled);
			Assert.Single(adapter.ReadCreatedDesktopGroups());
			var reloaded = typeof(DictionaryProvider).GetField("Reloaded", BindingFlags.Instance | BindingFlags.NonPublic);
			var subscriptions = ((Delegate)reloaded.GetValue(provider)).GetInvocationList().Length;

			general.DesktopWallpapersOnCreation.Value = new[] { first, second };
			Assert.Equal(2, adapter.ReadWallpapersOnCreation().Length);
			general.DesktopWallpapersOnCreation.Value = new[] { second };
			Assert.Same(second, Assert.Single(adapter.ReadWallpapersOnCreation()));
			general.DesktopWallpapersOnCreation.Value = Array.Empty<DesktopWallpaperOnCreation>();
			general.ChangeBackgroundEachDesktop.Value = false;
			placement.CreatedDesktopGroups.Value = Array.Empty<AppPlacement.PlacementCreatedGroup>();
			for (var index = 0; index < 20; index++)
			{
				Assert.Empty(adapter.ReadWallpapersOnCreation());
				Assert.Empty(adapter.ReadCreatedDesktopGroups());
				Assert.False(adapter.PerDesktopWallpaperEnabled);
			}
			var groups = new[] { new AppPlacement.PlacementCreatedGroup(new[] { Guid.NewGuid() }, true) };
			adapter.WriteCreatedDesktopGroups(groups);
			Assert.Same(groups[0], Assert.Single(placement.CreatedDesktopGroups.Value));
			Assert.Equal(subscriptions, ((Delegate)reloaded.GetValue(provider)).GetInvocationList().Length);
			provider.Clear();
			Assert.Empty(adapter.ReadCreatedDesktopGroups());
			Assert.Empty(adapter.ReadWallpapersOnCreation());
		}

		private sealed class RegistryHost<T> : SettingsHost { }

		[Fact]
		public async Task GeneralSettingsSchemaKeysTypesAndDefaultsRemainStable()
		{
			var provider = await CreateProviderAsync();
			var settings = new GeneralSettings(provider);
			var expected = new[]
			{
				Scalar("LoopDesktop", false),
				Scalar("NotificationWhenSwitchedDesktop", true),
				Scalar("AlwaysShowDesktopNotification", false),
				Scalar("SimpleNotification", false),
				Scalar("NotificationDuration", 2500),
				Scalar("ChangeBackgroundEachDesktop", false),
				Scalar("DesktopWallpapersOnCreation", Array.Empty<DesktopWallpaperOnCreation>()),
				Scalar<string>("DesktopBackgroundFolderPath", null),
				Scalar("OverrideWindowsDefaultKeyCombination", false),
				Scalar("SuspendKeyDetection", false),
				Scalar("FirstTime", true),
				Scalar<string>("Culture", null),
				Scalar("Placement", 5U),
				Scalar("Display", 0U),
				Scalar("NotificationWindowStyle", 4U),
				Scalar("NotificationCornerStyle", 1U),
				Scalar("NotificationHeaderAlignment", 0U),
				Scalar("NotificationBodyAlignment", 0U),
				Scalar<string>("NotificationFontFamily", null),
				Scalar("NotificationHeaderFontSize", 18),
				Scalar("NotificationBodyFontSize", 32),
				Scalar("NotificationLineSpacing", -4),
				Scalar("NotificationMinWidth", 500),
				Scalar("SimpleNotificationMinWidth", 210),
				Scalar("PinWindowMinWidth", 400),
				Scalar("NotificationMinHeight", 100),
				Scalar("NotificationOffsetX", 0),
				Scalar("NotificationOffsetY", 0),
				Scalar("PinWindowOffsetX", 0),
				Scalar("PinWindowOffsetY", 0),
				Scalar("TrayShowDesktop", false),
				Scalar("TrayShowOnlyCurrentNumber", false),
				Scalar("UseDesktopName", false),
				Scalar("OverrideDesktopsOnStartup", false),
				List("DesktopNames", typeof(DesktopNamePropertyList)),
				List("DesktopBackgroundImagePaths", typeof(WallpaperPathPropertyList)),
				List("DesktopBackgroundPositions", typeof(WallpaperPositionsPropertyList)),
			};

			AssertSettingsContract(settings, expected);
		}

		[Fact]
		public async Task ShortcutSettingsSchemaKeysAndDefaultsRemainStable()
		{
			var provider = await CreateProviderAsync();
			var settings = new ShortcutKeySettings(provider);
			var defaults = new Dictionary<string, int[]>(StringComparer.Ordinal)
			{
				["MoveLeftAndSwitch"] = new[] { 37, 162, 164, 91 },
				["MoveRightAndSwitch"] = new[] { 39, 162, 164, 91 },
				["MoveNewAndSwitch"] = new[] { 68, 162, 164, 91 },
				["SwitchToLeftWithDefault"] = new[] { 37, 162, 91 },
				["SwitchToRightWithDefault"] = new[] { 39, 162, 91 },
				["TogglePin"] = new[] { 80, 162, 164, 91 },
			};

			AssertShortcutContract(settings, "ShortcutKeySettings", defaults);
		}

		[Fact]
		public async Task MouseShortcutSettingsUseIndependentKeysAndRejectUnsupportedDefaults()
		{
			var provider = await CreateProviderAsync();
			var settings = new MouseShortcutSettings(provider);

			AssertShortcutContract(
				settings,
				"MouseShortcutSettings",
				new Dictionary<string, int[]>(StringComparer.Ordinal)
				{
					["SwitchToLeftWithDefault"] = new[] { 37, 162, 91 },
					["SwitchToRightWithDefault"] = new[] { 39, 162, 91 },
				});
		}

		private static void AssertSettingsContract(object settings, IReadOnlyCollection<SettingContract> expected)
		{
			var actualProperties = GetSettingProperties(settings.GetType());
			Assert.Equal(expected.Select(item => item.Name).OrderBy(name => name), actualProperties.Keys.OrderBy(name => name));

			foreach (var contract in expected)
			{
				var propertyInfo = actualProperties[contract.Name];
				Assert.Equal(contract.PropertyType, propertyInfo.PropertyType);
				var property = propertyInfo.GetValue(settings);
				Assert.Equal(settings.GetType().Name + "." + contract.Name, GetPropertyValue<string>(property, "Key"));

				if (contract.IsList)
				{
					Assert.Equal(0, GetPropertyValue<int>(property, "Count"));
					continue;
				}

				AssertValue(contract.DefaultValue, GetPropertyValue<object>(property, "Default"));
				AssertValue(contract.DefaultValue, GetPropertyValue<object>(property, "Value"));
			}
		}

		private static void AssertShortcutContract(
			ShortcutKeySettings settings,
			string category,
			IReadOnlyDictionary<string, int[]> defaults)
		{
			var expectedNames = new[]
			{
				"MoveLeft", "MoveLeftAndSwitch", "MoveRight", "MoveRightAndSwitch", "MoveNew", "MoveNewAndSwitch",
				"MoveToPrevious", "MoveToPreviousAndSwitch", "MoveToIndices", "MoveToIndicesAndSwitch",
				"SwitchToLeftWithDefault", "SwitchToRightWithDefault", "SwitchToLeft", "SwitchToRight", "SwitchToPrevious",
				"SwitchToIndices", "SwapDesktopLeft", "SwapDesktopRight", "SwapDesktopFirst", "SwapDesktopLast",
				"SwapDesktopIndices", "CloseAndSwitchLeft", "CloseAndSwitchRight", "ShowTaskView", "ShowWindowSwitch",
				"Pin", "Unpin", "TogglePin", "PinApp", "UnpinApp", "TogglePinApp", "ShowSettings",
				"ToggleDesktopNotification",
			};
			var listNames = new HashSet<string>(new[]
			{
				"MoveToIndices", "MoveToIndicesAndSwitch", "SwitchToIndices", "SwapDesktopIndices",
			}, StringComparer.Ordinal);
			var actualProperties = GetSettingProperties(settings.GetType());
			Assert.Equal(expectedNames.OrderBy(name => name), actualProperties.Keys.OrderBy(name => name));

			foreach (var name in expectedNames)
			{
				var propertyInfo = actualProperties[name];
				var isList = listNames.Contains(name);
				Assert.Equal(isList ? typeof(ShortcutkeyPropertyList) : typeof(ShortcutkeyProperty), propertyInfo.PropertyType);
				var property = propertyInfo.GetValue(settings);
				Assert.Equal(category + "." + name, GetPropertyValue<string>(property, "Key"));

				if (isList)
				{
					Assert.Equal(0, GetPropertyValue<int>(property, "Count"));
					continue;
				}

				var expectedDefault = defaults.TryGetValue(name, out var value) ? value : null;
				Assert.Equal(expectedDefault, GetPropertyValue<IList<int>>(property, "Value"));
			}
		}

		private static Dictionary<string, PropertyInfo> GetSettingProperties(Type type)
		{
			return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(property => property.GetIndexParameters().Length == 0)
				.ToDictionary(property => property.Name, StringComparer.Ordinal);
		}

		private static T GetPropertyValue<T>(object source, string name)
		{
			var value = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public).GetValue(source);
			return typeof(T) == typeof(object) ? (T)value : (T)value;
		}

		private static void AssertValue(object expected, object actual)
		{
			if (expected is int[] expectedArray)
			{
				Assert.Equal(expectedArray, Assert.IsAssignableFrom<IEnumerable<int>>(actual));
				return;
			}

			Assert.Equal(expected, actual);
		}

		private static SettingContract Scalar<T>(string name, T defaultValue)
			=> new SettingContract(name, typeof(SerializableProperty<T>), defaultValue, false);

		private static SettingContract List(string name, Type propertyType)
			=> new SettingContract(name, propertyType, null, true);

		private static async Task<MemoryDictionaryProvider> CreateProviderAsync()
		{
			var provider = new MemoryDictionaryProvider();
			await provider.InitializeAsync();
			return provider;
		}

		private sealed class SettingContract
		{
			internal SettingContract(string name, Type propertyType, object defaultValue, bool isList)
			{
				this.Name = name;
				this.PropertyType = propertyType;
				this.DefaultValue = defaultValue;
				this.IsList = isList;
			}

			internal string Name { get; }
			internal Type PropertyType { get; }
			internal object DefaultValue { get; }
			internal bool IsList { get; }
		}
	}
}
