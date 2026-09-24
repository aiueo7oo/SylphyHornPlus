using System;
using System.Collections.Generic;
using System.Linq;
using SylphyHorn.Serialization;
using SylphyHorn.Services.DesktopTransitions;

namespace SylphyHorn.Services
{
	internal static class SettingsService
	{
		internal const string DesktopNamesKey = "GeneralSettings.DesktopNames";
		internal const string DesktopWallpaperPathsKey = "GeneralSettings.DesktopBackgroundImagePaths";
		internal const string DesktopPositionsKey = "GeneralSettings.DesktopBackgroundPositions";

		internal static IDisposable BindGeneralSettings(GeneralSettings settings, Action reloadInput, Action<bool> showNotification, Action reloadTray)
		{
			var subscriptions = new SylphyHorn.Lifetime.DisposableCollection();
			subscriptions.Add(ObserveChange(settings.LoopDesktop, _ => reloadInput()));
			subscriptions.Add(ObserveChange(settings.OverrideWindowsDefaultKeyCombination, _ => reloadInput()));
			subscriptions.Add(ObserveChange(settings.AlwaysShowDesktopNotification, showNotification));
			subscriptions.Add(ObserveChange(settings.TrayShowDesktop, _ => reloadTray()));
			subscriptions.Add(ObserveChange(settings.TrayShowOnlyCurrentNumber, _ => reloadTray()));
			return subscriptions;
		}

		internal static IDisposable ObserveNotificationAppearance(GeneralSettings settings, Action changed)
		{
			var subscriptions = new SylphyHorn.Lifetime.DisposableCollection();
			subscriptions.Add(ObserveChange(settings.Display, _ => changed()));
			subscriptions.Add(ObserveChange(settings.Placement, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationOffsetX, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationOffsetY, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationMinWidth, _ => changed()));
			subscriptions.Add(ObserveChange(settings.SimpleNotificationMinWidth, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationMinHeight, _ => changed()));
			subscriptions.Add(ObserveChange(settings.PinWindowMinWidth, _ => changed()));
			subscriptions.Add(ObserveChange(settings.PinWindowOffsetX, _ => changed()));
			subscriptions.Add(ObserveChange(settings.PinWindowOffsetY, _ => changed()));
			subscriptions.Add(ObserveChange(settings.SimpleNotification, _ => changed()));
			subscriptions.Add(ObserveChange(settings.UseDesktopName, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationWindowStyle, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationCornerStyle, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationFontFamily, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationHeaderFontSize, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationBodyFontSize, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationHeaderAlignment, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationBodyAlignment, _ => changed()));
			subscriptions.Add(ObserveChange(settings.NotificationLineSpacing, _ => changed()));
			return subscriptions;
		}

		private static IDisposable ObserveChange<T>(SerializableProperty<T> property, Action<T> changed)
		{
			var previous = property.Value;
			return property.Subscribe(value =>
			{
				if (EqualityComparer<T>.Default.Equals(previous, value)) return;
				previous = value;
				changed(value);
			});
		}

		internal static DesktopStartupSeed CaptureDesktopStartupSeed()
		{
			var general = Settings.General;
			return new DesktopStartupSeed(
				general.DesktopNames.Value.Select(item => item.Value),
				general.DesktopBackgroundImagePaths.Value.Select(item => item.Value),
				general.DesktopBackgroundPositions.Value.Select(item => (WallpaperPosition)item.Value));
		}

		internal static DesktopStartupSeed CaptureDesktopStartupSeed(IReadOnlyDictionary<string, object> dictionary)
		{
			if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
			return new DesktopStartupSeed(
				ReadList<string>(dictionary, DesktopNamesKey),
				ReadList<string>(dictionary, DesktopWallpaperPathsKey),
				ReadList<byte>(dictionary, DesktopPositionsKey).Select(value => (WallpaperPosition)value));
		}
		internal static void ApplyDesktopProjection(DesktopSettingsProjection projection)
		{
			if (projection == null) throw new ArgumentNullException(nameof(projection));
			var count = projection.Names.Count;
			if (projection.WallpaperPaths.Count != count || projection.Positions.Count != count)
				throw new ArgumentException("Desktop settings projection lists must have the same count.", nameof(projection));

			var general = Settings.General;
			general.DesktopNames.Resize(count);
			general.DesktopBackgroundImagePaths.Resize(count);
			general.DesktopBackgroundPositions.Resize(count);
			for (var index = 0; index < count; index++)
			{
				general.DesktopNames.Value[index].Value = projection.Names[index];
				general.DesktopBackgroundImagePaths.Value[index].Value = projection.WallpaperPaths[index];
				general.DesktopBackgroundPositions.Value[index].Value = (byte)projection.Positions[index];
			}
			ResizeShortcutListsIfEmpty(count);
		}

