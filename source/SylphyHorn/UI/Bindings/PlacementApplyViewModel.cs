using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.DesktopTransitions;

namespace SylphyHorn.UI.Bindings
{
	public sealed class PlacementApplyRow : ObservableObject
	{
		private bool _selected, _selectable;
		private string _result;
		private readonly Action _selectionChanged;

		internal PlacementPreviewItem Item { get; }

		internal PlacementApplyRow(PlacementPreviewItem item, string source, string target, PlacementText text, Action selectionChanged)
		{
			this.Item = item;
			this.Source = source;
			this.Target = target;
			this._selectionChanged = selectionChanged;
			this.Title = string.IsNullOrWhiteSpace(item.Title) ? item.Rule.DisplayName ?? text["UnknownApplication"] : item.Title;
			this.Path = item.Identity.AppProcess?.Path ?? item.Rule.DisplayExecutablePath
				?? (item.Rule.App.Kind == PlacementAppKind.ExecutablePath ? item.Rule.App.Value : text["PathUnavailable"]);
			this._selectable = item.CanApply;
			this._result = text[item.Excluded.HasValue ? OutcomeKey(item.Excluded.Value) : "ReadyToApply"];
		}

		public string Title { get; }

		public string Path { get; }

		public string Source { get; }

		public string Target { get; }

		public bool Selected
		{
			get => this._selected;
			set
			{
				if ((!value || this.Selectable) && this.SetProperty(ref this._selected, value))
				{
					this._selectionChanged();
				}
			}
		}

		public bool Selectable { get => this._selectable; internal set => this.SetProperty(ref this._selectable, value); }

		public string Result { get => this._result; internal set => this.SetProperty(ref this._result, value); }

		// Placement_OutcomeMoved, Placement_OutcomeAlreadyPlaced, ...: one resource for each PlacementOutcome value.
		internal static string OutcomeKey(PlacementOutcome outcome) => "Outcome" + outcome;
	}

	public sealed class PlacementApplyViewModel : ObservableObject, IDisposable
	{
		private readonly DesktopTransitionRuntime _runtime;
		private CancellationTokenSource _request;
		private PlacementPreview _preview;
		private bool _disposed, _busy;
		private string _message = "";

		public PlacementText Text { get; } = new PlacementText();

		public ObservableCollection<PlacementApplyRow> Rows { get; } = new ObservableCollection<PlacementApplyRow>();

		public AsyncRelayCommand RefreshCommand { get; }

		public AsyncRelayCommand ApplyCommand { get; }

		public RelayCommand StopCommand { get; }

		public bool IsBusy
		{
			get => this._busy;
			private set
			{
				this.SetProperty(ref this._busy, value);
				this.NotifyCommands();
			}
		}

		public string Status
		{
			get
			{
				var status = this._runtime.PlacementStatus;
				// While placement is not active: Placement_ApplyDisabled, ApplyNoRules, ApplyPaused, ApplyPreparing,
				// ApplyStopping or ApplySuspended.
				return status == PlacementStatuses.Active ? this._message : this.Text["Apply" + status];
			}
		}

		public string ApplyLabel => string.Format(CultureInfo.CurrentCulture, this.Text["ApplySelection"], this.Rows.Count(row => row.Selected));

		private bool IsPlacementActive => this._runtime.PlacementStatus == PlacementStatuses.Active;

		internal PlacementApplyViewModel(DesktopTransitionRuntime runtime)
		{
			this._runtime = runtime;
			this.RefreshCommand = new AsyncRelayCommand(this.LoadAsync, this.CanRefresh);
			this.ApplyCommand = new AsyncRelayCommand(this.ApplyAsync, this.CanApply);
			this.StopCommand = new RelayCommand(
				() =>
				{
					this._request?.Cancel();
					this.Message("ApplyStopping");
					this.NotifyCommands();
				},
				() => this.IsBusy && this._request?.IsCancellationRequested == false);
			this._runtime.StateChanged += this.StateChanged;
		}

		private bool CanRefresh() => !this._disposed && !this.IsBusy && this.IsPlacementActive;

		// Only a current preview can be applied, and only to windows the user checked.
		private bool CanApply()
		{
			return !this._disposed
				&& !this.IsBusy
				&& this._preview != null
				&& this.IsPlacementActive
				&& this.Rows.Any(row => row.Selected && row.Selectable);
		}

		private void NotifyCommands()
		{
			this.RefreshCommand.NotifyCanExecuteChanged();
			this.ApplyCommand.NotifyCanExecuteChanged();
			this.StopCommand.NotifyCanExecuteChanged();
			this.OnPropertyChanged(nameof(this.ApplyLabel));
			this.OnPropertyChanged(nameof(this.Status));
		}

		private void Message(string key)
		{
			this._message = this.Text[key];
			this.OnPropertyChanged(nameof(this.Status));
		}

