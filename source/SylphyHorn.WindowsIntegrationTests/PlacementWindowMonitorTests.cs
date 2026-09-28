using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementWindowMonitorTests
	{
		[WpfFact(Timeout = 30000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task ReadyAdmitsFirstNewWindowWithoutSettlingDelay()
		{
			// Repeated fresh monitors exercise the readiness boundary, not retry-until-success.
			// Every iteration must admit its first hidden fixture; no desktop COM or movement.
			for (var iteration = 0; iteration < 16; iteration++)
			{
				var monitor = PlacementWindowMonitor.Start();
				var window = IntPtr.Zero;
				try
				{
					Assert.True(await monitor.Ready);
					window = CreateFixture();
					NotifyWinEvent(0x8002, window, 0, 0);
					await Until(() =>
					{
						monitor.Events.ProcessBatch();
						Assert.Equal(PlacementMonitorState.Running, monitor.Events.State);
						PlacementCandidate candidate;
						while ((candidate = monitor.Events.TakeCandidate()) != null)
						{
							if (candidate.Window == window) return true;
							monitor.Events.Complete(candidate);
						}
						return false;
					});
				}
				finally
				{
					if (window != IntPtr.Zero)
					{
						DestroyWindow(window);
					}
					try
					{
						await monitor.StopAsync();
					}
					finally
					{
						monitor.Dispose();
					}
				}
			}
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task NativeHookAdmitsHiddenBaselineAndNewFixtureButExcludesChildren()
		{
			// Own, never-visible windows only. NotifyWinEvent simulates SHOW without activating any window.
			var existing = CreateFixture();
			var child = IntPtr.Zero;
			var created = IntPtr.Zero;
			var monitor = PlacementWindowMonitor.Start();
			try
			{
				Assert.True(await monitor.Ready);
				NotifyWinEvent(0x8002, existing, 0, 0);
				child = CreateFixture(existing);
				NotifyWinEvent(0x8002, child, 0, 0);
				created = CreateFixture();
				NotifyWinEvent(0x8002, created, 0, 0);
				PlacementCandidate candidate = null;
				var admittedExisting = false;
				await Until(() =>
				{
					monitor.Events.ProcessBatch();
					Assert.Equal(PlacementMonitorState.Running, monitor.Events.State);
					PlacementCandidate value;
					while ((value = monitor.Events.TakeCandidate()) != null)
					{
						if (value.Window == existing)
						{
							admittedExisting = true;
						}
						Assert.NotEqual(child, value.Window);
						if (value.Window == created)
						{
							candidate = value;
						}
						else
						{
							monitor.Events.Complete(value);
						}
					}
					return candidate != null && admittedExisting;
				});
				Assert.True(monitor.Events.IsCurrent(candidate));
				Assert.True(DestroyWindow(created));
				created = IntPtr.Zero;
				await Until(() =>
				{
					monitor.Events.ProcessBatch();
					Assert.Equal(PlacementMonitorState.Running, monitor.Events.State);
					return monitor.Events.BufferedCount == 0 && !monitor.Events.IsCurrent(candidate);
				});
			}
			finally
			{
				if (created != IntPtr.Zero)
				{
					DestroyWindow(created);
				}
				if (child != IntPtr.Zero)
				{
					DestroyWindow(child);
				}
				DestroyWindow(existing);
				try
				{
					await monitor.StopAsync();
				}
				finally
				{
					monitor.Dispose();
				}
			}
			Assert.Equal(TaskStatus.RanToCompletion, monitor.Completion.Status);
			Assert.Equal(PlacementMonitorState.Stopped, monitor.Events.State);
		}

		[Fact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task ImmediateStopReleasesThreadAndHook()
		{
			var monitor = PlacementWindowMonitor.Start();
			try
			{
				await monitor.StopAsync();
			}
			finally
			{
				monitor.Dispose();
			}
			Assert.True(monitor.Completion.IsCompleted);
			Assert.Equal(TaskStatus.RanToCompletion, monitor.Completion.Status);
			Assert.Equal(PlacementMonitorState.Stopped, monitor.Events.State);
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task BaselineOverflowReleasesNativeHookWithoutStartingObservation()
		{
			var first = CreateFixture();
			var second = CreateFixture();
			var monitor = PlacementWindowMonitor.Start(trackingLimit: 1);
			try
			{
				Assert.False(await monitor.Ready);
				await monitor.Completion;
				Assert.Equal(PlacementMonitorState.Paused, monitor.Events.State);
				Assert.Equal("BaselineCapacity", monitor.Events.PauseReason);
				Assert.Equal(TaskStatus.RanToCompletion, monitor.Completion.Status);
			}
			finally
			{
				DestroyWindow(second);
				DestroyWindow(first);
				try
				{
					await monitor.StopAsync();
				}
				finally
				{
					monitor.Dispose();
				}
			}
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task NativeEventOverflowPausesAndUnhooksWithoutDrainingConsumer()
		{
			var window = CreateFixture();
			var monitor = PlacementWindowMonitor.Start(eventLimit: 2);
			try
			{
				Assert.True(await monitor.Ready);
				NotifyWinEvent(0x8002, window, 0, 0);
				NotifyWinEvent(0x8003, window, 0, 0);
				NotifyWinEvent(0x8002, window, 0, 0);
				await Until(() => monitor.Completion.IsCompleted);
				await monitor.Completion;
				Assert.Equal(PlacementMonitorState.Paused, monitor.Events.State);
				Assert.Equal("EventCapacity", monitor.Events.PauseReason);
				Assert.Equal(0, monitor.Events.BufferedCount);
				Assert.Equal(TaskStatus.RanToCompletion, monitor.Completion.Status);
			}
			finally
			{
				DestroyWindow(window);
				try
				{
					await monitor.StopAsync();
				}
				finally
				{
					monitor.Dispose();
				}
			}
		}

		private static IntPtr CreateFixture(IntPtr parent = default(IntPtr))
		{
			var window = CreateWindowEx(
				0,
				"STATIC",
				"App placement test fixture",
				parent == IntPtr.Zero ? 0x80000000u : 0x40000000u,
				0,
				0,
				1,
				1,
				parent,
				IntPtr.Zero,
				IntPtr.Zero,
				IntPtr.Zero);
			if (window == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			return window;
		}

		private static async Task Until(Func<bool> predicate)
		{
			var watch = Stopwatch.StartNew();
			while (!predicate())
			{
				Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The native event was not delivered within five seconds.");
				await Task.Delay(10);
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateWindowEx(
			uint extended,
			string className,
			string title,
			uint style,
			int x,
			int y,
			int width,
			int height,
			IntPtr parent,
			IntPtr menu,
			IntPtr instance,
			IntPtr parameter);

		[DllImport("user32.dll")]
		private static extern void NotifyWinEvent(uint kind, IntPtr window, int objectId, int childId);

		[DllImport("user32.dll")]
		private static extern bool DestroyWindow(IntPtr window);
	}
}
