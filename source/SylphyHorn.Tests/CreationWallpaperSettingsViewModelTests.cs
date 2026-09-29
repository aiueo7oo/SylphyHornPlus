using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Serialization;
using SylphyHorn.UI.Bindings;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[Collection(PlacementUiCollection.Name)]
	public sealed class CreationWallpaperSettingsViewModelTests
	{
		[Fact]
		public async Task EntriesAreValidatedOnceAndSavedWithoutDuplicatesOrUnneededImageReads()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				var work = await fixture.Add(true, "Work", "  \"C:\\Wallpapers\\work.jpg\" ");
				Assert.Equal("", work.Error);
				Assert.Equal(@"C:\Wallpapers\work.jpg", work.WallpaperPath);
				Assert.Equal(new[] { @"C:\Wallpapers\work.jpg" }, fixture.Images.Validated);
				var entry = Assert.Single(fixture.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(("Work", (int?)null, @"C:\Wallpapers\work.jpg"), (entry.Name, entry.Number, entry.WallpaperPath));

				var duplicate = await fixture.Add(true, "Work", @"C:\Wallpapers\other.jpg");
				Assert.Equal(fixture.Model.Text["DuplicateName"], duplicate.Error);
				var third = await fixture.Add(false, "03", @"C:\Wallpapers\third.jpg");
				Assert.Equal("3", third.Destination);
				var number = await fixture.Add(false, "3", @"C:\Wallpapers\another.jpg");
				Assert.Equal(fixture.Model.Text["DuplicateNumber"], number.Error);
				number.Destination = "0";
				await fixture.Model.CommitAsync(number);
				Assert.Equal(fixture.Model.Text["InvalidNumber"], number.Error);
				number.Destination = "4";
				number.WallpaperPath = "another.jpg";
				await fixture.Model.CommitAsync(number);
				Assert.Equal(fixture.Model.Text["AbsolutePath"], number.Error);
				number.WallpaperPath = " ";
				await fixture.Model.CommitAsync(number);
				Assert.Equal(fixture.Model.Text["EnterPath"], number.Error);
				var blank = fixture.Model.AddRow(fixture.Model.NameGroup);
				blank.WallpaperPath = @"C:\Wallpapers\blank.jpg";
				await fixture.Model.CommitAsync(blank);
				Assert.Equal(fixture.Model.Text["InvalidName"], blank.Error);
				Assert.Equal(2, fixture.Images.Validated.Count);

				// Changing only the destination keeps the image and does not read it again.
				work.Destination = "Home";
				await fixture.Model.CommitAsync(work);
				Assert.Equal(2, fixture.Images.Validated.Count);
				Assert.Equal(new[] { "Home" }, fixture.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name != null).Select(item => item.Name));
				await fixture.Model.CommitAsync(work);
				Assert.Equal(2, fixture.Images.Validated.Count);

				await work.RemoveCommand.ExecuteAsync(null);
				await number.RemoveCommand.ExecuteAsync(null);
				Assert.Equal(3, Assert.Single(fixture.Settings.DesktopWallpapersOnCreation.Value).Number);
				Assert.Equal(new[] { "3" }, fixture.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.Empty(fixture.Harness.Operations.DesktopOperationNames);
				Assert.Equal(0, fixture.Harness.Operations.WallpaperCalls);
			}
		}

		[Fact]
		public async Task NumbersTypedWithTheImeAreSavedAsDigitsAndLimitedToTheMaximum()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				var tooHigh = await fixture.Add(false, "101", @"C:\Wallpapers\high.jpg");
				Assert.Equal(fixture.Model.Text["InvalidNumber"], tooHigh.Error);
				var typed = await fixture.Add(false, " \uFF17 ", @"C:\Wallpapers\seven.jpg");
				Assert.Equal("", typed.Error);
				Assert.Equal("7", typed.Destination);
				Assert.Equal(7, Assert.Single(fixture.Settings.DesktopWallpapersOnCreation.Value).Number);
			}
		}

		[Fact]
		public async Task UnreadableImagesAndLateResultsNeverOverwriteNewerInput()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				fixture.Images.Failure = new FileFormatException("synthetic");
				var row = await fixture.Add(true, "Work", @"C:\Wallpapers\notes.txt");
				Assert.Equal(fixture.Model.Text["UnreadableImage"], row.Error);
				Assert.Empty(fixture.Settings.DesktopWallpapersOnCreation.Value);
				fixture.Images.Failure = null;

				var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Images.Pending = gate.Task;
				row.WallpaperPath = @"C:\Wallpapers\first.jpg";
				var commit = fixture.Model.CommitAsync(row);
				row.WallpaperPath = @"C:\Wallpapers\second.jpg";
				gate.SetResult(true);
				await commit;
				Assert.Empty(fixture.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(@"C:\Wallpapers\second.jpg", row.WallpaperPath);
				Assert.Equal("", row.Error);

				// A failure reported after Esc restored the row does not show an error on the restored text.
				gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Images.Pending = gate.Task;
				commit = fixture.Model.CommitAsync(row);
				fixture.Model.Revert(row);
				gate.SetException(new IOException("late"));
				await commit;
				Assert.Equal("", row.Error);
				Assert.Equal("", row.WallpaperPath);

				// A row removed while its image is read is not saved afterwards.
				gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				fixture.Images.Pending = gate.Task;
				row.Destination = "Work";
				row.WallpaperPath = @"C:\Wallpapers\third.jpg";
				commit = fixture.Model.CommitAsync(row);
				await fixture.Model.RemoveAsync(row);
				gate.SetResult(true);
				await commit;
				Assert.Empty(fixture.Settings.DesktopWallpapersOnCreation.Value);
			}
		}

		[Fact]
		public async Task MissingSavedImageKeepsTheEntryEditableAndIsReportedWhileItsPathIsShown()
		{
			var missing = @"D:\Images\presentation.png";
			using (var fixture = await CreationWallpaperUiFixture.Create(new[]
			{
				new DesktopWallpaperOnCreation("Presentation", null, missing),
				new DesktopWallpaperOnCreation(null, 2, @"C:\Wallpapers\second.jpg")
			}, missing: new[] { missing }))
			{
				var row = Assert.Single(fixture.Model.NameGroup.Rows);
				await Until(() => row.Error.Length > 0);
				Assert.Equal(fixture.Model.Text["MissingImage"], row.Error);
				Assert.Equal("", Assert.Single(fixture.Model.NumberGroup.Rows).Error);
				Assert.Empty(fixture.Images.Validated);

				row.WallpaperPath = @"C:\Wallpapers\fixed.jpg";
				Assert.Equal("", row.Error);
				fixture.Model.Revert(row);
				Assert.Equal(fixture.Model.Text["MissingImage"], row.Error);

				// The saved image is not read again when only the destination changes, and it stays reported.
				row.Destination = "Meeting";
				await fixture.Model.CommitAsync(row);
				Assert.Empty(fixture.Images.Validated);
				Assert.Equal("Meeting", fixture.Settings.DesktopWallpapersOnCreation.Value[0].Name);
				Assert.Equal(missing, fixture.Settings.DesktopWallpapersOnCreation.Value[0].WallpaperPath);
				Assert.Equal(fixture.Model.Text["MissingImage"], row.Error);

				await row.RemoveCommand.ExecuteAsync(null);
				Assert.Equal(2, Assert.Single(fixture.Settings.DesktopWallpapersOnCreation.Value).Number);
			}
		}

		[Fact]
		public async Task ExternalChangesKeepUnrelatedEditsAndReportOnlyDiscardedOnes()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				var work = await fixture.Add(true, "Work", @"C:\Wallpapers\work.jpg");
				var third = await fixture.Add(false, "3", @"C:\Wallpapers\third.jpg");
				work.WallpaperPath = @"C:\Wallpapers\typing";
				var draft = fixture.Model.AddRow(fixture.Model.NumberGroup);
				draft.Destination = "5";

				// Like "desktop creation wallpaper set --number 3": the other rows keep what is being typed.
				fixture.Settings.DesktopWallpapersOnCreation.Value = new[]
				{
					new DesktopWallpaperOnCreation("Work", null, @"C:\Wallpapers\work.jpg"),
					new DesktopWallpaperOnCreation(null, 3, @"C:\Wallpapers\cli.jpg"),
					new DesktopWallpaperOnCreation(null, 4, @"C:\Wallpapers\fourth.jpg")
				};
				Assert.Same(work, Assert.Single(fixture.Model.NameGroup.Rows));
				Assert.Equal(@"C:\Wallpapers\typing", work.WallpaperPath);
				Assert.Equal(new[] { "3", "4", "5" }, fixture.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.NotSame(third, fixture.Model.NumberGroup.Rows[0]);
				Assert.Equal(@"C:\Wallpapers\cli.jpg", fixture.Model.NumberGroup.Rows[0].WallpaperPath);
				Assert.Same(draft, fixture.Model.NumberGroup.Rows[2]);
				Assert.Equal("", fixture.Model.EditDiscardedMessage);

				// The entry being edited is removed elsewhere: its edit is discarded and reported.
				fixture.Settings.DesktopWallpapersOnCreation.Value = fixture.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name == null).ToArray();
				Assert.Empty(fixture.Model.NameGroup.Rows);
				Assert.Equal(fixture.Model.Text["ConfigurationChanged"], fixture.Model.EditDiscardedMessage);

				// Import and reset go through the same notification.
				var imported = new[] { new DesktopWallpaperOnCreation("Imported", null, @"E:\Images\imported.png") };
				fixture.Harness.Settings.Provider.NextImport = new Dictionary<string, object> { [GeneralSettings.DesktopWallpapersOnCreationKey] = imported };
				var stage = await fixture.Harness.Settings.PrepareImportAsync("synthetic");
				Assert.True((await fixture.Harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken)).Succeeded);
				Assert.Equal("Imported", Assert.Single(fixture.Model.NameGroup.Rows).Destination);
				Assert.Equal(new[] { "5" }, fixture.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.True((await fixture.Harness.Runtime.ResetSettingsAsync(TestContext.Current.CancellationToken)).Succeeded);
				Assert.Empty(fixture.Model.NameGroup.Rows);
				Assert.Same(draft, Assert.Single(fixture.Model.NumberGroup.Rows));
			}
		}

		[Fact]
		public async Task SaveFailureCanRetryAndChosenImageIsCommitted()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				fixture.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				fixture.Chosen = @"C:\Wallpapers\chosen.png";
				var row = fixture.Model.AddRow(fixture.Model.NumberGroup);
				await fixture.Model.ChooseImageAsync(row);
				Assert.Equal(@"C:\Wallpapers\chosen.png", row.WallpaperPath);
				Assert.Empty(fixture.Settings.DesktopWallpapersOnCreation.Value);
				row.Destination = "2";
				await fixture.Model.ChooseImageAsync(row);
				Assert.Equal(2, Assert.Single(fixture.Settings.DesktopWallpapersOnCreation.Value).Number);
				Assert.True(fixture.Model.SaveFailed);
				Assert.Equal(fixture.Model.Text["SaveFailed"], fixture.Model.SaveMessage);
				fixture.Harness.Settings.Provider.SaveFailure = null;
				var entries = fixture.Settings.DesktopWallpapersOnCreation.Value;
				await fixture.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.False(fixture.Model.SaveFailed);
				Assert.Equal("", fixture.Model.SaveMessage);
				Assert.Same(entries, fixture.Settings.DesktopWallpapersOnCreation.Value);
				fixture.Chosen = null;
				await fixture.Model.ChooseImageAsync(row);
				Assert.Equal(@"C:\Wallpapers\chosen.png", row.WallpaperPath);
			}
		}

		[Fact]
		public async Task SaveFailureAndDiscardedEditAreReportedSeparately()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				var work = await fixture.Add(true, "Work", @"C:\Wallpapers\work.jpg");
				fixture.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				var third = await fixture.Add(false, "3", @"C:\Wallpapers\third.jpg");
				Assert.True(fixture.Model.SaveFailed);
				Assert.Equal(fixture.Model.Text["SaveFailed"], fixture.Model.SaveMessage);
				Assert.True(fixture.Model.RetrySaveCommand.CanExecute(null));

				// The entry being edited is removed elsewhere while the save failure is still unresolved.
				work.WallpaperPath = @"C:\Wallpapers\typing";
				var draft = fixture.Model.AddRow(fixture.Model.NumberGroup);
				draft.Destination = "7";
				fixture.Settings.DesktopWallpapersOnCreation.Value = fixture.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name == null).ToArray();
				Assert.Equal(fixture.Model.Text["ConfigurationChanged"], fixture.Model.EditDiscardedMessage);
				Assert.True(fixture.Model.SaveFailed);
				Assert.Equal(fixture.Model.Text["SaveFailed"], fixture.Model.SaveMessage);
				Assert.True(fixture.Model.RetrySaveCommand.CanExecute(null));

				// Retrying saves the settings already in memory; it does not commit the unsaved entry or touch desktops.
				fixture.Harness.Settings.Provider.SaveFailure = null;
				var entries = fixture.Settings.DesktopWallpapersOnCreation.Value;
				await fixture.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.False(fixture.Model.SaveFailed);
				Assert.Equal("", fixture.Model.SaveMessage);
				Assert.False(fixture.Model.RetrySaveCommand.CanExecute(null));
				Assert.Equal(fixture.Model.Text["ConfigurationChanged"], fixture.Model.EditDiscardedMessage);
				Assert.Same(entries, fixture.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(3, Assert.Single(entries).Number);
				Assert.Null(draft.Saved);
				Assert.Contains(draft, fixture.Model.NumberGroup.Rows);
				Assert.Equal(0, fixture.Harness.Operations.WallpaperCalls);

				// A later successful save also leaves the discarded-edit notice in place.
				await third.RemoveCommand.ExecuteAsync(null);
				Assert.Equal("", fixture.Model.SaveMessage);
				Assert.Equal(fixture.Model.Text["ConfigurationChanged"], fixture.Model.EditDiscardedMessage);
			}
		}

		[Fact]
		public async Task ChoicesNameSupportAndLegacyNoteFollowTheEnvironment()
		{
			using (var fixture = await CreationWallpaperUiFixture.Create())
			{
				fixture.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Work", ""), Entry(B, 1, "", ""), Entry(C, 2, "Work", "")));
				fixture.Harness.Owner.Drain();
				fixture.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work" }, fixture.Model.NameGroup.Choices);
				Assert.Equal(new[] { "1", "2", "3" }, fixture.Model.NumberGroup.Choices);
				Assert.Equal("", fixture.Model.LegacyNote);
			}
			using (var fixture = await CreationWallpaperUiFixture.Create(nameSupported: false, legacyWallpaper: true))
			{
				Assert.Equal(fixture.Model.Text["NumberList"], Assert.Single(fixture.Model.Groups).Title);
				fixture.Settings.ChangeBackgroundEachDesktop.Value = false;
				Assert.Contains(SylphyHorn.Properties.Resources.Settings_Background_ChangeBackground, fixture.Model.LegacyNote);
				fixture.Settings.ChangeBackgroundEachDesktop.Value = true;
				Assert.Equal("", fixture.Model.LegacyNote);
			}
		}

		private static async Task Until(Func<bool> condition)
		{
			for (var waited = 0; waited < 5000 && !condition(); waited += 10)
			{
				await Task.Delay(10);
			}
		}
	}

	internal sealed class CreationWallpaperUiFixture : IDisposable
	{
		internal Harness Harness;
		internal GeneralSettings Settings;
		internal CreationWallpaperSettingsViewModel Model;
		internal FakeCreationWallpaperImages Images = new FakeCreationWallpaperImages();
		internal string Chosen;

		internal static async Task<CreationWallpaperUiFixture> Create(DesktopWallpaperOnCreation[] entries = null, string[] missing = null,
			bool nameSupported = true, bool legacyWallpaper = false)
		{
			var fixture = new CreationWallpaperUiFixture { Harness = await Harness.Initialized() };
			fixture.Settings = new GeneralSettings(fixture.Harness.Settings.Provider);
			if (entries != null)
			{
				fixture.Settings.DesktopWallpapersOnCreation.Value = entries;
			}
			if (missing != null)
			{
				fixture.Images.Missing.UnionWith(missing);
			}
			fixture.Model = new CreationWallpaperSettingsViewModel(fixture.Settings, fixture.Harness.Runtime, fixture.Images,
				() => fixture.Harness.Settings.Provider.SaveWithResultAsync(), () => fixture.Chosen, nameSupported, legacyWallpaper);
			return fixture;
		}

		internal async Task<CreationWallpaperRow> Add(bool byName, string destination, string path)
		{
			var row = this.Model.AddRow(byName ? this.Model.NameGroup : this.Model.NumberGroup);
			row.Destination = destination;
			row.WallpaperPath = path;
			await this.Model.CommitAsync(row);
			return row;
		}

		public void Dispose() => this.Model.Dispose();
	}

	internal sealed class FakeCreationWallpaperImages : ICreationWallpaperImages
	{
		internal readonly List<string> Validated = new List<string>();
		internal readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);
		internal Task Pending;
		internal Exception Failure;

		public async Task ValidateAsync(string path, CancellationToken cancellation)
		{
			this.Validated.Add(path);
			if (this.Pending != null)
			{
				await this.Pending;
			}
			if (this.Failure != null)
			{
				throw this.Failure;
			}
		}

		public Task<IReadOnlyCollection<string>> FindMissingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellation)
			=> Task.FromResult<IReadOnlyCollection<string>>(paths.Where(this.Missing.Contains).ToArray());
	}
}
