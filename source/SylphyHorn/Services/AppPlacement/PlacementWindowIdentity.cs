using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Services.AppPlacement
{
	internal enum PlacementInspectionStatus
	{
		Ready,
		NotReady,
		Excluded,
		Unavailable
	}

	internal sealed class PlacementProcessIdentity
	{
		internal PlacementProcessIdentity(uint id, long createdAt, string path, string family, string appId)
		{
			this.Id = id;
			this.CreatedAt = createdAt;
			this.Path = path;
			this.Family = family;
			this.AppId = appId;
		}

		internal uint Id { get; }

		internal long CreatedAt { get; }

		internal string Path { get; }

		internal string Family { get; }

		internal string AppId { get; }

		internal bool SameProcess(PlacementProcessIdentity other) => other != null && this.Id == other.Id
			&& this.CreatedAt == other.CreatedAt && string.Equals(this.Path, other.Path, StringComparison.OrdinalIgnoreCase);
	}

	internal sealed class PlacementWindowIdentity
	{
		internal PlacementWindowIdentity(
			IntPtr window,
			uint thread,
			PlacementProcessIdentity owner,
			PlacementProcessIdentity appProcess,
			PlacementAppIdentity app)
		{
			this.Window = window;
			this.Thread = thread;
			this.Owner = owner;
			this.AppProcess = appProcess;
			this.App = app;
		}

		internal IntPtr Window { get; }

		internal uint Thread { get; }

		internal PlacementProcessIdentity Owner { get; }

		internal PlacementProcessIdentity AppProcess { get; }

		internal PlacementAppIdentity App { get; }

		internal bool SameInstance(PlacementWindowIdentity other) => other != null && this.Window == other.Window && this.Thread == other.Thread
			&& this.Owner.SameProcess(other.Owner) && this.App.Equals(other.App)
			&& (this.AppProcess == null ? other.AppProcess == null : this.AppProcess.SameProcess(other.AppProcess));
	}

	internal sealed class PlacementWindowInspection
	{
		internal PlacementWindowInspection(PlacementInspectionStatus status, string reason, PlacementWindowIdentity identity = null)
		{
			this.Status = status;
			this.Reason = reason;
			this.Identity = identity;
		}

		internal PlacementInspectionStatus Status { get; }

		internal string Reason { get; }

		internal PlacementWindowIdentity Identity { get; }
	}

	/// <summary>App identity policy, independent of COM and window visibility.</summary>
	internal static class PlacementAppIdentityResolver
	{
		internal static PlacementWindowInspection Resolve(
			IntPtr window,
			uint thread,
			string windowClass,
			PlacementProcessIdentity owner,
			string windowAppId,
			IReadOnlyList<PlacementProcessIdentity> children,
			Func<string, bool> isRegisteredPackageApp)
		{
			if (owner == null || owner.Id == 0 || owner.CreatedAt <= 0 || string.IsNullOrEmpty(owner.Path))
			{
				return Unavailable("ProcessIdentityUnavailable");
			}
			var executable = Path.GetFileName(owner.Path);
			var host = string.Equals(executable, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
			if (host || windowClass == "ApplicationFrameWindow")
			{
				// Never turn the shared frame host into an executable-path rule.
				if (!host || windowClass != "ApplicationFrameWindow")
				{
					return Unavailable("UnrecognizedHost");
				}
				var apps = new Dictionary<string, PlacementProcessIdentity>(StringComparer.Ordinal);
				foreach (var child in children ?? Array.Empty<PlacementProcessIdentity>())
				{
					if (child == null || child.Id == owner.Id) continue;
					if (child.CreatedAt <= 0 || string.IsNullOrEmpty(child.Family) || !IsPackageApp(child.AppId, child.Family))
					{
						return Unavailable("HostChildIdentityUnavailable");
					}
					if (apps.TryGetValue(child.AppId, out var previous) && !previous.SameProcess(child))
					{
						return Unavailable("AmbiguousHostProcesses");
					}
					apps[child.AppId] = child;
				}
				if (apps.Count > 1)
				{
					return Unavailable("AmbiguousHostApps");
				}
				if (!string.IsNullOrEmpty(windowAppId))
				{
					if (!IsPackageApp(windowAppId, null) || !isRegisteredPackageApp(windowAppId))
					{
						return Unavailable("UnverifiedHostAppId");
					}
					if (apps.Count != 0 && !apps.ContainsKey(windowAppId))
					{
						return Unavailable("ConflictingHostAppId");
					}
					apps.TryGetValue(windowAppId, out var process);
					return Package(window, thread, owner, process, windowAppId);
				}
				if (apps.Count == 1)
				{
					var app = apps.Single();
					return Package(window, thread, owner, app.Value, app.Key);
				}
				return new PlacementWindowInspection(PlacementInspectionStatus.NotReady, "HostAppNotReady");
			}
			if (string.Equals(executable, "RuntimeBroker.exe", StringComparison.OrdinalIgnoreCase))
			{
				return Unavailable("SharedHost");
			}
			if (!string.IsNullOrEmpty(owner.Family))
			{
				var appId = !string.IsNullOrEmpty(windowAppId) ? windowAppId : owner.AppId;
				if (string.IsNullOrEmpty(appId))
				{
					return new PlacementWindowInspection(PlacementInspectionStatus.NotReady, "PackageAppNotReady");
				}
				if (!IsPackageApp(appId, owner.Family))
				{
					return Unavailable("PackageAppIdUnavailable");
				}
				if (!string.IsNullOrEmpty(owner.AppId) && !string.Equals(appId, owner.AppId, StringComparison.Ordinal))
				{
					return Unavailable("ConflictingPackageAppId");
				}
				if (string.IsNullOrEmpty(owner.AppId) && !isRegisteredPackageApp(appId))
				{
					return Unavailable("UnverifiedPackageAppId");
				}
				return Package(window, thread, owner, owner, appId);
			}
			// An explicit desktop AUMID is not a package identity. Normal Win32 apps use their executable.
			try
			{
				return Ready(new PlacementWindowIdentity(window, thread, owner, owner, new PlacementAppIdentity(PlacementAppKind.ExecutablePath, owner.Path)));
			}
			catch (SerializationException)
			{
				return Unavailable("ExecutablePathUnavailable");
			}
		}

		internal static bool IsPackageApp(string appId, string family)
		{
			if (string.IsNullOrWhiteSpace(appId)) return false;
			var split = appId.IndexOf('!');
			return split > 0 && split < appId.Length - 1 && appId.IndexOf('!', split + 1) < 0
				&& (family == null || string.Equals(appId.Substring(0, split), family, StringComparison.Ordinal));
		}

		private static PlacementWindowInspection Package(IntPtr window, uint thread, PlacementProcessIdentity owner, PlacementProcessIdentity process, string id)
			=> Ready(new PlacementWindowIdentity(window, thread, owner, process, new PlacementAppIdentity(PlacementAppKind.PackageAppId, id)));

		private static PlacementWindowInspection Ready(PlacementWindowIdentity identity) => new PlacementWindowInspection(PlacementInspectionStatus.Ready, null, identity);

		private static PlacementWindowInspection Unavailable(string reason) => new PlacementWindowInspection(PlacementInspectionStatus.Unavailable, reason);
	}
}
