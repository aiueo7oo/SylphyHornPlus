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
		public async Task AutocloseEditsPreserveRulesAndFlagsAndKeepNamesSeparateFromNumbers()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\Apps\Editor.exe");
				f.Settings.Configuration.Value = new AppPlacementConfiguration(false, f.Settings.Configuration.Value.Rules, true, true,
					new[] { PlacementDestination.ByName("3"), PlacementDestination.ByNumber(3), PlacementDestination.ByNumber(3) });
				var service = Service(f);
				var remove = CliCommand.Parse(new[] { "desktop", "autoclose", "remove", "--number", "3" });
				var removed = await service.ExecuteAsync(remove, CancellationToken.None);
				Assert.True(removed.Success);
				Assert.Equal("3", Assert.Single(removed.Data.ClosingTargets).DesktopName);
				Assert.False(removed.Data.AssignmentEnabled);
				Assert.True(removed.Data.CloseCreatedDesktops);
				Assert.True(removed.Data.CreateMissingDesktops);
				Assert.Equal(row.Id, Assert.Single(f.Settings.Configuration.Value.Rules).Id);
				Assert.False((await service.ExecuteAsync(remove, CancellationToken.None)).Data.Changed);
				var add = CliCommand.Parse(new[] { "desktop", "autoclose", "add", "--number", "99" });
				Assert.True((await service.ExecuteAsync(add, CancellationToken.None)).Data.Changed);
				Assert.False((await service.ExecuteAsync(add, CancellationToken.None)).Data.Changed);
				var list = await service.ExecuteAsync(CliCommand.Parse(new[] { "desktop", "autoclose", "list" }), CancellationToken.None);
				Assert.Equal(2, list.Data.ClosingTargets.Length);
				Assert.Equal(99, list.Data.ClosingTargets[1].DesktopNumber);
				Assert.Null(list.Data.Assignments);
				Assert.Single(f.Model.ClosingGroups[0].Rows);
				Assert.Single(f.Model.ClosingGroups[1].Rows);
				Assert.Empty(f.Harness.Operations.DesktopOperationIds);
			}
		}

		[Fact]
		public async Task EditingPackageRulesByIdPreservesIdentityAndDoesNotReadExecutableFiles()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var app = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				var first = new AppPlacementRule(Guid.NewGuid(), true, app, PlacementDestination.ByNumber(1));
				var second = new AppPlacementRule(Guid.NewGuid(), false, app, PlacementDestination.ByNumber(2), "Example", @"C:\Package\Example.exe");
				f.Settings.Configuration.Value = new AppPlacementConfiguration(false, new[] { first, second });
				f.Catalog.ExecutablePending = Task.FromException<PlacementAppChoice>(new FileNotFoundException());
				var service = Service(f);
				var set = CliCommand.Parse(new[] { "app", "assignment", "set", "--id", second.Id.ToString(), "--desktop-name", "work" });
				Assert.True((await service.ExecuteAsync(set, CancellationToken.None)).Success);
				var updated = f.Settings.Configuration.Value.Rules[1];
				Assert.Equal(second.Id, updated.Id);
				Assert.Same(second.App, updated.App);
				Assert.False(updated.Enabled);
				Assert.Equal(second.DisplayName, updated.DisplayName);
				Assert.Equal(second.DisplayExecutablePath, updated.DisplayExecutablePath);
				Assert.Equal("work", updated.Destination.Name);
				Assert.Same(first, f.Settings.Configuration.Value.Rules[0]);
				var remove = CliCommand.Parse(new[] { "app", "assignment", "remove", "--id", second.Id.ToString() });
				Assert.True((await service.ExecuteAsync(remove, CancellationToken.None)).Success);
				Assert.Same(first, Assert.Single(f.Settings.Configuration.Value.Rules));
				Assert.Equal("assignment_not_found", (await service.ExecuteAsync(remove, CancellationToken.None)).Error.Code);
				Assert.Equal("assignment_not_found", (await service.ExecuteAsync(set, CancellationToken.None)).Error.Code);
			}
		}

		[Fact]
		public async Task ApplyByRuleIdResolvesPackageIdentityAndRejectsDisabledOrUnknownRules()
		{
			var factory = new Factory();
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "work", "")), factory);
			await harness.Runtime.InitializeAsync(false, CancellationToken.None);
			var app = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
			var rule = new AppPlacementRule(Guid.NewGuid(), true, app, PlacementDestination.ByName("work"));
			var disabled = new AppPlacementRule(Guid.NewGuid(), false, app, PlacementDestination.ByNumber(2));
			await harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, new[] { rule, disabled }));
			factory.Session.RuleApplication = dryRun => new PlacementRuleApplication(
				new PlacementPreview(Array.Empty<PlacementPreviewItem>(), long.MaxValue), Array.Empty<PlacementResult>());
			harness.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "work", "")));
			var command = CliCommand.Parse(new[] { "app", "assignment", "apply", "--id", rule.Id.ToString(), "--dry-run" });
			Assert.True((await harness.Runtime.ApplyCliAssignmentsAsync(command, CancellationToken.None)).Success);
			Assert.Same(app, factory.Session.RequestedApp);
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "work", "")));
			command = CliCommand.Parse(new[] { "app", "assignment", "apply", "--id", disabled.Id.ToString() });
			Assert.Equal("assignment_disabled", (await harness.Runtime.ApplyCliAssignmentsAsync(command, CancellationToken.None)).Error.Code);
			harness.Provider.EnqueueResult(Batch(1, 4, A, Entry(A, 0, "work", "")));
			command = CliCommand.Parse(new[] { "app", "assignment", "apply", "--id", Guid.NewGuid().ToString() });
			Assert.Equal("assignment_not_found", (await harness.Runtime.ApplyCliAssignmentsAsync(command, CancellationToken.None)).Error.Code);
			await harness.Runtime.ShutdownAsync();
		}

		[Fact]
		public async Task ConfigurePreservesRulesAndClosingTargetsAndReportsPreparationSeparately()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\Apps\Editor.exe");
				var current = f.Settings.Configuration.Value;
				f.Settings.Configuration.Value = new AppPlacementConfiguration(false, current.Rules, true, true,
					new[] { PlacementDestination.ByName("work"), PlacementDestination.ByNumber(3) });
				var service = new CliAssignmentService(f.Settings, f.Catalog,
					() => f.Harness.Settings.Provider.SaveWithResultAsync(), () => true, () => "Preparing");
				var response = await service.ExecuteAsync(CliCommand.Parse(new[]
				{
					"app", "assignment", "configure", "--enabled", "true", "--close-created-desktops", "false",
				}), CancellationToken.None);
				Assert.True(response.Success);
				Assert.True(response.Data.AssignmentEnabled);
				Assert.Equal("preparing", response.Data.AssignmentStatus);
				Assert.True(response.Data.CreateMissingDesktops);
				Assert.False(response.Data.CloseCreatedDesktops);
				Assert.Equal(row.Id.ToString(), Assert.Single(response.Data.Assignments).Id);
				Assert.Equal("work", response.Data.ClosingTargets[0].DesktopName);
				Assert.Equal(3, response.Data.ClosingTargets[1].DesktopNumber);
				Assert.True(f.Model.IsEnabled);
				Assert.False(f.Model.CloseCreatedDesktops);
			}
		}

		[Fact]
		public async Task EnableUsesThePersistedPackageRuleIdAndRejectsConflictingRules()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var app = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				var first = new AppPlacementRule(Guid.NewGuid(), true, app, PlacementDestination.ByNumber(1));
				var second = new AppPlacementRule(Guid.NewGuid(), false, app, PlacementDestination.ByName("work"), "Example", @"C:\Package\Example.exe");
				var original = new AppPlacementConfiguration(false, new[] { first, second });
				f.Settings.Configuration.Value = original;
				var service = Service(f);
				var enable = CliCommand.Parse(new[] { "app", "assignment", "enable", "--id", second.Id.ToString() });
				Assert.Equal("assignment_conflict", (await service.ExecuteAsync(enable, CancellationToken.None)).Error.Code);
				Assert.Same(original, f.Settings.Configuration.Value);
				Assert.True((await service.ExecuteAsync(CliCommand.Parse(new[]
				{
					"app", "assignment", "disable", "--id", first.Id.ToString(),
				}), CancellationToken.None)).Success);
				var result = await service.ExecuteAsync(enable, CancellationToken.None);
				Assert.True(result.Success);
				var updated = f.Settings.Configuration.Value.Rules[1];
				Assert.True(updated.Enabled);
				Assert.Equal(second.Id, updated.Id);
				Assert.Same(second.App, updated.App);
				Assert.Same(second.Destination, updated.Destination);
				Assert.Equal(second.DisplayExecutablePath, updated.DisplayExecutablePath);
				Assert.Equal(second.DisplayName, updated.DisplayName);
				Assert.False(result.Data.AssignmentEnabled);
				Assert.False((await service.ExecuteAsync(enable, CancellationToken.None)).Data.Changed);
			}
		}

		[Fact]
		public async Task StatusRemainsReadableWhileBusyWithoutSavingOrStartingMonitoring()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var original = f.Settings.Configuration.Value;
				var service = new CliAssignmentService(f.Settings, f.Catalog,
					() => throw new InvalidOperationException("Read-only status must not save."), () => false, () => "Suspended");
				var response = await service.ExecuteAsync(CliCommand.Parse(new[] { "app", "assignment", "status" }), CancellationToken.None);
				Assert.True(response.Success);
				Assert.Equal("suspended", response.Data.AssignmentStatus);
				Assert.False(response.Data.AssignmentEnabled);
				Assert.Null(response.Data.Assignments);
				Assert.Same(original, f.Settings.Configuration.Value);
				var refused = await service.ExecuteAsync(CliCommand.Parse(new[] { "app", "assignment", "configure", "--enabled", "true" }), CancellationToken.None);
				Assert.Equal("host_busy", refused.Error.Code);
				Assert.Same(original, f.Settings.Configuration.Value);
			}
		}

		[Fact]
		public async Task MissingRuleAndCancelledConfigurationDoNotChangeSettings()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var original = f.Settings.Configuration.Value;
				var service = Service(f);
				var missing = await service.ExecuteAsync(CliCommand.Parse(new[] { "app", "assignment", "enable", "--id", Guid.NewGuid().ToString() }), CancellationToken.None);
				Assert.Equal("assignment_not_found", missing.Error.Code);
				var cancelled = await service.ExecuteAsync(CliCommand.Parse(new[] { "app", "assignment", "configure", "--enabled", "true" }), new CancellationToken(true));
				Assert.Equal("request_cancelled", cancelled.Error.Code);
				Assert.Same(original, f.Settings.Configuration.Value);
			}
		}

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
			f.Settings, f.Catalog, () => f.Harness.Settings.Provider.SaveWithResultAsync(), () => true, () => "Disabled");

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
