using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SylphyHorn.AppPlacement;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.DesktopTransitions;

namespace SylphyHorn.UI.Bindings
{
	public sealed class PlacementText
	{
		public string this[string key] => Resources.ResourceManager.GetString("Placement_" + key, Resources.Culture) ?? key;
	}

	// DesktopTransitionRuntime.PlacementStatus values that the settings pages act on.
	internal static class PlacementStatuses
	{
		internal const string Active = "Active";

		internal const string Paused = "Paused";
	}

	// One choice of the per-rule / global switch setting. The instances are shared by every combo box and keep
	// their identity; only Label changes with the language or the global value.
	public sealed class PlacementFollowOption : ObservableObject
	{
		private readonly AppPlacementSettingsViewModel _owner;
		private readonly bool? _assumedDefault;

		internal PlacementFollowOption(AppPlacementSettingsViewModel owner, bool? value, bool? assumedDefault = null)
		{
			this._owner = owner;
			this.Value = value;
			this._assumedDefault = assumedDefault;
		}

		internal bool? Value { get; }

		// "Default (On)" shows the global value it currently resolves to, without storing that value in the rule.
		public string Label => this.Value.HasValue
			? this._owner.Text[this.Value.Value ? "FollowYes" : "FollowNo"]
			: string.Format(CultureInfo.CurrentCulture, this._owner.Text["FollowDefaultState"],
				this._owner.Text[(this._assumedDefault ?? this._owner.FollowForeground) ? "FollowYes" : "FollowNo"]);

		// Lets a hidden width probe show exactly this option as its selected item.
		public IReadOnlyList<PlacementFollowOption> Alone => new[] { this };

		internal void Refresh() => this.OnPropertyChanged(nameof(this.Label));
	}

	public sealed class PlacementRuleGroup : ObservableObject
	{
		public IReadOnlyList<string> Choices { get; internal set; } = Array.Empty<string>();

		internal PlacementRuleGroup(AppPlacementSettingsViewModel owner, PlacementDestinationKind kind)
		{
			this.Owner = owner;
			this.Kind = kind;
		}

		public AppPlacementSettingsViewModel Owner { get; }

		internal PlacementDestinationKind Kind { get; }

		public string Title => this.Owner.Text[this.Kind == PlacementDestinationKind.Name ? "NameList" : "NumberList"];

		public string Description => this.Kind == PlacementDestinationKind.Name ? this.Owner.Text["NameMatchHint"] : "";

		public string DestinationLabel => this.Owner.Text[this.Kind == PlacementDestinationKind.Name ? "NameColumn" : "NumberColumn"];

		public ObservableCollection<PlacementRuleRow> Rows { get; } = new ObservableCollection<PlacementRuleRow>();

		internal void RefreshLanguage()
		{
			this.OnPropertyChanged(nameof(this.Title));
			this.OnPropertyChanged(nameof(this.Description));
			this.OnPropertyChanged(nameof(this.DestinationLabel));
			foreach (var row in this.Rows)
			{
				row.RefreshLanguage();
			}
		}
	}

	public sealed class PlacementRuleRow : ObservableObject
	{
		private string _appText = "", _destination = "", _errorKey = "";
		private bool _enabled = true;
		private bool? _followForeground;
		private PlacementAppChoice _choice;
		private string _choiceText;
		private PlacementAppPresence _presence;

		internal PlacementRuleRow(PlacementRuleGroup group, AppPlacementRule saved = null)
		{
			this.Group = group;
			this.Id = saved?.Id ?? Guid.NewGuid();
			this.Saved = saved;
			this.RemoveCommand = new AsyncRelayCommand(() => group.Owner.RemoveAsync(this));
			this.Restore();
		}

		public PlacementRuleGroup Group { get; }

		internal Guid Id { get; }

		internal AppPlacementRule Saved { get; private set; }

		internal long Revision { get; private set; }

		public AsyncRelayCommand RemoveCommand { get; }

		public string AppText
		{
			get => this._appText;
			set
			{
				if (this.SetProperty(ref this._appText, value))
				{
					this.Revision++;
					this.SetErrorKey("");
					this.NotifyApplication();
				}
			}
		}

		public string Destination
		{
			get => this._destination;
			set
			{
				if (this.SetProperty(ref this._destination, value))
				{
					this.Revision++;
					this.SetErrorKey("");
				}
			}
		}

		public bool Enabled
		{
			get => this._enabled;
			set
			{
				if (this.SetProperty(ref this._enabled, value))
				{
					this.Revision++;
				}
			}
		}

