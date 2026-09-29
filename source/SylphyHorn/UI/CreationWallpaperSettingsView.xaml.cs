using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
			this.Dispatcher.BeginInvoke(
				new Action(() => EditableRowInput.FocusField(this, row, "Destination", selectText: false)),
				DispatcherPriority.Input);
		}

		private void DestinationOpened(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			var group = ((CreationWallpaperRow)combo.DataContext).Group;
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
			if (!((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row)) return;
			// Moving between the fields of a new entry must not show an error before both are filled in.
			if (row.Saved == null && (string.IsNullOrWhiteSpace(row.Destination) || string.IsNullOrWhiteSpace(row.WallpaperPath)))
			{
				return;
			}
			await row.Group.Owner.CommitAsync(row);
		}

		private async void ChooseImage(object sender, RoutedEventArgs args)
		{
			if ((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row)
			{
				await row.Group.Owner.ChooseImageAsync(row);
			}
		}

		private async void InputKey(object sender, KeyEventArgs args)
		{
			if (!((sender as FrameworkElement)?.DataContext is CreationWallpaperRow row)) return;
			var owner = row.Group.Owner;
			await EditableRowInput.HandleEntryKeyAsync((DependencyObject)sender, args, () => owner.Revert(row), () => owner.CommitAsync(row));
		}
	}
}
