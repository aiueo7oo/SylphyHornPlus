using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.UI.Bindings
{
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

		// The same desktop choices as the placement rules of this kind.
		internal IReadOnlyList<string> Choices => this.Owner.GroupFor(this.Kind).Choices;

		internal void RefreshLanguage()
		{
			this.OnPropertyChanged(nameof(this.Title));
			foreach (var row in this.Rows)
			{
				row.RefreshLanguage();
			}
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

		public string Destination
		{
			get => this._destination;
			set => this.SetProperty(ref this._destination, value);
		}

		public string Error
		{
			get
			{
				if (!this._invalid) return "";
				return this.Group.Owner.Text[this.Group.Kind == PlacementDestinationKind.Name ? "InvalidName" : "InvalidNumber"];
			}
		}

		internal void SetInvalid(bool invalid)
		{
			this._invalid = invalid;
			this.RefreshLanguage();
		}

		internal void RefreshLanguage() => this.OnPropertyChanged(nameof(this.Error));

		internal void Restore()
		{
			this.Destination = this.Saved == null ? "" : AppPlacementSettingsViewModel.FormatDestination(this.Saved);
			this.SetInvalid(false);
		}
	}
}