		// null inherits the global value; choosing "Default" must never store the value it currently resolves to.
		public bool? FollowForeground
		{
			get => this._followForeground;
			set
			{
				if (!this.SetProperty(ref this._followForeground, value)) return;
				this.Revision++;
				this.OnPropertyChanged(nameof(this.FollowOption));
			}
		}

		public PlacementFollowOption FollowOption
		{
			get => this.Group.Owner.FollowOptions.First(option => option.Value == this.FollowForeground);
			set
			{
				// A combo box reports null while its items are being replaced; that is not a user choice.
				if (value != null)
				{
					this.FollowForeground = value.Value;
				}
			}
		}

		// A saved executable that no longer exists is reported without treating the rule as unsaved.
		public string Error => !string.IsNullOrEmpty(this._errorKey) ? this.Group.Owner.Text[this._errorKey]
			: this.IsAppMissing ? this.Group.Owner.Text["InvalidPath"] : "";

		internal bool IsAppMissing => this.Choice != null && this._presence == PlacementAppPresence.Missing;

		internal void SetErrorKey(string key)
		{
			this.SetProperty(ref this._errorKey, key, nameof(this.Error));
		}

		internal void RefreshLanguage()
		{
			this.OnPropertyChanged(nameof(this.Error));
		}

		public string Name => this.Choice?.Name ?? "";

		// null shows the empty-state glyph: no path yet, an edited path not yet confirmed, or a missing executable.
		public BitmapSource Icon => this.Choice?.Icon;

		public bool IsPackage => this.Choice?.Identity?.Kind == PlacementAppKind.PackageAppId;

		internal PlacementAppChoice Choice => this.AppText == this._choiceText ? this._choice : null;

		// A new number rule starts at the first desktop; a new name rule starts empty.
		private string NewDestination => this.Group.Kind == PlacementDestinationKind.Number ? "1" : "";

		internal void Use(PlacementAppChoice choice, PlacementAppPresence presence = PlacementAppPresence.Unknown)
		{
			this._choice = choice;
			this._presence = presence;
			this.AppText = choice?.Path ?? choice?.Name ?? "";
			this._choiceText = this.AppText;
			this.Revision++;
			this.NotifyApplication();
		}

		private void NotifyApplication()
		{
			this.OnPropertyChanged(nameof(this.Name));
			this.OnPropertyChanged(nameof(this.Icon));
			this.OnPropertyChanged(nameof(this.IsPackage));
			this.OnPropertyChanged(nameof(this.Error));
		}

		internal void Accept(AppPlacementRule rule, PlacementAppChoice choice)
		{
			this.Saved = rule;
			this.Use(choice);
			this.SetErrorKey("");
		}

		internal void Restore()
		{
			var rule = this.Saved;
			// Keep the icon and presence already read for the same application instead of dropping them on Esc.
			if (rule != null && this._choice?.Identity != null && this._choice.Identity.Equals(rule.App))
			{
				this.Use(this._choice, this._presence);
			}
			else if (rule != null)
			{
				this.Use(new PlacementAppChoice(this.DisplayNameOf(rule), "", DisplayPathOf(rule), rule.App));
			}
			else
			{
				this.Use(null);
			}
			this.Destination = rule == null ? this.NewDestination : AppPlacementSettingsViewModel.FormatDestination(rule.Destination);
			this.Enabled = rule?.Enabled ?? true;
			this.FollowForeground = rule?.FollowForeground;
			this.SetErrorKey("");
		}

		// Whether committing the row with this destination would save exactly the rule already saved.
		internal bool IsUnchanged(PlacementDestination destination)
		{
			var saved = this.Saved;
			return saved != null
				&& this.Choice != null
				&& saved.Enabled == this.Enabled
				&& saved.FollowForeground == this.FollowForeground
				&& saved.App.Equals(this.Choice.Identity)
				&& saved.Destination.Kind == destination.Kind
				&& saved.Destination.Number == destination.Number
				&& saved.Destination.Name == destination.Name;
		}

		private string DisplayNameOf(AppPlacementRule rule)
		{
			if (rule.DisplayName != null) return rule.DisplayName;
			return rule.App.Kind == PlacementAppKind.ExecutablePath
				? Path.GetFileNameWithoutExtension(rule.App.Value)
				: this.Group.Owner.Text["UnknownApplication"];
		}

