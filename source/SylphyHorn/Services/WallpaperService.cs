using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using SylphyHorn.Interop;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services.DesktopTransitions;

namespace SylphyHorn.Services
{
	public class WallpaperService : IDisposable
	{
		private static readonly ImageFormatSupportDetector[] _detectors =
		{
			new JpegXrSupportDetector(),
			new WebPSupportDetector(),
			new HeifSupportDetector(),
		};

		private static readonly Tuple<string, string, string>[] _defaultSupportedFormats =
		{
			Tuple.Create("JPEG", "JPEG", "*.jpg;*.jpeg;*.jpe;*.jfif"),
			Tuple.Create("PNG", "PNG", "*.png"),
			Tuple.Create("BMP", "Bitmap", "*.bmp;*.dib"),
			Tuple.Create("GIF", "GIF", "*.gif"),
			Tuple.Create("TIFF", "TIFF", "*.tif;*.tiff"),
		};

		public static readonly string SupportedFormats = CreateSupportFormatText();
		public static string[] SupportedFileTypes { get; } = _defaultSupportedFormats.Select(f => f.Item1).Concat(_detectors.Where(d => d.IsSupported).Select(d => d.FileType)).ToArray();
		public static WallpaperService Instance { get; } = new WallpaperService();

		private readonly WallpaperApplyQueue _applyQueue;
		private DesktopTransitionRuntime _runtime;
		private IDisposable _settingsSubscription;

		private WallpaperService()
		{
			this._applyQueue = new WallpaperApplyQueue(
				ApplyDesktopWallpaper,
				exception => LoggingService.Instance.Register(exception),
				action => Task.Run(action));
		}

		internal void BindDesktopRuntime(DesktopTransitionRuntime runtime)
		{
			if (runtime == null) throw new ArgumentNullException(nameof(runtime));
			if (this._runtime != null)
			{
				if (ReferenceEquals(this._runtime, runtime)) return;
				throw new InvalidOperationException("WallpaperService cannot be rebound to another desktop runtime.");
			}
			this._runtime = runtime;
			runtime.StateChanged += this.OnDesktopStateChanged;
			this._settingsSubscription = SettingsService.ObserveWallpaperSettings(Settings.General,
				() => this.ApplyCurrent(runtime.State));
			this.ApplyCurrent(runtime.State);
		}

		private void OnDesktopStateChanged(object sender, DesktopRuntimeStateChanged e)
		{
			var state = e.Change.Snapshot;
			var current = state.CurrentDesktopId;
			if (!current.HasValue) return;
			if (e.Change.Kind == DesktopStateChangeKind.CurrentChanged || e.Change.Kind == DesktopStateChangeKind.Initialized || e.Change.Kind == DesktopStateChangeKind.Reset ||
				e.Change.WallpaperChanges.Any(change => change.Id == current.Value) || e.Change.PositionChanges.Any(change => change.Id == current.Value))
				this.ApplyCurrent(state);
		}

		private void ApplyCurrent(DesktopRuntimeState state)
		{
			if (state?.CurrentDesktopId == null || !state.Records.TryGetValue(state.CurrentDesktopId.Value, out var record)) return;
			var path = record.WallpaperPath.HasValue ? record.WallpaperPath.Value : null;
			if (!ProductInfo.IsWallpaperSupportBuild && !Settings.General.ChangeBackgroundEachDesktop) path = null;
			this._applyQueue.Enqueue(path, record.WallpaperPosition);
		}

		internal static void ApplyDesktopWallpaper(string path, WallpaperPosition position)
		{
			var wallpaper = DesktopWallpaperFactory.Create();
			if (!ProductInfo.IsWallpaperSupportBuild && !string.IsNullOrEmpty(path)) wallpaper.SetWallpaper(null, path);
			var target = (DesktopWallpaperPosition)position;
			if (wallpaper.GetPosition() != target) wallpaper.SetPosition(target);
		}

		internal void ApplyDesktopWallpaperNow(string path, WallpaperPosition position)
			=> this._applyQueue.ApplyNow(path, position);

		public void Dispose()
		{
			if (this._runtime != null) this._runtime.StateChanged -= this.OnDesktopStateChanged;
			this._settingsSubscription?.Dispose();
			this._settingsSubscription = null;
			this._runtime = null;
			this._applyQueue.Dispose();
		}

		public static void SetWallpaperEnabled(bool enabled)
		{
			var wallpaper = DesktopWallpaperFactory.Create();
			wallpaper.Enable(enabled);
		}

		public static void SetBackgroundColor(Color color)
		{
			var wallpaper = DesktopWallpaperFactory.Create();
			wallpaper.SetBackgroundColor(new COLORREF { R = color.R, G = color.G, B = color.B });
		}

		public static Tuple<Color, string> GetCurrentColorAndWallpaper()
		{
			var wallpaper = DesktopWallpaperFactory.Create();
			var colorref = wallpaper.GetBackgroundColor();
			string path = null;
			if (wallpaper.GetMonitorDevicePathCount() >= 1)
			{
				var monitorId = wallpaper.GetMonitorDevicePathAt(0);
				path = wallpaper.GetWallpaper(monitorId);
			}
			return Tuple.Create(Color.FromRgb(colorref.R, colorref.G, colorref.B), path);
		}

		private static string CreateSupportFormatText()
		{
			var defaultExtensions = string.Join(";", _defaultSupportedFormats.Select(f => f.Item3).Concat(_detectors.Where(d => d.IsSupported).SelectMany(d => d.Extensions.Select(e => $"*{e}"))));
			return $"Image File ({defaultExtensions})|{defaultExtensions}|" +
				string.Join("|", _defaultSupportedFormats.Select(f => $"{f.Item2} ({f.Item3})|{f.Item3}").Concat(_detectors.Where(d => d.IsSupported).Select(d => d.FormatInfo)));
		}
	}

	public enum WallpaperPosition : byte
	{
		Center = 0,
		Tile,
		Stretch,
		Fit,
		Fill,
		Span,
	}
}