		private void Invalidate()
		{
			this._preview = null;
			this._request?.Cancel();
			foreach (var row in this.Rows)
			{
				row.Selectable = false;
			}
			this.NotifyCommands();
		}

		private void StateChanged(object sender, DesktopRuntimeStateChanged args)
		{
			this.Invalidate();
			this.Message("ApplyChanged");
		}

		internal void RefreshStatus()
		{
			if (this._disposed) return;
			if (!this.IsPlacementActive)
			{
				this.Invalidate();
			}
			this.NotifyCommands();
		}

		private async Task LoadAsync()
		{
			if (!this.RefreshCommand.CanExecute(null)) return;
			this._preview = null;
			this.Rows.Clear();
			this.IsBusy = true;
			this.Message("Loading");
			using (var request = new CancellationTokenSource())
			{
				this._request = request;
				this.NotifyCommands();
				try
				{
					var state = this._runtime.State;
					var preview = await this._runtime.PreviewExistingPlacementAsync(request.Token);
					if (this._disposed) return;
					if (request.IsCancellationRequested)
					{
						this.Message("ApplyCancelled");
						return;
					}
					this._preview = preview;
					foreach (var item in preview.Items)
					{
						this.Rows.Add(new PlacementApplyRow(item, this.DesktopLabel(state, item.Source), this.TargetLabel(state, item), this.Text, this.NotifyCommands));
					}
					this.Message(this.Rows.Count == 0 ? "ApplyEmpty" : "ApplyReview");
				}
				catch (OperationCanceledException)
				{
					if (!this._disposed)
					{
						this.Message("ApplyCancelled");
					}
				}
				catch (Exception)
				{
					if (!this._disposed)
					{
						this.Message("ApplyQueryFailed");
					}
				}
				finally
				{
					this._request = null;
					if (!this._disposed)
					{
						this.IsBusy = false;
					}
				}
			}
		}

		// The resolved target desktop, or the rule's destination when it did not resolve to one.
		private string TargetLabel(DesktopRuntimeState state, PlacementPreviewItem item)
		{
			if (item.Target.HasValue) return this.DesktopLabel(state, item.Target.Value);
			var destination = item.Rule.Destination;
			return destination.Kind == PlacementDestinationKind.Name
				? destination.Name
				: string.Format(CultureInfo.CurrentCulture, this.Text["DesktopNumber"], destination.Number);
		}

		private string DesktopLabel(DesktopRuntimeState state, Guid id)
		{
			var index = state.Order.ToList().IndexOf(id);
			if (index < 0)
			{
				return this.Text["DesktopUnknown"];
			}
			var number = string.Format(CultureInfo.CurrentCulture, this.Text["DesktopNumber"], index + 1);
			var name = state.Records[id].Name;
			return HasReadableName(name) ? number + " — " + name.Value : number;
		}

		private static bool HasReadableName(DesktopPropertyState name)
		{
			return name.IsConfirmed
				&& name.HasValue
				&& name.ReadStatus == WindowsDesktop.VirtualDesktopReadStatus.Success
				&& !string.IsNullOrWhiteSpace(name.Value);
		}

		private async Task ApplyAsync()
		{
			if (!this.ApplyCommand.CanExecute(null)) return;
			var preview = this._preview;
			var selected = this.Rows.Where(row => row.Selected && row.Selectable).ToArray();
			this._preview = null;
			foreach (var row in this.Rows)
			{
				if (row.Selectable && !row.Selected)
				{
					row.Result = this.Text["ApplyNotSelected"];
				}
				row.Selectable = false;
			}
			foreach (var row in selected)
			{
				row.Result = this.Text["Applying"];
			}
			this.IsBusy = true;
			this.Message("ApplyRunning");
			using (var request = new CancellationTokenSource())
			{
				this._request = request;
				this.NotifyCommands();
				try
				{
					var results = await this._runtime.ApplyExistingPlacementAsync(preview, selected.Select(row => row.Item.Id).ToArray(), request.Token);
					if (this._disposed) return;
					foreach (var row in selected)
					{
						var result = results.FirstOrDefault(value => value.Window == row.Item.Candidate.Window && value.Rule == row.Item.Rule.Id);
						row.Result = this.Text[PlacementApplyRow.OutcomeKey(result?.Outcome ?? PlacementOutcome.Unconfirmed)];
					}
					this.Message("ApplyFinished");
				}
				catch (Exception)
				{
					if (!this._disposed)
					{
						foreach (var row in selected)
						{
							row.Result = this.Text["OutcomeUnconfirmed"];
						}
						this.Message("ApplyFailed");
					}
				}
				finally
				{
					this._request = null;
					if (!this._disposed)
					{
						this.IsBusy = false;
					}
				}
			}
		}

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			this._runtime.StateChanged -= this.StateChanged;
			this.Invalidate();
		}
	}
}
