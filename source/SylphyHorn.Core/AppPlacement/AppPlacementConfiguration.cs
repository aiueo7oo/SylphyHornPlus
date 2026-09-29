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
		// One placement creates at most this many missing desktops. A numbered destination needing more is left
		// unavailable, so that a mistyped number cannot fill the desktop list.
		public const int MaximumCreatedDesktops = 10;

		[DataMember(Order = 0, IsRequired = true)]
		public bool Enabled { get; private set; }

		[DataMember(Name = "Rules", Order = 1, IsRequired = true)]
		private AppPlacementRule[] _rules;
		private ReadOnlyCollection<AppPlacementRule> _rulesView;
		private Dictionary<PlacementAppIdentity, AppPlacementRule> _enabledRules;

		[DataMember(Order = 2, EmitDefaultValue = false)]
		public bool CreateMissingDesktops { get; private set; }

		[DataMember(Order = 3, EmitDefaultValue = false)]
		public bool CloseCreatedDesktops { get; private set; }

		[DataMember(Name = "ClosingTargets", Order = 4, EmitDefaultValue = false)]
		private PlacementDestination[] _closingTargets;
		private ReadOnlyCollection<PlacementDestination> _closingView;

		[DataMember(Order = 5)]
		public bool FollowForeground { get; private set; }

		public static AppPlacementConfiguration Empty { get; } = new AppPlacementConfiguration(false, Array.Empty<AppPlacementRule>());

		public IReadOnlyList<AppPlacementRule> Rules => this._rulesView;

		public IReadOnlyList<PlacementDestination> ClosingTargets => this._closingView;

		// Whether empty desktops can be closed automatically, either desktops created by placement or listed targets.
		public bool ClosesDesktops => this.CloseCreatedDesktops || this._closingTargets.Length != 0;

		public AppPlacementConfiguration(bool enabled, IEnumerable<AppPlacementRule> rules, bool createMissingDesktops = false,
			bool closeCreatedDesktops = false, IEnumerable<PlacementDestination> closingTargets = null, bool followForeground = true)
		{
			this.Enabled = enabled;
			this.FollowForeground = followForeground;
			this.CreateMissingDesktops = createMissingDesktops;
			this.CloseCreatedDesktops = closeCreatedDesktops;
			this._closingTargets = closingTargets?.ToArray();
			this._rules = rules?.ToArray() ?? throw new ArgumentNullException(nameof(rules));
			this.Initialize();
		}

		public AppPlacementConfiguration WithRules(IEnumerable<AppPlacementRule> rules)
			=> new AppPlacementConfiguration(this.Enabled, rules, this.CreateMissingDesktops, this.CloseCreatedDesktops,
				this.ClosingTargets, this.FollowForeground);

		public AppPlacementConfiguration WithClosingTargets(IEnumerable<PlacementDestination> closingTargets)
			=> new AppPlacementConfiguration(this.Enabled, this.Rules, this.CreateMissingDesktops, this.CloseCreatedDesktops,
				closingTargets, this.FollowForeground);

		public AppPlacementConfiguration WithOptions(bool enabled, bool createMissingDesktops, bool closeCreatedDesktops, bool followForeground)
			=> new AppPlacementConfiguration(enabled, this.Rules, createMissingDesktops, closeCreatedDesktops,
				this.ClosingTargets, followForeground);

		public AppPlacementRule FindEnabledRule(PlacementAppIdentity app)
		{
			if (app == null) throw new ArgumentNullException(nameof(app));
			return this.Enabled && this._enabledRules.TryGetValue(app, out var rule) ? rule : null;
		}

		private void Initialize()
		{
			this._closingTargets = this._closingTargets ?? Array.Empty<PlacementDestination>();
			if (this._closingTargets.Any(target => target == null))
			{
				throw new SerializationException("Null closing targets are not allowed.");
			}
			this._closingView = Array.AsReadOnly(this._closingTargets);
			if (this._rules == null)
			{
				throw new SerializationException("A rule list is required.");
			}
			var ids = new HashSet<Guid>();
			var index = new Dictionary<PlacementAppIdentity, AppPlacementRule>();
			foreach (var rule in this._rules)
			{
				if (rule == null || !ids.Add(rule.Id))
				{
					throw new SerializationException("Null rules and duplicate rule IDs are not allowed.");
				}
				if (!rule.Enabled) continue;
				if (index.ContainsKey(rule.App))
				{
					throw new SerializationException("An application has multiple enabled placement rules.");
				}
				index.Add(rule.App, rule);
			}
			this._rulesView = Array.AsReadOnly(this._rules);
			this._enabledRules = index;
		}

		[OnDeserializing]
		private void OnDeserializing(StreamingContext context) => this.FollowForeground = true;

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context) => this.Initialize();
	}
}
