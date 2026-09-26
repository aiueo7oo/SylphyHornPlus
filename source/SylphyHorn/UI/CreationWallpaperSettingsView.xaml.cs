using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	public partial class CreationWallpaperSettingsView : UserControl
	{
		public CreationWallpaperSettingsView()
		{
			this.InitializeComponent();
		}

		private void Add(object sender, RoutedEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is CreationWallpaperGroup group)) return;
			var row = group.Owner.AddRow(group);
			this.Dispatcher.BeginInvoke(new Action(() =>
			{
				this.UpdateLayout();
				var field = AppPlacementSettingsView.FindField(this, row, "Destination");
				field?.BringIntoView();
				field?.Focus();
			}), DispatcherPriority.Input);
		}

		private void DestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var text = combo.Text;
			var group = ((CreationWallpaperRow)combo.DataContext).Group;
			group.Owner.RefreshDestinationChoices();
			// Refresh only this control, preserving text typed in other rows.
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
			if (!((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row)) return;
			// Moving between the fields of a new entry must not show an error before both are filled in.
			if (row.Saved == null && (string.IsNullOrWhiteSpace(row.Destination) || string.IsNullOrWhiteSpace(row.WallpaperPath))) return;
			await row.Group.Owner.CommitAsync(row);
		}

		private async void ChooseImage(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row) await row.Group.Owner.ChooseImageAsync(row);
		}

		private async void InputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row)) return;
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
	}
}
