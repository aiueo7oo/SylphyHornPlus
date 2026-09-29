using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	// Shares settings, pending rows and the save path through AppPlacementSettingsViewModel,
	// so switching tabs keeps edits.
	public partial class DesktopAutoCloseSettingsView : UserControl
	{
		public DesktopAutoCloseSettingsView()
		{
			this.InitializeComponent();
		}

		private void AddClosing(object sender, RoutedEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is PlacementClosingGroup group)) return;
			var row = group.Owner.AddClosingRow(group);
			this.Dispatcher.BeginInvoke(
				new Action(() => EditableRowInput.FocusField(this, row, "Destination", selectText: false)),
				DispatcherPriority.Input);
		}

		private void ClosingDestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var group = ((PlacementClosingRow)combo.DataContext).Group;
			group.Owner.RefreshDestinationChoices();
			EditableRowInput.DestinationListOpened(combo, group.Choices);
		}

		private void ClosingDestinationClosed(object sender, EventArgs args)
		{
			if (EditableRowInput.DestinationListClosed((ComboBox)sender))
			{
				this.ClosingCommit(sender, args);
			}
		}

		private async void ClosingCommit(object sender, EventArgs args)
		{
			if (EditableRowInput.IsFocusMovingWithinComboBox(sender, args)) return;
			if ((sender as FrameworkElement)?.DataContext is PlacementClosingRow row)
			{
				await row.Group.Owner.CommitClosingAsync(row);
			}
		}

		// Unlike the other entry rows, Esc and Enter apply wherever focus is in the row, and Esc also closes an open list.
		private async void ClosingInputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is PlacementClosingRow row)) return;
			if (args.Key == Key.Escape)
			{
				if (EditableRowInput.FindField(this, row, "Destination") is ComboBox combo)
				{
					EditableRowInput.CloseListWithoutCommit(combo);
				}
				row.Restore();
				args.Handled = true;
			}
			else if (args.Key == Key.Enter)
			{
				await row.Group.Owner.CommitClosingAsync(row);
				args.Handled = true;
			}
		}
	}
}
