using System;
using System.IO;
using System.Linq;
using SylphyHorn.Serialization;
using System.Collections.Generic;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Services;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class CreationWallpaperTests : IDisposable
	{
#if NET10_0_OR_GREATER
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task CliCreateUsesNameEvenWithReentrantUnnamedPublication(bool reentrant)
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation("Work", null, Image("named.jpg")) }).ToArray();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			h.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "name", "wall")));
			h.Operations.Creating = () =>
			{
				if (reentrant) h.Provider.PublishStable(Batch(1, 3, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "", "")));
				return B;
			};
			h.Provider.EnqueueResult(Batch(1, 4, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", reentrant ? Image("named.jpg") : "")));
			var response = await h.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "create", "--name", "Work" }), TestContext.Current.CancellationToken);
			Assert.True(response.Success, response.Error?.Code);
			Assert.Equal(Image("named.jpg"), h.Runtime.State.Records[B].WallpaperPath.Value);
			Assert.Equal(1, h.Operations.WallpaperCalls);
		}
#endif

		[Fact]
		public async Task ExternalAdditionUsesNameBeforeNumberAndDoesNotRepeat()
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation("Work", null, Image("named.jpg")) }).ToArray();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			var batch = Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", ""));
			h.Provider.PublishStable(batch);
			Assert.Equal(Image("named.jpg"), h.Runtime.State.Records[B].WallpaperPath.Value);
			h.Provider.PublishStable(batch);
			Assert.Equal(1, h.Operations.WallpaperCalls);
#if NET10_0_OR_GREATER
			Assert.True(h.Runtime.CliAvailable);
#endif
		}

		[Fact]
		public async Task InitialAndResetSnapshotsDoNotApply()
		{
			var h = Harness.Create(Batch(1, 1, A, Entry(A, 0, "Work", "original")));
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation("Work", null, Image("named.jpg")) }).ToArray();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			await h.Runtime.InitializeAsync(false, TestContext.Current.CancellationToken);
			h.Provider.PublishStable(Batch(2, 1, A, Entry(A, 0, "Work", "original"), Entry(B, 1, "Work", "original")));
			Assert.Equal(0, h.Operations.WallpaperCalls);
		}

		[Fact]
		public async Task FailedNamedWallpaperDoesNotFallBackOrBreakRuntime()
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation("Work", null, Image("bad.jpg")) }).ToArray();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			h.Operations.FailWallpaperValue = Image("bad.jpg");
			var faults = 0;
			h.Runtime.Faulted += (_, __) => faults++;
			h.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", "original")));
			Assert.Equal(1, faults);
			Assert.Equal(1, h.Operations.WallpaperCalls);
			Assert.Equal("original", h.Runtime.State.Records[B].WallpaperPath.Value);
#if NET10_0_OR_GREATER
			Assert.True(h.Runtime.CliAvailable);
#endif
		}

		[Fact]
		public async Task NameArrivingAfterAdditionDoesNotCorrectNumberWallpaper()
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation("Work", null, Image("named.jpg")) }).ToArray();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			h.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "", "")));
			h.Provider.PublishStable(Batch(1, 3, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", Image("number.jpg"))));
			Assert.Equal(Image("number.jpg"), h.Runtime.State.Records[B].WallpaperPath.Value);
			Assert.Equal(1, h.Operations.WallpaperCalls);
		}

		[Fact]
		public async Task FailedImportCreationKeepsImportedDesktopExcludedDuringRecovery()
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = h.Settings.WallpapersOnCreation.Concat(new[] { new DesktopWallpaperOnCreation(null, 2, Image("number.jpg")) }).ToArray();
			h.Operations.Creating = () => B;
			h.Operations.FailNameValue = "imported";
			h.Settings.Provider.NextImport = new Dictionary<string, object>
			{
				[SettingsService.DesktopNamesKey + "#Count"] = 2,
				[SettingsService.DesktopNamesKey + "[0]"] = "imported",
				[SettingsService.DesktopNamesKey + "[1]"] = "Work",
			};
			var batch = Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", "original"));
			h.Provider.EnqueueResult(batch);
			h.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", "original")));
			var stage = await h.Settings.PrepareImportAsync("synthetic");
			var result = await h.Runtime.CommitPreparedImportAsync(stage, true, TestContext.Current.CancellationToken);
			Assert.False(result.Succeeded);
			Assert.Equal(1, h.Operations.CreateCalls);
			Assert.Equal(0, h.Operations.WallpaperCalls);
		}
		[Fact]
		public async Task MissingNamedImageLogsFailureWithoutNumberFallback()
		{
			var h = await Harness.Initialized();
			h.Settings.WallpapersOnCreation = new[]
			{
				new DesktopWallpaperOnCreation("Work", null, Path.Combine(this._directory, "absent.bmp")),
				new DesktopWallpaperOnCreation(null, 2, Image("fallback.bmp")),
			};
			var faults = 0;
			h.Runtime.Faulted += (_, __) => faults++;
			h.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "name", "wall"), Entry(B, 1, "Work", "original")));
			Assert.Equal(1, faults);
			Assert.Equal(0, h.Operations.WallpaperCalls);
			Assert.Equal("original", h.Runtime.State.Records[B].WallpaperPath.Value);
		}

		private readonly string _directory = Path.Combine(Path.GetTempPath(), "SylphyHorn-wallpaper-" + Guid.NewGuid().ToString("N"));

		private string Image(string name)
		{
			Directory.CreateDirectory(this._directory);
			var path = Path.Combine(this._directory, name);
			if (!File.Exists(path))
			{
				// One opaque pixel, generated as a standard BMP without depending on an image encoder.
				var bytes = new byte[58];
				bytes[0] = 66; bytes[1] = 77; bytes[2] = 58; bytes[10] = 54; bytes[14] = 40;
				bytes[18] = 1; bytes[22] = 1; bytes[26] = 1; bytes[28] = 24; bytes[34] = 4;
				File.WriteAllBytes(path, bytes);
			}
			return path;
		}

		public void Dispose()
		{
			if (Directory.Exists(this._directory)) Directory.Delete(this._directory, true);
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task NumberWorksWithoutNameSupportAndHonorsLegacyWallpaperSetting(bool enabled)
		{
			var h = await Harness.Initialized();
			h.Settings.PerDesktopWallpaperEnabled = enabled;
			h.Settings.WallpapersOnCreation = new[] { new DesktopWallpaperOnCreation(null, 2, Image("legacy.bmp")) };
			var entry = new WindowsDesktop.VirtualDesktopStableEntry(B, 1, null, WindowsDesktop.VirtualDesktopReadStatus.Unsupported,
				null, WindowsDesktop.VirtualDesktopReadStatus.Unsupported);
			h.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "name", "wall"), entry));
			Assert.Equal(0, h.Operations.WallpaperCalls);
			Assert.Equal(enabled ? Image("legacy.bmp") : null, h.Runtime.State.Records[B].WallpaperPath.Value);
		}

