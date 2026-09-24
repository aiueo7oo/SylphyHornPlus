using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
			else this._refresh.Stop();
		}

		private void AddClosing(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is PlacementClosingGroup group)
			{
				var row = group.Owner.AddClosingRow(group);
				this.Dispatcher.BeginInvoke(new Action(() =>
				{
					this.UpdateLayout();
					var field = FindField(this, row, "Destination");
					field?.BringIntoView();
					field?.Focus();
				}), DispatcherPriority.Input);
			}
		}

		private void ClosingDestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var row = (PlacementClosingRow)combo.DataContext;
			var text = combo.Text;
			row.Group.Owner.RefreshDestinationChoices();
			combo.ItemsSource = row.Group.Owner.Groups[row.Group.Kind == SylphyHorn.AppPlacement.PlacementDestinationKind.Name ? 0 : 1].Choices;
			combo.Text = text;
			combo.Tag = text;
		}

		private void ClosingDestinationClosed(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			if (combo.Tag == null) return;
			combo.Tag = null;
			this.ClosingCommit(sender, args);
		}

		private async void ClosingCommit(object sender, EventArgs args)
		{
			if (sender is ComboBox combo && (combo.IsDropDownOpen || (args is KeyboardFocusChangedEventArgs && combo.IsKeyboardFocusWithin))) return;
			if ((sender as FrameworkElement)?.DataContext is PlacementClosingRow row) await row.Group.Owner.CommitClosingAsync(row);
		}

		private async void ClosingInputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is PlacementClosingRow row)) return;
			if (args.Key == Key.Escape)
			{
				if (FindField(this, row, "Destination") is ComboBox combo)
				{
					combo.Tag = null;
					combo.IsDropDownOpen = false;
				}
				row.Restore();
				args.Handled = true;
			}
			else if (args.Key == Key.Enter) { await row.Group.Owner.CommitClosingAsync(row); args.Handled = true; }
		}

		private void OpenFollowMenu(object sender, RoutedEventArgs args)
		{
			var button = (Button)sender;
			button.ContextMenu.PlacementTarget = button;
			button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
			button.ContextMenu.IsOpen = true;
		}

		private async void FollowSelected(object sender, RoutedEventArgs args)
		{
			if (sender is MenuItem item && item.DataContext is PlacementRuleRow row)
			{
				row.FollowForeground = (string)item.Tag == "default" ? (bool?)null : (string)item.Tag == "true";
				await row.Group.Owner.CommitAsync(row);
				args.Handled = true;
			}
		}

		private void Add(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is PlacementRuleGroup group) this.FocusRow(group.Owner.AddRow(group));
		}

		private void DestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var text = combo.Text;
			var group = ((PlacementRuleRow)combo.DataContext).Group;
			group.Owner.RefreshDestinationChoices();
			// Refresh only this control, preserving text in other rows even when a desktop was renamed or removed.
			combo.ItemsSource = group.Choices;
			combo.Text = text;
			combo.Tag = text;
		}

		private void DestinationClosed(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			if (combo.Tag == null) return;
			combo.Tag = null;
			this.Commit(sender, args);
		}

		private async void Commit(object sender, EventArgs args)
		{
			if (sender is ComboBox combo && args is KeyboardFocusChangedEventArgs && (combo.IsDropDownOpen || combo.IsKeyboardFocusWithin)) return;
			if ((sender as FrameworkElement)?.DataContext is PlacementRuleRow row)
			{
				// Moving between fields of a new row must not show an error before the user has filled it in.
				if (row.Saved == null && (string.IsNullOrWhiteSpace(row.AppText) || string.IsNullOrWhiteSpace(row.Destination))) return;
				await row.Group.Owner.CommitAsync(row);
			}
		}

		private async void InputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is PlacementRuleRow row)) return;
			// Handle Escape on the row, before ComboBox's class handler closes the popup and commits it.
			ComboBox combo = null;
			TextBox textBox = null;
			for (var source = args.OriginalSource as DependencyObject; source != null && source != sender; source = VisualTreeHelper.GetParent(source))
			{
				if (source is ComboBox box) combo = box;
				if (source is TextBox editor) textBox = editor;
			}
			if (combo == null && textBox == null) return;
			if (combo != null && combo.IsDropDownOpen)
			{
				if (args.Key == Key.Escape)
				{
					var text = combo.Tag as string;
					combo.Tag = null;
					combo.IsDropDownOpen = false;
					combo.Text = text;
					args.Handled = true;
				}
				return;
			}
			if (args.Key == Key.Escape)
			{
				row.Group.Owner.Revert(row);
				args.Handled = true;
			}
			else if (args.Key == Key.Enter)
			{
				args.Handled = true;
				await row.Group.Owner.CommitAsync(row);
			}
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
				var dialog = new MetroWindow
				{
					Title = picker.Text["ChooseApp"],
					Content = view,
					Width = 660,
					Height = 500,
					MinWidth = 480,
					MinHeight = 380,
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
				view.Accepted += (_, __) => dialog.DialogResult = true;
				view.Cancelled += (_, __) => dialog.DialogResult = false;
				dialog.Loaded += async (_, __) => await picker.WindowsCommand.ExecuteAsync(null);
				EventHandler invalidate = (_, __) => dialog.Close();
				group.Owner.EditingInvalidated += invalidate;
				try
				{
					if (dialog.ShowDialog() != true || !picker.CanChoose) return;
				}
				finally
				{
					group.Owner.EditingInvalidated -= invalidate;
				}
				row = row ?? group.Owner.AddRow(group);
				row.Use(picker.Selected);
				if (!string.IsNullOrWhiteSpace(row.Destination)) await group.Owner.CommitAsync(row);
				this.FocusRow(row);
			}
		}

		private void OpenApply(object sender, RoutedEventArgs args)
		{
			if (!(this.DataContext is AppPlacementSettingsViewModel owner)) return;
			using (var model = owner.CreateApplyViewModel())
			{
				var dialog = new MetroWindow
				{
					Title = model.Text["ApplyTitle"],
					Content = new PlacementApplyView { DataContext = model },
					Width = 890,
					Height = 520,
					MinWidth = 660,
					MinHeight = 380,
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
				dialog.Loaded += async (_, __) =>
				{
					if (model.RefreshCommand.CanExecute(null)) await model.RefreshCommand.ExecuteAsync(null);
				};
				dialog.Closing += (_, __) => model.Dispose();
				EventHandler invalidate = (_, __) => dialog.Close();
				owner.EditingInvalidated += invalidate;
				try
				{
					dialog.ShowDialog();
				}
				finally
				{
					owner.EditingInvalidated -= invalidate;
				}
			}
		}

		private void FocusRow(PlacementRuleRow row)
		{
			this.Dispatcher.BeginInvoke(
				new Action(() =>
			{
				this.UpdateLayout();
				var field = FindField(this, row, string.IsNullOrEmpty(row.Destination) ? "Destination" : "AppText");
				field?.BringIntoView();
				field?.Focus();
				if (field is TextBox text) text.SelectAll();
				else if (field is ComboBox combo && combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
				{
					editor.Focus();
					editor.SelectAll();
				}
			}),
				DispatcherPriority.Loaded);
		}

		private static Control FindField(DependencyObject parent, object row, string property)
		{
			for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
			{
				var child = VisualTreeHelper.GetChild(parent, index);
				if (child is TextBox text && ReferenceEquals(text.DataContext, row) && text.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == property) return text;
				if (child is ComboBox combo && ReferenceEquals(combo.DataContext, row) && combo.GetBindingExpression(ComboBox.TextProperty)?.ParentBinding.Path.Path == property) return combo;
				var found = FindField(child, row, property);
				if (found != null) return found;
			}
			return null;
		}
	}
}
