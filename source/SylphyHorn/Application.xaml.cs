using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MetroRadiance.UI;
using MetroTrilithon.Lifetime;
using MetroTrilithon.Threading.Tasks;
using SylphyHorn.Interop;
using SylphyHorn.Lifetime;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.UI;

namespace SylphyHorn
{
	sealed partial class Application : IDisposableHolder
	{
		private readonly DisposableCollection _compositeDisposable = new DisposableCollection();
		private readonly CancellationTokenSource _startupCancellation = new CancellationTokenSource();
		private ApplicationPreparation _preparation;
		private StartupTrace _startupTrace;
		private Task _startupTask;
		private int _shutdownStarted;

		internal HookService HookService { get; private set; }

		internal TaskTrayIcon TaskTrayIcon { get; private set; }

		protected override void OnStartup(StartupEventArgs e)
		{
			Args = new CommandLineArgs(e.Args);

			if (Args.Setup)
			{
				this.SetupShortcut();
			}

			this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			this.DispatcherUnhandledException += this.HandleDispatcherUnhandledException;
			TaskLog.Occured += (sender, log) => LoggingService.Instance.Register(log);
			this._startupTrace = new StartupTrace();
			this._startupTrace.Write(StartupPhase.ProcessStart);

			if (ProductInfo.OSBuild < 14393)
			{
				this._startupTrace.Write(StartupPhase.ShutdownOrFailure, StartupTraceResult.NotSupported);
				MessageBox.Show("This application is supported on Windows 10 Anniversary Update (build 14393) or later.", "Not supported", MessageBoxButton.OK, MessageBoxImage.Stop);
				this.BeginShutdown();
				return;
			}

#if !DEBUG
			var acquireTimeout = Args.Restarted.HasValue ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
			var appInstance = new SingleInstance(typeof(Application).Assembly, acquireTimeout).AddTo(this);
			if (!appInstance.IsFirst)
			{
				this._startupTrace.Write(StartupPhase.SingleInstance, StartupTraceResult.DuplicateInstance);
				if (Args.Restarted.HasValue)
				{
					var now = DateTimeOffset.Now;
					ReportException(
						"SingleInstance",
						typeof(Application),
						new TimeoutException($@"Failed to acquire the application Mutex within the restart timeout.
Mutex name: {appInstance.MutexName}
Wait time: {acquireTimeout}
Restarted: {Args.Restarted.Value}
PID: {Process.GetCurrentProcess().Id}
Time: {now:O}"));
					return;
				}

				this.BeginShutdown();
				return;
			}
#endif
			this._startupTrace.Write(StartupPhase.SingleInstance, StartupTraceResult.Succeeded);
			base.OnStartup(e);
			this._startupTask = this.StartApplicationAsync();
		}

		private async Task StartApplicationAsync()
		{
			try
			{
				var readiness = new ShellReadiness();
				var result = await readiness.WaitAndContinueAsync(
					TimeSpan.FromSeconds(30),
					TimeSpan.FromMilliseconds(250),
					3,
					async () =>
					{
						this._startupTrace.Write(StartupPhase.ShellReady, StartupTraceResult.Succeeded);
						await this.InitializeAfterShellReadyAsync();
					},
					this._startupCancellation.Token);
				if (result == ShellReadinessResult.Ready) return;

				this._startupTrace.Write(
					StartupPhase.ShellReady,
					result == ShellReadinessResult.Cancelled ? StartupTraceResult.Cancelled : StartupTraceResult.TimedOut);
				this._startupTrace.Write(StartupPhase.ShutdownOrFailure, result == ShellReadinessResult.Cancelled ? StartupTraceResult.Cancelled : StartupTraceResult.TimedOut);
				this.BeginShutdown();
			}
			catch (Exception ex)
			{
				this._startupTrace.Write(StartupPhase.ShutdownOrFailure, StartupTraceResult.Failed, ex.GetType(), ex.HResult);
				LoggingService.Instance.Register(ex);
				this.BeginShutdown();
			}
		}

		private async Task InitializeAfterShellReadyAsync()
		{
			await LocalSettingsProvider.Instance.LoadOrMigrateAsync();
			this._startupTrace.Write(StartupPhase.SettingsLoaded, StartupTraceResult.Succeeded);

			Settings.General.Culture.Subscribe(x => ResourceService.Current.ChangeCulture(x)).AddTo(this);
			ResourceService.Current.CaptureStartupCulture();
			ThemeService.Current.Register(this, Theme.Windows, Accent.Windows);

			this.HookService = new HookService().AddTo(this);

			this._preparation = new ApplicationPreparation(this.HookService, this.BeginShutdown, this, this._startupTrace);
			var preparation = this._preparation;
			this.TaskTrayIcon = preparation.CreateTaskTrayIcon().AddTo(this);

			preparation.VirtualDesktopInitialized += () =>
			{
				this.TaskTrayIcon.Show();
				this._startupTrace.Write(StartupPhase.TrayShown, StartupTraceResult.Succeeded);
				this.TaskTrayIcon.Reload();
				if (Settings.General.FirstTime)
				{
					preparation.CreateFirstTimeBaloon().Show();

					Settings.General.FirstTime.Value = false;
					LocalSettingsProvider.Instance.SaveAsync().Forget();
				}
				if (Settings.General.AlwaysShowDesktopNotification)
				{
					NotificationService.Instance.ShowCurrentDesktop();
				}
			};
			preparation.VirtualDesktopInitializationCanceled += () =>
			{
				this._startupTrace.Write(StartupPhase.ShutdownOrFailure, StartupTraceResult.Cancelled);
				this.BeginShutdown();
			};
			preparation.VirtualDesktopInitializationFailed += (ex, autoRestart) =>
			{
				this._startupTrace.Write(StartupPhase.ShutdownOrFailure, StartupTraceResult.Failed, ex?.GetType(), ex?.HResult ?? 0);
				this.TaskTrayIcon.Show();
				this._startupTrace.Write(StartupPhase.TrayShown, StartupTraceResult.Failed);
				LoggingService.Instance.Register(ex);

				if ((Args.Restarted == null || Args.Restarted == 0) && autoRestart)
				{
					try
					{
						Restart();
						this.BeginShutdown();
						return;
					}
					catch (Exception ex2)
					{
						LoggingService.Instance.Register(ex2);
					}
				}
				this.RestartOrShutdown("Virtual desktop initialization is failed.", "Virtual Desktop Initialization Failed");
			};
			this._startupTrace.Write(StartupPhase.ProviderInitStarted);
			preparation.PrepareVirtualDesktop();

			NotificationService.Instance.AddTo(this);
			WallpaperService.Instance.AddTo(this);
		}

		private async void BeginShutdown()
		{
			if (Interlocked.Exchange(ref this._shutdownStarted, 1) != 0) return;
			this._startupCancellation.Cancel();
			try
			{
				if (this._preparation != null) await this._preparation.ShutdownAsync();
			}
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
			}
			base.Shutdown();
		}
		protected override void OnExit(ExitEventArgs e)
		{
			base.OnExit(e);
			((IDisposable)this).Dispose();
		}

		private void SetupShortcut()
		{
			var startup = new Startup();
			if (!startup.IsExists)
			{
				startup.Create();
			}
		}

		private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
		{
			LoggingService.Instance.Register(args.Exception);
			args.Handled = true;
		}

		private void RestartOrShutdown(string message, string caption)
		{
			var result = MessageBox.Show(
				$"{message}\n\nDo you want to restart {ProductInfo.Title} now?",
				caption,
				MessageBoxButton.YesNo,
				MessageBoxImage.Stop
			);
			if (result == MessageBoxResult.Yes)
			{
				try
				{
					Restart();
				}
				catch (Exception ex)
				{
					LoggingService.Instance.Register(ex);
					this.BeginShutdown();
					return;
				}
			}
			this.BeginShutdown();
		}

		#region IDisposable members

		ICollection<IDisposable> IDisposableHolder.CompositeDisposable => this._compositeDisposable;

		void IDisposable.Dispose()
		{
			this._compositeDisposable.Dispose();
		}

		#endregion
	}
}
