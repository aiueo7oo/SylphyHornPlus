using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.UI
{
	public partial class PlacementApplyView : UserControl
	{
		private readonly DispatcherTimer _refresh;

		public PlacementApplyView()
		{
			this.InitializeComponent();
			this._refresh = new DispatcherTimer(
				TimeSpan.FromSeconds(1),
				DispatcherPriority.Background,
				(_, __) => (this.DataContext as PlacementApplyViewModel)?.RefreshStatus(),
				this.Dispatcher);
			this._refresh.Stop();
		}

		private void OnLoaded(object sender, RoutedEventArgs args) => this._refresh.Start();

		private void OnUnloaded(object sender, RoutedEventArgs args) => this._refresh.Stop();

		private void Close(object sender, RoutedEventArgs args) => Window.GetWindow(this)?.Close();
	}
}
