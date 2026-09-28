using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SylphyHorn.Services.AppPlacement
{
	/// <summary>Read once per inspection session. No activation, command execution, or process enumeration.</summary>
	internal sealed class PlacementPackageCatalog
	{
		private HashSet<string> _ids;
		private Exception _loadFailure;

		internal bool Contains(string appId)
		{
			if (this._loadFailure != null)
			{
				throw new InvalidOperationException("The installed app catalog is unavailable for this session.", this._loadFailure);
			}
			if (this._ids == null)
			{
				try
				{
					this._ids = Read();
				}
				catch (Exception ex)
				{
					this._loadFailure = ex;
					throw new InvalidOperationException("The installed app catalog is unavailable for this session.", ex);
				}
			}
			if (!this._ids.Contains(appId)) return false;
			uint count = 0, length = 0;
			var status = GetPackagesByPackageFamily(appId.Substring(0, appId.IndexOf('!')), ref count, IntPtr.Zero, ref length, IntPtr.Zero);
			return (status == 0 || status == 122) && count > 0;
		}

		private static HashSet<string> Read()
		{
			object shell = null, folder = null, items = null;
			try
			{
				shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
				folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
				if (folder == null)
				{
					throw new InvalidOperationException("The installed app catalog is unavailable.");
				}
				items = ((dynamic)folder).Items();
				int count = ((dynamic)items).Count;
				if (count > 10000)
				{
					throw new InvalidOperationException("The installed app catalog exceeds its limit.");
				}
				var result = new HashSet<string>(StringComparer.Ordinal);
				for (var i = 0; i < count; i++)
				{
					object item = null;
					try
					{
						item = ((dynamic)items).Item(i);
						string id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
						if (PlacementAppIdentityResolver.IsPackageApp(id, null))
						{
							result.Add(id);
						}
					}
					finally
					{
						Release(item);
					}
				}
				return result;
			}
			finally
			{
				Release(items);
				Release(folder);
				Release(shell);
			}
		}

		private static void Release(object value)
		{
			if (value != null && Marshal.IsComObject(value))
			{
				Marshal.ReleaseComObject(value);
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
	}
}
