using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Services.AppPlacement
{
	public sealed class PlacementAppChoice
	{
		internal PlacementAppChoice(
			string name,
			string detail,
			string path,
			PlacementAppIdentity identity,
			BitmapSource icon = null,
			bool confirmPath = false,
			string problem = null)
		{
			this.Name = name;
			this.Detail = detail;
			this.Path = path;
			this.Identity = identity;
			this.Icon = icon;
			this.ConfirmPath = confirmPath;
			this.Problem = problem;
		}

		public string Name { get; }

		public string Detail { get; }

		public string Path { get; }

		public BitmapSource Icon { get; }

		internal PlacementAppIdentity Identity { get; }

		internal bool ConfirmPath { get; }

		internal string Problem { get; }
	}

	internal enum PlacementAppPresence
	{
		// Not checked, or not checkable (a package that the Shell could not resolve is not proof of removal).
		Unknown,
		Present,
		Missing,
	}

	internal sealed class PlacementAppIcon
	{
		internal PlacementAppIcon(BitmapSource icon, PlacementAppPresence presence)
		{
			this.Icon = icon;
			this.Presence = presence;
		}

		internal BitmapSource Icon { get; }

		internal PlacementAppPresence Presence { get; }
	}

	internal interface IPlacementAppCatalog
	{
		Task<IReadOnlyList<PlacementAppChoice>> ReadAsync(bool windows, CancellationToken cancellation, bool includeIcons = true);

		Task<PlacementAppChoice> ReadExecutableAsync(string path, CancellationToken cancellation);

		Task<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>> ReadIconsAsync(IReadOnlyCollection<PlacementAppIdentity> apps, CancellationToken cancellation);
	}

	internal sealed class PlacementAppCatalog : IPlacementAppCatalog
	{
		// Only explicit user queries enumerate applications/windows. One Shell operation at a time, including across reopened settings.
		private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);

		public Task<IReadOnlyList<PlacementAppChoice>> ReadAsync(bool windows, CancellationToken cancellation, bool includeIcons = true)
			=> OnSta<IReadOnlyList<PlacementAppChoice>>(token => windows ? ReadWindows(token, includeIcons) : ReadInstalled(token, includeIcons), cancellation);

		public Task<PlacementAppChoice> ReadExecutableAsync(string path, CancellationToken cancellation)
			=> OnSta(token => Executable(path), cancellation);

		public Task<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>> ReadIconsAsync(IReadOnlyCollection<PlacementAppIdentity> apps, CancellationToken cancellation)
			=> OnSta<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>>(
				token =>
				{
					var result = new Dictionary<PlacementAppIdentity, PlacementAppIcon>();
					foreach (var app in apps)
					{
						token.ThrowIfCancellationRequested();
						if (!result.ContainsKey(app))
						{
							result[app] = ReadIcon(app);
						}
					}
					return result;
				},
				cancellation);

		internal static PlacementAppIcon ReadIcon(PlacementAppIdentity app)
		{
			if (app.Kind == PlacementAppKind.PackageAppId)
			{
				// A package ID is not a file path; only the Shell can resolve it.
				var logo = Icon("shell:AppsFolder\\" + app.Value);
				return new PlacementAppIcon(logo, logo == null ? PlacementAppPresence.Unknown : PlacementAppPresence.Present);
			}
			if (!File.Exists(app.Value))
			{
				return new PlacementAppIcon(null, PlacementAppPresence.Missing);
			}
			return new PlacementAppIcon(Icon(app.Value) ?? DefaultApplicationIcon(), PlacementAppPresence.Present);
		}

		private static async Task<T> OnSta<T>(Func<CancellationToken, T> read, CancellationToken cancellation)
		{
			await Gate.WaitAsync(cancellation).ConfigureAwait(false);
			try
			{
				var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
				var thread = new Thread(() =>
				{
					try
					{
						cancellation.ThrowIfCancellationRequested();
						completion.TrySetResult(read(cancellation));
					}
					catch (OperationCanceledException)
					{
						completion.TrySetCanceled();
					}
					catch (Exception ex)
					{
						completion.TrySetException(ex);
					}
				})
				{
					IsBackground = true,
					Name = "Placement app selection"
				};
				thread.SetApartmentState(ApartmentState.STA);
				thread.Start();
				return await completion.Task.ConfigureAwait(false);
			}
			finally
			{
				Gate.Release();
			}
		}

		private static PlacementAppChoice Executable(string path)
		{
			var identity = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, path);
			if (!File.Exists(identity.Value))
			{
				throw new FileNotFoundException();
			}
			var name = FileVersionInfo.GetVersionInfo(identity.Value).FileDescription;
			return new PlacementAppChoice(
				string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name,
				identity.Value,
				identity.Value,
				identity,
				Icon(path) ?? DefaultApplicationIcon());
		}

		private static IReadOnlyList<PlacementAppChoice> ReadWindows(CancellationToken cancellation, bool includeIcons)
		{
			var handles = new List<IntPtr>();
			var ok = EnumWindows(
				(window, state) =>
				{
					if (cancellation.IsCancellationRequested || handles.Count == 4096) return false;
					if (IsWindowVisible(window))
					{
						handles.Add(window);
					}
					return true;
				},
				IntPtr.Zero);
			cancellation.ThrowIfCancellationRequested();
			if (!ok)
			{
				throw new InvalidOperationException("Window enumeration unavailable or over capacity.");
			}
			var reader = new PlacementWindowReader();
			var choices = new List<PlacementAppChoice>();
			uint own;
			using (var process = Process.GetCurrentProcess()) own = (uint)process.Id;
			foreach (var window in handles)
			{
				cancellation.ThrowIfCancellationRequested();
				var read = reader.Read(window);
				if (read.Status == PlacementInspectionStatus.Excluded || read.Identity?.Owner.Id == own) continue;
				var title = new StringBuilder(1024);
				GetWindowText(window, title, title.Capacity);
				if (title.Length == 0) continue;
				var identity = read.Identity;
				var path = identity?.AppProcess?.Path ?? (identity?.App.Kind == PlacementAppKind.ExecutablePath ? identity.Owner.Path : null);
				var name = path == null ? title.ToString() : System.IO.Path.GetFileNameWithoutExtension(path);
				choices.Add(new PlacementAppChoice(
					name,
					title.ToString(),
					path,
					identity?.App,
					!includeIcons || path == null ? null : Icon(path) ?? DefaultApplicationIcon(),
					problem: identity == null ? "IdentityUnavailable" : null));
			}
			return choices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
		}

		private static IReadOnlyList<PlacementAppChoice> ReadInstalled(CancellationToken cancellation, bool includeIcons)
		{
			object shell = null, folder = null, items = null;
			try
			{
				shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
				folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
				if (folder == null)
				{
					throw new InvalidOperationException("AppsFolder unavailable.");
				}
				items = ((dynamic)folder).Items();
				int count = ((dynamic)items).Count;
				if (count > 10000)
				{
					throw new InvalidOperationException("AppsFolder over capacity.");
				}
				var choices = new List<PlacementAppChoice>();
				var packages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
				for (var i = 0; i < count; i++)
				{
					cancellation.ThrowIfCancellationRequested();
					object item = null;
					try
					{
						item = ((dynamic)items).Item(i);
						string name = ((dynamic)item).Name;
						string id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
						string target = ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string;
						string path = ((dynamic)item).Path;
						PlacementAppIdentity identity = null;
						bool confirm = false;
						string problem = null;
						if (PlacementAppIdentityResolver.IsPackageApp(id, null))
						{
							var family = id.Substring(0, id.IndexOf('!'));
							if (!packages.TryGetValue(family, out var applications))
							{
								packages[family] = applications = ReadPackage(family);
							}
							if (applications.TryGetValue(id.Substring(id.IndexOf('!') + 1), out target))
							{
								identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, id);
							}
							else
							{
								problem = "IdentityUnavailable";
							}
						}
						else
						{
							try
							{
								// Shell parsing names can be virtual items or commands, not filesystem paths.
								if (string.IsNullOrEmpty(target) && System.IO.Path.IsPathRooted(path ?? "") && string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
								{
									target = path;
								}
								if (File.Exists(target))
								{
									identity = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, target);
									confirm = true;
								}
							}
							catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.Runtime.Serialization.SerializationException) { }
							if (identity == null)
							{
								problem = "LauncherOnly";
							}
						}
						choices.Add(new PlacementAppChoice(
							name,
							identity?.Kind == PlacementAppKind.PackageAppId ? id : target ?? path,
							target,
							identity,
							includeIcons ? Icon("shell:AppsFolder\\" + (id ?? path)) : null,
							confirm,
							problem));
					}
					finally
					{
						Release(item);
					}
				}
				return choices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
			}
			finally
			{
				Release(items);
				Release(folder);
				Release(shell);
			}
		}

		// Read current-user registration and manifest; never equate a shared host or launch command with the app executable.
		private static Dictionary<string, string> ReadPackage(string family)
		{
			var applications = new Dictionary<string, string>(StringComparer.Ordinal);
			uint count = 0, length = 0;
			var status = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
			if (status != 122 || count == 0 || count > 128 || length > 1048576) return applications;
			var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
			var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
			try
			{
				if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0) return applications;
				for (var i = 0; i < count; i++)
				{
					var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
					uint capacity = 0;
					if (GetPackagePathByFullName(fullName, ref capacity, null) != 122 || capacity > 32768) continue;
					var path = new StringBuilder((int)capacity);
					if (GetPackagePathByFullName(fullName, ref capacity, path) != 0) continue;
					try
					{
						using (var xml = XmlReader.Create(
							System.IO.Path.Combine(path.ToString(), "AppxManifest.xml"),
							new XmlReaderSettings
{
	DtdProcessing = DtdProcessing.Prohibit,
	XmlResolver = null,
	MaxCharactersInDocument = 4194304
}))
						{
							foreach (var app in XDocument.Load(xml).Descendants().Where(node => node.Name.LocalName == "Application" && node.Parent?.Name.LocalName == "Applications"))
							{
								var id = (string)app.Attribute("Id");
								if (string.IsNullOrEmpty(id)) continue;
								string executable = null;
								var relative = (string)app.Attribute("Executable");
								if (!string.IsNullOrEmpty(relative))
								{
									var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(path.ToString(), relative));
									if (resolved.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(resolved))
									{
										executable = resolved;
									}
								}
								applications[id] = applications.ContainsKey(id) ? null : executable;
							}
						}
					}
					catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is ArgumentException) { }
				}
				return applications;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
				Marshal.FreeHGlobal(names);
			}
		}

		private static BitmapSource Icon(string parsingName)
		{
			object item = null;
			IntPtr bitmap = IntPtr.Zero;
			try
			{
				var iid = typeof(IShellItemImageFactory).GUID;
				if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item) != 0) return null;
				((IShellItemImageFactory)item).GetImage(new NativeSize
				{
					Width = 24,
					Height = 24
				}, 4, out bitmap);
				var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return source;
			}
			catch (Exception ex) when (ex is COMException || ex is ArgumentException)
			{
				return null;
			}
			finally
			{
				if (bitmap != IntPtr.Zero)
				{
					DeleteObject(bitmap);
				}
				Release(item);
			}
		}

		private static BitmapSource _defaultApplicationIcon;

		// The Shell's generic application icon, for executables that exist but whose own icon is unavailable.
		internal static BitmapSource DefaultApplicationIcon()
		{
			if (_defaultApplicationIcon != null) return _defaultApplicationIcon;
			var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
			if (SHGetStockIconInfo(StockIconApplication, StockIconHandle, ref info) != 0 || info.Icon == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return _defaultApplicationIcon = source;
			}
			catch (Exception ex) when (ex is COMException || ex is ArgumentException)
			{
				return null;
			}
			finally
			{
				DestroyIcon(info.Icon);
			}
		}

		private static void Release(object value)
		{
			if (value != null && Marshal.IsComObject(value))
			{
				Marshal.ReleaseComObject(value);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct NativeSize { internal int Width, Height; }

		[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellItemImageFactory { void GetImage(NativeSize size, uint flags, out IntPtr bitmap); }

		private delegate bool EnumWindow(IntPtr window, IntPtr parameter);

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackagePathByFullName(string name, ref uint length, StringBuilder path);

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr value);

		private const uint StockIconApplication = 2, StockIconHandle = 0x100;

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct StockIconInfo
		{
			internal uint Size;
			internal IntPtr Icon;
			internal int SystemImageIndex;
			internal int IconIndex;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Path;
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);

		[DllImport("user32.dll")]
		private static extern bool DestroyIcon(IntPtr icon);
	}
}
