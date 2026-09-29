using System;
using System.Collections.Generic;
using System.Windows;
using SylphyHorn.UI;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.Services
{
	internal interface INotificationPresentation : IDisposable
	{
		void Update(string title, string header, string body, NotificationVisualSettings visual);
	}

	internal interface INotificationPresenter
	{
		INotificationPresentation Present(DesktopNotificationRequest request);
		IDisposable Present(PinNotificationRequest request);
	}

	internal interface INotificationWindowHandle : IDisposable
	{
		object DataContext { set; }
		void Show();
	}

	internal interface INotificationWindowFactory
	{
		IReadOnlyList<Rect> GetSwitchAreas(uint display);
		INotificationWindowHandle CreateSwitch(Rect area, NotificationVisualSettings visual);
		INotificationWindowHandle CreatePin(PinTargetGeometry geometry, NotificationVisualSettings visual);
	}

	internal sealed class NotificationPresenter : INotificationPresenter
	{
		private readonly INotificationWindowFactory _windows;

		internal NotificationPresenter() : this(new NotificationWindowFactory()) { }

		internal NotificationPresenter(INotificationWindowFactory windows)
		{
			this._windows = windows ?? throw new ArgumentNullException(nameof(windows));
		}

		public INotificationPresentation Present(DesktopNotificationRequest request)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			var vmodel = new NotificationWindowViewModel(request.Title, request.Header, request.Body, request.Visual);
			var windows = new List<INotificationWindowHandle>();
			try
			{
				foreach (var area in this._windows.GetSwitchAreas(request.Visual.Display))
				{
					var window = this._windows.CreateSwitch(area, request.Visual);
					windows.Add(window);
					window.DataContext = vmodel;
					window.Show();
				}
				return new SwitchWindowPresentation(windows);
			}
			catch (Exception presentationException)
			{
				var cleanupException = DisposeAll(windows);
				if (cleanupException != null)
					throw CombineExceptions("Notification presentation and cleanup both failed.", presentationException, cleanupException);
				throw;
			}
		}

		public IDisposable Present(PinNotificationRequest request)
		{
			if (request == null) throw new ArgumentNullException(nameof(request));
			if (request.Geometry == null) throw new ArgumentException("Pin geometry is required.", nameof(request));
			var window = this._windows.CreatePin(request.Geometry, request.Visual);
			try
			{
				window.DataContext = new NotificationWindowViewModel(request.Title, request.Header, request.Body, request.Visual);
				window.Show();
				return window;
			}
			catch
			{
				window.Dispose();
				throw;
			}
		}

		private sealed class SwitchWindowPresentation : INotificationPresentation
		{
			private List<INotificationWindowHandle> _windows;

			internal SwitchWindowPresentation(List<INotificationWindowHandle> windows)
			{
				this._windows = windows;
			}

			public void Update(string title, string header, string body, NotificationVisualSettings visual)
			{
				var vmodel = new NotificationWindowViewModel(title, header, body, visual);
				foreach (var window in this._windows) window.DataContext = vmodel;
			}

			public void Dispose()
			{
				var windows = this._windows;
				if (windows == null) return;
				this._windows = null;
				var cleanupException = DisposeAll(windows);
				if (cleanupException != null) throw cleanupException;
			}
		}

		private static Exception DisposeAll(IEnumerable<INotificationWindowHandle> windows)
		{
			List<Exception> exceptions = null;
			foreach (var window in windows)
			{
				try
				{
					window.Dispose();
				}
				catch (Exception ex)
				{
					if (exceptions == null) exceptions = new List<Exception>();
					exceptions.Add(ex);
				}
			}

			if (exceptions == null) return null;
			return exceptions.Count == 1 ? exceptions[0] : new AggregateException("Multiple notification windows failed to close.", exceptions);
		}

		private static AggregateException CombineExceptions(string message, Exception operationException, Exception cleanupException)
		{
			var cleanupAggregate = cleanupException as AggregateException;
			if (cleanupAggregate == null) return new AggregateException(message, operationException, cleanupException);
			var exceptions = new List<Exception> { operationException };
			exceptions.AddRange(cleanupAggregate.InnerExceptions);
			return new AggregateException(message, exceptions);
		}
	}

	internal sealed class NotificationWindowFactory : INotificationWindowFactory
	{
		public IReadOnlyList<Rect> GetSwitchAreas(uint display)
			=> ResolveSwitchAreas(display, MonitorService.GetAreas(), () => MonitorService.GetCurrentArea().WorkArea);

		internal static IReadOnlyList<Rect> ResolveSwitchAreas(uint display, IReadOnlyList<Monitor> monitors, Func<Rect> currentArea)
		{
			if (monitors.Count == 0)
			{
				return Array.Empty<Rect>();
			}
			if (display == uint.MaxValue)
			{
				var areas = new Rect[monitors.Count];
				for (var index = 0; index < monitors.Count; index++)
				{
					areas[index] = monitors[index].WorkArea;
				}
				return areas;
			}
			if (display == 0 || display > monitors.Count)
			{
				return new[] { currentArea() };
			}
			return new[] { monitors[(int)display - 1].WorkArea };
		}

		public INotificationWindowHandle CreateSwitch(Rect area, NotificationVisualSettings visual)
			=> new NotificationWindowHandle(new SwitchWindow(area, visual));

		public INotificationWindowHandle CreatePin(PinTargetGeometry geometry, NotificationVisualSettings visual)
			=> new NotificationWindowHandle(new PinWindow(geometry, visual));

		private sealed class NotificationWindowHandle : INotificationWindowHandle
		{
			private Window _window;
			internal NotificationWindowHandle(Window window) { this._window = window; }
			public object DataContext { set { this._window.DataContext = value; } }
			public void Show() => this._window.Show();
			public void Dispose()
			{
				var window = this._window;
				if (window == null) return;
				this._window = null;
				window.Close();
			}
		}
	}
}
