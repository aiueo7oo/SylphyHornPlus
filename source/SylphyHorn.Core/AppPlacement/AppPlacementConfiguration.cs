using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;

namespace SylphyHorn.AppPlacement
{
	[DataContract]
	public sealed class AppPlacementConfiguration
	{
		[DataMember(Order = 0, IsRequired = true)]
		public bool Enabled { get; private set; }

		[DataMember(Order = 2, EmitDefaultValue = false)]
		public bool CreateMissingDesktops { get; private set; }

		[DataMember(Order = 3, EmitDefaultValue = false)]
		public bool CloseCreatedDesktops { get; private set; }

		[DataMember(Name = "ClosingTargets", Order = 4, EmitDefaultValue = false)]
		private PlacementDestination[] _closingTargets;
		private ReadOnlyCollection<PlacementDestination> _closingView;

		public IReadOnlyList<PlacementDestination> ClosingTargets => this._closingView;

		public bool HasClosingTargets => this.CloseCreatedDesktops || this._closingTargets.Length != 0;

		[DataMember(Name = "Rules", Order = 1, IsRequired = true)]
		private AppPlacementRule[] _rules;
		private ReadOnlyCollection<AppPlacementRule> _view;
		private Dictionary<PlacementAppIdentity, AppPlacementRule> _enabledRules;

		public static AppPlacementConfiguration Empty { get; } = new AppPlacementConfiguration(false, Array.Empty<AppPlacementRule>());

		public IReadOnlyList<AppPlacementRule> Rules => this._view;

		public AppPlacementConfiguration(bool enabled, IEnumerable<AppPlacementRule> rules, bool createMissingDesktops = false,
			bool closeCreatedDesktops = false, IEnumerable<PlacementDestination> closingTargets = null)
		{
			this.Enabled = enabled;
			this.CreateMissingDesktops = createMissingDesktops;
			this.CloseCreatedDesktops = closeCreatedDesktops;
			this._closingTargets = closingTargets?.ToArray();
			this._rules = rules?.ToArray() ?? throw new ArgumentNullException(nameof(rules));
			this.Initialize();
		}

		public AppPlacementRule FindEnabledRule(PlacementAppIdentity app)
		{
			if (app == null) throw new ArgumentNullException(nameof(app));
			return this.Enabled && this._enabledRules.TryGetValue(app, out var rule) ? rule : null;
		}

		private void Initialize()
		{
			this._closingTargets = this._closingTargets ?? Array.Empty<PlacementDestination>();
			if (this._closingTargets.Any(target => target == null)) throw new SerializationException("Null closing targets are not allowed.");
			this._closingView = Array.AsReadOnly(this._closingTargets);
			if (this._rules == null) throw new SerializationException("A rule list is required.");
			var ids = new HashSet<Guid>();
			var index = new Dictionary<PlacementAppIdentity, AppPlacementRule>();
			foreach (var rule in this._rules)
			{
				if (rule == null || !ids.Add(rule.Id)) throw new SerializationException("Null rules and duplicate rule IDs are not allowed.");
				if (!rule.Enabled) continue;
				if (index.ContainsKey(rule.App)) throw new SerializationException("An application has multiple enabled placement rules.");
				index.Add(rule.App, rule);
			}
			this._view = Array.AsReadOnly(this._rules);
			this._enabledRules = index;
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Initialize();
	}
}
