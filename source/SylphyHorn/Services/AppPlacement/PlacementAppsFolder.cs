using System;
using System.Threading;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>One shell:AppsFolder enumeration on the calling STA thread. Dispose releases every COM object it obtained.</summary>
	internal sealed class PlacementAppsFolder : IDisposable
	{
		internal const int ItemLimit = 10000;

		private object _shell;
		private object _folder;
		private object _items;

		private PlacementAppsFolder() { }

		/// <summary>False when the Shell has no AppsFolder namespace.</summary>
		internal bool IsAvailable => this._folder != null;

		internal int Count { get; private set; }

		internal static PlacementAppsFolder Open()
		{
			var appsFolder = new PlacementAppsFolder();
			try
			{
				appsFolder._shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
				appsFolder._folder = ((dynamic)appsFolder._shell).NameSpace("shell:AppsFolder");
				if (appsFolder._folder != null)
				{
					appsFolder._items = ((dynamic)appsFolder._folder).Items();
					appsFolder.Count = ((dynamic)appsFolder._items).Count;
				}
				return appsFolder;
			}
			catch
			{
				appsFolder.Dispose();
				throw;
			}
		}

		/// <summary>Visits items in Shell order. Each item is released as soon as its visit returns or throws.</summary>
		internal void ForEachItem(Action<object> visit, CancellationToken cancellation)
		{
			for (var i = 0; i < this.Count; i++)
			{
				cancellation.ThrowIfCancellationRequested();
				object item = null;
				try
				{
					item = ((dynamic)this._items).Item(i);
					visit(item);
				}
				finally
				{
					PlacementNativeMethods.ReleaseComObject(item);
				}
			}
		}

		public void Dispose()
		{
			PlacementNativeMethods.ReleaseComObject(this._items);
			PlacementNativeMethods.ReleaseComObject(this._folder);
			PlacementNativeMethods.ReleaseComObject(this._shell);
			this._items = this._folder = this._shell = null;
		}
	}
}
