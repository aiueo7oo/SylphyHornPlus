using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.UI.Bindings;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[CollectionDefinition(Name, DisableParallelization = true)]
	public sealed class PlacementUiCollection { public const string Name = "Placement UI"; }

	[Collection(PlacementUiCollection.Name)]
	public sealed class AppPlacementSettingsViewModelTests
	{
		[Fact]
		public async Task ClosingSettingsSurvivePlacementEditsAndRejectInvalidNumbers()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				fixture.Model.CloseCreatedDesktops = true;
				var name = fixture.Model.AddClosingRow(fixture.Model.ClosingGroups[0]);
				name.Destination = "work";
				await fixture.Model.CommitClosingAsync(name);
				var number = fixture.Model.AddClosingRow(fixture.Model.ClosingGroups[1]);
				number.Destination = "3";
				await fixture.Model.CommitClosingAsync(number);
				var app = await fixture.Add(@"C:\Apps\Editor.exe", 3);
				fixture.Model.IsEnabled = true;
				fixture.Model.CreateMissingDesktops = true;
				await fixture.Model.RemoveAsync(app);
				Assert.True(fixture.Settings.Configuration.Value.CloseCreatedDesktops);
				Assert.Equal(2, fixture.Settings.Configuration.Value.ClosingTargets.Count);
				number.Destination = "0";
				await fixture.Model.CommitClosingAsync(number);
				Assert.NotEmpty(number.Error);
				Assert.Equal(3, number.Saved.Number);
				number.Restore();
				Assert.Equal("3", number.Destination);
				await fixture.Model.RemoveClosingAsync(name);
				Assert.Equal(3, Assert.Single(fixture.Settings.Configuration.Value.ClosingTargets).Number);
				Assert.True(fixture.Settings.Configuration.Value.CreateMissingDesktops);
			}
		}

		[Fact]
		public async Task EditingRulesAndTogglingPlacementPreservesCreationOption()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				fixture.Model.CreateMissingDesktops = true;
				var row = await fixture.Add(@"C:\Apps\Editor.exe", 3);
				Assert.True(fixture.Settings.Configuration.Value.CreateMissingDesktops);
				fixture.Model.IsEnabled = true;
				Assert.True(fixture.Settings.Configuration.Value.CreateMissingDesktops);
				await fixture.Model.RemoveAsync(row);
				Assert.True(fixture.Settings.Configuration.Value.CreateMissingDesktops);
				fixture.Model.IsEnabled = false;
				Assert.True(fixture.Settings.Configuration.Value.CreateMissingDesktops);
				fixture.Model.CreateMissingDesktops = false;
				Assert.False(fixture.Settings.Configuration.Value.CreateMissingDesktops);
			}
		}

		[Fact]
		public async Task DestinationChoicesFollowCurrentStateWithoutChangingStoredOrTypedTargets()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\editor.exe", name: "Future");
				row.Destination = "still typing";
				f.Harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "Work", ""), Entry(C, 1, "Web", "")));
				f.Harness.Owner.Drain();
				f.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work", "Web" }, f.Model.Groups[0].Choices);
				Assert.Equal(new[] { "1", "2" }, f.Model.Groups[1].Choices);
				Assert.Equal("still typing", row.Destination);
				Assert.Equal("Future", row.Saved.Destination.Name);
				f.Harness.Provider.PublishStable(Batch(1, 3, B, Entry(B, 0, "Work", ""), Entry(C, 1, "Work", "")));
				f.Harness.Owner.Drain();
				f.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work" }, f.Model.Groups[0].Choices);
				Assert.Equal(2, f.Model.Groups[1].Choices.Count);
			}
		}

		[Fact]
		public async Task InlineRowsPreserveGroupIdentityAndDoNotRepublishUnchangedInput()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\one\editor.exe", name: " future ");
				await f.Add(@"C:\two\editor.exe", 42);
				Assert.Same(row, Assert.Single(f.Model.Groups[0].Rows));
				Assert.Equal(42, Assert.Single(f.Model.Groups[1].Rows).Saved.Destination.Number);
				var configuration = f.Settings.Configuration.Value;
				await f.Model.CommitAsync(row);
				Assert.Same(configuration, f.Settings.Configuration.Value);
				row.Destination = "work";
				await f.Model.CommitAsync(row);
				Assert.Equal(row.Id, f.Settings.Configuration.Value.Rules[0].Id);
				Assert.Equal("work", f.Settings.Configuration.Value.Rules[0].Destination.Name);
				Assert.NotEmpty(f.Harness.Settings.Provider.SavedDictionaries);
			}
		}

		[Theory]
		[InlineData("")]
		[InlineData("0")]
		[InlineData("-1")]
		[InlineData("1.5")]
		[InlineData("2147483648")]
		public async Task InvalidInputKeepsSavedRuleAndEscapeRestoresIt(string number)
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\one\editor.exe", 2);
				var saved = row.Saved;
				row.Destination = number;
				await f.Model.CommitAsync(row);
				Assert.Same(saved, Assert.Single(f.Settings.Configuration.Value.Rules));
				Assert.Equal(f.Model.Text["InvalidNumber"], row.Error);
				f.Model.Revert(row);
				Assert.Equal("2", row.Destination);
				Assert.Empty(row.Error);
			}
		}

		[Fact]
		public async Task DifferentPathsAreDistinctButDuplicateEnabledIdentityIsRejected()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				await f.Add(@"C:\one\editor.exe");
				await f.Add(@"C:\two\editor.exe");
				var duplicate = await f.Add(@"C:\ONE\editor.exe");
				Assert.Equal(2, f.Settings.Configuration.Value.Rules.Count);
				Assert.Equal(f.Model.Text["Duplicate"], duplicate.Error);
				duplicate.Enabled = false;
				await f.Model.CommitAsync(duplicate);
				Assert.Equal(3, f.Settings.Configuration.Value.Rules.Count);
				duplicate.Enabled = true;
				await f.Model.CommitAsync(duplicate);
				Assert.False(duplicate.Saved.Enabled);
				Assert.NotEmpty(duplicate.Error);
			}
		}

		[Fact]
		public async Task RuntimeChangesAndRefreshPreserveTypedNameAndRows()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\one\editor.exe", name: "work");
				row.Destination = " future ";
				f.Harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "different", "")));
				f.Harness.Owner.Drain();
				for (var n = 0; n < 10; n++) f.Model.Refresh();
				Assert.Same(row, Assert.Single(f.Model.Groups[0].Rows));
				Assert.Equal(" future ", row.Destination);
				await f.Model.CommitAsync(row);
				Assert.Equal(" future ", row.Saved.Destination.Name);
			}
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task LatePathLookupCannotRestoreRemovedOrDisposedRow(bool dispose)
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Catalog.ExecutablePending = gate.Task;
				var row = f.Model.AddRow(f.Model.Groups[1]);
				row.AppText = @"C:\late\app.exe";
				var commit = f.Model.CommitAsync(row);
				if (dispose) f.Model.Dispose(); else await f.Model.RemoveAsync(row);
				gate.SetResult(PlacementUiCatalog.Choice(row.AppText));
				await commit;
				Assert.Empty(f.Settings.Configuration.Value.Rules);
			}
		}

		[Fact]
		public async Task ImportInvalidatesPendingInputAndUsesImportedRules()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var invalidations = 0;
				f.Model.EditingInvalidated += (_, __) => invalidations++;
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Catalog.ExecutablePending = gate.Task;
				var row = f.Model.AddRow(f.Model.Groups[1]);
				row.AppText = @"C:\old\app.exe";
				var commit = f.Model.CommitAsync(row);
				var imported = new AppPlacementConfiguration(
					false,
					new[] { new AppPlacementRule(Guid.NewGuid(), true, PlacementUiCatalog.Choice(@"C:\imported\app.exe").Identity, PlacementDestination.ByName("future")) });
				f.Harness.Settings.Provider.NextImport = new Dictionary<string, object> { ["AppPlacementSettings.Configuration"] = imported };
				var stage = await f.Harness.Settings.PrepareImportAsync("synthetic");
				Assert.True((await f.Harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken)).Succeeded);
				gate.SetResult(PlacementUiCatalog.Choice(row.AppText));
				await commit;
				Assert.Same(imported.Rules[0], Assert.Single(f.Model.Groups[0].Rows).Saved);
				Assert.Empty(f.Model.Groups[1].Rows);
				Assert.Equal(f.Model.Text["ConfigurationChanged"], f.Model.Message);
				Assert.Equal(1, invalidations);
			}
		}

		[Fact]
		public async Task SaveFailureCanRetryWithoutRepublishingAndRemoveDoesNotMoveWindows()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				f.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				var row = await f.Add(@"C:\one\app.exe");
				Assert.True(f.Model.SaveFailed);
				Assert.Equal(f.Model.Text["SaveFailed"], f.Model.Message);
				f.Harness.Settings.Provider.SaveFailure = null;
				var configuration = f.Settings.Configuration.Value;
				await f.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.Same(configuration, f.Settings.Configuration.Value);
				Assert.False(f.Model.SaveFailed);
				var draft = f.Model.AddRow(f.Model.Groups[0]);
				await f.Model.RemoveAsync(draft);
				Assert.Same(configuration, f.Settings.Configuration.Value);
				await row.RemoveCommand.ExecuteAsync(null);
				Assert.Empty(f.Settings.Configuration.Value.Rules);
				Assert.Empty(f.Harness.Operations.DesktopOperationNames);
			}
		}

		[Fact]
		public async Task PickerSearchUsesPathsAndUnverifiedCandidatesCannotBeChosen()
		{
			using (var f = await PlacementUiFixture.Create())
			using (var picker = f.Model.CreatePicker())
			{
				f.Catalog.Pending = Task.FromResult<IReadOnlyList<PlacementAppChoice>>(new[]
				{
					PlacementUiCatalog.Choice(@"C:\one\editor.exe"),
					PlacementUiCatalog.Choice(@"C:\two\editor.exe", true),
					new PlacementAppChoice("Unknown", "Unverified", null, null, problem: "IdentityUnavailable")
				});
				await picker.InstalledCommand.ExecuteAsync(null);
				picker.Search = "two";
				picker.Selected = Assert.Single(picker.Candidates);
				Assert.True(picker.CanChoose);
				Assert.Equal(@"C:\two\editor.exe", picker.Details);
				Assert.Equal(picker.Text["LauncherWarning"], picker.Warning);
				picker.Search = "Unknown";
				picker.Selected = Assert.Single(picker.Candidates);
				Assert.False(picker.CanChoose);
				Assert.Equal(picker.Text["IdentityUnavailable"], picker.Warning);
				Assert.Empty(f.Settings.Configuration.Value.Rules);
			}
		}

		[Fact]
		public async Task PickerRejectsLatePreviousSourceAndDisposedQuery()
		{
			using (var f = await PlacementUiFixture.Create())
			using (var picker = f.Model.CreatePicker())
			{
				var gate = new TaskCompletionSource<IReadOnlyList<PlacementAppChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Catalog.Pending = gate.Task;
				var old = picker.WindowsCommand.ExecuteAsync(null);
				f.Catalog.Pending = Task.FromResult<IReadOnlyList<PlacementAppChoice>>(new[] { PlacementUiCatalog.Choice(@"C:\new\app.exe") });
				await picker.InstalledCommand.ExecuteAsync(null);
				gate.SetResult(new[] { PlacementUiCatalog.Choice(@"C:\old\app.exe") });
				await old;
				Assert.Equal(@"C:\new\app.exe", Assert.Single(picker.Candidates).Path);
				gate = new TaskCompletionSource<IReadOnlyList<PlacementAppChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Catalog.Pending = gate.Task;
				old = picker.WindowsCommand.ExecuteAsync(null);
				picker.Dispose();
				gate.SetResult(new[] { PlacementUiCatalog.Choice(@"C:\late\app.exe") });
				await old;
				Assert.Empty(picker.Candidates);
			}
		}

		[Fact]
		public async Task FileSelectionValidatesPathAndFailureCannotReusePreviousChoice()
		{
			using (var f = await PlacementUiFixture.Create())
			using (var picker = f.Model.CreatePicker())
			{
				Assert.True(await picker.SelectFileAsync(@"C:\portable\app.exe"));
				Assert.True(picker.CanChoose);
				f.Catalog.ExecutablePending = Task.FromException<PlacementAppChoice>(new FileNotFoundException());
				Assert.False(await picker.SelectFileAsync(@"C:\missing\app.exe"));
				Assert.False(picker.CanChoose);
				Assert.Equal(picker.Text["InvalidPath"], picker.Status);
			}
		}

		[Fact]
		public async Task PathChangedDuringLookupIsNotOverwrittenByItsLateResult()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = await f.Add(@"C:\first\app.exe");
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Catalog.ExecutablePending = gate.Task;
				row.AppText = @"C:\second\app.exe";
				var commit = f.Model.CommitAsync(row);
				row.AppText = @"C:\third\app.exe";
				gate.SetResult(PlacementUiCatalog.Choice(@"C:\second\app.exe"));
				await commit;
				Assert.Equal(@"C:\first\app.exe", row.Saved.App.Value);
				Assert.Equal(@"C:\third\app.exe", row.AppText);
				Assert.Empty(row.Name);
			}
		}

		[Fact]
		public async Task ImportedPackageWithoutDisplayInformationDoesNotExposeItsId()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				f.Settings.Configuration.Value = new AppPlacementConfiguration(false, new[] { new AppPlacementRule(Guid.NewGuid(), true, identity, PlacementDestination.ByNumber(1)) });
				var row = Assert.Single(f.Model.Groups[1].Rows);
				Assert.DoesNotContain(identity.Value, row.Name);
				Assert.DoesNotContain(identity.Value, row.AppText);
				Assert.True(row.IsPackage);
			}
		}

		[Fact]
		public async Task PackagedChoiceKeepsVerifiedIdentityAndPathIsNotEditable()
		{
			using (var f = await PlacementUiFixture.Create())
			{
				var row = f.Model.AddRow(f.Model.Groups[1]);
				var identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				row.Use(new PlacementAppChoice("Terminal", "", @"C:\Package\terminal.exe", identity));
				await f.Model.CommitAsync(row);
				Assert.True(row.IsPackage);
				Assert.Equal(identity, row.Saved.App);
				Assert.Equal(@"C:\Package\terminal.exe", row.AppText);
				Assert.DoesNotContain(identity.Value, row.AppText);
			}
		}
	}

	internal sealed class PlacementUiFixture : IDisposable
	{
		internal Harness Harness;
		internal AppPlacementSettings Settings;
		internal AppPlacementSettingsViewModel Model;
		internal PlacementUiCatalog Catalog = new PlacementUiCatalog();

		internal static async Task<PlacementUiFixture> Create()
		{
			var f = new PlacementUiFixture { Harness = await Harness.Initialized() };
			f.Settings = new AppPlacementSettings(f.Harness.Settings.Provider);
			f.Model = new AppPlacementSettingsViewModel(f.Settings, f.Harness.Runtime, f.Catalog, () => f.Harness.Settings.Provider.SaveWithResultAsync());
			return f;
		}

		internal async Task<PlacementRuleRow> Add(string path, int number = 1, string name = null)
		{
			var row = this.Model.AddRow(this.Model.Groups[name == null ? 1 : 0]);
			row.AppText = path;
			row.Destination = name ?? number.ToString();
			await this.Model.CommitAsync(row);
			return row;
		}

		public void Dispose() => this.Model.Dispose();
	}

	internal sealed class PlacementUiCatalog : IPlacementAppCatalog
	{
		internal Task<IReadOnlyList<PlacementAppChoice>> Pending;
		internal Task<PlacementAppChoice> ExecutablePending;
		internal bool? IncludeIcons;
		internal bool? Windows;

		internal static PlacementAppChoice Choice(string path, bool confirm = false) => new PlacementAppChoice(
			Path.GetFileNameWithoutExtension(path),
			"Example document",
			path,
			new PlacementAppIdentity(PlacementAppKind.ExecutablePath, path),
			confirmPath: confirm);

		public Task<IReadOnlyList<PlacementAppChoice>> ReadAsync(bool windows, CancellationToken cancellation, bool includeIcons = true)
		{
			this.Windows = windows;
			this.IncludeIcons = includeIcons;
			return this.Pending ?? Task.FromResult<IReadOnlyList<PlacementAppChoice>>(Array.Empty<PlacementAppChoice>());
		}

		public Task<PlacementAppChoice> ReadExecutableAsync(string path, CancellationToken cancellation) => this.ExecutablePending ?? Task.FromResult(Choice(path));

		public Task<IReadOnlyDictionary<string, BitmapSource>> ReadIconsAsync(string[] paths, CancellationToken cancellation) => Task.FromResult<IReadOnlyDictionary<string, BitmapSource>>(new Dictionary<string, BitmapSource>());
	}
}
