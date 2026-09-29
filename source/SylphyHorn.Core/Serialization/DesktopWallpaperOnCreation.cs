using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace SylphyHorn.Serialization
{
	[DataContract]
	public sealed class DesktopWallpaperOnCreation
	{
		internal const string SettingsKey = GeneralSettings.DesktopWallpapersOnCreationKey;

		[DataMember(Order = 0, EmitDefaultValue = false)]
		public string Name { get; private set; }

		[DataMember(Order = 1, EmitDefaultValue = false)]
		public int? Number { get; private set; }

		[DataMember(Order = 2, IsRequired = true)]
		public string WallpaperPath { get; private set; }

		public DesktopWallpaperOnCreation(string name, int? number, string wallpaperPath)
		{
			this.Name = name;
			this.Number = number;
			this.WallpaperPath = wallpaperPath;
			this.Validate();
		}

		private void Validate()
		{
			var hasName = this.Name != null;
			var identifiesOneDesktop = hasName != this.Number.HasValue;
			if (!identifiesOneDesktop || (hasName && string.IsNullOrWhiteSpace(this.Name))
				|| (this.Number.HasValue && this.Number.Value < 1))
			{
				throw new SerializationException("Specify a desktop name or a positive number.");
			}
			if (string.IsNullOrWhiteSpace(this.WallpaperPath) || !Path.IsPathRooted(this.WallpaperPath)
				|| Path.GetPathRoot(this.WallpaperPath).Length < 3 || this.WallpaperPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
			{
				throw new SerializationException("Specify an absolute wallpaper path.");
			}
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Validate();

		internal static void ValidateEntry(string key, object value)
		{
			if (key != GeneralSettings.DesktopWallpapersOnCreationKey) return;
			if (!(value is DesktopWallpaperOnCreation[] entries) || entries.Any(item => item == null)
				|| entries.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Key != null && group.Count() > 1)
				|| entries.GroupBy(item => item.Number).Any(group => group.Key.HasValue && group.Count() > 1))
			{
				throw new SerializationException("Invalid or duplicate creation wallpaper settings.");
			}
		}
	}
}
