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
		[Theory]
		[InlineData("\uFF13", 3)]
		[InlineData(" 12\u3000", 12)]
		[InlineData("\uFF11\uFF10\uFF10", 100)]
		public async Task NumbersTypedWithTheImeOrSpacesAreSavedAsDigits(string typed, int expected)
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\one\editor.exe", 2);
				row.Destination = typed;
				await fixture.Model.CommitAsync(row);
				Assert.Empty(row.Error);
				Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Destination);
				Assert.Equal(expected, Assert.Single(fixture.Settings.Configuration.Value.Rules).Destination.Number);
			}
		}

		[Fact]
		public async Task ClosingNumbersAcceptImeDigitsAndRejectNumbersAboveTheMaximum()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = fixture.Model.AddClosingRow(fixture.Model.NumberClosingGroup);
				row.Destination = "101";
				await fixture.Model.CommitClosingAsync(row);
				Assert.Equal(fixture.Model.Text["InvalidNumber"], row.Error);
				Assert.Empty(fixture.Settings.Configuration.Value.ClosingTargets);
				row.Destination = " \uFF14 ";
				await fixture.Model.CommitClosingAsync(row);
				Assert.Empty(row.Error);
				Assert.Equal("4", row.Destination);
				Assert.Equal(4, Assert.Single(fixture.Settings.Configuration.Value.ClosingTargets).Number);
			}
		}

		[Fact]
		public async Task ClosingSettingsSurvivePlacementEditsAndRejectInvalidNumbers()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				fixture.Model.CloseCreatedDesktops = true;
				var name = fixture.Model.AddClosingRow(fixture.Model.NameClosingGroup);
				name.Destination = "work";
				await fixture.Model.CommitClosingAsync(name);
				var number = fixture.Model.AddClosingRow(fixture.Model.NumberClosingGroup);
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
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\editor.exe", name: "Future");
				row.Destination = "still typing";
				fixture.Harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "Work", ""), Entry(C, 1, "Web", "")));
				fixture.Harness.Owner.Drain();
				fixture.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work", "Web" }, fixture.Model.NameGroup.Choices);
				Assert.Equal(new[] { "1", "2" }, fixture.Model.NumberGroup.Choices);
				Assert.Equal("still typing", row.Destination);
				Assert.Equal("Future", row.Saved.Destination.Name);
				fixture.Harness.Provider.PublishStable(Batch(1, 3, B, Entry(B, 0, "Work", ""), Entry(C, 1, "Work", "")));
				fixture.Harness.Owner.Drain();
				fixture.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work" }, fixture.Model.NameGroup.Choices);
				Assert.Equal(2, fixture.Model.NumberGroup.Choices.Count);
			}
		}

		[Fact]
		public async Task InlineRowsPreserveGroupIdentityAndDoNotRepublishUnchangedInput()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\one\editor.exe", name: " future ");
				await fixture.Add(@"C:\two\editor.exe", 42);
				Assert.Same(row, Assert.Single(fixture.Model.NameGroup.Rows));
				Assert.Equal(42, Assert.Single(fixture.Model.NumberGroup.Rows).Saved.Destination.Number);
				var configuration = fixture.Settings.Configuration.Value;
				await fixture.Model.CommitAsync(row);
				Assert.Same(configuration, fixture.Settings.Configuration.Value);
				row.Destination = "work";
				await fixture.Model.CommitAsync(row);
				Assert.Equal(row.Id, fixture.Settings.Configuration.Value.Rules[0].Id);
				Assert.Equal("work", fixture.Settings.Configuration.Value.Rules[0].Destination.Name);
				Assert.NotEmpty(fixture.Harness.Settings.Provider.SavedDictionaries);
			}
		}

		[Theory]
		[InlineData("")]
		[InlineData("0")]
		[InlineData("-1")]
		[InlineData("1.5")]
		[InlineData("2147483648")]
		[InlineData("101")]
		[InlineData("abc")]
		public async Task InvalidInputKeepsSavedRuleAndEscapeRestoresIt(string number)
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\one\editor.exe", 2);
				var saved = row.Saved;
				row.Destination = number;
				await fixture.Model.CommitAsync(row);
				Assert.Same(saved, Assert.Single(fixture.Settings.Configuration.Value.Rules));
				Assert.Equal(fixture.Model.Text["InvalidNumber"], row.Error);
				fixture.Model.Revert(row);
				Assert.Equal("2", row.Destination);
				Assert.Empty(row.Error);
			}
		}

		[Fact]
		public async Task DifferentPathsAreDistinctButDuplicateEnabledIdentityIsRejected()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				await fixture.Add(@"C:\one\editor.exe");
				await fixture.Add(@"C:\two\editor.exe");
				var duplicate = await fixture.Add(@"C:\ONE\editor.exe");
				Assert.Equal(2, fixture.Settings.Configuration.Value.Rules.Count);
				Assert.Equal(fixture.Model.Text["Duplicate"], duplicate.Error);
				duplicate.Enabled = false;
				await fixture.Model.CommitAsync(duplicate);
				Assert.Equal(3, fixture.Settings.Configuration.Value.Rules.Count);
				duplicate.Enabled = true;
				await fixture.Model.CommitAsync(duplicate);
				Assert.False(duplicate.Saved.Enabled);
				Assert.NotEmpty(duplicate.Error);
			}
		}

		[Fact]
		public async Task RuntimeChangesAndRefreshPreserveTypedNameAndRows()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\one\editor.exe", name: "work");
				row.Destination = " future ";
				fixture.Harness.Provider.PublishStable(Batch(1, 2, B, Entry(B, 0, "different", "")));
				fixture.Harness.Owner.Drain();
				for (var n = 0; n < 10; n++)
				{
					fixture.Model.Refresh();
				}
				Assert.Same(row, Assert.Single(fixture.Model.NameGroup.Rows));
				Assert.Equal(" future ", row.Destination);
				await fixture.Model.CommitAsync(row);
				Assert.Equal(" future ", row.Saved.Destination.Name);
			}
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task LatePathLookupCannotRestoreRemovedOrDisposedRow(bool dispose)
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.ExecutablePending = gate.Task;
				var row = fixture.Model.AddRow(fixture.Model.NumberGroup);
				row.AppText = @"C:\late\app.exe";
				var commit = fixture.Model.CommitAsync(row);
				if (dispose)
				{
					fixture.Model.Dispose();
				}
				else
				{
					await fixture.Model.RemoveAsync(row);
				}
				gate.SetResult(PlacementUiCatalog.Choice(row.AppText));
				await commit;
				Assert.Empty(fixture.Settings.Configuration.Value.Rules);
			}
		}

		[Fact]
		public async Task ImportInvalidatesPendingInputAndUsesImportedRules()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var invalidations = 0;
				fixture.Model.EditingInvalidated += (_, __) => invalidations++;
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.ExecutablePending = gate.Task;
				var row = fixture.Model.AddRow(fixture.Model.NumberGroup);
				row.AppText = @"C:\old\app.exe";
				var commit = fixture.Model.CommitAsync(row);
				var imported = new AppPlacementConfiguration(
					false,
					new[] { new AppPlacementRule(Guid.NewGuid(), true, PlacementUiCatalog.Choice(@"C:\imported\app.exe").Identity, PlacementDestination.ByName("future")) });
				fixture.Harness.Settings.Provider.NextImport = new Dictionary<string, object> { ["AppPlacementSettings.Configuration"] = imported };
				var stage = await fixture.Harness.Settings.PrepareImportAsync("synthetic");
				Assert.True((await fixture.Harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken)).Succeeded);
				gate.SetResult(PlacementUiCatalog.Choice(row.AppText));
				await commit;
				Assert.Same(imported.Rules[0], Assert.Single(fixture.Model.NameGroup.Rows).Saved);
				Assert.Empty(fixture.Model.NumberGroup.Rows);
				Assert.Equal(fixture.Model.Text["ConfigurationChanged"], fixture.Model.Message);
				Assert.Equal(1, invalidations);
			}
		}

		[Fact]
		public async Task SaveFailureCanRetryWithoutRepublishingAndRemoveDoesNotMoveWindows()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				fixture.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				var row = await fixture.Add(@"C:\one\app.exe");
				Assert.True(fixture.Model.SaveFailed);
				Assert.Equal(fixture.Model.Text["SaveFailed"], fixture.Model.Message);
				fixture.Harness.Settings.Provider.SaveFailure = null;
				var configuration = fixture.Settings.Configuration.Value;
				await fixture.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.Same(configuration, fixture.Settings.Configuration.Value);
				Assert.False(fixture.Model.SaveFailed);
				var draft = fixture.Model.AddRow(fixture.Model.NameGroup);
				await fixture.Model.RemoveAsync(draft);
				Assert.Same(configuration, fixture.Settings.Configuration.Value);
				await row.RemoveCommand.ExecuteAsync(null);
				Assert.Empty(fixture.Settings.Configuration.Value.Rules);
				Assert.Empty(fixture.Harness.Operations.DesktopOperationNames);
			}
		}

		[Fact]
		public async Task PickerSearchUsesPathsAndUnverifiedCandidatesCannotBeChosen()
		{
			using (var fixture = await PlacementUiFixture.Create())
			using (var picker = fixture.Model.CreatePicker())
			{
				fixture.Catalog.Pending = Task.FromResult<IReadOnlyList<PlacementAppChoice>>(new[]
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
				Assert.Empty(fixture.Settings.Configuration.Value.Rules);
			}
		}

		[Fact]
		public async Task PickerRejectsLatePreviousSourceAndDisposedQuery()
		{
			using (var fixture = await PlacementUiFixture.Create())
			using (var picker = fixture.Model.CreatePicker())
			{
				var gate = new TaskCompletionSource<IReadOnlyList<PlacementAppChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.Pending = gate.Task;
				var old = picker.WindowsCommand.ExecuteAsync(null);
				fixture.Catalog.Pending = Task.FromResult<IReadOnlyList<PlacementAppChoice>>(new[] { PlacementUiCatalog.Choice(@"C:\new\app.exe") });
				await picker.InstalledCommand.ExecuteAsync(null);
				gate.SetResult(new[] { PlacementUiCatalog.Choice(@"C:\old\app.exe") });
				await old;
				Assert.Equal(@"C:\new\app.exe", Assert.Single(picker.Candidates).Path);
				gate = new TaskCompletionSource<IReadOnlyList<PlacementAppChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.Pending = gate.Task;
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
			using (var fixture = await PlacementUiFixture.Create())
			using (var picker = fixture.Model.CreatePicker())
			{
				Assert.True(await picker.SelectFileAsync(@"C:\portable\app.exe"));
				Assert.True(picker.CanChoose);
				fixture.Catalog.ExecutablePending = Task.FromException<PlacementAppChoice>(new FileNotFoundException());
				Assert.False(await picker.SelectFileAsync(@"C:\missing\app.exe"));
				Assert.False(picker.CanChoose);
				Assert.Equal(picker.Text["InvalidPath"], picker.Status);
			}
		}

		[Fact]
		public async Task PathChangedDuringLookupIsNotOverwrittenByItsLateResult()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\first\app.exe");
				var gate = new TaskCompletionSource<PlacementAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.ExecutablePending = gate.Task;
				row.AppText = @"C:\second\app.exe";
				var commit = fixture.Model.CommitAsync(row);
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
			using (var fixture = await PlacementUiFixture.Create())
			{
				var identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				fixture.Settings.Configuration.Value = new AppPlacementConfiguration(false, new[] { new AppPlacementRule(Guid.NewGuid(), true, identity, PlacementDestination.ByNumber(1)) });
				var row = Assert.Single(fixture.Model.NumberGroup.Rows);
				Assert.DoesNotContain(identity.Value, row.Name);
				Assert.DoesNotContain(identity.Value, row.AppText);
				Assert.True(row.IsPackage);
			}
		}

		[Fact]
		public async Task PackagedChoiceKeepsVerifiedIdentityAndPathIsNotEditable()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = fixture.Model.AddRow(fixture.Model.NumberGroup);
				var identity = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				row.Use(new PlacementAppChoice("Terminal", "", @"C:\Package\terminal.exe", identity));
				await fixture.Model.CommitAsync(row);
				Assert.True(row.IsPackage);
				Assert.Equal(identity, row.Saved.App);
				Assert.Equal(@"C:\Package\terminal.exe", row.AppText);
				Assert.DoesNotContain(identity.Value, row.AppText);
			}
		}

		[Fact]
		public async Task SwitchChoicesKeepDefaultInheritedWhileShowingTheCurrentGlobalValue()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var row = await fixture.Add(@"C:\Apps\Editor.exe", 2);
				Assert.Null(row.FollowForeground);
				Assert.Same(fixture.Model.FollowOptions[0], row.FollowOption);
				fixture.Model.DefaultFollowOption = fixture.Model.DefaultFollowOptions[0];
				var defaultOn = fixture.Model.FollowOptions[0].Label;
				fixture.Model.DefaultFollowOption = fixture.Model.DefaultFollowOptions[1];
				Assert.False(fixture.Settings.Configuration.Value.FollowForeground);
				Assert.NotEqual(defaultOn, fixture.Model.FollowOptions[0].Label);
				Assert.Contains(fixture.Model.Text["FollowNo"], fixture.Model.FollowOptions[0].Label);
				// Displaying "Default (Off)" must not store Off in the rule.
				Assert.Same(row, Assert.Single(fixture.Model.NumberGroup.Rows));
				Assert.Null(Assert.Single(fixture.Settings.Configuration.Value.Rules).FollowForeground);
				row.FollowOption = fixture.Model.FollowOptions[1];
				await fixture.Model.CommitAsync(row);
				Assert.True(Assert.Single(fixture.Settings.Configuration.Value.Rules).FollowForeground);
				row.FollowOption = fixture.Model.FollowOptions[0];
				await fixture.Model.CommitAsync(row);
				Assert.Null(Assert.Single(fixture.Settings.Configuration.Value.Rules).FollowForeground);
				// A combo box reports null while its items are replaced; that is not a choice.
				row.FollowOption = null;
				Assert.Null(row.FollowForeground);
				// The width samples always contain both default labels, whatever the global value is.
				Assert.Equal(4, fixture.Model.FollowWidthSamples.Select(sample => sample.Label).Distinct().Count());
				Assert.Contains(defaultOn, fixture.Model.FollowWidthSamples.Select(sample => sample.Label));
			}
		}

		[Fact]
		public async Task MissingExecutableIsReportedUntilEditedAndLateIconsCannotOverwriteNewerInput()
		{
			using (var fixture = await PlacementUiFixture.Create())
			{
				var missing = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\Removed\old.exe");
				var package = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Example_abc!App");
				var present = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\Apps\Editor.exe");
				var gate = new TaskCompletionSource<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Catalog.IconsPending = gate.Task;
				fixture.Settings.Configuration.Value = new AppPlacementConfiguration(false, new[]
				{
					new AppPlacementRule(Guid.NewGuid(), true, missing, PlacementDestination.ByNumber(1)),
					new AppPlacementRule(Guid.NewGuid(), true, package, PlacementDestination.ByNumber(2)),
					new AppPlacementRule(Guid.NewGuid(), true, present, PlacementDestination.ByNumber(3))
				});
				var rows = fixture.Model.NumberGroup.Rows.ToArray();
				rows[2].AppText = @"C:\Apps\Other.exe";
				var icon = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
				icon.Freeze();
				gate.SetResult(new Dictionary<PlacementAppIdentity, PlacementAppIcon>
				{
					[missing] = new PlacementAppIcon(null, PlacementAppPresence.Missing),
					[package] = new PlacementAppIcon(null, PlacementAppPresence.Unknown),
					[present] = new PlacementAppIcon(icon, PlacementAppPresence.Present)
				});
				for (var wait = 0; wait < 500 && rows[0].Error.Length == 0; wait++)
				{
					await Task.Delay(10, TestContext.Current.CancellationToken);
				}
				Assert.Equal(fixture.Model.Text["InvalidPath"], rows[0].Error);
				Assert.Null(rows[0].Icon);
				// An unresolved package is not reported as removed.
				Assert.Empty(rows[1].Error);
				// The row edited while the Shell was reading keeps its newer text and gets no stale icon.
				Assert.Equal(@"C:\Apps\Other.exe", rows[2].AppText);
				Assert.Null(rows[2].Icon);
				// Leaving an unchanged row or pressing Esc keeps the report; editing the path clears it.
				await fixture.Model.CommitAsync(rows[0]);
				Assert.Equal(fixture.Model.Text["InvalidPath"], rows[0].Error);
				fixture.Model.Revert(rows[0]);
				Assert.Equal(fixture.Model.Text["InvalidPath"], rows[0].Error);
				rows[0].AppText = @"C:\Replaced\new.exe";
				Assert.Empty(rows[0].Error);
				Assert.Equal(3, fixture.Settings.Configuration.Value.Rules.Count);
			}
		}

		[Fact]
		public async Task IconReaderSeparatesMissingExecutablesAndNeverTreatsPackageIdsAsPaths()
		{
			var directory = Path.Combine(Path.GetTempPath(), "SylphyHorn.Tests." + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				// An executable without an icon resource of its own still exists and gets an icon.
				var existing = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, Path.Combine(directory, "no-icon.exe"));
				File.WriteAllBytes(existing.Value, new byte[] { 0x4D, 0x5A });
				var missing = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, Path.Combine(directory, "missing.exe"));
				var package = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "SylphyHorn.Tests.Absent_0000000000000!App");
				var icons = await new PlacementAppCatalog().ReadIconsAsync(new[] { existing, missing, package, existing }, TestContext.Current.CancellationToken);
				Assert.Equal(3, icons.Count);
				Assert.Equal(PlacementAppPresence.Present, icons[existing].Presence);
				Assert.NotNull(icons[existing].Icon);
				Assert.Equal(PlacementAppPresence.Missing, icons[missing].Presence);
				Assert.Null(icons[missing].Icon);
				Assert.Equal(PlacementAppPresence.Unknown, icons[package].Presence);
			}
			finally
			{
				try { Directory.Delete(directory, true); }
				catch (IOException) { /* The Shell may still hold the file briefly. */ }
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
			var fixture = new PlacementUiFixture { Harness = await Harness.Initialized() };
			fixture.Settings = new AppPlacementSettings(fixture.Harness.Settings.Provider);
			fixture.Model = new AppPlacementSettingsViewModel(fixture.Settings, fixture.Harness.Runtime, fixture.Catalog, () => fixture.Harness.Settings.Provider.SaveWithResultAsync());
			return fixture;
		}

		internal async Task<PlacementRuleRow> Add(string path, int number = 1, string name = null)
		{
			var row = this.Model.AddRow(name == null ? this.Model.NumberGroup : this.Model.NameGroup);
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
		internal Task<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>> IconsPending;
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

		public Task<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>> ReadIconsAsync(IReadOnlyCollection<PlacementAppIdentity> apps, CancellationToken cancellation)
			=> this.IconsPending ?? Task.FromResult<IReadOnlyDictionary<PlacementAppIdentity, PlacementAppIcon>>(new Dictionary<PlacementAppIdentity, PlacementAppIcon>());
	}
}
