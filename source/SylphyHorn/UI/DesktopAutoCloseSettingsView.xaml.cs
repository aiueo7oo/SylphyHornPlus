using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SylphyHorn.AppPlacement;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	// Automatic closing of empty desktops, split from the app placement tab. The settings, the pending rows
	// and the save path stay in the shared AppPlacementSettingsViewModel, so switching tabs keeps edits.
	public partial class DesktopAutoCloseSettingsView : UserControl
	{
		public DesktopAutoCloseSettingsView()
		{
			this.InitializeComponent();
		}

		private void AddClosing(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is PlacementClosingGroup group)
			{
				var row = group.Owner.AddClosingRow(group);
				this.Dispatcher.BeginInvoke(new Action(() =>
				{
					this.UpdateLayout();
					var field = AppPlacementSettingsView.FindField(this, row, "Destination");
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
			combo.ItemsSource = row.Group.Owner.Groups[row.Group.Kind == PlacementDestinationKind.Name ? 0 : 1].Choices;
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
				if (AppPlacementSettingsView.FindField(this, row, "Destination") is ComboBox combo)
				{
					combo.Tag = null;
					combo.IsDropDownOpen = false;
				}
				row.Restore();
				args.Handled = true;
			}
			else if (args.Key == Key.Enter) { await row.Group.Owner.CommitClosingAsync(row); args.Handled = true; }
		}
	}
}
