using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using MetroTrilithon.Serialization;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Serialization
{
	public sealed class AppPlacementSettings : SettingsHost
	{
		internal const string ConfigurationKey = "AppPlacementSettings.Configuration";
		private readonly ISerializationProvider _provider;

		public AppPlacementSettings(ISerializationProvider provider)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public SerializableProperty<AppPlacementConfiguration> Configuration => this.Cache(key => new SerializableProperty<AppPlacementConfiguration>(key, this._provider, AppPlacementConfiguration.Empty));

		public SerializableProperty<PlacementCreatedGroup[]> CreatedDesktopGroups => this.Cache(key => new SerializableProperty<PlacementCreatedGroup[]>(key, this._provider, Array.Empty<PlacementCreatedGroup>()));

		internal static void ValidateEntry(string key, object value)
		{
			if (key == "AppPlacementSettings.CreatedDesktopGroups" && (!(value is PlacementCreatedGroup[] groups) || Array.Exists(groups, group => group == null)))
				throw new SerializationException("Invalid created desktop groups.");
			if (key == ConfigurationKey && !(value is AppPlacementConfiguration))
				throw new SerializationException("Invalid app placement configuration.");
		}

		internal static void ValidateDictionary(IEnumerable<KeyValuePair<string, object>> values)
		{
			if (values == null) return;
			foreach (var pair in values) ValidateEntry(pair.Key, pair.Value);
		}
	}
}
