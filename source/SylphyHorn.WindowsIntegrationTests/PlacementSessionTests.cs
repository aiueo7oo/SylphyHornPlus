using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementSessionTests
	{
		private readonly ITestOutputHelper _output;

		public PlacementSessionTests(ITestOutputHelper output) => this._output = output;

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task ExplicitPreviewUsesWorkerAndStopsWithoutDesktopOperations()
		{
			var configuration = new AppPlacementConfiguration(
				true,
				new[] { new AppPlacementRule(
					Guid.NewGuid(),
					true,
					new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\placement-test-not-installed\app.exe"),
					PlacementDestination.ByNumber(1)) });
			var session = new PlacementSessionFactory().Start(configuration, (_, __, ___) => throw new InvalidOperationException("Unexpected move authorization"), new PlacementHistory());
			try
			{
				var watch = Stopwatch.StartNew();
				while (!session.IsReady && !session.Completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, TestContext.Current.CancellationToken);
				Assert.True(session.IsReady);
				var preview = await session.PreviewAsync(PlacementDesktopMap.Unavailable, TestContext.Current.CancellationToken);
				Assert.Empty(preview.Items);
				await Assert.ThrowsAsync<ArgumentException>(() => session.ApplyAsync(preview, new[] { Guid.NewGuid() }, TestContext.Current.CancellationToken));
				using (var cancellation = new CancellationTokenSource())
				{
					cancellation.Cancel();
					await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PreviewAsync(PlacementDesktopMap.Unavailable, cancellation.Token));
				}
				var pending = session.PreviewAsync(PlacementDesktopMap.Unavailable, TestContext.Current.CancellationToken);
				await session.StopAsync();
				try
				{
					await pending;
				}
				catch (OperationCanceledException) { }
				Assert.True(pending.IsCompleted);
				await Assert.ThrowsAsync<InvalidOperationException>(() => session.PreviewAsync(PlacementDesktopMap.Unavailable, TestContext.Current.CancellationToken));
			}
			finally
			{
				await session.StopAsync();
			}
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task WorkerConsumesNativeEventsAndExcludesItsOwnWindowsBeforeDesktopCom()
		{
			// Match only this test executable. No window is shown, moved, activated, or pinned.
			AppPlacementConfiguration configuration;
			using (var process = Process.GetCurrentProcess())
				configuration = new AppPlacementConfiguration(
					true,
					new[] { new AppPlacementRule(
						Guid.NewGuid(),
						true,
						new PlacementAppIdentity(PlacementAppKind.ExecutablePath, process.MainModule.FileName),
						PlacementDestination.ByNumber(1)) });
			var history = new PlacementHistory();
			var authorizations = 0;
			var session = new PlacementSessionFactory().Start(
				configuration,
				(_, __, ___) =>
				{
					authorizations++;
					throw new InvalidOperationException("The test host must be excluded before authorization.");
				},
				history);
			try
			{
				var watch = Stopwatch.StartNew();
				var consumed = false;
				while (!consumed && watch.Elapsed < TimeSpan.FromSeconds(5))
				{
					// Windows created during baseline capture are deliberately excluded. Retry with a new lifetime.
					await Task.Delay(50);
					var window = CreateWindowEx(0, "STATIC", "Placement worker fixture", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
					Assert.NotEqual(IntPtr.Zero, window);
					try
					{
						NotifyWinEvent(0x8002, window, 0, 0);
						await Task.Delay(100);
						consumed = history.Snapshot().Any(result => result.Window == window && result.Outcome == PlacementOutcome.Excluded && result.Reason == "OwnProcess");
					}
					finally
					{
						Assert.True(DestroyWindow(window));
					}
				}
				Assert.True(consumed, "The native worker did not consume a fixture within five seconds.");
				Assert.Equal(0, authorizations);
				// Short native capacity probe; this is not the long-running/COM performance acceptance gate.
				for (var batch = 0; batch < 4; batch++)
				{
					var windows = new List<IntPtr>();
					var started = DateTimeOffset.Now;
					watch.Restart();
					try
					{
						for (var n = 0; n < 64; n++)
						{
							var window = CreateWindowEx(0, "STATIC", "Placement burst fixture", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
							Assert.NotEqual(IntPtr.Zero, window);
							windows.Add(window);
							NotifyWinEvent(0x8002, window, 0, 0);
						}
						while (true)
						{
							var results = history.Snapshot();
							Assert.True(results.Length <= 200);
							Assert.DoesNotContain(results, result => result.Outcome == PlacementOutcome.MonitorPaused);
							if (windows.All(window => results.Any(result => result.Window == window && result.Time >= started && result.Reason == "OwnProcess"))) break;
							Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The native burst did not finish within five seconds.");
							await Task.Delay(10);
						}
						this._output.WriteLine("Native batch {0}: 64 hidden fixtures consumed in {1} ms; history={2}.", batch + 1, watch.ElapsedMilliseconds, history.Snapshot().Length);
					}
					finally
					{
						foreach (var window in windows) Assert.True(DestroyWindow(window));
					}
					await Task.Delay(40);
				}
				Assert.Equal(0, authorizations);
			}
			finally
			{
				await session.StopAsync();
			}
			Assert.Equal(TaskStatus.RanToCompletion, session.Completion.Status);
			await session.StopAsync();
		}

		[Fact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task ImmediateRepeatedStopJoinsWorkerAndHook()
		{
			var configuration = new AppPlacementConfiguration(
				true,
				new[] { new AppPlacementRule(
					Guid.NewGuid(),
					true,
					new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\placement-test-not-installed\app.exe"),
					PlacementDestination.ByNumber(1)) });
			for (var n = 0; n < 10; n++)
			{
				var session = new PlacementSessionFactory().Start(configuration, (_, __, ___) => throw new InvalidOperationException("Unexpected authorization"), new PlacementHistory());
				await Task.WhenAll(session.StopAsync(), session.StopAsync());
				Assert.Equal(TaskStatus.RanToCompletion, session.Completion.Status);
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
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
