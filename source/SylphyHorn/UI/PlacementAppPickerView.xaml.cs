using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	public partial class PlacementAppPickerView : UserControl
	{
		internal event EventHandler Accepted;
		internal event EventHandler Cancelled;

		public PlacementAppPickerView()
		{
			this.InitializeComponent();
		}

		private void Choose(object sender, RoutedEventArgs args)
		{
			if ((this.DataContext as PlacementAppPickerViewModel)?.CanChoose == true) this.Accepted?.Invoke(this, EventArgs.Empty);
		}

		private void Cancel(object sender, RoutedEventArgs args) => this.Cancelled?.Invoke(this, EventArgs.Empty);

		private async void Browse(object sender, RoutedEventArgs args)
		{
			var model = this.DataContext as PlacementAppPickerViewModel;
			if (model == null) return;
			var dialog = new OpenFileDialog
			{
				Filter = model.Text["ExeFilter"] + "|*.exe",
				CheckFileExists = true,
				Multiselect = false,
				Title = model.Text["Browse"]
			};
			if (dialog.ShowDialog(Window.GetWindow(this)) == true && await model.SelectFileAsync(dialog.FileName)) this.Accepted?.Invoke(this, EventArgs.Empty);
		}
	}
}