		private static string DisplayPathOf(AppPlacementRule rule)
		{
			if (rule.DisplayExecutablePath != null) return rule.DisplayExecutablePath;
			return rule.App.Kind == PlacementAppKind.ExecutablePath ? rule.App.Value : null;
		}
	}

	public sealed class AppPlacementSettingsViewModel : ObservableObject, IDisposable
	{
		private readonly AppPlacementSettings _settings;
		private readonly DesktopTransitionRuntime _runtime;
		private readonly IPlacementAppCatalog _catalog;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly IDisposable _subscription;
		private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
		private readonly Dictionary<PlacementRuleRow, CancellationTokenSource> _reads = new Dictionary<PlacementRuleRow, CancellationTokenSource>();
		private bool _publishing, _disposed, _saveFailed;
		private long _generation, _saveVersion;
		private string _messageKey = "";

		internal event EventHandler EditingInvalidated;

		public PlacementText Text { get; } = new PlacementText();

		internal PlacementRuleGroup NameGroup { get; }

		internal PlacementRuleGroup NumberGroup { get; }

		public IReadOnlyList<PlacementRuleGroup> Groups { get; }

		internal PlacementClosingGroup NameClosingGroup { get; }

		internal PlacementClosingGroup NumberClosingGroup { get; }

		public IReadOnlyList<PlacementClosingGroup> ClosingGroups { get; }

		public AsyncRelayCommand RetrySaveCommand { get; }

		public AsyncRelayCommand RestartCommand { get; }

		public string Message => string.IsNullOrEmpty(this._messageKey) ? "" : this.Text[this._messageKey];

		public bool SaveFailed
		{
			get => this._saveFailed;
			private set
			{
				this.SetProperty(ref this._saveFailed, value);
				this.RetrySaveCommand.NotifyCanExecuteChanged();
			}
		}

		public bool IsPaused => this._runtime.PlacementStatus == PlacementStatuses.Paused;

		public bool IsEnabled
		{
			get => this._settings.Configuration.Value.Enabled;
			set
			{
				if (!this._disposed && value != this.IsEnabled)
				{
					_ = this.PublishAsync(this.ConfigurationWith(enabled: value));
				}
			}
		}

		public bool FollowForeground
		{
			get => this._settings.Configuration.Value.FollowForeground;
			set
			{
				if (!this._disposed && value != this.FollowForeground)
				{
					_ = this.PublishAsync(this.ConfigurationWith(followForeground: value));
				}
			}
		}

		// Per-rule choices: Default (resolved from the global value), On, Off.
		public IReadOnlyList<PlacementFollowOption> FollowOptions { get; }

		// Global choices: On, Off (the same instances as in FollowOptions).
		public IReadOnlyList<PlacementFollowOption> DefaultFollowOptions { get; }

		// Every label a switch combo box can show, including "Default (Off)" while the global value is On,
		// so the view can size all switch combo boxes to the longest one.
		public IReadOnlyList<PlacementFollowOption> FollowWidthSamples { get; }

		public PlacementFollowOption DefaultFollowOption
		{
			get => this.DefaultFollowOptions[this.FollowForeground ? 0 : 1];
			set
			{
				if (value?.Value != null)
				{
					this.FollowForeground = value.Value.Value;
				}
			}
		}

		public bool CreateMissingDesktops
		{
			get => this._settings.Configuration.Value.CreateMissingDesktops;
			set
			{
				if (!this._disposed && value != this.CreateMissingDesktops)
				{
					_ = this.PublishAsync(this.ConfigurationWith(createMissing: value));
				}
			}
		}

		public bool CanConfigureCreatedDesktopClosing => this.IsEnabled && this.CreateMissingDesktops;

		public bool CloseCreatedDesktops
		{
			get => this._settings.Configuration.Value.CloseCreatedDesktops;
			set
			{
				if (!this._disposed && value != this.CloseCreatedDesktops)
				{
					_ = this.PublishAsync(this.ConfigurationWith(closeCreated: value));
				}
			}
		}

