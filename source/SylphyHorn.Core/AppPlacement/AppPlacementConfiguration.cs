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

		[DataMember(Name = "Rules", Order = 1, IsRequired = true)]
		private AppPlacementRule[] _rules;
		private ReadOnlyCollection<AppPlacementRule> _view;
		private Dictionary<PlacementAppIdentity, AppPlacementRule> _enabledRules;

		public static AppPlacementConfiguration Empty { get; } = new AppPlacementConfiguration(false, Array.Empty<AppPlacementRule>());

		public IReadOnlyList<AppPlacementRule> Rules => this._view;

		public AppPlacementConfiguration(bool enabled, IEnumerable<AppPlacementRule> rules)
		{
			this.Enabled = enabled;
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