		internal static void ApplyDesktopProjection(IDictionary<string, object> dictionary, DesktopSettingsProjection projection)
		{
			if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
			if (projection == null) throw new ArgumentNullException(nameof(projection));
			var count = projection.Names.Count;
			if (projection.WallpaperPaths.Count != count || projection.Positions.Count != count)
				throw new ArgumentException("Desktop settings projection lists must have the same count.", nameof(projection));
			WriteList(dictionary, DesktopNamesKey, projection.Names.Cast<object>().ToArray());
			WriteList(dictionary, DesktopWallpaperPathsKey, projection.WallpaperPaths.Cast<object>().ToArray());
			WriteList(dictionary, DesktopPositionsKey, projection.Positions.Select(value => (object)(byte)value).ToArray());
		}
		internal static void StretchShortcutListsTo(int count)
		{
			Settings.ShortcutKey.SwitchToIndices.StretchTo(count);
			Settings.ShortcutKey.MoveToIndices.StretchTo(count);
			Settings.ShortcutKey.MoveToIndicesAndSwitch.StretchTo(count);
			Settings.MouseShortcut.SwitchToIndices.StretchTo(count);
			Settings.MouseShortcut.MoveToIndices.StretchTo(count);
			Settings.MouseShortcut.MoveToIndicesAndSwitch.StretchTo(count);
			if (Properties.ProductInfo.IsReorderingSupportBuild)
			{
				Settings.ShortcutKey.SwapDesktopIndices.StretchTo(count);
				Settings.MouseShortcut.SwapDesktopIndices.StretchTo(count);
			}
			else
			{
				Settings.ShortcutKey.SwapDesktopIndices.Resize(count);
				Settings.MouseShortcut.SwapDesktopIndices.Resize(count);
			}
		}

		private static void ResizeShortcutListsIfEmpty(int count)
		{
			Settings.ShortcutKey.SwitchToIndices.ResizeIfEmpty(count);
			Settings.ShortcutKey.MoveToIndices.ResizeIfEmpty(count);
			Settings.ShortcutKey.MoveToIndicesAndSwitch.ResizeIfEmpty(count);
			Settings.ShortcutKey.SwapDesktopIndices.ResizeIfEmpty(count);
			Settings.MouseShortcut.SwitchToIndices.ResizeIfEmpty(count);
			Settings.MouseShortcut.MoveToIndices.ResizeIfEmpty(count);
			Settings.MouseShortcut.MoveToIndicesAndSwitch.ResizeIfEmpty(count);
			Settings.MouseShortcut.SwapDesktopIndices.ResizeIfEmpty(count);
		}

		private static IReadOnlyList<T> ReadList<T>(IReadOnlyDictionary<string, object> dictionary, string key)
		{
			var count = dictionary.TryGetValue(key + "#Count", out var countValue) && countValue is int storedCount ? Math.Max(0, storedCount) : 0;
			var result = new List<T>(count);
			for (var index = 0; index < count; index++)
			{
				if (dictionary.TryGetValue(key + "[" + index + "]", out var value) && value is T typed) result.Add(typed);
				else result.Add(default(T));
			}
			return result;
		}

		private static void WriteList(IDictionary<string, object> dictionary, string key, IReadOnlyList<object> values)
		{
			var prefix = key + "[";
			foreach (var existing in dictionary.Keys.Where(existing => existing.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) dictionary.Remove(existing);
			dictionary[key + "#Count"] = values.Count;
			for (var index = 0; index < values.Count; index++) dictionary[key + "[" + index + "]"] = values[index];
		}
	}
}
