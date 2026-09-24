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
		private Services.Commands.CliServer _cliServer;
		private Services.Commands.CliAssignmentService _cliAssignments;
		private Services.Commands.CliSettingsService _cliSettings;
		private Services.Commands.CliShortcutService _cliShortcuts;
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
			if (SettingsWindow.Instance != null) SettingsWindow.Instance.Activate();
			else
			{
				var window = this.CreateSettingsWindow();
				SettingsWindow.Instance = window;
				window.ShowDialog();
				SettingsWindow.Instance = null;
			}
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
				if (ReferenceEquals(SettingsWindow.Instance, window)) SettingsWindow.Instance = null;
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
			if (command.Operation.StartsWith("shortcut ", StringComparison.Ordinal))
				return this._cliShortcuts.ExecuteAsync(command, cancellation);
			if (Services.Commands.CliSettingsService.Handles(command.Operation))
				return this._cliSettings.ExecuteAsync(command, cancellation);
			if (command.Operation == "app assignment apply")
				return this._desktopRuntime.ApplyCliAssignmentsAsync(command, cancellation);
			if (command.Operation == "app list" || command.Operation.StartsWith("app assignment ", StringComparison.Ordinal)
				|| command.Operation.StartsWith("desktop autoclose ", StringComparison.Ordinal))
				return this._cliAssignments.ExecuteAsync(command, cancellation);
			if (!command.Operation.StartsWith("ui ", StringComparison.Ordinal))
				return this._desktopRuntime.ExecuteCliAsync(command, cancellation);
			if (cancellation.IsCancellationRequested)
				return Task.FromResult(CliResponse.Fail(command.Operation, "request_cancelled",
					"The request expired before the UI action was submitted."));

			if (command.Operation == "ui settings")
			{
				if (!this.ShowSettingsFromCli())
					return Task.FromResult(CliResponse.Fail(command.Operation, "settings_unavailable",
						"The settings window is unavailable."));
			}
			else if (command.Operation == "ui task-view") VirtualDesktopService.ShowTaskView();
			else if (command.Operation == "ui window-switch") VirtualDesktopService.ShowWindowSwitch();
			else if (command.Operation == "ui notification-toggle") NotificationService.Instance.ToggleCurrentDesktop();
			else return Task.FromResult(CliResponse.Fail(command.Operation, "invalid_arguments", "Unknown UI command."));
			return Task.FromResult(CliResponse.Ok(command.Operation, new CliData()));
		}
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
					new ApplicationDesktopSettingsTransactions(LocalSettingsProvider.Instance),
					new DispatcherDesktopOwnerContext(Application.Current.Dispatcher),
					new VirtualDesktopOperations());
				runtime.Faulted += (sender, fault) => LoggingService.Instance.Register(new DesktopRuntimeLog(fault));
				var result = await runtime.InitializeAsync(Settings.General.OverrideDesktopsOnStartup, CancellationToken.None);
				if (!result.Succeeded)
				{
					this._startupTrace?.Write(
						StartupPhase.RuntimeInitialized,
						result.Status == DesktopRuntimeInitializationStatus.Cancelled || result.Status == DesktopRuntimeInitializationStatus.ShuttingDown
							? StartupTraceResult.Cancelled
							: StartupTraceResult.Failed);
					if (result.Status == DesktopRuntimeInitializationStatus.Cancelled || result.Status == DesktopRuntimeInitializationStatus.ShuttingDown) this.VirtualDesktopInitializationCanceled?.Invoke();
					else this.VirtualDesktopInitializationFailed?.Invoke(new InvalidOperationException("Virtual desktop runtime initialization did not produce a stable state."), false);
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
					if (alwaysShow) NotificationService.Instance.ShowCurrentDesktop();
					else NotificationService.Instance.HideCurrentDesktop();
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
					this._cliShortcuts = new Services.Commands.CliShortcutService(Settings.ShortcutKey, Settings.MouseShortcut,
						Settings.General, () => LocalSettingsProvider.Instance.SaveWithResultAsync(),
						() => runtime.CliAvailable && !this._hookService.IsSuspended,
						() => this._hookService.Reload(), () => runtime.State.Order.Count);
					this._cliSettings = new Services.Commands.CliSettingsService(Settings.General,
						() => LocalSettingsProvider.Instance.SaveWithResultAsync(), () => runtime.CliAvailable,
						ResourceService.Current.StartupCulture);
					this._cliAssignments = new Services.Commands.CliAssignmentService(Settings.AppPlacement,
						new Services.AppPlacement.PlacementAppCatalog(), () => LocalSettingsProvider.Instance.SaveWithResultAsync(),
						() => runtime.CliAvailable, () => runtime.PlacementStatus);
					this._cliServer = new Services.Commands.CliServer(
						Commands.CliProtocol.PipeName(ProductInfo.Company, ProductInfo.Product),
						(command, token) => Application.Current.Dispatcher.InvokeAsync(
							() => this.ExecuteCliAsync(command, token), System.Windows.Threading.DispatcherPriority.Background, token).Task.Unwrap());
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
			if (this._cliServer != null) await this._cliServer.StopAsync();
#endif
			if (this._desktopRuntime != null) await this._desktopRuntime.ShutdownAsync();
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
