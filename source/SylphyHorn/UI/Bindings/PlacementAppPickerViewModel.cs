using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SylphyHorn.Services.AppPlacement;

namespace SylphyHorn.UI.Bindings
{
	public sealed class PlacementAppPickerViewModel : ObservableObject, IDisposable
	{
		private readonly IPlacementAppCatalog _catalog;
		private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
		private CancellationTokenSource _query;
		private IReadOnlyList<PlacementAppChoice> _all = Array.Empty<PlacementAppChoice>();
		private bool _disposed, _busy;
		private string _search = "", _status = "";
		private PlacementAppChoice _selected;

		public PlacementText Text { get; } = new PlacementText();

		public ObservableCollection<PlacementAppChoice> Candidates { get; } = new ObservableCollection<PlacementAppChoice>();

		public AsyncRelayCommand WindowsCommand { get; }

		public AsyncRelayCommand InstalledCommand { get; }

		public string Search
		{
			get => this._search;
			set
			{
				if (this.SetProperty(ref this._search, value))
				{
					this.Filter();
				}
			}
		}

		public string Status { get => this._status; private set => this.SetProperty(ref this._status, value); }

		public bool IsBusy
		{
			get => this._busy;
			private set
			{
				this.SetProperty(ref this._busy, value);
				this.OnPropertyChanged(nameof(this.CanChoose));
			}
		}

		public PlacementAppChoice Selected
		{
			get => this._selected;
			set
			{
				if (this.SetProperty(ref this._selected, value))
				{
					this.OnPropertyChanged(nameof(this.CanChoose));
					this.OnPropertyChanged(nameof(this.Details));
					this.OnPropertyChanged(nameof(this.Warning));
				}
			}
		}

		public bool CanChoose => !this.IsBusy && this.Selected?.Identity != null;

		public string Details => this.Selected?.Path ?? (this.Selected == null ? "" : this.Text["PathUnavailable"]);

		public string Warning => this.Selected?.Problem != null ? this.Text[this.Selected.Problem] : this.Selected?.ConfirmPath == true ? this.Text["LauncherWarning"] : "";

		internal PlacementAppPickerViewModel(IPlacementAppCatalog catalog)
		{
			this._catalog = catalog;
			this.WindowsCommand = new AsyncRelayCommand(() => this.LoadAsync(true));
			this.InstalledCommand = new AsyncRelayCommand(() => this.LoadAsync(false));
		}

		internal async Task LoadAsync(bool windows)
		{
			this._query?.Cancel();
			using (var query = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
			{
				this._query = query;
				this.IsBusy = true;
				this.Status = this.Text["Loading"];
				this._all = Array.Empty<PlacementAppChoice>();
				this.Filter();
				try
				{
					var choices = await this._catalog.ReadAsync(windows, query.Token);
					if (this._disposed || query.IsCancellationRequested) return;
					this._all = choices;
					this.Filter();
					this.Status = this.Candidates.Count == 0 ? this.Text["NoCandidates"] : "";
				}
				catch (OperationCanceledException) { }
				catch (Exception)
				{
					if (!this._disposed && !query.IsCancellationRequested)
					{
						this.Status = this.Text["QueryFailed"];
					}
				}
				finally
				{
					if (ReferenceEquals(this._query, query))
					{
						this._query = null;
						this.IsBusy = false;
					}
				}
			}
		}

		internal async Task<bool> SelectFileAsync(string path)
		{
			this._query?.Cancel();
			using (var query = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
			{
				this._query = query;
				this.IsBusy = true;
				this.Selected = null;
				try
				{
					var choice = await this._catalog.ReadExecutableAsync(path, query.Token);
					if (this._disposed || query.IsCancellationRequested) return false;
					this.Selected = choice;
					return choice?.Identity != null;
				}
				catch (OperationCanceledException)
				{
					return false;
				}
				catch (Exception)
				{
					if (!this._disposed && !query.IsCancellationRequested)
					{
						this.Status = this.Text["InvalidPath"];
					}
					return false;
				}
				finally
				{
					if (ReferenceEquals(this._query, query))
					{
						this._query = null;
						this.IsBusy = false;
					}
				}
			}
		}

		private void Filter()
		{
			this.Candidates.Clear();
			this.Selected = null;
			foreach (var choice in this._all.Where(choice => string.IsNullOrEmpty(this.Search) || (choice.Name + " " + choice.Detail + " " + choice.Path).IndexOf(this.Search, StringComparison.CurrentCultureIgnoreCase) >= 0)) this.Candidates.Add(choice);
		}

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			this._lifetime.Cancel();
			this._lifetime.Dispose();
		}
	}
}