		internal AppPlacementSettingsViewModel(
			AppPlacementSettings settings,
			DesktopTransitionRuntime runtime,
			IPlacementAppCatalog catalog,
			Func<Task<SettingsSaveResult>> save)
		{
			this._settings = settings;
			this._runtime = runtime;
			this._catalog = catalog;
			this._save = save;
			this.FollowOptions = Array.AsReadOnly(new[]
			{
				new PlacementFollowOption(this, null),
				new PlacementFollowOption(this, true),
				new PlacementFollowOption(this, false)
			});
			this.DefaultFollowOptions = Array.AsReadOnly(new[] { this.FollowOptions[1], this.FollowOptions[2] });
			this.FollowWidthSamples = Array.AsReadOnly(new[]
			{
				new PlacementFollowOption(this, null, true),
				new PlacementFollowOption(this, null, false),
				new PlacementFollowOption(this, true),
				new PlacementFollowOption(this, false)
			});
			this.NameGroup = new PlacementRuleGroup(this, PlacementDestinationKind.Name);
			this.NumberGroup = new PlacementRuleGroup(this, PlacementDestinationKind.Number);
			this.Groups = Array.AsReadOnly(new[] { this.NameGroup, this.NumberGroup });
			this.NameClosingGroup = new PlacementClosingGroup(this, PlacementDestinationKind.Name);
			this.NumberClosingGroup = new PlacementClosingGroup(this, PlacementDestinationKind.Number);
			this.ClosingGroups = Array.AsReadOnly(new[] { this.NameClosingGroup, this.NumberClosingGroup });
			this.RetrySaveCommand = new AsyncRelayCommand(this.SaveAsync, () => this.SaveFailed && !this._disposed);
			this.RestartCommand = new AsyncRelayCommand(async () =>
			{
				await runtime.RestartPlacementAsync();
				this.Refresh();
			}, () => this.IsPaused && !this._disposed);
			this._subscription = settings.Configuration.Subscribe(_ => this.Reload());
			if (this._generation == 0)
			{
				this.Reload();
			}
			this.RefreshDestinationChoices();
			ResourceService.Current.PropertyChanged += this.OnResourcesChanged;
		}

		internal static string FormatDestination(PlacementDestination destination)
			=> destination.Kind == PlacementDestinationKind.Number ? destination.Number.ToString(CultureInfo.InvariantCulture) : destination.Name;

		internal PlacementRuleGroup GroupFor(PlacementDestinationKind kind)
			=> kind == PlacementDestinationKind.Name ? this.NameGroup : this.NumberGroup;

		private void SetMessageKey(string key) => this.SetProperty(ref this._messageKey, key, nameof(this.Message));

		private void RefreshFollowOptions()
		{
			foreach (var option in this.FollowOptions.Concat(this.FollowWidthSamples))
			{
				option.Refresh();
			}
		}

		private void OnResourcesChanged(object sender, PropertyChangedEventArgs args)
		{
			if (args.PropertyName != nameof(ResourceService.Resources)) return;
			this.OnPropertyChanged(nameof(this.Text));
			this.OnPropertyChanged(nameof(this.Message));
			this.RefreshFollowOptions();
			foreach (var group in this.Groups)
			{
				group.RefreshLanguage();
			}
			foreach (var group in this.ClosingGroups)
			{
				group.RefreshLanguage();
			}
		}

		internal void Refresh()
		{
			this.OnPropertyChanged(nameof(this.IsPaused));
			this.RestartCommand.NotifyCanExecuteChanged();
		}

		internal void RefreshDestinationChoices()
		{
			var map = this._runtime.PlacementDestinations;
			var state = this._runtime.State;
			this.NameGroup.Choices = state.Order.Select(id => state.Records[id].Name.Value)
				.Where(name => !string.IsNullOrWhiteSpace(name) && map.Resolve(PlacementDestination.ByName(name)).Status == PlacementResolutionStatus.Resolved)
				.Distinct(StringComparer.Ordinal).ToArray();
			this.NumberGroup.Choices = Enumerable.Range(1, state.Order.Count)
				.Where(number => map.Resolve(PlacementDestination.ByNumber(number)).Status == PlacementResolutionStatus.Resolved)
				.Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
		}

		private void Reload()
		{
			if (this._disposed) return;
			this.OnPropertyChanged(nameof(this.IsEnabled));
			this.OnPropertyChanged(nameof(this.CreateMissingDesktops));
			this.OnPropertyChanged(nameof(this.CanConfigureCreatedDesktopClosing));
			this.OnPropertyChanged(nameof(this.FollowForeground));
			this.OnPropertyChanged(nameof(this.DefaultFollowOption));
			this.RefreshFollowOptions();
			this.OnPropertyChanged(nameof(this.CloseCreatedDesktops));
			if (this._publishing) return;
			this._generation++;
			foreach (var read in this._reads.Values)
			{
				read.Cancel();
			}
			var configuration = this._settings.Configuration.Value;
			foreach (var group in this.Groups)
			{
				group.Rows.Clear();
				foreach (var rule in configuration.Rules.Where(rule => rule.Destination.Kind == group.Kind))
				{
					group.Rows.Add(new PlacementRuleRow(group, rule));
				}
			}
			foreach (var group in this.ClosingGroups)
			{
				group.Rows.Clear();
				foreach (var target in configuration.ClosingTargets.Where(target => target.Kind == group.Kind))
				{
					group.Rows.Add(new PlacementClosingRow(group, target));
				}
			}
			if (this._generation > 1)
			{
				this.SetMessageKey("ConfigurationChanged");
				this.EditingInvalidated?.Invoke(this, EventArgs.Empty);
			}
			_ = this.LoadIconsAsync(this._generation);
		}

