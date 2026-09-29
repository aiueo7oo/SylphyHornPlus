using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.DesktopTransitions;

namespace SylphyHorn.UI.Bindings
{
	public sealed class CreationWallpaperText
	{
		public string this[string key] => Resources.ResourceManager.GetString("CreationWallpaper_" + key, Resources.Culture) ?? key;
	}

	internal interface ICreationWallpaperImages
	{
		// Throws when the file cannot be opened or decoded as an image.
		Task ValidateAsync(string path, CancellationToken cancellation);

		Task<IReadOnlyCollection<string>> FindMissingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellation);
	}

	internal sealed class CreationWallpaperImages : ICreationWallpaperImages
	{
		public Task ValidateAsync(string path, CancellationToken cancellation)
		{
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var thread = new Thread(() =>
			{
				try
				{
					cancellation.ThrowIfCancellationRequested();
					WallpaperService.ValidateImage(path);
					completion.TrySetResult(true);
				}
				catch (OperationCanceledException)
				{
					completion.TrySetCanceled();
				}
				catch (Exception ex)
				{
					completion.TrySetException(ex);
				}
			})
			{
				IsBackground = true,
				Name = "Creation wallpaper validation"
			};
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			return completion.Task;
		}

		// Saved entries are only checked for existence; their images are not decoded when the page loads.
		public Task<IReadOnlyCollection<string>> FindMissingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellation)
			=> Task.Run<IReadOnlyCollection<string>>(() => paths.Where(path => !File.Exists(path)).ToArray(), cancellation);
	}

	public sealed class CreationWallpaperGroup : ObservableObject
	{
		internal CreationWallpaperGroup(CreationWallpaperSettingsViewModel owner, bool byName)
		{
			this.Owner = owner;
			this.ByName = byName;
		}

		public CreationWallpaperSettingsViewModel Owner { get; }

		internal bool ByName { get; }

		public string Title => this.Owner.Text[this.ByName ? "NameList" : "NumberList"];

		public string Description => this.Owner.Text[this.ByName ? "NameHint" : "NumberHint"];

		public string DestinationLabel => this.Owner.Text[this.ByName ? "NameColumn" : "NumberColumn"];

		// Names may need the IME; number fields turn it off so that digits are typed as ASCII.
		public bool UsesInputMethod => this.ByName;

		public IReadOnlyList<string> Choices { get; internal set; } = Array.Empty<string>();

		public ObservableCollection<CreationWallpaperRow> Rows { get; } = new ObservableCollection<CreationWallpaperRow>();

		internal bool Matches(DesktopWallpaperOnCreation entry) => this.ByName ? entry.Name != null : entry.Number.HasValue;

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

	public sealed class CreationWallpaperRow : ObservableObject
	{
		private string _destination = "", _wallpaperPath = "", _errorKey = "";
		private string _missingPath;

		internal CreationWallpaperRow(CreationWallpaperGroup group, DesktopWallpaperOnCreation saved = null)
		{
			this.Group = group;
			this.Saved = saved;
			this.RemoveCommand = new AsyncRelayCommand(() => group.Owner.RemoveAsync(this));
			this.Restore();
		}

		public CreationWallpaperGroup Group { get; }

		internal DesktopWallpaperOnCreation Saved { get; private set; }

		internal long Revision { get; private set; }

		public AsyncRelayCommand RemoveCommand { get; }

		public string Destination
		{
			get => this._destination;
			set
			{
				if (!this.SetProperty(ref this._destination, value)) return;
				this.Revision++;
				this.SetErrorKey("");
			}
		}

		public string WallpaperPath
		{
			get => this._wallpaperPath;
			set
			{
				if (!this.SetProperty(ref this._wallpaperPath, value)) return;
				this.Revision++;
				this.SetErrorKey("");
				this.OnPropertyChanged(nameof(this.Error));
			}
		}

		public string Error
		{
			get
			{
				if (!string.IsNullOrEmpty(this._errorKey)) return this.Group.Owner.Text[this._errorKey];
				// A saved image that no longer exists is reported while the row shows that path; the entry itself is kept.
				var showsMissingPath = this._missingPath != null && this.WallpaperPath == this._missingPath;
				return showsMissingPath ? this.Group.Owner.Text["MissingImage"] : "";
			}
		}

		internal string SavedDestination => this.Saved == null ? ""
			: this.Saved.Name ?? this.Saved.Number.Value.ToString(CultureInfo.InvariantCulture);

		internal bool IsEdited => this.Destination != this.SavedDestination || this.WallpaperPath != (this.Saved?.WallpaperPath ?? "");

		internal void SetErrorKey(string key) => this.SetProperty(ref this._errorKey, key, nameof(this.Error));

		internal void MarkMissing(string path)
		{
			this._missingPath = path;
			this.OnPropertyChanged(nameof(this.Error));
		}

		internal void Accept(DesktopWallpaperOnCreation entry)
		{
			// A missing image stays reported when only the destination changed; a new image was read before saving.
			this.Saved = entry;
			this.Restore();
		}

		// The same entry read again (for example after an import): keep what the user is typing.
		internal void Rebase(DesktopWallpaperOnCreation entry) => this.Saved = entry;

		internal void Restore()
		{
			this.Destination = this.SavedDestination;
			this.WallpaperPath = this.Saved?.WallpaperPath ?? "";
			this.SetErrorKey("");
			this.OnPropertyChanged(nameof(this.Error));
		}

		internal void RefreshLanguage() => this.OnPropertyChanged(nameof(this.Error));
	}

	public sealed class CreationWallpaperSettingsViewModel : ObservableObject, IDisposable
	{
		private readonly GeneralSettings _settings;
		private readonly DesktopTransitionRuntime _runtime;
		private readonly ICreationWallpaperImages _images;
		private readonly Func<Task<SettingsSaveResult>> _save;
		private readonly Func<string> _chooseImage;
		private readonly bool _legacyWallpaper;
		private readonly IDisposable _subscription;
		private readonly IDisposable _legacySubscription;
		private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
		private readonly Dictionary<CreationWallpaperRow, CancellationTokenSource> _validations = new Dictionary<CreationWallpaperRow, CancellationTokenSource>();
		private bool _publishing, _disposed, _saveFailed, _loaded, _editDiscarded;
		private long _saveVersion;

		public CreationWallpaperText Text { get; } = new CreationWallpaperText();

		// null on Windows builds without desktop names.
		internal CreationWallpaperGroup NameGroup { get; }

		internal CreationWallpaperGroup NumberGroup { get; }

		public IReadOnlyList<CreationWallpaperGroup> Groups { get; }

		public AsyncRelayCommand RetrySaveCommand { get; }

		public string SaveMessage => this.SaveFailed ? this.Text["SaveFailed"] : "";

		public string EditDiscardedMessage => this._editDiscarded ? this.Text["ConfigurationChanged"] : "";

		public bool SaveFailed
		{
			get => this._saveFailed;
			private set
			{
				if (!this.SetProperty(ref this._saveFailed, value)) return;
				this.OnPropertyChanged(nameof(this.SaveMessage));
				this.RetrySaveCommand.NotifyCanExecuteChanged();
			}
		}

		// Without native per-desktop wallpapers, creation wallpapers follow the existing per-desktop background option.
		public string LegacyNote => this._legacyWallpaper && !this._settings.ChangeBackgroundEachDesktop.Value
			? string.Format(CultureInfo.CurrentCulture, this.Text["LegacyNote"],
				Resources.Settings_Desktop + " 1", Resources.Settings_Background_ChangeBackground)
			: "";

		internal CreationWallpaperSettingsViewModel(
			GeneralSettings settings,
			DesktopTransitionRuntime runtime,
			ICreationWallpaperImages images,
			Func<Task<SettingsSaveResult>> save,
			Func<string> chooseImage,
			bool nameSupported,
			bool legacyWallpaper)
		{
			this._settings = settings;
			this._runtime = runtime;
			this._images = images;
			this._save = save;
			this._chooseImage = chooseImage;
			this._legacyWallpaper = legacyWallpaper;
			this.NameGroup = nameSupported ? new CreationWallpaperGroup(this, true) : null;
			this.NumberGroup = new CreationWallpaperGroup(this, false);
			this.Groups = Array.AsReadOnly(nameSupported ? new[] { this.NameGroup, this.NumberGroup } : new[] { this.NumberGroup });
			this.RetrySaveCommand = new AsyncRelayCommand(this.SaveAsync, () => this.SaveFailed && !this._disposed);
			this._subscription = settings.DesktopWallpapersOnCreation.Subscribe(_ => this.Reload());
			if (!this._loaded)
			{
				this.Reload();
			}
			this._legacySubscription = settings.ChangeBackgroundEachDesktop.Subscribe(_ => this.OnPropertyChanged(nameof(this.LegacyNote)));
			this.RefreshDestinationChoices();
			ResourceService.Current.PropertyChanged += this.OnResourcesChanged;
		}

		private void OnResourcesChanged(object sender, PropertyChangedEventArgs args)
		{
			if (args.PropertyName != nameof(ResourceService.Resources)) return;
			this.OnPropertyChanged(nameof(this.Text));
			this.OnPropertyChanged(nameof(this.SaveMessage));
			this.OnPropertyChanged(nameof(this.EditDiscardedMessage));
			this.OnPropertyChanged(nameof(this.LegacyNote));
			foreach (var group in this.Groups)
			{
				group.RefreshLanguage();
			}
		}

		internal void RefreshDestinationChoices()
		{
			var state = this._runtime.State;
			if (this.NameGroup != null)
			{
				this.NameGroup.Choices = state.Order.Select(id => state.Records[id].Name)
					.Where(name => name.HasValue && !string.IsNullOrWhiteSpace(name.Value))
					.Select(name => name.Value).Distinct(StringComparer.Ordinal).ToArray();
			}
			this.NumberGroup.Choices = Enumerable.Range(1, state.Order.Count)
				.Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
		}

		private static bool SameTarget(DesktopWallpaperOnCreation left, DesktopWallpaperOnCreation right)
			=> left.Name != null ? string.Equals(left.Name, right.Name, StringComparison.Ordinal) : right.Name == null && left.Number == right.Number;

		private static bool Same(DesktopWallpaperOnCreation left, DesktopWallpaperOnCreation right)
			=> SameTarget(left, right) && string.Equals(left.WallpaperPath, right.WallpaperPath, StringComparison.Ordinal);

		private DesktopWallpaperOnCreation[] Entries => this._settings.DesktopWallpapersOnCreation.Value ?? Array.Empty<DesktopWallpaperOnCreation>();

		// A change made elsewhere (CLI, import, reset) replaces only the rows whose saved entry changed. Other rows, and rows
		// not saved yet, keep their controls and any text being typed; discarding an edit is reported.
		private void Reload()
		{
			if (this._disposed || this._publishing) return;
			this._loaded = true;
			var entries = this.Entries;
			var discarded = false;
			foreach (var group in this.Groups)
			{
				var previous = group.Rows.Where(row => row.Saved != null).ToList();
				var ordered = new List<CreationWallpaperRow>();
				foreach (var entry in entries.Where(group.Matches))
				{
					var row = previous.FirstOrDefault(candidate => Same(candidate.Saved, entry));
					if (row != null)
					{
						previous.Remove(row);
						row.Rebase(entry);
					}
					else
					{
						row = new CreationWallpaperRow(group, entry);
					}
					ordered.Add(row);
				}
				foreach (var row in previous)
				{
					discarded |= row.IsEdited;
					this.CancelValidation(row);
				}
				ordered.AddRange(group.Rows.Where(row => row.Saved == null));
				for (var index = group.Rows.Count - 1; index >= 0; index--)
				{
					if (!ordered.Contains(group.Rows[index]))
					{
						group.Rows.RemoveAt(index);
					}
				}
				for (var index = 0; index < ordered.Count; index++)
				{
					var current = group.Rows.IndexOf(ordered[index]);
					if (current < 0)
					{
						group.Rows.Insert(index, ordered[index]);
					}
					else if (current != index)
					{
						group.Rows.Move(current, index);
					}
				}
			}
			if (discarded && !this._editDiscarded)
			{
				this._editDiscarded = true;
				this.OnPropertyChanged(nameof(this.EditDiscardedMessage));
			}
			_ = this.CheckImagesAsync();
		}

		private async Task CheckImagesAsync()
		{
			try
			{
				var rows = this.Groups.SelectMany(group => group.Rows).Where(row => row.Saved != null).ToArray();
				if (rows.Length == 0) return;
				var paths = rows.Select(row => row.Saved.WallpaperPath).Distinct(StringComparer.Ordinal).ToArray();
				var missing = new HashSet<string>(await this._images.FindMissingAsync(paths, this._lifetime.Token), StringComparer.Ordinal);
				if (this._disposed) return;
				// A row saved with another image while the check ran keeps its newer state.
				foreach (var row in rows)
				{
					var path = row.Saved?.WallpaperPath;
					if (this.Contains(row) && path != null && paths.Contains(path, StringComparer.Ordinal))
					{
						row.MarkMissing(missing.Contains(path) ? path : null);
					}
				}
			}
			catch (Exception) { /* A failed check leaves the rows editable without a message. */ }
		}

		internal CreationWallpaperRow AddRow(CreationWallpaperGroup group)
		{
			var row = new CreationWallpaperRow(group);
			group.Rows.Add(row);
			return row;
		}

		private bool Contains(CreationWallpaperRow row) => !this._disposed && this.Groups.Contains(row.Group) && row.Group.Rows.Contains(row);

		private void CancelValidation(CreationWallpaperRow row)
		{
			if (this._validations.TryGetValue(row, out var validation))
			{
				validation.Cancel();
			}
		}

		private bool IsDuplicate(DesktopWallpaperOnCreation entry, DesktopWallpaperOnCreation saved)
			=> this.Entries.Any(existing => SameTarget(existing, entry) && !(saved != null && Same(existing, saved)));

		internal async Task CommitAsync(CreationWallpaperRow row)
		{
			if (!this.Contains(row)) return;
			this.CancelValidation(row);
			var path = (row.WallpaperPath ?? "").Trim().Trim('"').Trim();
			if (path != row.WallpaperPath)
			{
				row.WallpaperPath = path;
			}
			if (!row.Group.ByName)
			{
				row.Destination = DesktopNumberInput.Normalize(row.Destination);
			}
			var revision = row.Revision;
			if (!TryCreateEntry(row, path, out var entry)) return;
			if (row.Saved != null && Same(row.Saved, entry))
			{
				row.Restore();
				return;
			}
			if (this.RejectDuplicate(row, entry)) return;
			// Only a new or changed image is read; a saved image that is missing does not block editing the destination.
			if (row.Saved == null || row.Saved.WallpaperPath != entry.WallpaperPath)
			{
				if (!await this.ValidateImageAsync(row, entry.WallpaperPath, revision)) return;
				// The entries may have changed elsewhere while the image was read.
				if (this.RejectDuplicate(row, entry)) return;
			}
			var entries = this.Entries.ToList();
			var index = row.Saved == null ? -1 : entries.FindIndex(existing => Same(existing, row.Saved));
			if (index < 0)
			{
				entries.Add(entry);
			}
			else
			{
				entries[index] = entry;
			}
			row.Accept(entry);
			await this.PublishAsync(entries.ToArray());
		}

		// Reads the row's destination and the trimmed image path into an entry, or shows why they are invalid.
		private static bool TryCreateEntry(CreationWallpaperRow row, string path, out DesktopWallpaperOnCreation entry)
		{
			entry = null;
			string name = null;
			int? number = null;
			if (row.Group.ByName)
			{
				if (string.IsNullOrWhiteSpace(row.Destination))
				{
					row.SetErrorKey("InvalidName");
					return false;
				}
				name = row.Destination;
			}
			else
			{
				if (!DesktopNumberInput.TryParse(row.Destination, out var value))
				{
					row.SetErrorKey("InvalidNumber");
					return false;
				}
				number = value;
			}
			if (path.Length == 0)
			{
				row.SetErrorKey("EnterPath");
				return false;
			}
			try
			{
				entry = new DesktopWallpaperOnCreation(name, number, path);
				return true;
			}
			catch (SerializationException)
			{
				row.SetErrorKey("AbsolutePath");
				return false;
			}
		}

		private bool RejectDuplicate(CreationWallpaperRow row, DesktopWallpaperOnCreation entry)
		{
			if (!this.IsDuplicate(entry, row.Saved)) return false;
			row.SetErrorKey(row.Group.ByName ? "DuplicateName" : "DuplicateNumber");
			return true;
		}

		// Returns whether the image could be read and the row still holds the input being committed.
		private async Task<bool> ValidateImageAsync(CreationWallpaperRow row, string path, long revision)
		{
			using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
			{
				this._validations[row] = cancellation;
				try
				{
					await this._images.ValidateAsync(path, cancellation.Token);
				}
				catch (OperationCanceledException)
				{
					return false;
				}
				catch (Exception)
				{
					if (this.Contains(row) && revision == row.Revision && !cancellation.IsCancellationRequested)
					{
						row.SetErrorKey("UnreadableImage");
					}
					return false;
				}
				finally
				{
					if (this._validations.TryGetValue(row, out var current) && ReferenceEquals(current, cancellation))
					{
						this._validations.Remove(row);
					}
				}
				// Input edited, restored or removed while the image was read keeps its newer state.
				return this.Contains(row) && revision == row.Revision && !cancellation.IsCancellationRequested;
			}
		}

		internal void Revert(CreationWallpaperRow row)
		{
			this.CancelValidation(row);
			row.Restore();
		}

		internal async Task RemoveAsync(CreationWallpaperRow row)
		{
			if (!this.Contains(row)) return;
			this.CancelValidation(row);
			row.Group.Rows.Remove(row);
			if (row.Saved == null) return;
			var current = this.Entries;
			var entries = current.Where(entry => !Same(entry, row.Saved)).ToArray();
			if (entries.Length != current.Length)
			{
				await this.PublishAsync(entries);
			}
		}

		internal async Task ChooseImageAsync(CreationWallpaperRow row)
		{
			if (!this.Contains(row) || this._chooseImage == null) return;
			var path = this._chooseImage();
			if (string.IsNullOrEmpty(path) || !this.Contains(row)) return;
			row.WallpaperPath = path;
			if (!string.IsNullOrWhiteSpace(row.Destination))
			{
				await this.CommitAsync(row);
			}
		}

		private Task PublishAsync(DesktopWallpaperOnCreation[] entries)
		{
			this._publishing = true;
			try
			{
				this._settings.DesktopWallpapersOnCreation.Value = entries;
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
				}
			}
			catch (Exception)
			{
				if (!this._disposed && version == this._saveVersion)
				{
					this.SaveFailed = true;
				}
			}
		}

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			ResourceService.Current.PropertyChanged -= this.OnResourcesChanged;
			this._lifetime.Cancel();
			this._subscription.Dispose();
			this._legacySubscription.Dispose();
			this._lifetime.Dispose();
		}
	}
}
