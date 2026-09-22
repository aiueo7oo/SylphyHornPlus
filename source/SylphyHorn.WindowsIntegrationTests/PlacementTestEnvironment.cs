using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using SylphyHorn.AppPlacement;
using WindowsDesktop;
using WindowsDesktop.Interop;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	// Owns only the isolated provider and dedicated child process, never product settings.
	internal sealed class PlacementTestEnvironment : IDisposable
	{
		private Process _host;
		private Guid[] _order;

		internal VirtualDesktopProvider Provider { get; private set; }

		internal string Root { get; private set; }

		internal string HostPath { get; private set; }

		internal Guid Source { get; private set; }

		internal Guid Target { get; private set; }

		internal PlacementDesktopMap Map { get; private set; }

		internal static async Task<PlacementTestEnvironment> Create(ITestOutputHelper output)
		{
			Assert.SkipUnless(
				Environment.GetEnvironmentVariable("SYLPHYHORN_PLACEMENT_MOVE_TESTS") == "1",
				"Opt in to dedicated fixture movement with SYLPHYHORN_PLACEMENT_MOVE_TESTS=1.");
			var fixture = new PlacementTestEnvironment();
			try
			{
				fixture.Root = Path.Combine(Path.GetTempPath(), "SylphyHorn-PlacementMovement-" + Guid.NewGuid().ToString("N"));
				fixture.Provider = new VirtualDesktopProvider
				{
					AutoRestart = false,
					ComInterfaceAssemblyPath = fixture.Root
				};
				fixture.Provider.EnableDispatcherEventScheduling(Dispatcher.CurrentDispatcher);
				VirtualDesktop.Provider = fixture.Provider;
				await fixture.Provider.Initialize();
				var desktops = VirtualDesktop.GetDesktops();
				Assert.True(desktops.Length >= 2, "Two existing desktops are required; tests do not create or remove desktops.");
				fixture.Source = VirtualDesktop.Current.Id;
				fixture.Target = desktops.First(desktop => desktop.Id != fixture.Source).Id;
				fixture._order = desktops.Select(desktop => desktop.Id).ToArray();
				fixture.Map = new PlacementDesktopMap(desktops.Select(desktop => new PlacementDesktop(desktop.Id, desktop.Name, true)));
				fixture.HostPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlacementTestHost.path")).Trim();
				Assert.Equal("SylphyHorn.PlacementTestHost.exe", Path.GetFileName(fixture.HostPath));
				fixture._host = Process.Start(new ProcessStartInfo(fixture.HostPath)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardInput = true,
					RedirectStandardOutput = true
				});
				output.WriteLine(
					"Windows {0}; {1}-bit host; desktops={2}; fixture PID={3}; artifacts={4}",
					Environment.OSVersion.Version,
					IntPtr.Size * 8,
					desktops.Length,
					fixture._host.Id,
					fixture.Root);
				return fixture;
			}
			catch
			{
				fixture.Dispose();
				throw;
			}
		}

		internal AppPlacementConfiguration Configuration() => new AppPlacementConfiguration(
			true,
			new[] { new AppPlacementRule(
				Guid.NewGuid(),
				true,
				new PlacementAppIdentity(PlacementAppKind.ExecutablePath, this.HostPath),
				PlacementDestination.ByNumber(Array.IndexOf(this._order, this.Target) + 1)) });

		internal async Task<IntPtr> Show()
		{
			Assert.Equal(this.Source, VirtualDesktop.Current.Id);
			await this._host.StandardInput.WriteLineAsync("show");
			await this._host.StandardInput.FlushAsync();
			var line = await Bounded(this._host.StandardOutput.ReadLineAsync(), "fixture window creation");
			Assert.False(string.IsNullOrWhiteSpace(line), "Fixture process exited before creating a window.");
			var window = new IntPtr(long.Parse(line, CultureInfo.InvariantCulture));
			this.AssertOwned(window);
			return window;
		}

		internal void MoveFixture(IntPtr window)
		{
			this.AssertOwned(window);
			VirtualDesktopHelper.MoveToDesktop(window, VirtualDesktop.FromId(this.Target));
		}

		internal Guid Location(IntPtr window)
		{
			this.AssertOwned(window);
			// Public Shell COM remains usable to verify location after runtime disposes its provider.
			var manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID.VirtualDesktopManager));
			try
			{
				return manager.GetWindowDesktopId(window);
			}
			finally
			{
				Marshal.ReleaseComObject(manager);
			}
		}

		private void AssertOwned(IntPtr window)
		{
			GetWindowThreadProcessId(window, out var pid);
			Assert.Equal((uint)this._host.Id, pid);
		}

		internal void AssertDesktopUnchanged()
		{
			Assert.Equal(this.Source, VirtualDesktop.Current.Id);
			Assert.Equal(this._order, VirtualDesktop.GetDesktops().Select(desktop => desktop.Id).ToArray());
		}

		internal static async Task Until(Func<bool> condition, string phase)
		{
			var timer = Stopwatch.StartNew();
			while (!condition())
			{
				Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Timed out: " + phase);
				await Task.Delay(20, TestContext.Current.CancellationToken);
			}
		}

		internal static async Task<T> Bounded<T>(Task<T> task, string phase)
		{
			Assert.True(ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(15000, TestContext.Current.CancellationToken))), "Timed out: " + phase);
			return await task;
		}

		public void Dispose()
		{
			try
			{
				if (this._host != null)
				{
					try
					{
						if (!this._host.HasExited)
						{
							this._host.StandardInput.Close();
							if (!this._host.WaitForExit(3000)) this._host.Kill();
						}
						Assert.True(this._host.WaitForExit(3000), "Fixture process did not exit.");
					}
					finally
					{
						this._host.Dispose();
					}
				}
			}
			finally
			{
				this.Provider?.Dispose();
				VirtualDesktop.Provider = null;
			}
		}

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
	}
}
