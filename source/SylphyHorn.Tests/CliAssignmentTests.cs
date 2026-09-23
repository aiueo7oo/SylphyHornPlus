#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.Services.Commands;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[Collection(PlacementUiCollection.Name)]
	public sealed class CliAssignmentTests
	{
		[Fact]
		public async Task DryRunWindowIdsRemainStableAndPartialApplyPreservesConfirmedResults()
		{
			var factory = new Factory();
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "source", ""), Entry(B, 1, "Development", "")), factory);
			await harness.Runtime.InitializeAsync(false, CancellationToken.None);
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, new[] { factory.Session.Preview.Items[0].Rule }));
			factory.Session.RuleApplication = dryRun => new PlacementRuleApplication(factory.Session.Preview,
				dryRun ? Array.Empty<PlacementResult>() : new[]
				{
					factory.Session.Result(0, PlacementOutcome.Moved),
					factory.Session.Result(1, PlacementOutcome.Unconfirmed),
				});
			harness.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "source", ""), Entry(B, 1, "Development", "")));
			var preview = await harness.Runtime.ApplyCliAssignmentsAsync(
				CliCommand.Parse(new[] { "app", "assignment", "apply", "--all", "--dry-run" }), CancellationToken.None);
			Assert.True(preview.Success);
			Assert.True(preview.Data.DryRun);
			Assert.False(preview.Data.Changed);
			Assert.Equal("would_move", preview.Data.Results[0].Outcome);
			Assert.Equal("already_placed", preview.Data.Results[2].Outcome);
			Assert.True(Guid.TryParse(preview.Data.Results[0].WindowId, out _));
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "source", ""), Entry(B, 1, "Development", "")));
			var applied = await harness.Runtime.ApplyCliAssignmentsAsync(
				CliCommand.Parse(new[] { "app", "assignment", "apply", "--all" }), CancellationToken.None);
			Assert.False(applied.Success);
			Assert.Equal("result_unconfirmed", applied.Error.Code);
			Assert.Equal("moved", applied.Error.Results[0].Outcome);
			Assert.Equal(preview.Data.Results[0].WindowId, applied.Error.Results[0].WindowId);
			Assert.Equal(5, applied.ExitCode);
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task SetUpdatesTheGuiRuleAndPreservesOtherSettingsAndDisabledState()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\Apps\Editor.exe");
				row.Enabled = false;
				await f.Model.CommitAsync(row);
				f.Model.CreateMissingDesktops = true;
				f.Model.CloseCreatedDesktops = true;
				var response = await Service(f).ExecuteAsync(Set(@"c:\apps\EDITOR.exe", "--desktop-name", "work"), CancellationToken.None);
				Assert.True(response.Success);
				Assert.True(response.Data.Changed);
				var rule = Assert.Single(f.Settings.Configuration.Value.Rules);
				Assert.Equal(row.Id, rule.Id);
				Assert.False(rule.Enabled);
				Assert.False(response.Data.AssignmentEnabled);
				Assert.Equal("work", rule.Destination.Name);
				Assert.True(f.Settings.Configuration.Value.CreateMissingDesktops);
				Assert.True(f.Settings.Configuration.Value.CloseCreatedDesktops);
				Assert.Equal("work", Assert.Single(f.Model.Groups[0].Rows).Destination);
				Assert.Empty(f.Harness.Operations.DesktopOperationIds);
			}
		}

		[Fact]
		public async Task SaveFailureIsReportedAndAnIdenticalSetRetriesPersistence()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var service = Service(f);
				f.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				var command = Set(@"C:\Apps\Editor.exe", "--desktop-number", "3");
				var failed = await service.ExecuteAsync(command, CancellationToken.None);
				Assert.Equal("settings_save_failed", failed.Error.Code);
				Assert.Single(f.Settings.Configuration.Value.Rules);
				f.Harness.Settings.Provider.SaveFailure = null;
				var retried = await service.ExecuteAsync(command, CancellationToken.None);
				Assert.True(retried.Success);
				Assert.False(retried.Data.Changed);
				Assert.Single(retried.Data.Assignments);
			}
		}

		[Fact]
		public async Task SettingsChangedDuringPathLookupAreNotOverwritten()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var pending = new TaskCompletionSource<PlacementAppChoice>();
				f.Catalog.ExecutablePending = pending.Task;
				var task = Service(f).ExecuteAsync(Set(@"C:\Apps\Editor.exe", "--desktop-number", "3"), CancellationToken.None);
				f.Model.CreateMissingDesktops = true;
				pending.SetResult(PlacementUiCatalog.Choice(@"C:\Apps\Editor.exe"));
				Assert.Equal("state_changed", (await task).Error.Code);
				Assert.Empty(f.Settings.Configuration.Value.Rules);
				Assert.True(f.Settings.Configuration.Value.CreateMissingDesktops);
			}
		}

		[Fact]
		public async Task RemoveDoesNotReadTheExecutableAndDoesNotMoveWindows()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				await f.Add(@"C:\Apps\Editor.exe");
				f.Catalog.ExecutablePending = Task.FromException<PlacementAppChoice>(new FileNotFoundException());
				var command = CliCommand.Parse(new[] { "app", "assignment", "remove", "--path", @"C:\Apps\Editor.exe" });
				var result = await Service(f).ExecuteAsync(command, CancellationToken.None);
				Assert.True(result.Success);
				Assert.Empty(result.Data.Assignments);
				Assert.Empty(f.Settings.Configuration.Value.Rules);
				Assert.Empty(f.Harness.Operations.DesktopOperationIds);
			}
		}

		private static CliAssignmentService Service(PlacementUiFixture f) => new CliAssignmentService(
			f.Settings, f.Catalog, () => f.Harness.Settings.Provider.SaveWithResultAsync(), () => true);

		private static CliCommand Set(string path, string selector, string value)
			=> CliCommand.Parse(new[] { "app", "assignment", "set", "--path", path, selector, value });

		private sealed class Factory : IPlacementSessionFactory
		{
			internal readonly PlacementApplySession Session = new PlacementApplySession();

			public IPlacementSession Start(AppPlacementConfiguration configuration,
				Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
				PlacementHistory history, Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null)
				=> this.Session;
		}
	}
}
#endif
