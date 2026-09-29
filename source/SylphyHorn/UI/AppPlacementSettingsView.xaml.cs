using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MetroRadiance.UI.Controls;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	public partial class AppPlacementSettingsView : UserControl
	{
		private readonly DispatcherTimer _refresh;

		public AppPlacementSettingsView()
		{
			this.InitializeComponent();
			this._refresh = new DispatcherTimer(
				TimeSpan.FromSeconds(1),
				DispatcherPriority.Background,
				(_, __) => (this.DataContext as AppPlacementSettingsViewModel)?.Refresh(),
				this.Dispatcher);
			this._refresh.Stop();
		}

		private void OnLoaded(object sender, RoutedEventArgs args) => this.UpdateRefresh();

		private void OnUnloaded(object sender, RoutedEventArgs args) => this._refresh?.Stop();

		private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => this.UpdateRefresh();

		private void UpdateRefresh()
		{
			if (this._refresh == null) return;
			if (this.IsLoaded && this.IsVisible)
			{
				(this.DataContext as AppPlacementSettingsViewModel)?.Refresh();
				this._refresh.Start();
			}
			else
			{
				this._refresh.Stop();
			}
		}

		private void FollowChanged(object sender, SelectionChangedEventArgs args)
		{
			// Only a choice made in this combo box saves; filling the items, Esc restoring the row
			// or a reload replacing it does not.
			var combo = (ComboBox)sender;
			if (args.RemovedItems.Count == 0 || args.AddedItems.Count == 0 || !(combo.IsKeyboardFocusWithin || combo.IsDropDownOpen))
			{
				return;
			}
			this.Commit(sender, args);
		}

		private void Add(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is PlacementRuleGroup group)
			{
				this.FocusRow(group.Owner.AddRow(group));
			}
		}

		private void DestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var group = ((PlacementRuleRow)combo.DataContext).Group;
			group.Owner.RefreshDestinationChoices();
			EditableRowInput.DestinationListOpened(combo, group.Choices);
		}

		private void DestinationClosed(object sender, EventArgs args)
		{
			if (EditableRowInput.DestinationListClosed((ComboBox)sender))
			{
				this.Commit(sender, args);
			}
		}

		private async void Commit(object sender, EventArgs args)
		{
			if (EditableRowInput.IsFocusMovingWithinComboBox(sender, args)) return;
			if (!((sender as FrameworkElement)?.DataContext is PlacementRuleRow row)) return;
			// Moving between fields of a new row must not show an error before the user has filled it in.
			if (row.Saved == null && (string.IsNullOrWhiteSpace(row.AppText) || string.IsNullOrWhiteSpace(row.Destination)))
			{
				return;
			}
			await row.Group.Owner.CommitAsync(row);
		}

		private async void InputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is PlacementRuleRow row)) return;
			var owner = row.Group.Owner;
			await EditableRowInput.HandleEntryKeyAsync((DependencyObject)sender, args, () => owner.Revert(row), () => owner.CommitAsync(row));
		}

		private async void Pick(object sender, RoutedEventArgs args)
		{
			var context = (sender as FrameworkElement)?.DataContext;
			var row = context as PlacementRuleRow;
			var group = row?.Group ?? context as PlacementRuleGroup;
			if (group == null) return;
			using (var picker = group.Owner.CreatePicker())
			{
				var view = new PlacementAppPickerView { DataContext = picker };
				var dialog = this.CreatePlacementDialog(picker.Text["ChooseApp"], view, width: 660, height: 500, minWidth: 480, minHeight: 380);
				view.Accepted += (_, __) => dialog.DialogResult = true;
				view.Cancelled += (_, __) => dialog.DialogResult = false;
				dialog.Loaded += async (_, __) => await picker.WindowsCommand.ExecuteAsync(null);
				if (ShowUntilEditingInvalidated(dialog, group.Owner) != true || !picker.CanChoose) return;
				row = row ?? group.Owner.AddRow(group);
				row.Use(picker.Selected);
				if (!string.IsNullOrWhiteSpace(row.Destination))
				{
					await group.Owner.CommitAsync(row);
				}
				this.FocusRow(row);
			}
		}

		private void OpenApply(object sender, RoutedEventArgs args)
		{
			if (!(this.DataContext is AppPlacementSettingsViewModel owner)) return;
			using (var model = owner.CreateApplyViewModel())
			{
				var view = new PlacementApplyView { DataContext = model };
				var dialog = this.CreatePlacementDialog(model.Text["ApplyTitle"], view, width: 890, height: 520, minWidth: 660, minHeight: 380);
				dialog.Loaded += async (_, __) =>
				{
					if (model.RefreshCommand.CanExecute(null))
					{
						await model.RefreshCommand.ExecuteAsync(null);
					}
				};
				dialog.Closing += (_, __) => model.Dispose();
				ShowUntilEditingInvalidated(dialog, owner);
			}
		}

		private MetroWindow CreatePlacementDialog(string title, FrameworkElement content, double width, double height, double minWidth, double minHeight)
		{
			var dialog = new MetroWindow
			{
				Title = title,
				Content = content,
				Width = width,
				Height = height,
				MinWidth = minWidth,
				MinHeight = minHeight,
				FontFamily = this.FontFamily,
				FontSize = this.FontSize,
				Owner = Window.GetWindow(this),
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				ShowInTaskbar = false,
				SnapsToDevicePixels = true,
				UseLayoutRounding = true,
				ContentTemplate = (DataTemplate)this.FindResource("PlacementDialogContent")
			};
			dialog.SetResourceReference(Control.BackgroundProperty, "ThemeBrushKey");
			dialog.SetResourceReference(Control.ForegroundProperty, "ActiveForegroundBrushKey");
			return dialog;
		}

		// A reload or disposal of the settings closes the dialog.
		private static bool? ShowUntilEditingInvalidated(Window dialog, AppPlacementSettingsViewModel owner)
		{
			EventHandler invalidate = (_, __) => dialog.Close();
			owner.EditingInvalidated += invalidate;
			try
			{
				return dialog.ShowDialog();
			}
			finally
			{
				owner.EditingInvalidated -= invalidate;
			}
		}

		private void FocusRow(PlacementRuleRow row)
		{
			this.Dispatcher.BeginInvoke(
				new Action(() => EditableRowInput.FocusField(this, row, string.IsNullOrEmpty(row.Destination) ? "Destination" : "AppText", selectText: true)),
				DispatcherPriority.Loaded);
		}
	}
}
