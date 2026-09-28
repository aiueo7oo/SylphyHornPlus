using System;
using System.Runtime.Serialization;
using MetroTrilithon.Serialization;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Serialization
{
	public sealed class AppPlacementSettings : SettingsHost
	{
		internal const string ConfigurationKey = "AppPlacementSettings.Configuration";
		internal const string CreatedDesktopGroupsKey = "AppPlacementSettings.CreatedDesktopGroups";
		private readonly ISerializationProvider _provider;

		public AppPlacementSettings(ISerializationProvider provider)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public SerializableProperty<AppPlacementConfiguration> Configuration => this.Cache(key => new SerializableProperty<AppPlacementConfiguration>(key, this._provider, AppPlacementConfiguration.Empty));

		public SerializableProperty<PlacementCreatedGroup[]> CreatedDesktopGroups => this.Cache(key => new SerializableProperty<PlacementCreatedGroup[]>(key, this._provider, Array.Empty<PlacementCreatedGroup>()));

		internal static void ValidateEntry(string key, object value)
		{
			if (key == CreatedDesktopGroupsKey && (!(value is PlacementCreatedGroup[] groups) || Array.Exists(groups, group => group == null)))
			{
				throw new SerializationException("Invalid created desktop groups.");
			}
			if (key == ConfigurationKey && !(value is AppPlacementConfiguration))
			{
				throw new SerializationException("Invalid app placement configuration.");
			}
		}

	}
}
