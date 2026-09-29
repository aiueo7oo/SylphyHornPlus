using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDesktop;
using MetroTrilithon.Lifetime;
using SylphyHorn.Commands;
using SylphyHorn.Interop;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.DesktopTransitions;
using SylphyHorn.UI;
using SylphyHorn.UI.Bindings;
#if !NETFRAMEWORK
using System.Globalization;
using System.Windows.Threading;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.Commands;
#endif

namespace SylphyHorn
{
	using ActionRegister = Func<Func<ShortcutKey>, Action<IntPtr>, IDisposable>;

	public class ApplicationPreparation
	{
		private readonly HookService _hookService;
		private readonly Action _shutdownAction;
		private readonly IDisposableHolder _disposable;
		private readonly StartupTrace _startupTrace;
		private TaskTrayIcon _taskTrayIcon;
		private DesktopTransitionRuntime _desktopRuntime;
#if !NETFRAMEWORK
		private CliServer _cliServer;
		private CliAssignmentService _cliAssignments;
		private CliCreationWallpaperService _cliCreationWallpapers;
		private CliSettingsService _cliSettings;
		private CliShortcutService _cliShortcuts;
		private CliSettingsFileService _cliSettingsFiles;
		private CliStartupService _cliStartup;
		private CliSettingsResetService _cliSettingsReset;
#endif

		public event Action VirtualDesktopInitialized;
		public event Action VirtualDesktopInitializationCanceled;
		public event Action<Exception, bool> VirtualDesktopInitializationFailed;

		public ApplicationPreparation(HookService hookService, Action shutdownAction, IDisposableHolder disposable)
			: this(hookService, shutdownAction, disposable, null)
		{
		}

		internal ApplicationPreparation(HookService hookService, Action shutdownAction, IDisposableHolder disposable, StartupTrace startupTrace)
		{
			this._hookService = hookService;
			this._shutdownAction = shutdownAction;
			this._disposable = disposable;
			this._startupTrace = startupTrace;
			this._hookService.Reload = this.RegisterActions;
		}

		public void RegisterActions()
		{
			this.RegisterActions(Settings.ShortcutKey, this._hookService.RegisterKeyAction);
			this.RegisterActions(Settings.MouseShortcut, this._hookService.RegisterMouseAction);
		}

		public TaskTrayIcon CreateTaskTrayIcon()
		{
			if (this._taskTrayIcon == null)
			{
				const string iconUri = "pack://application:,,,/SylphyHorn;Component/.assets/tasktray.dark.ico";
				const string lightIconUri = "pack://application:,,,/SylphyHorn;Component/.assets/tasktray.light.ico";
				if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)) return null;
				if (!Uri.TryCreate(lightIconUri, UriKind.Absolute, out var lightUri)) return null;
				var darkIcon = IconHelper.GetIconFromResource(uri);
				var lightIcon = IconHelper.GetIconFromResource(lightUri);
				var menus = new[]
				{
					new TaskTrayIconItem(
						Resources.TaskTray_Menu_Settings,
						this.ShowSettings,
						() => Application.Args.CanSettings,
						() => this._desktopRuntime?.IsInitialized == true),
					new TaskTrayIconItem(Resources.TaskTray_Menu_Exit, this._shutdownAction),
#if DEBUG
					new TaskTrayIconItem("Tasktray Icon Test", () => new TaskTrayTestWindow().Show()),
#endif
				};
				this._taskTrayIcon = new TaskTrayIcon(darkIcon, lightIcon, menus);
			}
			return this._taskTrayIcon;
		}

		private void ShowSettings()
		{
			if (this._desktopRuntime == null || !this._desktopRuntime.IsInitialized) return;
			if (SettingsWindow.Instance != null)
			{
				SettingsWindow.Instance.Activate();
				return;
			}

			var window = this.CreateSettingsWindow();
			SettingsWindow.Instance = window;
			window.ShowDialog();
			SettingsWindow.Instance = null;
		}

		private SettingsWindow CreateSettingsWindow()
		{
			var window = new SettingsWindow();
			var dialogService = new SettingsDialogService();
			window.DataContext = new SettingsWindowViewModel(this._hookService, this._desktopRuntime, dialogService);
			return window;
		}

