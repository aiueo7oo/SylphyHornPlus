using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;

namespace SylphyHorn.AppPlacement
{
	[DataContract]
	public sealed class PlacementCreatedGroup
	{
		[DataMember(Name = "Desktops", Order = 0, IsRequired = true)]
		private Guid[] _desktops;
		private ReadOnlyCollection<Guid> _view;

		[DataMember(Order = 1)]
		public bool Used { get; private set; }

		public IReadOnlyList<Guid> Desktops => this._view;

		public PlacementCreatedGroup(IEnumerable<Guid> desktops, bool used)
		{
			this._desktops = desktops?.ToArray() ?? throw new ArgumentNullException(nameof(desktops));
			this.Used = used;
			this.Initialize();
		}

		private void Initialize()
		{
			if (this._desktops == null || this._desktops.Any(id => id == Guid.Empty)
				|| this._desktops.Distinct().Count() != this._desktops.Length)
			{
				throw new SerializationException("Created desktops must have unique, nonempty IDs.");
			}
			this._view = Array.AsReadOnly(this._desktops);
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Initialize();
	}
}
