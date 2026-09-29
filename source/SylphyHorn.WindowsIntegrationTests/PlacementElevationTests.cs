using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementElevationTests
	{
		private readonly ITestOutputHelper _output;

		public PlacementElevationTests(ITestOutputHelper output) => this._output = output;

		[WpfFact(Timeout = 180000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task MediumIntegrityCanObserveElevatedFixturePlacementOutcome()
		{
			Assert.SkipUnless(
				Environment.GetEnvironmentVariable("SYLPHYHORN_ELEVATION_MOVE_TESTS") == "1",
				"Requires interactive UAC approval for a dedicated 20-second fixture.");
			using (var current = Process.GetCurrentProcess()) Assert.Equal(0x2000, Integrity(current.Id));
			using (var environment = await PlacementTestEnvironment.Create(this._output))
			{
				var ordinary = await environment.Show();
				var monitor = PlacementWindowMonitor.Start();
				Process elevated = null;
				try
				{
					Assert.True(await monitor.Ready);
					var started = DateTime.UtcNow;
					// ShellExecute is intentionally interactive; no credential or UAC bypass.
					elevated = Process.Start(new ProcessStartInfo(environment.HostPath, "--elevated-window")
					{
						UseShellExecute = true,
						Verb = "runas",
						WindowStyle = ProcessWindowStyle.Hidden
					});
					Assert.NotNull(elevated);
					Assert.True(elevated.StartTime.ToUniversalTime() >= started);
					Assert.Equal(0x3000, Integrity(elevated.Id));
					this._output.WriteLine("Integrity: observer=0x2000, fixture=0x3000; PID={0}", elevated.Id);
					await PlacementTestEnvironment.Until(() =>
					{
						elevated.Refresh();
						return elevated.MainWindowHandle != IntPtr.Zero;
					}, "elevated fixture window");
					PlacementCandidate selected = null;
					await PlacementTestEnvironment.Until(
						() =>
						{
							elevated.Refresh();
							var window = elevated.MainWindowHandle;
							monitor.Events.ProcessBatch();
							Assert.Equal(PlacementMonitorState.Running, monitor.Events.State);
							PlacementCandidate candidate;
							while ((candidate = monitor.Events.TakeCandidate()) != null)
							{
								GetWindowThreadProcessId(candidate.Window, out var pid);
								if (window != IntPtr.Zero && candidate.Window == window && pid == (uint)elevated.Id)
								{
									selected = candidate;
									break;
								}
								monitor.Events.Complete(candidate);
							}
							return selected != null;
						},
						"elevated fixture WinEvent candidate");
					var windows = new OwnedWindows(elevated, selected.Window);
					bool Current(PlacementCandidate candidate)
					{
						monitor.Events.ProcessBatch();
						return monitor.Events.IsCurrent(candidate);
					}
					var processor = new PlacementProcessor(
						environment.Configuration(),
						windows,
						(target, deadline) => new PlacementAuthorization(environment.Map.Resolve(target), new PlacementMovePermit()),
						Current,
						Now);
					var work = new PlacementWorkItem(selected);
					await PlacementTestEnvironment.Until(() =>
					{
						if (Now() >= work.NextAt)
						{
							processor.Step(work);
						}
						return work.Result != null;
					}, "elevated placement outcome");
					this._output.WriteLine("Outcome={0}; reason={1}; move calls={2}; identity={3}", work.Result.Outcome, work.Result.Reason, windows.Moves, work.Identity?.App.Value);
					Assert.Contains(
						work.Result.Outcome,
						new[]
						{
							PlacementOutcome.Moved,
							PlacementOutcome.Unavailable,
							PlacementOutcome.MoveFailed,
							PlacementOutcome.Unconfirmed
						});
					if (work.Result.Outcome == PlacementOutcome.Moved)
					{
						Assert.Equal(environment.Target, VirtualDesktop.FromHwnd(selected.Window).Id);
					}
					else
					{
						Assert.False(string.IsNullOrWhiteSpace(work.Result.Reason), "A denied or unconfirmed result must have diagnostic evidence.");
					}
					var moves = windows.Moves;
					for (var n = 0; n < 10; n++)
					{
						processor.Step(work);
					}
					Assert.InRange(moves, 0, 1);
					Assert.Equal(moves, windows.Moves);
					Assert.Equal(environment.Source, environment.Location(ordinary));
					environment.AssertDesktopUnchanged();
				}
				finally
				{
					try
					{
						await monitor.StopAsync();
					}
					finally
					{
						monitor.Dispose();
						if (elevated != null)
						{
							using (elevated) Assert.True(await Task.Run(() => elevated.WaitForExit(25000)), "Elevated fixture did not self-terminate; no other process will be killed.");
						}
					}
				}
			}
		}

		private sealed class OwnedWindows : IPlacementWindows
		{
			private readonly PlacementWindows _inner = new PlacementWindows();
			private readonly Process _process;
			private readonly IntPtr _window;
			internal int Moves;

			internal OwnedWindows(Process process, IntPtr window)
			{
				this._process = process;
				this._window = window;
			}

			private void AssertOwned(IntPtr window)
			{
				Assert.False(this._process.HasExited);
				Assert.Equal(this._window, window);
				GetWindowThreadProcessId(window, out var pid);
				Assert.Equal((uint)this._process.Id, pid);
			}

			public PlacementWindowInspection Inspect(IntPtr window)
			{
				this.AssertOwned(window);
				return this._inner.Inspect(window);
			}

			public PlacementWindowLocation Locate(IntPtr window)
			{
				this.AssertOwned(window);
				return this._inner.Locate(window);
			}

			public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
				PlacementMovePermit permit, Func<bool> current, Action beforeMove = null)
			{
				this.AssertOwned(expected.Window);
				Assert.Equal(this._process.StartTime.ToFileTimeUtc(), expected.Owner.CreatedAt);
				this.Moves++;
				return this._inner.Move(expected, source, target, permit, current, beforeMove);
			}
		}

		private static int Integrity(int pid)
		{
			using (var process = OpenProcess(0x1000, false, pid))
			{
				if (process.IsInvalid)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				if (!OpenProcessToken(process, 8, out var token))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				using (token)
				{
					GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);
					var buffer = Marshal.AllocHGlobal(size);
					try
					{
						if (!GetTokenInformation(token, 25, buffer, size, out size))
						{
							throw new Win32Exception(Marshal.GetLastWin32Error());
						}
						var sid = Marshal.ReadIntPtr(buffer);
						return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(GetSidSubAuthorityCount(sid)) - 1)));
					}
					finally
					{
						Marshal.FreeHGlobal(buffer);
					}
				}
			}
		}

		private static long Now() => unchecked((long)GetTickCount64());

		[DllImport("kernel32.dll")]
		private static extern ulong GetTickCount64();

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int info, IntPtr buffer, int size, out int required);

		[DllImport("advapi32.dll")]
		private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

		[DllImport("advapi32.dll")]
		private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
	}
}