		private async Task LoadIconsAsync(long generation)
		{
			try
			{
				var rows = this.Groups.SelectMany(group => group.Rows).Where(row => row.Choice?.Identity != null).ToArray();
				if (rows.Length == 0) return;
				var choices = rows.ToDictionary(row => row, row => row.Choice);
				var icons = await this._catalog.ReadIconsAsync(choices.Values.Select(choice => choice.Identity).Distinct().ToArray(), this._lifetime.Token);
				if (this._disposed || generation != this._generation) return;
				// A row edited or re-chosen while the Shell was reading keeps its newer state.
				foreach (var row in rows)
				{
					var choice = choices[row];
					if (this.Contains(row) && ReferenceEquals(row.Choice, choice) && icons.TryGetValue(choice.Identity, out var read))
					{
						row.Use(new PlacementAppChoice(choice.Name, choice.Detail, choice.Path, choice.Identity, read.Icon ?? choice.Icon), read.Presence);
					}
				}
			}
			catch (Exception) { /* Missing icons do not prevent registration or editing. */ }
		}

		internal PlacementRuleRow AddRow(PlacementRuleGroup group)
		{
			var row = new PlacementRuleRow(group);
			group.Rows.Add(row);
			return row;
		}

		private bool Contains(PlacementRuleRow row) => !this._disposed && this.Groups.Contains(row.Group) && row.Group.Rows.Contains(row);

		internal async Task CommitAsync(PlacementRuleRow row)
		{
			if (!this.Contains(row)) return;
			if (this._reads.TryGetValue(row, out var previous))
			{
				previous.Cancel();
			}
			var revision = row.Revision;
			var generation = this._generation;
			if (!TryReadDestination(row, out var destination)) return;
			if (string.IsNullOrWhiteSpace(row.AppText))
			{
				row.SetErrorKey("EnterPath");
				return;
			}
			if (row.IsUnchanged(destination))
			{
				row.SetErrorKey("");
				return;
			}
			using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
			{
				this._reads[row] = cancellation;
				try
				{
					var choice = row.Choice ?? await this._catalog.ReadExecutableAsync(row.AppText, cancellation.Token);
					if (!this.Contains(row) || cancellation.IsCancellationRequested || generation != this._generation || revision != row.Revision)
					{
						return;
					}
					if (choice?.Identity == null)
					{
						row.SetErrorKey("IdentityUnavailable");
						return;
					}
					var rules = this._settings.Configuration.Value.Rules.ToList();
					if (row.Enabled && rules.Any(existing => existing.Id != row.Id && existing.Enabled && existing.App.Equals(choice.Identity)))
					{
						row.SetErrorKey("Duplicate");
						return;
					}
					var rule = new AppPlacementRule(row.Id, row.Enabled, choice.Identity, destination, choice.Name, choice.Path, row.FollowForeground);
					var index = rules.FindIndex(existing => existing.Id == row.Id);
					if (index < 0)
					{
						rules.Add(rule);
					}
					else
					{
						rules[index] = rule;
					}
					row.Accept(rule, choice);
					await this.PublishAsync(this.ConfigurationWith(rules: rules));
				}
				catch (OperationCanceledException) { }
				catch (Exception)
				{
					if (this.Contains(row) && generation == this._generation && revision == row.Revision)
					{
						row.SetErrorKey("InvalidPath");
					}
				}
				finally
				{
					if (this._reads.TryGetValue(row, out var read) && ReferenceEquals(read, cancellation))
					{
						this._reads.Remove(row);
					}
				}
			}
		}

