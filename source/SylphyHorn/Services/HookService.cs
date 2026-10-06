using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using MetroTrilithon.Lifetime;

namespace SylphyHorn.Services
{
	public class HookService : IDisposable
	{
		private readonly IHookDetector _detector;
		private readonly List<HookAction> _keyHookActions = new List<HookAction>();
		private readonly List<HookAction> _mouseHookActions = new List<HookAction>();
		private readonly Dispatcher _dispatcher;
		private int _suspendRequestCount;
		private Action _reloadAction;
		private bool _disposeRequested;
		private bool _disposed;
		private bool _started;

		internal bool IsSuspended => this._suspendRequestCount > 0;

		public Action Reload
		{
			get
			{
				return this.RequestReload;
			}
			set
			{
				if (!this._dispatcher.CheckAccess())
				{
					this._dispatcher.Invoke(() => this._reloadAction = value);
					return;
				}

				_reloadAction = value;
			}
		}

		/// <summary>
		/// Occurs when a hook service is suspended.
		/// </summary>
		public event Action Suspended;

		public HookService()
			: this(new HookDetector())
		{
		}

		internal HookService(IHookDetector detector)
		{
			this._detector = detector ?? throw new ArgumentNullException(nameof(detector));
			this._dispatcher = Dispatcher.CurrentDispatcher;
			this._detector.KeyPressed += this.KeyHookOnPressed;
			this._detector.KeyUp += this.KeyHookOnUp;
			this._detector.ButtonPressed += this.MouseHookOnPressed;
			this._detector.ButtonUp += this.MouseHookOnUp;
		}

		public void Start()
		{
			if (!this._dispatcher.CheckAccess())
			{
				this._dispatcher.Invoke((Action)this.StartCore);
				return;
			}

			this.StartCore();
		}

		private void StartCore()
		{
			if (this._disposeRequested)
			{
				throw new ObjectDisposedException(nameof(HookService));
			}
			if (this._started) return;

			if (this._suspendRequestCount == 0)
			{
				this._detector.Start();
			}

			this._started = true;
		}

		public IDisposable Suspend()
		{
			if (!this._dispatcher.CheckAccess())
			{
				return this._dispatcher.Invoke((Func<IDisposable>)this.SuspendCore);
			}

			return this.SuspendCore();
		}

		private IDisposable SuspendCore()
		{
			if (this._disposeRequested)
			{
				return Disposable.Create(() => { });
			}

			this._suspendRequestCount++;
			if (this._started)
			{
				this._detector.Stop();
			}

			this.Suspended?.Invoke();

			return Disposable.Create(this.RequestResume);
		}

		private void RequestResume()
		{
			if (this._dispatcher.CheckAccess())
			{
				this.ResumeCore();
				return;
			}

			this.TryBeginInvoke(this.ResumeCore);
		}

		private void RequestReload()
		{
			if (this._dispatcher.CheckAccess())
			{
				this.ReloadCore();
				return;
			}

			this.TryBeginInvoke(this.ReloadCore);
		}

		private void TryBeginInvoke(Action action)
		{
			if (this._dispatcher.HasShutdownStarted || this._dispatcher.HasShutdownFinished)
			{
				return;
			}

			try
			{
				this._dispatcher.BeginInvoke(action);
			}
			catch (InvalidOperationException ex)
			{
				// The dispatcher is shutting down. Lifecycle work must not continue.
				Debug.WriteLine(ex);
			}
			catch (TaskCanceledException ex)
			{
				// The dispatcher is shutting down. Lifecycle work must not continue.
				Debug.WriteLine(ex);
			}
		}

		private void ResumeCore()
		{
			if (this._disposeRequested)
			{
				return;
			}

			this._suspendRequestCount--;
			if (this._suspendRequestCount == 0)
			{
				this.ReloadCore();
				if (this._started)
				{
					this._detector.Start();
				}
			}
		}

		private void ReloadCore()
		{
			if (this._disposeRequested || this._reloadAction == null)
			{
				return;
			}

			this._keyHookActions.Clear();
			this._mouseHookActions.Clear();
			this._reloadAction();
		}

		public IDisposable RegisterKeyAction(Func<ShortcutKey> getShortcutKey, Action<IntPtr> action)
		{
			return this.Register(this._keyHookActions, getShortcutKey, action, () => true);
		}

		public IDisposable RegisterKeyAction(Func<ShortcutKey> getShortcutKey, Action<IntPtr> action, Func<bool> canExecute)
		{
			return this.Register(this._keyHookActions, getShortcutKey, action, canExecute);
		}

		public IDisposable RegisterMouseAction(Func<ShortcutKey> getShortcutKey, Action<IntPtr> action)
		{
			return this.Register(this._mouseHookActions, getShortcutKey, action, () => true);
		}

