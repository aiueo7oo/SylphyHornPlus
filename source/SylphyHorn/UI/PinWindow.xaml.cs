using System;
using System.Windows;
using System.Windows.Interop;
using SylphyHorn.Services;
using SylphyHorn.Serialization;

namespace SylphyHorn.UI
{
	partial class PinWindow
	{
		private readonly PinTargetGeometry _geometry;
		private readonly NotificationVisualSettings _visual;

		public PinWindow(IntPtr target)
			: this(NotificationRequestMaterializer.CapturePinGeometry(target), NotificationVisualSettings.Capture(Settings.General))
		{
		}

		internal PinWindow(PinTargetGeometry geometry, NotificationVisualSettings visual) : base(visual)
		{
			this._geometry = geometry;
			this._visual = visual ?? throw new ArgumentNullException(nameof(visual));
			this.InitializeComponent();
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			if (this._geometry != null)
			{
				var width = this.ActualWidth * this._geometry.DpiScaleX;
				var height = this.ActualHeight * this._geometry.DpiScaleY;

				this.Left = (this._geometry.Left + (this._geometry.Width - width) / 2) / this._geometry.DpiScaleX + this._visual.PinOffsetX;
				this.Top = (this._geometry.Top + (this._geometry.Height - height) / 2) / this._geometry.DpiScaleY - this._visual.PinOffsetY;
			}
		}
	}
}