#if !NETFRAMEWORK
		private bool ShowSettingsFromCli()
		{
			if (!Application.Args.CanSettings || this._desktopRuntime?.IsInitialized != true) return false;
			if (SettingsWindow.Instance != null)
			{
				SettingsWindow.Instance.Activate();
				return true;
			}

			var window = this.CreateSettingsWindow();
			window.Closed += (_, __) =>
			{
				if (ReferenceEquals(SettingsWindow.Instance, window))
				{
					SettingsWindow.Instance = null;
				}
			};
			SettingsWindow.Instance = window;
			try { window.Show(); }
			catch
			{
				SettingsWindow.Instance = null;
				throw;
			}
			return true;
		}

		private Task<CliResponse> ExecuteCliAsync(CliCommand command, CancellationToken cancellation)
		{
			var operation = command.Operation;
			switch (operation)
			{
				case "exit":
				case "version":
				case "logs":
					return Task.FromResult(ExecuteHostCommand(command));
				case "settings reset":
					return this._cliSettingsReset.ExecuteAsync(command, cancellation);
				case "settings export":
				case "settings import":
					return this._cliSettingsFiles.ExecuteAsync(command, cancellation);
				case "app list":
					return this._cliAssignments.ExecuteAsync(command, cancellation);
				// These exact operations belong to the desktop runtime even though they share
				// the "app assignment " prefix routed to the assignment service below.
				case "app assignment resume":
					return this._desktopRuntime.ResumeCliPlacementAsync(command, cancellation);
				case "app assignment apply":
					return this._desktopRuntime.ApplyCliAssignmentsAsync(command, cancellation);
			}

			if (HasPrefix(operation, "startup "))
			{
				return this._cliStartup.ExecuteAsync(command, cancellation);
			}
			if (HasPrefix(operation, "shortcut "))
			{
				return this._cliShortcuts.ExecuteAsync(command, cancellation);
			}
			if (CliSettingsService.Handles(operation))
			{
				return this._cliSettings.ExecuteAsync(command, cancellation);
			}
			if (HasPrefix(operation, "desktop creation wallpaper "))
			{
				return this._cliCreationWallpapers.ExecuteAsync(command, cancellation);
			}
			if (HasPrefix(operation, "app assignment ") || HasPrefix(operation, "desktop autoclose "))
			{
				return this._cliAssignments.ExecuteAsync(command, cancellation);
			}
			if (HasPrefix(operation, "ui "))
			{
				return Task.FromResult(this.ExecuteUiCommand(command, cancellation));
			}

			// The remaining desktop and window commands are owned by the desktop runtime.
			return this._desktopRuntime.ExecuteCliAsync(command, cancellation);
		}

		private static bool HasPrefix(string operation, string prefix) => operation.StartsWith(prefix, StringComparison.Ordinal);

		private static CliResponse ExecuteHostCommand(CliCommand command)
		{
			switch (command.Operation)
			{
				case "exit":
					return CliResponse.Ok(command.Operation, new CliData { Accepted = true });
				case "version":
					return CliResponse.Ok(command.Operation, new CliData { Host = CliVersionInfo.Read(typeof(ApplicationPreparation).Assembly) });
				case "logs":
					return CliResponse.Ok(command.Operation, DescribeRecentLogs(command.Limit));
				default:
					throw new ArgumentException("The operation is not a host command.", nameof(command));
			}
		}

		private static CliData DescribeRecentLogs(int limit)
		{
			var entries = LoggingService.Instance.GetRecent(limit, out var totalCount);
			return new CliData
			{
				Logs = entries.Select(entry => new CliLog
				{
					Timestamp = entry.Log.DateTime.ToString("O", CultureInfo.InvariantCulture),
					Header = entry.Log.Header,
					Content = entry.Log.Content,
				}).ToArray(),
				TotalCount = totalCount,
				OmittedCount = totalCount - entries.Length,
			};
		}

		private CliResponse ExecuteUiCommand(CliCommand command, CancellationToken cancellation)
		{
			if (cancellation.IsCancellationRequested)
			{
				return CliResponse.Fail(command.Operation, "request_cancelled", "The request expired before the UI action was submitted.");
			}

			switch (command.Operation)
			{
				case "ui settings":
					if (!this.ShowSettingsFromCli())
					{
						return CliResponse.Fail(command.Operation, "settings_unavailable", "The settings window is unavailable.");
					}
					break;
				case "ui task-view":
					VirtualDesktopService.ShowTaskView();
					break;
				case "ui window-switch":
					VirtualDesktopService.ShowWindowSwitch();
					break;
				case "ui notification-toggle":
					NotificationService.Instance.ToggleCurrentDesktop();
					break;
				default:
					return CliResponse.Fail(command.Operation, "invalid_arguments", "Unknown UI command.");
			}
			return CliResponse.Ok(command.Operation, new CliData());
		}

		private void CreateCliServices(DesktopTransitionRuntime runtime)
		{
			Func<bool> runtimeAvailable = () => runtime.CliAvailable;
			Func<bool> runtimeAndInputAvailable = () => runtime.CliAvailable && !this._hookService.IsSuspended;
			Func<IDisposable> suspendInput = () => this._hookService.Suspend();
			Func<Task<SettingsSaveResult>> saveSettings = () => LocalSettingsProvider.Instance.SaveWithResultAsync();
			Action refreshSettingsWindow = () => OpenSettingsViewModel()?.RefreshAfterExternalSettings();

			this._cliSettingsReset = new CliSettingsResetService(
				reset: runtime.ResetSettingsAsync,
				available: runtimeAndInputAvailable,
				suspendInput: suspendInput,
				refresh: refreshSettingsWindow);
			this._cliSettingsFiles = new CliSettingsFileService(
				provider: LocalSettingsProvider.Instance,
				settingsPath: LocalSettingsProvider.Instance.FilePath,
				available: runtimeAndInputAvailable,
				suspendInput: suspendInput,
				commit: (stage, applyDesktops, token) => runtime.CommitPreparedImportAsync(stage, applyDesktops, token),
				refresh: refreshSettingsWindow,
				nameSupported: ProductInfo.IsNameSupportBuild);
			this._cliStartup = new CliStartupService(
				registration: new WindowsStartupRegistration(Environment.ProcessPath),
				available: runtimeAvailable,
				refresh: startup => OpenSettingsViewModel()?.RefreshAfterExternalStartup(startup.NormalRegistered, startup.ElevatedRegistered));
			this._cliShortcuts = new CliShortcutService(
				keyboard: Settings.ShortcutKey,
				mouse: Settings.MouseShortcut,
				general: Settings.General,
				save: saveSettings,
				available: runtimeAndInputAvailable,
				reload: () => this._hookService.Reload(),
				desktopCount: () => runtime.State.Order.Count);
			this._cliSettings = new CliSettingsService(
				settings: Settings.General,
				save: saveSettings,
				available: runtimeAvailable,
				startupCulture: ResourceService.Current.StartupCulture);
			this._cliCreationWallpapers = new CliCreationWallpaperService(
				settings: Settings.General,
				save: saveSettings,
				available: runtimeAvailable);
			this._cliAssignments = new CliAssignmentService(
				settings: Settings.AppPlacement,
				catalog: new PlacementAppCatalog(),
				save: saveSettings,
				available: runtimeAvailable,
				status: () => runtime.PlacementStatus);
			this._cliServer = new CliServer(
				name: CliProtocol.PipeName(ProductInfo.Company, ProductInfo.Product),
				execute: (command, token) => Application.Current.Dispatcher.InvokeAsync(
					() => this.ExecuteCliAsync(command, token), DispatcherPriority.Background, token).Task.Unwrap(),
				shutdown: () => Application.Current.Dispatcher.BeginInvoke(this._shutdownAction));
		}

		private static SettingsWindowViewModel OpenSettingsViewModel() => SettingsWindow.Instance?.DataContext as SettingsWindowViewModel;
