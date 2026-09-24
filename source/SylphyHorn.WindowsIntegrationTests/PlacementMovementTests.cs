using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services;
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

		[WpfFact(Timeout = 60000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task AutomaticPlacementFollowsOnlyForegroundWindowsUnlessOverridden()
		{
			Assert.SkipUnless(
				System.Environment.GetEnvironmentVariable("SYLPHYHORN_PLACEMENT_FOLLOW_TESTS") == "1",
				"Opt in to foreground activation and desktop switching with SYLPHYHORN_PLACEMENT_FOLLOW_TESTS=1.");
			using (var fixture = await Fixture.Create(this._output))
			{
				try
				{
					await this.VerifyFollowing(fixture, active: true, follow: null);
					await this.VerifyFollowing(fixture, active: true, follow: false);
					await this.VerifyFollowing(fixture, active: false, follow: null);
				}
				finally
				{
					await fixture.Stop();
					// Restore only a switch to the fixture's destination, not an unrelated user switch.
					if (VirtualDesktop.Current?.Id == fixture.Environment.Target)
					{
						VirtualDesktop.FromId(fixture.Environment.Source)?.Switch();
						await Until(() => VirtualDesktop.Current?.Id == fixture.Environment.Source, "restore original desktop");
					}
				}
				fixture.Environment.AssertDesktopUnchanged();
			}
		}

		private async Task VerifyFollowing(Fixture fixture, bool active, bool? follow)
		{
			var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			fixture.Authorize = async (destination, cancellation) =>
			{
				await ready.Task;
				cancellation.ThrowIfCancellationRequested();
				return new PlacementAuthorization(fixture.Environment.Map.Resolve(destination), new PlacementMovePermit());
			};
			await fixture.Start(fixture.Environment.Configuration(follow));
			try
			{
				var window = await fixture.Environment.Show(active);
				Assert.True(active == (InteropHelper.GetForegroundWindowEx() == window),
					"Fixture foreground activation did not match the requested state. Run from a foreground interactive console without other input.");
				ready.TrySetResult(true);
				await Until(() => fixture.History.Snapshot().Any(entry => entry.Window == window), "automatic placement with foreground policy");
				var result = fixture.History.Snapshot().Last(entry => entry.Window == window);
				Assert.Equal(PlacementOutcome.Moved, result.Outcome);
				Assert.DoesNotContain("FollowFailed", result.Reason ?? string.Empty);
				Assert.Equal(fixture.Environment.Target, fixture.Environment.Location(window));
				var expected = active && follow != false ? fixture.Environment.Target : fixture.Environment.Source;
				await Until(() => VirtualDesktop.Current?.Id == expected, "foreground follow destination");
				this._output.WriteLine("Active={0}, override={1}: moved=True, followed={2}", active, follow?.ToString() ?? "default", expected == fixture.Environment.Target);
			}
			finally
			{
				ready.TrySetCanceled();
				await fixture.Stop();
			}
			if (VirtualDesktop.Current?.Id == fixture.Environment.Target)
			{
				VirtualDesktop.FromId(fixture.Environment.Source).Switch();
				await Until(() => VirtualDesktop.Current?.Id == fixture.Environment.Source, "return before next case");
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

			internal async Task Start(AppPlacementConfiguration configuration = null)
			{
				Assert.Null(this.Session);
				this.Session = new PlacementSessionFactory().Start(
					configuration ?? this.Environment.Configuration(),
					(destination, allowCreation, cancellation) => this.Authorize(destination, cancellation),
					this.History);
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
