using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;
using Xunit;
using static SylphyHorn.WindowsIntegrationTests.PlacementTestEnvironment;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementMovementTests
	{
		private readonly ITestOutputHelper _output;

		public PlacementMovementTests(ITestOutputHelper output) => this._output = output;

		private async Task VerifyNewWindowsAndRestart(Fixture fixture)
		{
			await fixture.Start();
			var timer = Stopwatch.StartNew();
			var window = await fixture.Environment.Show();
			await Until(() => fixture.History.Snapshot().Any(entry => entry.Window == window), "native placement result");
			var result = fixture.History.Snapshot().Last(item => item.Window == window);
			Assert.True(result.Outcome == PlacementOutcome.Moved, result.Outcome + ": " + result.Reason);
			Assert.Equal(fixture.Environment.Target, VirtualDesktop.FromHwnd(window).Id);
			this._output.WriteLine("Automatic move and verification: {0} ms", timer.ElapsedMilliseconds);
			fixture.Environment.AssertDesktopUnchanged();
			await fixture.Stop();
			var stoppedWindow = await fixture.Environment.Show();
			await Task.Delay(400);
			Assert.Equal(fixture.Environment.Source, VirtualDesktop.FromHwnd(stoppedWindow).Id);
			await fixture.Start();
			await Task.Delay(400);
			Assert.Equal(fixture.Environment.Source, VirtualDesktop.FromHwnd(stoppedWindow).Id);
			var preview = await fixture.Session.PreviewAsync(fixture.Environment.Map, TestContext.Current.CancellationToken);
			Assert.Contains(preview.Items, item => item.Candidate.Window == stoppedWindow && item.CanApply);
			await fixture.Stop();
			fixture.Environment.AssertDesktopUnchanged();
		}

		[WpfFact(Timeout = 60000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task FixtureWindowsRespectSelectionCancellationAutomaticPlacementAndRestart()
		{
			using (var fixture = await Fixture.Create(this._output))
			{
				var first = await fixture.Environment.Show();
				var second = await fixture.Environment.Show();
				var third = await fixture.Environment.Show();
				await fixture.Start();
				var preview = await fixture.Session.PreviewAsync(fixture.Environment.Map, TestContext.Current.CancellationToken);
				// WinForms may also own hidden framework windows; those must not be movable.
				foreach (var item in preview.Items)
					this._output.WriteLine("Preview fixture: {0}; {1}", item.Title, item.Excluded?.ToString() ?? "Ready");
				Assert.Equal(
					new[] { first, second, third }.OrderBy(window => window.ToInt64()),
					preview.Items.Where(item => item.CanApply).Select(item => item.Candidate.Window).OrderBy(window => window.ToInt64()));
				var selected = preview.Items.Single(item => item.Candidate.Window == first);
				var results = await fixture.Session.ApplyAsync(preview, new[] { selected.Id }, TestContext.Current.CancellationToken);
				Assert.Equal(PlacementOutcome.Moved, Assert.Single(results).Outcome);
				Assert.Equal(fixture.Environment.Target, VirtualDesktop.FromHwnd(first).Id);
				Assert.Equal(fixture.Environment.Source, VirtualDesktop.FromHwnd(second).Id);
				Assert.Equal(fixture.Environment.Source, VirtualDesktop.FromHwnd(third).Id);
				await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.ApplyAsync(preview, new[] { selected.Id }, TestContext.Current.CancellationToken));

				preview = await fixture.Session.PreviewAsync(fixture.Environment.Map, TestContext.Current.CancellationToken);
				selected = preview.Items.Single(item => item.Candidate.Window == second);
				// Simulate a user's move, but only on a window owned by this fixture process.
				fixture.Environment.MoveFixture(second);
				await Until(() => VirtualDesktop.FromHwnd(second)?.Id == fixture.Environment.Target, "manual fixture move");
				results = await fixture.Session.ApplyAsync(preview, new[] { selected.Id }, TestContext.Current.CancellationToken);
				Assert.Equal(PlacementOutcome.Changed, Assert.Single(results).Outcome);

				preview = await fixture.Session.PreviewAsync(fixture.Environment.Map, TestContext.Current.CancellationToken);
				selected = preview.Items.Single(item => item.Candidate.Window == third);
				var authorizationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Authorize = async (_, cancellation) =>
				{
					authorizationEntered.TrySetResult(true);
					await Task.Delay(Timeout.Infinite, cancellation);
					throw new InvalidOperationException("Cancellation must end authorization.");
				};
				using (var cancellation = new CancellationTokenSource())
				{
					var applying = fixture.Session.ApplyAsync(preview, new[] { selected.Id }, cancellation.Token);
					await Bounded(authorizationEntered.Task, "authorization before cancellation");
					cancellation.Cancel();
					results = await Bounded(applying, "cancelled native application");
					Assert.Equal(PlacementOutcome.Cancelled, Assert.Single(results).Outcome);
				}
				Assert.Equal(fixture.Environment.Source, VirtualDesktop.FromHwnd(third).Id);
				fixture.Environment.AssertDesktopUnchanged();
				await fixture.Stop();
				this._output.WriteLine("Selection, single-use preview, manual-move conflict and in-flight cancellation verified against real desktop locations.");
				fixture.Authorize = (destination, cancellation) => Task.FromResult(new PlacementAuthorization(fixture.Environment.Map.Resolve(destination), new PlacementMovePermit()));
				await this.VerifyNewWindowsAndRestart(fixture);
			}
		}

		private sealed class Fixture : IDisposable
		{
			internal PlacementTestEnvironment Environment;
			internal IPlacementSession Session;
			internal readonly PlacementHistory History = new PlacementHistory();
			internal Func<PlacementDestination, CancellationToken, Task<PlacementAuthorization>> Authorize;

			internal static async Task<Fixture> Create(ITestOutputHelper output)
			{
				var fixture = new Fixture { Environment = await PlacementTestEnvironment.Create(output) };
				fixture.Authorize = (destination, cancellation) => Task.FromResult(new PlacementAuthorization(fixture.Environment.Map.Resolve(destination), new PlacementMovePermit()));
				return fixture;
			}

			internal async Task Start()
			{
				Assert.Null(this.Session);
				this.Session = new PlacementSessionFactory().Start(this.Environment.Configuration(), (destination, allowCreation, cancellation) => this.Authorize(destination, cancellation), this.History);
				await Until(() => this.Session.IsReady || this.Session.Completion.IsCompleted, "placement session startup");
				Assert.True(this.Session.IsReady && !this.Session.Completion.IsCompleted);
			}

			internal async Task Stop()
			{
				if (this.Session == null) return;
				var stop = this.Session.StopAsync();
				Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(15000)));
				await stop;
				this.Session = null;
			}

			public void Dispose()
			{
				var stopped = this.Session?.StopAsync();
				if (stopped != null && !stopped.Wait(15000)) throw new TimeoutException("Placement worker did not join; provider was retained.");
				this.Environment.Dispose();
			}
		}
	}
}