#if !NETFRAMEWORK
		[Fact]
		public async Task CliUpsertRemoveAndResolveUseTheSharedSetting()
		{
			var provider = new TestDictionaryProvider();
			await provider.InitializeAsync();
			var settings = new GeneralSettings(provider);
			var service = new SylphyHorn.Services.Commands.CliCreationWallpaperService(settings, () => provider.SaveWithResultAsync(), () => true);
			var args = new[] { "desktop", "creation", "wallpaper", "set", "--name", "Work", "--path", Image("cli.bmp") };
			var first = await service.ExecuteAsync(CliCommand.Parse(args), TestContext.Current.CancellationToken);
			Assert.True(first.Success, first.Error?.Message);
			Assert.True(first.Data.Changed);
			Assert.Equal(Image("cli.bmp"), Assert.Single(settings.DesktopWallpapersOnCreation.Value).WallpaperPath);
			var second = await service.ExecuteAsync(CliCommand.Parse(args), TestContext.Current.CancellationToken);
			Assert.True(second.Success, second.Error?.Message);
			Assert.False(second.Data.Changed);
			var spec = await CliSpecService.ExecuteAsync(new[] { "spec", "desktop", "creation", "wallpaper", "set", "--resolve" },
				query => query[1] == "list" ? Task.FromResult(CliResponse.Ok("desktop list", new CliData())) : service.ExecuteAsync(CliCommand.Parse(query), TestContext.Current.CancellationToken));
			Assert.Equal("complete", spec.Data.Resolution.Status);
			var removed = await service.ExecuteAsync(CliCommand.Parse(new[] { "desktop", "creation", "wallpaper", "remove", "--name", "Work" }), TestContext.Current.CancellationToken);
			Assert.True(removed.Success);
			Assert.Empty(settings.DesktopWallpapersOnCreation.Value);
		}
#endif

		[Fact]
		public async Task XmlRoundTripPreservesMissingImageAndRejectsDuplicateTargets()
		{
			Directory.CreateDirectory(this._directory);
			var file = Path.Combine(this._directory, "settings.xml");
			var writer = new FileDictionaryProvider(file);
			await writer.InitializeAsync();
			var settings = new GeneralSettings(writer);
			Assert.Empty(settings.DesktopWallpapersOnCreation.Value);
			var entry = new DesktopWallpaperOnCreation("Work", null, Path.Combine(this._directory, "absent.bmp"));
			settings.DesktopWallpapersOnCreation.Value = new[] { entry };
			await writer.SaveAsync();
			var reader = new FileDictionaryProvider(file);
			await reader.InitializeAsync();
			Assert.Equal(entry.WallpaperPath, Assert.Single(new GeneralSettings(reader).DesktopWallpapersOnCreation.Value).WallpaperPath);
			var export = Path.Combine(this._directory, "export.xml");
			await reader.ExportAsync(export);
			var reset = await reader.PrepareResetAsync();
			var resetResult = await reader.CommitStagedImportAsync(reset, reset.CreateCommitDictionary());
			Assert.True(resetResult.Succeeded);
			Assert.Empty(new GeneralSettings(reader).DesktopWallpapersOnCreation.Value);
			var import = await reader.PrepareImportAsync(export);
			var importResult = await reader.CommitStagedImportAsync(import, import.CreateCommitDictionary());
			Assert.True(importResult.Succeeded);
			Assert.Equal(entry.WallpaperPath, Assert.Single(new GeneralSettings(reader).DesktopWallpapersOnCreation.Value).WallpaperPath);
			Assert.Throws<System.Runtime.Serialization.SerializationException>(() => writer.SetValue(DesktopWallpaperOnCreation.SettingsKey, new[] { entry, entry }));
		}
	}
}
