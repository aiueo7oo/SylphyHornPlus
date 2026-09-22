using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
			foreach (var row in this.Rows) row.RefreshLanguage();
		}
	}

	public sealed class PlacementClosingGroup : ObservableObject
	{
		internal PlacementClosingGroup(AppPlacementSettingsViewModel owner, PlacementDestinationKind kind)
		{
			this.Owner = owner;
			this.Kind = kind;
		}

		public AppPlacementSettingsViewModel Owner { get; }
		internal PlacementDestinationKind Kind { get; }
		public string Title => this.Owner.Text[this.Kind == PlacementDestinationKind.Name ? "CloseByName" : "CloseByNumber"];
		public ObservableCollection<PlacementClosingRow> Rows { get; } = new ObservableCollection<PlacementClosingRow>();
		internal void RefreshLanguage()
		{
			this.OnPropertyChanged(nameof(this.Title));
			foreach (var row in this.Rows) row.RefreshLanguage();
		}
	}

	public sealed class PlacementClosingRow : ObservableObject
	{
		private string _destination;
		private bool _invalid;

		internal PlacementClosingRow(PlacementClosingGroup group, PlacementDestination saved = null)
		{
			this.Group = group;
			this.Saved = saved;
			this.Restore();
			this.RemoveCommand = new AsyncRelayCommand(() => group.Owner.RemoveClosingAsync(this));
		}

		public PlacementClosingGroup Group { get; }
		public AsyncRelayCommand RemoveCommand { get; }
		internal PlacementDestination Saved { get; set; }
		public string Destination { get => this._destination; set => this.SetProperty(ref this._destination, value); }
		public string Error => this._invalid ? this.Group.Owner.Text[this.Group.Kind == PlacementDestinationKind.Name ? "InvalidName" : "InvalidNumber"] : "";
		internal void Invalid(bool value) { this._invalid = value; this.RefreshLanguage(); }
		internal void RefreshLanguage() => this.OnPropertyChanged(nameof(this.Error));
		internal void Restore()
		{
			this.Destination = this.Saved == null ? "" : this.Saved.Kind == PlacementDestinationKind.Name
				? this.Saved.Name : this.Saved.Number.ToString(CultureInfo.InvariantCulture);
			this.Invalid(false);
		}
	}

	public sealed class PlacementRuleRow : ObservableObject
	{
		private string _appText = "", _destination = "", _errorKey = "";
		private bool _enabled = true;
		private PlacementAppChoice _choice;
		private string _choiceText;

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
				if (this.SetProperty(ref this._enabled, value)) this.Revision++;
			}
		}

		public string Error => string.IsNullOrEmpty(this._errorKey) ? "" : this.Group.Owner.Text[this._errorKey];

		internal void SetErrorKey(string key)
		{
			this.SetProperty(ref this._errorKey, key, nameof(this.Error));
		}

		internal void RefreshLanguage() => this.OnPropertyChanged(nameof(this.Error));

		public string Name => this.Choice?.Name ?? "";

		public BitmapSource Icon => this.Choice?.Icon;

		public bool IsPackage => this.Choice?.Identity?.Kind == PlacementAppKind.PackageAppId;

		internal PlacementAppChoice Choice => this.AppText == this._choiceText ? this._choice : null;

		internal void Use(PlacementAppChoice choice)
		{
			this._choice = choice;
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
			this.Use(rule == null ? null : new PlacementAppChoice(
				rule.DisplayName ?? (rule.App.Kind == PlacementAppKind.ExecutablePath ? System.IO.Path.GetFileNameWithoutExtension(rule.App.Value) : this.Group.Owner.Text["UnknownApplication"]),
				"",
				rule.DisplayExecutablePath ?? (rule.App.Kind == PlacementAppKind.ExecutablePath ? rule.App.Value : null),
				rule.App));
			this.Destination = rule == null ? (this.Group.Kind == PlacementDestinationKind.Number ? "1" : "") : rule.Destination.Kind == PlacementDestinationKind.Number ? rule.Destination.Number.ToString(CultureInfo.InvariantCulture) : rule.Destination.Name;
			this.Enabled = rule?.Enabled ?? true;
			this.SetErrorKey("");
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

		public IReadOnlyList<PlacementRuleGroup> Groups { get; }

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

		public bool IsEnabled
		{
			get => this._settings.Configuration.Value.Enabled;
			set
			{
				if (!this._disposed && value != this.IsEnabled)
					_ = this.PublishAsync(this.Configuration(enabled: value));
			}
		}

		public bool CreateMissingDesktops
		{
			get => this._settings.Configuration.Value.CreateMissingDesktops;
			set
			{
				if (!this._disposed && value != this.CreateMissingDesktops)
					_ = this.PublishAsync(this.Configuration(createMissing: value));
			}
		}

		public IReadOnlyList<PlacementClosingGroup> ClosingGroups { get; }

		public bool CloseCreatedDesktops
		{
			get => this._settings.Configuration.Value.CloseCreatedDesktops;
			set
			{
				if (!this._disposed && value != this.CloseCreatedDesktops) _ = this.PublishAsync(this.Configuration(closeCreated: value));
			}
		}

		private AppPlacementConfiguration Configuration(bool? enabled = null, bool? createMissing = null,
			bool? closeCreated = null, IEnumerable<AppPlacementRule> rules = null, IEnumerable<PlacementDestination> closingTargets = null)
		{
			var current = this._settings.Configuration.Value;
			return new AppPlacementConfiguration(enabled ?? current.Enabled, rules ?? current.Rules,
				createMissing ?? current.CreateMissingDesktops, closeCreated ?? current.CloseCreatedDesktops, closingTargets ?? current.ClosingTargets);
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
				row.Invalid(row.Saved != null);
				return Task.CompletedTask;
			}
			if (row.Group.Kind == PlacementDestinationKind.Number)
			{
				if (!int.TryParse(row.Destination, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
				{
					row.Invalid(true);
					return Task.CompletedTask;
				}
				row.Saved = PlacementDestination.ByNumber(number);
			}
			else row.Saved = PlacementDestination.ByName(row.Destination);
			row.Invalid(false);
			return this.SaveClosingRowsAsync();
		}

		internal Task RemoveClosingAsync(PlacementClosingRow row)
		{
			if (this._disposed || !row.Group.Rows.Remove(row)) return Task.CompletedTask;
			return row.Saved == null ? Task.CompletedTask : this.SaveClosingRowsAsync();
		}

		private Task SaveClosingRowsAsync() => this.PublishAsync(this.Configuration(closingTargets:
			this.ClosingGroups.SelectMany(group => group.Rows).Select(row => row.Saved).Where(target => target != null)));

		public bool IsPaused => this._runtime.PlacementStatus == "Paused";

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
			this.Groups = Array.AsReadOnly(new[]
			{
				new PlacementRuleGroup(this, PlacementDestinationKind.Name),
				new PlacementRuleGroup(this, PlacementDestinationKind.Number)
			});
			this.ClosingGroups = Array.AsReadOnly(new[]
			{
				new PlacementClosingGroup(this, PlacementDestinationKind.Name),
				new PlacementClosingGroup(this, PlacementDestinationKind.Number)
			});
			this.RetrySaveCommand = new AsyncRelayCommand(this.SaveAsync, () => this.SaveFailed && !this._disposed);
			this.RestartCommand = new AsyncRelayCommand(async () =>
			{
				await runtime.RestartPlacementAsync();
				this.Refresh();
			}, () => this.IsPaused && !this._disposed);
			this._subscription = settings.Configuration.Subscribe(_ => this.Reload());
			if (this._generation == 0) this.Reload();
			this.RefreshDestinationChoices();
			ResourceService.Current.PropertyChanged += this.OnResourcesChanged;
		}

		private void SetMessageKey(string key) => this.SetProperty(ref this._messageKey, key, nameof(this.Message));

		private void OnResourcesChanged(object sender, PropertyChangedEventArgs args)
		{
			if (args.PropertyName != nameof(ResourceService.Resources)) return;
			this.OnPropertyChanged(nameof(this.Text));
			this.OnPropertyChanged(nameof(this.Message));
			foreach (var group in this.Groups) group.RefreshLanguage();
			foreach (var group in this.ClosingGroups) group.RefreshLanguage();
		}

		internal void RefreshDestinationChoices()
		{
			var map = this._runtime.PlacementDestinations;
			var state = this._runtime.State;
			this.Groups[0].Choices = state.Order.Select(id => state.Records[id].Name.Value)
				.Where(name => !string.IsNullOrWhiteSpace(name) && map.Resolve(PlacementDestination.ByName(name)).Status == PlacementResolutionStatus.Resolved)
				.Distinct(StringComparer.Ordinal).ToArray();
			this.Groups[1].Choices = Enumerable.Range(1, state.Order.Count)
				.Where(number => map.Resolve(PlacementDestination.ByNumber(number)).Status == PlacementResolutionStatus.Resolved)
				.Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
		}

		private void Reload()
		{
			if (this._disposed) return;
			this.OnPropertyChanged(nameof(this.IsEnabled));
			this.OnPropertyChanged(nameof(this.CreateMissingDesktops));
			this.OnPropertyChanged(nameof(this.CloseCreatedDesktops));
			if (this._publishing) return;
			this._generation++;
			foreach (var read in this._reads.Values) read.Cancel();
			foreach (var group in this.Groups)
			{
				group.Rows.Clear();
				foreach (var rule in this._settings.Configuration.Value.Rules.Where(rule => rule.Destination.Kind == group.Kind)) group.Rows.Add(new PlacementRuleRow(group, rule));
			}
			foreach (var group in this.ClosingGroups)
			{
				group.Rows.Clear();
				foreach (var target in this._settings.Configuration.Value.ClosingTargets.Where(target => target.Kind == group.Kind))
					group.Rows.Add(new PlacementClosingRow(group, target));
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
				var rows = this.Groups.SelectMany(group => group.Rows).Where(row => row.Choice != null).ToArray();
				if (rows.Length == 0) return;
				Func<PlacementRuleRow, string> path = row => row.Choice.Identity.Kind == PlacementAppKind.PackageAppId ? "shell:AppsFolder\\" + row.Choice.Identity.Value : row.Choice.Identity.Value;
				var choices = rows.ToDictionary(row => row, row => row.Choice);
				var keys = rows.ToDictionary(row => row, path);
				var icons = await this._catalog.ReadIconsAsync(keys.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), this._lifetime.Token);
				if (this._disposed || generation != this._generation) return;
				foreach (var row in rows) if (this.Contains(row) && ReferenceEquals(row.Choice, choices[row]) && icons.TryGetValue(keys[row], out var icon))
				{
					var choice = choices[row];
					row.Use(new PlacementAppChoice(choice.Name, choice.Detail, choice.Path, choice.Identity, icon));
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
			if (this._reads.TryGetValue(row, out var previous)) previous.Cancel();
			var revision = row.Revision;
			var generation = this._generation;
			PlacementDestination destination;
			if (row.Group.Kind == PlacementDestinationKind.Number)
			{
				if (!int.TryParse(row.Destination, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
				{
					row.SetErrorKey("InvalidNumber");
					return;
				}
				destination = PlacementDestination.ByNumber(number);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(row.Destination))
				{
					row.SetErrorKey("InvalidName");
					return;
				}

				destination = PlacementDestination.ByName(row.Destination);
			}
			if (string.IsNullOrWhiteSpace(row.AppText))
			{
				row.SetErrorKey("EnterPath");
				return;
			}
			if (row.Saved != null && row.Choice != null && row.Saved.Enabled == row.Enabled && row.Saved.App.Equals(row.Choice.Identity)
				&& row.Saved.Destination.Kind == destination.Kind && row.Saved.Destination.Number == destination.Number && row.Saved.Destination.Name == destination.Name)
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
					if (!this.Contains(row) || cancellation.IsCancellationRequested || generation != this._generation || revision != row.Revision) return;
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
					var rule = new AppPlacementRule(row.Id, row.Enabled, choice.Identity, destination, choice.Name, choice.Path);
					var index = rules.FindIndex(existing => existing.Id == row.Id);
					if (index < 0) rules.Add(rule); else rules[index] = rule;
					row.Accept(rule, choice);
					await this.PublishAsync(this.Configuration(rules: rules));
				}
				catch (OperationCanceledException) { }
				catch (Exception)
				{
					if (this.Contains(row) && generation == this._generation && revision == row.Revision) row.SetErrorKey("InvalidPath");
				}
				finally
				{
					if (this._reads.TryGetValue(row, out var read) && ReferenceEquals(read, cancellation)) this._reads.Remove(row);
				}
			}
		}

		internal void Revert(PlacementRuleRow row)
		{
			if (this._reads.TryGetValue(row, out var read)) read.Cancel();
			row.Restore();
		}

		internal async Task RemoveAsync(PlacementRuleRow row)
		{
			if (!this.Contains(row)) return;
			if (this._reads.TryGetValue(row, out var read)) read.Cancel();
			row.Group.Rows.Remove(row);
			if (row.Saved != null)
			{
				var rules = this._settings.Configuration.Value.Rules.Where(rule => rule.Id != row.Id);
				await this.PublishAsync(this.Configuration(rules: rules));
			}
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

		internal void Refresh()
		{
			this.OnPropertyChanged(nameof(this.IsPaused));
			this.RestartCommand.NotifyCanExecuteChanged();
		}

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
