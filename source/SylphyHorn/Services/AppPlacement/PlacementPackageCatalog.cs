using System;
using System.Collections.Generic;
using System.Threading;
using static SylphyHorn.Services.AppPlacement.PlacementNativeMethods;

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
			return (status == ERROR_SUCCESS || status == ERROR_INSUFFICIENT_BUFFER) && count > 0;
		}

		private static HashSet<string> Read()
		{
			using (var appsFolder = PlacementAppsFolder.Open())
			{
				if (!appsFolder.IsAvailable)
				{
					throw new InvalidOperationException("The installed app catalog is unavailable.");
				}
				if (appsFolder.Count > PlacementAppsFolder.ItemLimit)
				{
					throw new InvalidOperationException("The installed app catalog exceeds its limit.");
				}
				var result = new HashSet<string>(StringComparer.Ordinal);
				appsFolder.ForEachItem(
					item =>
					{
						string id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
						if (PlacementAppIdentityResolver.IsPackageApp(id, null))
						{
							result.Add(id);
						}
					},
					CancellationToken.None);
				return result;
			}
		}
	}
}