#endif

		public TaskTrayBaloon CreateFirstTimeBaloon()
		{
			var baloon = this.CreateTaskTrayIcon().CreateBaloon();
			baloon.Title = ProductInfo.Title;
			baloon.Text = Resources.TaskTray_FirstTimeMessage;
			baloon.Timespan = TimeSpan.FromMilliseconds(5000);
			return baloon;
		}

		public void PrepareVirtualDesktop()
		{
			var provider = new VirtualDesktopProvider { ComInterfaceAssemblyPath = Path.Combine(Directories.LocalAppData.FullName, "assemblies") };
			provider.EnableDispatcherEventScheduling(Application.Current.Dispatcher);
			VirtualDesktop.Provider = provider;
			provider.Initialize().ContinueWith(task => this.CompleteProviderInitialization(task, provider), TaskScheduler.FromCurrentSynchronizationContext());
		}

		internal async void CompleteProviderInitialization(Task initialization, VirtualDesktopProvider provider)
		{
			var runtimeInitialized = false;
			if (initialization.IsCanceled)
			{
				this._startupTrace?.Write(StartupPhase.ProviderInitCompleted, StartupTraceResult.Cancelled);
				this.VirtualDesktopInitializationCanceled?.Invoke();
				return;
			}
			if (initialization.IsFaulted)
			{
				var exception = initialization.Exception;
				this._startupTrace?.Write(StartupPhase.ProviderInitCompleted, StartupTraceResult.Failed, exception?.GetType(), exception?.HResult ?? 0);
				this.VirtualDesktopInitializationFailed?.Invoke(initialization.Exception, false);
				return;
			}

			try
			{
				this._startupTrace?.Write(StartupPhase.ProviderInitCompleted, StartupTraceResult.Succeeded);
				var runtime = new DesktopTransitionRuntime(
					new VirtualDesktopProviderClient(provider),
					new ApplicationDesktopSettingsTransactions(LocalSettingsProvider.Instance, Settings.General, Settings.AppPlacement),
					new DispatcherDesktopOwnerContext(Application.Current.Dispatcher),
					new VirtualDesktopOperations());
				runtime.Faulted += (sender, fault) => LoggingService.Instance.Register(new DesktopRuntimeLog(fault));
				var result = await runtime.InitializeAsync(Settings.General.OverrideDesktopsOnStartup, CancellationToken.None);
				if (!result.Succeeded)
				{
					var cancelled = result.Status == DesktopRuntimeInitializationStatus.Cancelled
						|| result.Status == DesktopRuntimeInitializationStatus.ShuttingDown;
					this._startupTrace?.Write(StartupPhase.RuntimeInitialized, cancelled ? StartupTraceResult.Cancelled : StartupTraceResult.Failed);
					if (cancelled)
					{
						this.VirtualDesktopInitializationCanceled?.Invoke();
					}
					else
					{
						this.VirtualDesktopInitializationFailed?.Invoke(new InvalidOperationException("Virtual desktop runtime initialization did not produce a stable state."), false);
					}
					return;
				}
				this._startupTrace?.Write(StartupPhase.RuntimeInitialized, StartupTraceResult.Succeeded);
				runtimeInitialized = true;

				this._desktopRuntime = runtime;
				runtime.AddTo(this._disposable);
				this.CreateTaskTrayIcon().BindDesktopRuntime(runtime);
				NotificationService.Instance.BindDesktopRuntime(runtime, Application.Current.Dispatcher);
				WallpaperService.Instance.BindDesktopRuntime(runtime);
				SettingsService.StretchShortcutListsTo(runtime.State.Order.Count);
				this.RegisterActions();
				SettingsService.BindGeneralSettings(Settings.General, () => this._hookService.Reload(), alwaysShow =>
				{
					if (alwaysShow)
					{
						NotificationService.Instance.ShowCurrentDesktop();
					}
					else
					{
						NotificationService.Instance.HideCurrentDesktop();
					}
				}, () => this._taskTrayIcon.Reload()).AddTo(this._disposable);
				SettingsService.ObserveNotificationAppearance(Settings.General,
					NotificationService.Instance.RefreshAppearance).AddTo(this._disposable);
				Settings.AppPlacement.Configuration.Subscribe(configuration =>
				{
					_ = runtime.ConfigurePlacementAsync(configuration);
				}).AddTo(this._disposable);
				await runtime.ConfigurePlacementAsync(Settings.AppPlacement.Configuration.Value);
#if !NETFRAMEWORK
				try
				{
					this.CreateCliServices(runtime);
				}
				catch (Exception ex)
				{
					LoggingService.Instance.Register(ex);
				}
#endif
				this.CompleteSuccessfulInitialization();
			}
			catch (Exception ex)
			{
				if (!runtimeInitialized)
				{
					this._startupTrace?.Write(StartupPhase.RuntimeInitialized, StartupTraceResult.Failed, ex.GetType(), ex.HResult);
				}
				this.VirtualDesktopInitializationFailed?.Invoke(ex, false);
			}
		}

		internal void CompleteSuccessfulInitialization()
		{
			this._hookService.Start();
			this._startupTrace?.Write(StartupPhase.HookStarted, StartupTraceResult.Succeeded);
			this.VirtualDesktopInitialized?.Invoke();
		}

		internal async Task ShutdownAsync()
		{
#if !NETFRAMEWORK
			if (this._cliServer != null)
			{
				await this._cliServer.StopAsync();
			}
#endif
			if (this._desktopRuntime != null)
			{
				await this._desktopRuntime.ShutdownAsync();
			}
		}

		private sealed class DesktopRuntimeLog : ILog
		{
			internal DesktopRuntimeLog(DesktopRuntimeFault fault)
			{
				this.DateTime = DateTimeOffset.Now;
				this.Header = fault.Category;
				this.Content = "ExceptionType=" + (fault.ExceptionType ?? "none") + ";DesktopId=" + (fault.DesktopId.HasValue ? fault.DesktopId.Value.ToString("N").Substring(0, 8) : "none") + ";Sequence=" + (fault.Sequence?.ToString() ?? "none");
			}
			public DateTimeOffset DateTime { get; }
			public string Header { get; }
			public string Content { get; }
		}
		private void RegisterActions(ShortcutKeySettings settings, ActionRegister register)
		{
			register(() => settings.MoveLeft.ToShortcutKey(), hWnd => hWnd.MoveToLeft())
				.AddTo(this._disposable);

			register(() => settings.MoveLeftAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToLeft()?.Switch())
				.AddTo(this._disposable);

			register(() => settings.MoveRight.ToShortcutKey(), hWnd => hWnd.MoveToRight())
				.AddTo(this._disposable);

			register(() => settings.MoveRightAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToRight()?.Switch())
				.AddTo(this._disposable);

			register(() => settings.MoveNew.ToShortcutKey(), hWnd => hWnd.MoveToNew())
				.AddTo(this._disposable);

			register(() => settings.MoveNewAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToNew()?.Switch())
				.AddTo(this._disposable);

			register(() => settings.MoveToPrevious.ToShortcutKey(), hWnd => hWnd.MoveToPrevious())
				.AddTo(this._disposable);

			register(() => settings.MoveToPreviousAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToPrevious()?.Switch())
				.AddTo(this._disposable);

			var isKeyboardSettings = settings as MouseShortcutSettings == null;
			if (isKeyboardSettings)
			{
				if (Settings.General.OverrideWindowsDefaultKeyCombination)
				{
					register(() => settings.SwitchToLeftWithDefault.ToShortcutKey(), _ => { })
						.AddTo(this._disposable);

					register(() => settings.SwitchToRightWithDefault.ToShortcutKey(), _ => { })
						.AddTo(this._disposable);
				}
				else if (Settings.General.LoopDesktop)
				{
					register(
						() => settings.SwitchToLeftWithDefault.ToShortcutKey(),
						_ => VirtualDesktopService.GetLeft()?.Switch())
						.AddTo(this._disposable);

					register(
						() => settings.SwitchToRightWithDefault.ToShortcutKey(),
						_ => VirtualDesktopService.GetRight()?.Switch())
						.AddTo(this._disposable);
				}

				register(() => settings.SwitchToLeft.ToShortcutKey(), _ => VirtualDesktopService.GetLeft()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToRight.ToShortcutKey(), _ => VirtualDesktopService.GetRight()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToPrevious.ToShortcutKey(), _ => VirtualDesktopService.GetPrevious()?.Switch())
					.AddTo(this._disposable);
			}
			else
			{
				register(() => settings.SwitchToLeft.ToShortcutKey(), _ => VirtualDesktopService.GetLeft()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToRight.ToShortcutKey(), _ => VirtualDesktopService.GetRight()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToPrevious.ToShortcutKey(), _ => VirtualDesktopService.GetPrevious()?.Switch())
					.AddTo(this._disposable);
			}

			if (ProductInfo.IsReorderingSupportBuild)
			{
				register(() => settings.SwapDesktopLeft.ToShortcutKey(), _ => VirtualDesktopService.SwapCurrentForLeft())
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopRight.ToShortcutKey(), _ => VirtualDesktopService.SwapCurrentForRight())
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopFirst.ToShortcutKey(), _ => VirtualDesktopService.SwapCurrentForFirst())
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopLast.ToShortcutKey(), _ => VirtualDesktopService.SwapCurrentForLast())
					.AddTo(this._disposable);
			}
			else
			{
				register(() => settings.SwapDesktopLeft.ToShortcutKey(), _ => { })
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopRight.ToShortcutKey(), _ => { })
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopFirst.ToShortcutKey(), _ => { })
					.AddTo(this._disposable);

				register(() => settings.SwapDesktopLast.ToShortcutKey(), _ => { })
					.AddTo(this._disposable);
			}

			register(() => settings.CloseAndSwitchLeft.ToShortcutKey(), _ => VirtualDesktopService.CloseAndSwitchLeft())
				.AddTo(this._disposable);

			register(() => settings.CloseAndSwitchRight.ToShortcutKey(), _ => VirtualDesktopService.CloseAndSwitchRight())
				.AddTo(this._disposable);

			register(() => settings.ShowTaskView.ToShortcutKey(), _ => VirtualDesktopService.ShowTaskView())
				.AddTo(this._disposable);

			register(() => settings.ShowWindowSwitch.ToShortcutKey(), _ => VirtualDesktopService.ShowWindowSwitch())
				.AddTo(this._disposable);

			register(() => settings.Pin.ToShortcutKey(), hWnd => hWnd.Pin())
				.AddTo(this._disposable);

			register(() => settings.Unpin.ToShortcutKey(), hWnd => hWnd.Unpin())
				.AddTo(this._disposable);

			register(() => settings.TogglePin.ToShortcutKey(), hWnd => hWnd.TogglePin())
				.AddTo(this._disposable);

			register(() => settings.PinApp.ToShortcutKey(), hWnd => hWnd.PinApp())
				.AddTo(this._disposable);

			register(() => settings.UnpinApp.ToShortcutKey(), hWnd => hWnd.UnpinApp())
				.AddTo(this._disposable);

			register(() => settings.TogglePinApp.ToShortcutKey(), hWnd => hWnd.TogglePinApp())
				.AddTo(this._disposable);

			register(() => settings.ShowSettings.ToShortcutKey(), _ =>
				{
					if (Application.Args.CanSettings) this.ShowSettings();
				})
				.AddTo(this._disposable);

			register(() => settings.ToggleDesktopNotification.ToShortcutKey(), _ => NotificationService.Instance.ToggleCurrentDesktop())
				.AddTo(this._disposable);

			var desktopCount = VirtualDesktopService.Count;
			var switchToIndices = settings.SwitchToIndices.Value;
			for (var index = 0; index < desktopCount && index < switchToIndices.Count; ++index)
			{
				RegisterSpecifiedDesktopSwitching(index, switchToIndices[index].ToShortcutKey());
			}

			var swapDesktopIndices = settings.SwapDesktopIndices.Value;
			for (var index = 0; index < desktopCount && index < swapDesktopIndices.Count; ++index)
			{
				RegisterSpecifiedDesktopSwapping(index, swapDesktopIndices[index].ToShortcutKey());
			}

			var moveToIndices = settings.MoveToIndices.Value;
			for (var index = 0; index < desktopCount && index < moveToIndices.Count; ++index)
			{
				RegisterMovingToSpecifiedDesktop(index, moveToIndices[index].ToShortcutKey());
			}

			var moveToIndicesAndSwitch = settings.MoveToIndicesAndSwitch.Value;
			for (var index = 0; index < desktopCount && index < moveToIndicesAndSwitch.Count; ++index)
			{
				RegisterMovingToSpecifiedDesktopAndSwitch(index, moveToIndicesAndSwitch[index].ToShortcutKey());
			}

			void RegisterSpecifiedDesktopSwitching(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, _ => VirtualDesktopService.GetByIndex(i)?.Switch())
					.AddTo(this._disposable);
			}
			;

			void RegisterSpecifiedDesktopSwapping(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, _ => VirtualDesktopService.SwapCurrentByIndex(i))
					.AddTo(this._disposable);
			}
			;

			void RegisterMovingToSpecifiedDesktop(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, hWnd => hWnd.MoveToIndex(i))
					.AddTo(this._disposable);
			}
			;

			void RegisterMovingToSpecifiedDesktopAndSwitch(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, hWnd => hWnd.MoveToIndex(i)?.Switch())
					.AddTo(this._disposable);
			}
			;
		}
	}
}