		public IDisposable RegisterMouseAction(Func<ShortcutKey> getShortcutKey, Action<IntPtr> action, Func<bool> canExecute)
		{
			return this.Register(this._mouseHookActions, getShortcutKey, action, canExecute);
		}

		private IDisposable Register(List<HookAction> hookActions, Func<ShortcutKey> getShortcutKey, Action<IntPtr> action, Func<bool> canExecute)
		{
			if (getShortcutKey().Key == Keys.None) return Disposable.Create(() => { });

			var hook = new HookAction(getShortcutKey, action, canExecute);
			hookActions.Add(hook);

			return Disposable.Create(() => hookActions.Remove(hook));
		}

		private void KeyHookOnPressed(object sender, ShortcutKeyPressedEventArgs args)
		{
			HookOnPressed(sender, this._keyHookActions, args);
		}

		private void KeyHookOnUp(object sender, ShortcutKeyPressedEventArgs args)
		{
			HookOnUp(sender, this._keyHookActions, args);
		}

		private void MouseHookOnPressed(object sender, ShortcutKeyPressedEventArgs args)
		{
			HookOnPressed(sender, this._mouseHookActions, args);
		}

		private void MouseHookOnUp(object sender, ShortcutKeyPressedEventArgs args)
		{
			HookOnUp(sender, this._mouseHookActions, args);
		}

		private void HookOnPressed(object sender, List<HookAction> hookActions, ShortcutKeyPressedEventArgs args)
		{
			if (args.ShortcutKey == ShortcutKey.None) return;

			var target = hookActions.FirstOrDefault(x => x.GetShortcutKey() == args.ShortcutKey);
			if (target != null && target.CanExecute())
			{
				this._dispatcher.BeginInvoke(
					new Action(() =>
					{
						// The current desktop does not change until the end of a switch animation.
						SwitchAnimationService.CompletePendingSwitch();
						target.Action(InteropHelper.GetForegroundWindowEx());
					}),
					DispatcherPriority.Normal);
				args.Handled = true;
			}
		}

		private void HookOnUp(object sender, List<HookAction> hookActions, ShortcutKeyPressedEventArgs args)
		{
			if (args.ShortcutKey == ShortcutKey.None) return;

			var target = hookActions.FirstOrDefault(x => x.GetShortcutKey() == args.ShortcutKey);
			if (target != null && target.CanExecute())
			{
				args.Handled = true;
			}
		}

		public void Dispose()
		{
			if (!this._dispatcher.CheckAccess())
			{
				this._dispatcher.Invoke((Action)this.DisposeCore);
				return;
			}

			this.DisposeCore();
		}

		private void DisposeCore()
		{
			if (this._disposed)
			{
				return;
			}

			this._disposeRequested = true;
			this._detector.Dispose();
			this._disposed = true;
		}

		private class HookAction
		{
			public Func<ShortcutKey> GetShortcutKey { get; }

			public Action<IntPtr> Action { get; }

			public Func<bool> CanExecute { get; }

			public HookAction(Func<ShortcutKey> getShortcutKey, Action<IntPtr> action, Func<bool> canExecute)
			{
				this.GetShortcutKey = getShortcutKey;
				this.Action = action;
				this.CanExecute = canExecute;
			}
		}

		internal interface IHookDetector : IDisposable
		{
			event EventHandler<ShortcutKeyPressedEventArgs> KeyPressed;
			event EventHandler<ShortcutKeyPressedEventArgs> KeyUp;
			event EventHandler<ShortcutKeyPressedEventArgs> ButtonPressed;
			event EventHandler<ShortcutKeyPressedEventArgs> ButtonUp;

			void Start();
			void Stop();
		}

		private sealed class HookDetector : IHookDetector
		{
			private readonly ShortcutKeyDetector _detector = new ShortcutKeyDetector();

			public event EventHandler<ShortcutKeyPressedEventArgs> KeyPressed
			{
				add => this._detector.KeyPressed += value;
				remove => this._detector.KeyPressed -= value;
			}

			public event EventHandler<ShortcutKeyPressedEventArgs> KeyUp
			{
				add => this._detector.KeyUp += value;
				remove => this._detector.KeyUp -= value;
			}

			public event EventHandler<ShortcutKeyPressedEventArgs> ButtonPressed
			{
				add => this._detector.ButtonPressed += value;
				remove => this._detector.ButtonPressed -= value;
			}

			public event EventHandler<ShortcutKeyPressedEventArgs> ButtonUp
			{
				add => this._detector.ButtonUp += value;
				remove => this._detector.ButtonUp -= value;
			}

			public void Start() => this._detector.Start();

			public void Stop() => this._detector.Stop();

			public void Dispose() => this._detector.Dispose();
		}
	}
}
