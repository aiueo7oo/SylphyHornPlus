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
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				var work = await f.Add(true, "Work", "  \"C:\\Wallpapers\\work.jpg\" ");
				Assert.Equal("", work.Error);
				Assert.Equal(@"C:\Wallpapers\work.jpg", work.WallpaperPath);
				Assert.Equal(new[] { @"C:\Wallpapers\work.jpg" }, f.Images.Validated);
				var entry = Assert.Single(f.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(("Work", (int?)null, @"C:\Wallpapers\work.jpg"), (entry.Name, entry.Number, entry.WallpaperPath));

				var duplicate = await f.Add(true, "Work", @"C:\Wallpapers\other.jpg");
				Assert.Equal(f.Model.Text["DuplicateName"], duplicate.Error);
				var third = await f.Add(false, "03", @"C:\Wallpapers\third.jpg");
				Assert.Equal("3", third.Destination);
				var number = await f.Add(false, "3", @"C:\Wallpapers\another.jpg");
				Assert.Equal(f.Model.Text["DuplicateNumber"], number.Error);
				number.Destination = "0";
				await f.Model.CommitAsync(number);
				Assert.Equal(f.Model.Text["InvalidNumber"], number.Error);
				number.Destination = "4";
				number.WallpaperPath = "another.jpg";
				await f.Model.CommitAsync(number);
				Assert.Equal(f.Model.Text["AbsolutePath"], number.Error);
				number.WallpaperPath = " ";
				await f.Model.CommitAsync(number);
				Assert.Equal(f.Model.Text["EnterPath"], number.Error);
				var blank = f.Model.AddRow(f.Model.NameGroup);
				blank.WallpaperPath = @"C:\Wallpapers\blank.jpg";
				await f.Model.CommitAsync(blank);
				Assert.Equal(f.Model.Text["InvalidName"], blank.Error);
				Assert.Equal(2, f.Images.Validated.Count);

				// Changing only the destination keeps the image and does not read it again.
				work.Destination = "Home";
				await f.Model.CommitAsync(work);
				Assert.Equal(2, f.Images.Validated.Count);
				Assert.Equal(new[] { "Home" }, f.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name != null).Select(item => item.Name));
				await f.Model.CommitAsync(work);
				Assert.Equal(2, f.Images.Validated.Count);

				await work.RemoveCommand.ExecuteAsync(null);
				await number.RemoveCommand.ExecuteAsync(null);
				Assert.Equal(3, Assert.Single(f.Settings.DesktopWallpapersOnCreation.Value).Number);
				Assert.Equal(new[] { "3" }, f.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.Empty(f.Harness.Operations.DesktopOperationNames);
				Assert.Equal(0, f.Harness.Operations.WallpaperCalls);
			}
		}

		[Fact]
		public async Task UnreadableImagesAndLateResultsNeverOverwriteNewerInput()
		{
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				f.Images.Failure = new FileFormatException("synthetic");
				var row = await f.Add(true, "Work", @"C:\Wallpapers\notes.txt");
				Assert.Equal(f.Model.Text["UnreadableImage"], row.Error);
				Assert.Empty(f.Settings.DesktopWallpapersOnCreation.Value);
				f.Images.Failure = null;

				var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Images.Pending = gate.Task;
				row.WallpaperPath = @"C:\Wallpapers\first.jpg";
				var commit = f.Model.CommitAsync(row);
				row.WallpaperPath = @"C:\Wallpapers\second.jpg";
				gate.SetResult(true);
				await commit;
				Assert.Empty(f.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(@"C:\Wallpapers\second.jpg", row.WallpaperPath);
				Assert.Equal("", row.Error);

				// A failure reported after Esc restored the row does not show an error on the restored text.
				gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Images.Pending = gate.Task;
				commit = f.Model.CommitAsync(row);
				f.Model.Revert(row);
				gate.SetException(new IOException("late"));
				await commit;
				Assert.Equal("", row.Error);
				Assert.Equal("", row.WallpaperPath);

				// A row removed while its image is read is not saved afterwards.
				gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				f.Images.Pending = gate.Task;
				row.Destination = "Work";
				row.WallpaperPath = @"C:\Wallpapers\third.jpg";
				commit = f.Model.CommitAsync(row);
				await f.Model.RemoveAsync(row);
				gate.SetResult(true);
				await commit;
				Assert.Empty(f.Settings.DesktopWallpapersOnCreation.Value);
			}
		}

		[Fact]
		public async Task MissingSavedImageKeepsTheEntryEditableAndIsReportedWhileItsPathIsShown()
		{
			var missing = @"D:\Images\presentation.png";
			using (var f = await CreationWallpaperUiFixture.Create(new[]
			{
				new DesktopWallpaperOnCreation("Presentation", null, missing),
				new DesktopWallpaperOnCreation(null, 2, @"C:\Wallpapers\second.jpg")
			}, missing: new[] { missing }))
			{
				var row = Assert.Single(f.Model.NameGroup.Rows);
				await Until(() => row.Error.Length > 0);
				Assert.Equal(f.Model.Text["MissingImage"], row.Error);
				Assert.Equal("", Assert.Single(f.Model.NumberGroup.Rows).Error);
				Assert.Empty(f.Images.Validated);

				row.WallpaperPath = @"C:\Wallpapers\fixed.jpg";
				Assert.Equal("", row.Error);
				f.Model.Revert(row);
				Assert.Equal(f.Model.Text["MissingImage"], row.Error);

				// The saved image is not read again when only the destination changes, and it stays reported.
				row.Destination = "Meeting";
				await f.Model.CommitAsync(row);
				Assert.Empty(f.Images.Validated);
				Assert.Equal("Meeting", f.Settings.DesktopWallpapersOnCreation.Value[0].Name);
				Assert.Equal(missing, f.Settings.DesktopWallpapersOnCreation.Value[0].WallpaperPath);
				Assert.Equal(f.Model.Text["MissingImage"], row.Error);

				await row.RemoveCommand.ExecuteAsync(null);
				Assert.Equal(2, Assert.Single(f.Settings.DesktopWallpapersOnCreation.Value).Number);
			}
		}

		[Fact]
		public async Task ExternalChangesKeepUnrelatedEditsAndReportOnlyDiscardedOnes()
		{
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				var work = await f.Add(true, "Work", @"C:\Wallpapers\work.jpg");
				var third = await f.Add(false, "3", @"C:\Wallpapers\third.jpg");
				work.WallpaperPath = @"C:\Wallpapers\typing";
				var draft = f.Model.AddRow(f.Model.NumberGroup);
				draft.Destination = "5";

				// Like "desktop creation wallpaper set --number 3": the other rows keep what is being typed.
				f.Settings.DesktopWallpapersOnCreation.Value = new[]
				{
					new DesktopWallpaperOnCreation("Work", null, @"C:\Wallpapers\work.jpg"),
					new DesktopWallpaperOnCreation(null, 3, @"C:\Wallpapers\cli.jpg"),
					new DesktopWallpaperOnCreation(null, 4, @"C:\Wallpapers\fourth.jpg")
				};
				Assert.Same(work, Assert.Single(f.Model.NameGroup.Rows));
				Assert.Equal(@"C:\Wallpapers\typing", work.WallpaperPath);
				Assert.Equal(new[] { "3", "4", "5" }, f.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.NotSame(third, f.Model.NumberGroup.Rows[0]);
				Assert.Equal(@"C:\Wallpapers\cli.jpg", f.Model.NumberGroup.Rows[0].WallpaperPath);
				Assert.Same(draft, f.Model.NumberGroup.Rows[2]);
				Assert.Equal("", f.Model.EditDiscardedMessage);

				// The entry being edited is removed elsewhere: its edit is discarded and reported.
				f.Settings.DesktopWallpapersOnCreation.Value = f.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name == null).ToArray();
				Assert.Empty(f.Model.NameGroup.Rows);
				Assert.Equal(f.Model.Text["ConfigurationChanged"], f.Model.EditDiscardedMessage);

				// Import and reset go through the same notification.
				var imported = new[] { new DesktopWallpaperOnCreation("Imported", null, @"E:\Images\imported.png") };
				f.Harness.Settings.Provider.NextImport = new Dictionary<string, object> { [DesktopWallpaperOnCreation.SettingsKey] = imported };
				var stage = await f.Harness.Settings.PrepareImportAsync("synthetic");
				Assert.True((await f.Harness.Runtime.CommitPreparedImportAsync(stage, false, TestContext.Current.CancellationToken)).Succeeded);
				Assert.Equal("Imported", Assert.Single(f.Model.NameGroup.Rows).Destination);
				Assert.Equal(new[] { "5" }, f.Model.NumberGroup.Rows.Select(row => row.Destination));
				Assert.True((await f.Harness.Runtime.ResetSettingsAsync(TestContext.Current.CancellationToken)).Succeeded);
				Assert.Empty(f.Model.NameGroup.Rows);
				Assert.Same(draft, Assert.Single(f.Model.NumberGroup.Rows));
			}
		}

		[Fact]
		public async Task SaveFailureCanRetryAndChosenImageIsCommitted()
		{
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				f.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				f.Chosen = @"C:\Wallpapers\chosen.png";
				var row = f.Model.AddRow(f.Model.NumberGroup);
				await f.Model.ChooseImageAsync(row);
				Assert.Equal(@"C:\Wallpapers\chosen.png", row.WallpaperPath);
				Assert.Empty(f.Settings.DesktopWallpapersOnCreation.Value);
				row.Destination = "2";
				await f.Model.ChooseImageAsync(row);
				Assert.Equal(2, Assert.Single(f.Settings.DesktopWallpapersOnCreation.Value).Number);
				Assert.True(f.Model.SaveFailed);
				Assert.Equal(f.Model.Text["SaveFailed"], f.Model.SaveMessage);
				f.Harness.Settings.Provider.SaveFailure = null;
				var entries = f.Settings.DesktopWallpapersOnCreation.Value;
				await f.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.False(f.Model.SaveFailed);
				Assert.Equal("", f.Model.SaveMessage);
				Assert.Same(entries, f.Settings.DesktopWallpapersOnCreation.Value);
				f.Chosen = null;
				await f.Model.ChooseImageAsync(row);
				Assert.Equal(@"C:\Wallpapers\chosen.png", row.WallpaperPath);
			}
		}

		[Fact]
		public async Task SaveFailureAndDiscardedEditAreReportedSeparately()
		{
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				var work = await f.Add(true, "Work", @"C:\Wallpapers\work.jpg");
				f.Harness.Settings.Provider.SaveFailure = new IOException("synthetic");
				var third = await f.Add(false, "3", @"C:\Wallpapers\third.jpg");
				Assert.True(f.Model.SaveFailed);
				Assert.Equal(f.Model.Text["SaveFailed"], f.Model.SaveMessage);
				Assert.True(f.Model.RetrySaveCommand.CanExecute(null));

				// The entry being edited is removed elsewhere while the save failure is still unresolved.
				work.WallpaperPath = @"C:\Wallpapers\typing";
				var draft = f.Model.AddRow(f.Model.NumberGroup);
				draft.Destination = "7";
				f.Settings.DesktopWallpapersOnCreation.Value = f.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name == null).ToArray();
				Assert.Equal(f.Model.Text["ConfigurationChanged"], f.Model.EditDiscardedMessage);
				Assert.True(f.Model.SaveFailed);
				Assert.Equal(f.Model.Text["SaveFailed"], f.Model.SaveMessage);
				Assert.True(f.Model.RetrySaveCommand.CanExecute(null));

				// Retrying saves the settings already in memory; it does not commit the unsaved entry or touch desktops.
				f.Harness.Settings.Provider.SaveFailure = null;
				var entries = f.Settings.DesktopWallpapersOnCreation.Value;
				await f.Model.RetrySaveCommand.ExecuteAsync(null);
				Assert.False(f.Model.SaveFailed);
				Assert.Equal("", f.Model.SaveMessage);
				Assert.False(f.Model.RetrySaveCommand.CanExecute(null));
				Assert.Equal(f.Model.Text["ConfigurationChanged"], f.Model.EditDiscardedMessage);
				Assert.Same(entries, f.Settings.DesktopWallpapersOnCreation.Value);
				Assert.Equal(3, Assert.Single(entries).Number);
				Assert.Null(draft.Saved);
				Assert.Contains(draft, f.Model.NumberGroup.Rows);
				Assert.Equal(0, f.Harness.Operations.WallpaperCalls);

				// A later successful save also leaves the discarded-edit notice in place.
				await third.RemoveCommand.ExecuteAsync(null);
				Assert.Equal("", f.Model.SaveMessage);
				Assert.Equal(f.Model.Text["ConfigurationChanged"], f.Model.EditDiscardedMessage);
			}
		}

		[Fact]
		public async Task ChoicesNameSupportAndLegacyNoteFollowTheEnvironment()
		{
			using (var f = await CreationWallpaperUiFixture.Create())
			{
				f.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Work", ""), Entry(B, 1, "", ""), Entry(C, 2, "Work", "")));
				f.Harness.Owner.Drain();
				f.Model.RefreshDestinationChoices();
				Assert.Equal(new[] { "Work" }, f.Model.NameGroup.Choices);
				Assert.Equal(new[] { "1", "2", "3" }, f.Model.NumberGroup.Choices);
				Assert.Equal("", f.Model.LegacyNote);
			}
			using (var f = await CreationWallpaperUiFixture.Create(nameSupported: false, legacyWallpaper: true))
			{
				Assert.Equal(f.Model.Text["NumberList"], Assert.Single(f.Model.Groups).Title);
				f.Settings.ChangeBackgroundEachDesktop.Value = false;
				Assert.Contains(SylphyHorn.Properties.Resources.Settings_Background_ChangeBackground, f.Model.LegacyNote);
				f.Settings.ChangeBackgroundEachDesktop.Value = true;
				Assert.Equal("", f.Model.LegacyNote);
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
			var f = new CreationWallpaperUiFixture { Harness = await Harness.Initialized() };
			f.Settings = new GeneralSettings(f.Harness.Settings.Provider);
			if (entries != null)
			{
				f.Settings.DesktopWallpapersOnCreation.Value = entries;
			}
			if (missing != null)
			{
				f.Images.Missing.UnionWith(missing);
			}
			f.Model = new CreationWallpaperSettingsViewModel(f.Settings, f.Harness.Runtime, f.Images,
				() => f.Harness.Settings.Provider.SaveWithResultAsync(), () => f.Chosen, nameSupported, legacyWallpaper);
			return f;
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