		private static bool TryReadDestination(PlacementRuleRow row, out PlacementDestination destination)
		{
			destination = null;
			if (row.Group.Kind == PlacementDestinationKind.Number)
			{
				if (!int.TryParse(row.Destination, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
				{
					row.SetErrorKey("InvalidNumber");
					return false;
				}
				destination = PlacementDestination.ByNumber(number);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(row.Destination))
				{
					row.SetErrorKey("InvalidName");
					return false;
				}
				destination = PlacementDestination.ByName(row.Destination);
			}
			return true;
		}

		internal void Revert(PlacementRuleRow row)
		{
			if (this._reads.TryGetValue(row, out var read))
			{
				read.Cancel();
			}
			row.Restore();
		}

		internal async Task RemoveAsync(PlacementRuleRow row)
		{
			if (!this.Contains(row)) return;
			if (this._reads.TryGetValue(row, out var read))
			{
				read.Cancel();
			}
			row.Group.Rows.Remove(row);
			if (row.Saved != null)
			{
				var rules = this._settings.Configuration.Value.Rules.Where(rule => rule.Id != row.Id);
				await this.PublishAsync(this.ConfigurationWith(rules: rules));
			}
		}

		internal PlacementClosingRow AddClosingRow(PlacementClosingGroup group)
		{
			var row = new PlacementClosingRow(group);
			group.Rows.Add(row);
			return row;
		}

		internal Task CommitClosingAsync(PlacementClosingRow row)
		{
			if (this._disposed || !row.Group.Rows.Contains(row)) return Task.CompletedTask;
			if (string.IsNullOrWhiteSpace(row.Destination))
			{
				row.SetInvalid(row.Saved != null);
				return Task.CompletedTask;
			}
			if (row.Group.Kind == PlacementDestinationKind.Number)
			{
				if (!int.TryParse(row.Destination, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
				{
					row.SetInvalid(true);
					return Task.CompletedTask;
				}
				row.Saved = PlacementDestination.ByNumber(number);
			}
			else
			{
				row.Saved = PlacementDestination.ByName(row.Destination);
			}
			row.SetInvalid(false);
			return this.SaveClosingRowsAsync();
		}

		internal Task RemoveClosingAsync(PlacementClosingRow row)
		{
			if (this._disposed || !row.Group.Rows.Remove(row)) return Task.CompletedTask;
			return row.Saved == null ? Task.CompletedTask : this.SaveClosingRowsAsync();
		}

		private Task SaveClosingRowsAsync()
		{
			var targets = this.ClosingGroups.SelectMany(group => group.Rows).Select(row => row.Saved).Where(target => target != null);
			return this.PublishAsync(this.ConfigurationWith(closingTargets: targets));
		}

		// The current configuration with only the given values replaced.
		private AppPlacementConfiguration ConfigurationWith(
			bool? enabled = null,
			IEnumerable<AppPlacementRule> rules = null,
			bool? createMissing = null,
			bool? closeCreated = null,
			IEnumerable<PlacementDestination> closingTargets = null,
			bool? followForeground = null)
		{
			var current = this._settings.Configuration.Value;
			return new AppPlacementConfiguration(
				enabled ?? current.Enabled,
				rules ?? current.Rules,
				createMissingDesktops: createMissing ?? current.CreateMissingDesktops,
				closeCreatedDesktops: closeCreated ?? current.CloseCreatedDesktops,
				closingTargets: closingTargets ?? current.ClosingTargets,
				followForeground: followForeground ?? current.FollowForeground);
		}

		private Task PublishAsync(AppPlacementConfiguration configuration)
		{
			this._publishing = true;
			try
			{
				this._settings.Configuration.Value = configuration;
			}
			finally
			{
				this._publishing = false;
			}
			return this.SaveAsync();
		}

		private async Task SaveAsync()
		{
			var version = ++this._saveVersion;
			try
			{
				var result = await this._save();
				if (!this._disposed && version == this._saveVersion)
				{
					this.SaveFailed = !result.Succeeded;
					this.SetMessageKey(result.Succeeded ? "" : "SaveFailed");
				}
			}
			catch (Exception)
			{
				if (!this._disposed && version == this._saveVersion)
				{
					this.SaveFailed = true;
					this.SetMessageKey("SaveFailed");
				}
			}
		}

		internal PlacementAppPickerViewModel CreatePicker() => new PlacementAppPickerViewModel(this._catalog);

		internal PlacementApplyViewModel CreateApplyViewModel() => new PlacementApplyViewModel(this._runtime);

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			ResourceService.Current.PropertyChanged -= this.OnResourcesChanged;
			this.EditingInvalidated?.Invoke(this, EventArgs.Empty);
			this._lifetime.Cancel();
			this._subscription.Dispose();
			this._lifetime.Dispose();
		}
	}
}
