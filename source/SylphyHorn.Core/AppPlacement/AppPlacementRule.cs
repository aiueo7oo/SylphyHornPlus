using System;
using System.IO;
using System.Runtime.Serialization;

namespace SylphyHorn.AppPlacement
{
	[DataContract]
	public enum PlacementAppKind
	{
		[EnumMember]
		ExecutablePath,
		[EnumMember]
		PackageAppId,
	}

	[DataContract]
	public sealed class PlacementAppIdentity : IEquatable<PlacementAppIdentity>
	{
		[DataMember(Order = 0, IsRequired = true)]
		public PlacementAppKind Kind { get; private set; }

		[DataMember(Order = 1, IsRequired = true)]
		public string Value { get; private set; }

		public PlacementAppIdentity(PlacementAppKind kind, string value)
		{
			this.Kind = kind;
			this.Value = value;
			this.Validate();
		}

		private void Validate()
		{
			if (string.IsNullOrWhiteSpace(this.Value))
			{
				throw new SerializationException("An application identity is required.");
			}
			switch (this.Kind)
			{
				case PlacementAppKind.ExecutablePath:
					// Reject drive-relative and root-relative paths; never resolve against the working directory.
					var path = this.Value.Replace('/', '\\');
					var drivePath = path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';
					var uncPath = path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?", StringComparison.Ordinal) && !path.StartsWith(@"\\.", StringComparison.Ordinal);
					if ((!drivePath && !uncPath) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '*', '?' }) >= 0)
					{
						throw new SerializationException("An absolute executable path is required.");
					}
					try
					{
						this.Value = Path.GetFullPath(path);
					}
					catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
					{
						throw new SerializationException("Invalid executable path.", ex);
					}
					if (!string.Equals(Path.GetExtension(this.Value), ".exe", StringComparison.OrdinalIgnoreCase))
					{
						throw new SerializationException("The application path must identify an executable.");
					}
					break;
				case PlacementAppKind.PackageAppId:
					var separator = this.Value.IndexOf('!');
					if (separator <= 0 || separator == this.Value.Length - 1 || separator != this.Value.LastIndexOf('!'))
					{
						throw new SerializationException("A package family and application ID are required.");
					}
					break;
				default:
					throw new SerializationException("Unknown application identity kind.");
			}
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Validate();

		private StringComparer Comparer => this.Kind == PlacementAppKind.ExecutablePath ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

		public bool Equals(PlacementAppIdentity other) => other != null && this.Kind == other.Kind && this.Comparer.Equals(this.Value, other.Value);

		public override bool Equals(object obj) => this.Equals(obj as PlacementAppIdentity);

		public override int GetHashCode() => ((int)this.Kind * 397) ^ this.Comparer.GetHashCode(this.Value);
	}

	[DataContract]
	public enum PlacementDestinationKind
	{
		[EnumMember]
		Name,
		[EnumMember]
		Number,
	}

	[DataContract]
	public sealed class PlacementDestination
	{
		[DataMember(Order = 0, IsRequired = true)]
		public PlacementDestinationKind Kind { get; private set; }

		[DataMember(Order = 1)]
		public string Name { get; private set; }

		[DataMember(Order = 2)]
		public int Number { get; private set; }

		private PlacementDestination(PlacementDestinationKind kind, string name, int number)
		{
			this.Kind = kind;
			this.Name = name;
			this.Number = number;
			this.Validate();
		}

		public static PlacementDestination ByName(string name) => new PlacementDestination(PlacementDestinationKind.Name, name, 0);

		public static PlacementDestination ByNumber(int number) => new PlacementDestination(PlacementDestinationKind.Number, null, number);

		private void Validate()
		{
			if (this.Kind == PlacementDestinationKind.Name && !string.IsNullOrWhiteSpace(this.Name) && this.Number == 0)
			{
				return;
			}
			if (this.Kind == PlacementDestinationKind.Number && this.Number > 0 && this.Name == null) return;
			throw new SerializationException("Specify a nonempty desktop name or a positive desktop number.");
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Validate();
	}

	[DataContract]
	public sealed class AppPlacementRule
	{
		[DataMember(Order = 0, IsRequired = true)]
		public Guid Id { get; private set; }

		[DataMember(Order = 1, IsRequired = true)]
		public bool Enabled { get; private set; }

		[DataMember(Order = 2, IsRequired = true)]
		public PlacementAppIdentity App { get; private set; }

		[DataMember(Order = 3, IsRequired = true)]
		public PlacementDestination Destination { get; private set; }

		[DataMember(Order = 4)]
		public string DisplayName { get; private set; }

		[DataMember(Order = 5)]
		public string DisplayExecutablePath { get; private set; }

		[DataMember(Order = 6, EmitDefaultValue = false)]
		public bool? FollowForeground { get; private set; }

		public AppPlacementRule(
			Guid id,
			bool enabled,
			PlacementAppIdentity app,
			PlacementDestination destination,
			string displayName = null,
			string displayExecutablePath = null,
			bool? followForeground = null)
		{
			this.Id = id;
			this.Enabled = enabled;
			this.App = app;
			this.Destination = destination;
			this.DisplayName = displayName;
			this.DisplayExecutablePath = displayExecutablePath;
			this.FollowForeground = followForeground;
			this.Validate();
		}

		private void Validate()
		{
			if (this.Id == Guid.Empty || this.App == null || this.Destination == null)
			{
				throw new SerializationException("A rule ID, application and destination are required.");
			}
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Validate();
	}
}
