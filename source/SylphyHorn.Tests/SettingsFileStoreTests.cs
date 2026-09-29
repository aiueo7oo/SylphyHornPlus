using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class SettingsFileStoreTests
	{
		private static readonly Type[] KnownTypes = { typeof(bool), typeof(int[]), };

		[Fact]
		public async Task MissingFileReturnsNullDictionaryAndHash()
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "missing", "Settings.xml"));
				Assert.Null(await AtomicSettingsFile.ReadAsync(file, KnownTypes));
				Assert.Null(await AtomicSettingsFile.HashAsync(file));
			});
		}

		[Fact]
		public async Task WriteCreatesDirectoryAndPreservesUnknownKeysTypesNullAndArrays()
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "nested", "Settings.xml"));
				var values = new Dictionary<string, object>
				{
					["Future.Unknown"] = "future-value",
					["Boolean"] = true,
					["Integers"] = new[] { 1, 2, 3 },
					["Null"] = null,
				};

				await AtomicSettingsFile.WriteAsync(values, file, KnownTypes);
				var loaded = await AtomicSettingsFile.ReadAsync(file, KnownTypes);

				Assert.Equal(values.Count, loaded.Count);
				Assert.Equal("future-value", Assert.IsType<string>(loaded["Future.Unknown"]));
				Assert.True(Assert.IsType<bool>(loaded["Boolean"]));
				Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(loaded["Integers"]));
				Assert.Null(loaded["Null"]);
				Assert.Empty(Directory.GetFiles(file.DirectoryName, "*.tmp"));
			});
		}

		[Theory]
		[InlineData("")]
		[InlineData("not xml")]
		[InlineData("<root>")]
		public async Task CorruptFileIsRejectedWithoutChangingItsContents(string contents)
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "Settings.xml"));
				File.WriteAllText(file.FullName, contents, new UTF8Encoding(false));
				var before = File.ReadAllBytes(file.FullName);

				await Assert.ThrowsAnyAsync<Exception>(() => AtomicSettingsFile.ReadAsync(file, KnownTypes));

				Assert.Equal(before, File.ReadAllBytes(file.FullName));
			});
		}

		[Fact]
		public async Task HashMatchesFileBytesAndChangesAfterAtomicWrite()
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "Settings.xml"));
				await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["Value"] = "first" }, file, KnownTypes);
				var first = await AtomicSettingsFile.HashAsync(file);
				Assert.Equal(ComputeHash(file.FullName), first);

				await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["Value"] = "second" }, file, KnownTypes);
				var second = await AtomicSettingsFile.HashAsync(file);

				Assert.Equal(ComputeHash(file.FullName), second);
				Assert.NotEqual(first, second);
				Assert.Matches("^[0-9a-f]{64}$", second);
			});
		}

		[Fact]
		public async Task FailedLoadPreservesActiveDictionaryAndLoadedState()
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "Settings.xml"));
				await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["Value"] = "known-good" }, file, KnownTypes);
				var provider = new ExplicitPathSettingsProvider(file);
				await provider.LoadAsync();
				Assert.True(provider.IsLoaded);
				Assert.True(provider.TryGetValue<string>("Value", out var initial));
				Assert.Equal("known-good", initial);

				File.WriteAllText(file.FullName, "corrupt", new UTF8Encoding(false));
				await Assert.ThrowsAnyAsync<Exception>(() => provider.LoadAsync());

				Assert.True(provider.IsLoaded);
				Assert.True(provider.TryGetValue<string>("Value", out var preserved));
				Assert.Equal("known-good", preserved);
			});
		}

		[Fact]
		public async Task FailedImportPreparationPreservesStateAndReleasesTransaction()
		{
			await WithTemporaryDirectory(async root =>
			{
				var active = new FileInfo(Path.Combine(root, "active.xml"));
				var corrupt = Path.Combine(root, "corrupt.xml");
				var valid = new FileInfo(Path.Combine(root, "valid.xml"));
				await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["Value"] = "active" }, active, KnownTypes);
				await AtomicSettingsFile.WriteAsync(
					new Dictionary<string, object> { ["Value"] = "imported", ["Future.Unknown"] = 42 },
					valid,
					KnownTypes);
				File.WriteAllText(corrupt, "corrupt", new UTF8Encoding(false));
				var provider = new ExplicitPathSettingsProvider(active);
				await provider.LoadAsync();

				await Assert.ThrowsAnyAsync<Exception>(() => provider.PrepareImportAsync(corrupt));
				Assert.False(provider.ImportTransactionActive);
				Assert.True(provider.TryGetValue<string>("Value", out var preserved));
				Assert.Equal("active", preserved);

				var stage = await provider.PrepareImportAsync(valid.FullName);
				var result = await provider.CommitStagedImportAsync(stage, stage.CreateCommitDictionary());

				Assert.True(result.Succeeded);
				Assert.False(provider.ImportTransactionActive);
				Assert.True(provider.TryGetValue<string>("Value", out var imported));
				Assert.Equal("imported", imported);
				Assert.True(provider.TryGetValue<int>("Future.Unknown", out var unknown));
				Assert.Equal(42, unknown);
			});
		}

		[Theory]
		[InlineData(false, false)]
		[InlineData(false, true)]
		[InlineData(true, false)]
		[InlineData(true, true)]
		public async Task StructuredSettingsSurviveOldReaderSaveAndCurrentImport(bool empty, bool typedInput)
		{
			await WithTemporaryDirectory(async root =>
			{
				var file = new FileInfo(Path.Combine(root, "Settings.xml"));
				var configuration = empty ? AppPlacementConfiguration.Empty : new AppPlacementConfiguration(true,
					new[] { new AppPlacementRule(Guid.NewGuid(), true,
						new PlacementAppIdentity(PlacementAppKind.ExecutablePath, @"C:\Apps\Editor.exe"),
						PlacementDestination.ByName("Work"), "Editor", followForeground: false) }, true, true,
					new[] { PlacementDestination.ByNumber(3) }, false);
				var groups = empty
					? Array.Empty<PlacementCreatedGroup>()
					: new[] { new PlacementCreatedGroup(new[] { Guid.NewGuid() }, true) };
				var wallpapers = empty ? Array.Empty<DesktopWallpaperOnCreation>() : new[]
				{
					new DesktopWallpaperOnCreation("Work", null, @"C:\Images\work.png"),
					new DesktopWallpaperOnCreation(null, 2, @"C:\Images\other.png"),
				};
				var values = new Dictionary<string, object>
				{
					[AppPlacementSettings.ConfigurationKey] = configuration,
					[AppPlacementSettings.CreatedDesktopGroupsKey] = groups,
					[GeneralSettings.DesktopWallpapersOnCreationKey] = wallpapers,
					["GeneralSettings.ChangeBackgroundEachDesktop"] = true,
					["Future.Unknown"] = "preserved",
				};
				if (typedInput)
				{
					using (var stream = File.Create(file.FullName))
						new DataContractSerializer(typeof(IDictionary<string, object>), new[]
						{
							typeof(bool), typeof(int[]), typeof(AppPlacementConfiguration),
							typeof(PlacementCreatedGroup[]), typeof(DesktopWallpaperOnCreation[]),
						}).WriteObject(stream, values);
					var typedProvider = new ExplicitPathSettingsProvider(file);
					await typedProvider.LoadAsync();
					await typedProvider.SaveAsync();
				}
				else
				{
					await AtomicSettingsFile.WriteAsync(values, file, KnownTypes);
				}

				// beta.16 knows only these primitive types and reads the entire dictionary at once.
				var oldSerializer = new DataContractSerializer(typeof(IDictionary<string, object>), KnownTypes);
				IDictionary<string, object> oldValues;
				using (var stream = File.OpenRead(file.FullName))
					oldValues = (IDictionary<string, object>)oldSerializer.ReadObject(stream);
				Assert.IsType<string>(oldValues[AppPlacementSettings.ConfigurationKey]);
				Assert.IsType<string>(oldValues[AppPlacementSettings.CreatedDesktopGroupsKey]);
				Assert.IsType<string>(oldValues[GeneralSettings.DesktopWallpapersOnCreationKey]);
				Assert.True(Assert.IsType<bool>(oldValues["GeneralSettings.ChangeBackgroundEachDesktop"]));
				oldValues["GeneralSettings.ChangeBackgroundEachDesktop"] = false;
				using (var stream = File.Create(file.FullName)) oldSerializer.WriteObject(stream, oldValues);

				var provider = new ExplicitPathSettingsProvider(new FileInfo(Path.Combine(root, "imported.xml")));
				await provider.LoadAsync();
				await provider.ImportAsync(file.FullName);
				Assert.True(provider.TryGetValue<AppPlacementConfiguration>(AppPlacementSettings.ConfigurationKey, out var restored));
				Assert.Equal(SerializeValue(configuration), SerializeValue(restored));
				Assert.True(provider.TryGetValue<PlacementCreatedGroup[]>(AppPlacementSettings.CreatedDesktopGroupsKey, out var restoredGroups));
				Assert.Equal(SerializeValue(groups), SerializeValue(restoredGroups));
				Assert.True(provider.TryGetValue<DesktopWallpaperOnCreation[]>(GeneralSettings.DesktopWallpapersOnCreationKey, out var restoredWallpapers));
				Assert.Equal(SerializeValue(wallpapers), SerializeValue(restoredWallpapers));
				Assert.True(provider.TryGetValue<bool>("GeneralSettings.ChangeBackgroundEachDesktop", out var changed));
				Assert.False(changed);
				var export = Path.Combine(root, "export.xml");
				await provider.ExportAsync(export);
				using (var stream = File.OpenRead(export))
				{
					var exported = (IDictionary<string, object>)oldSerializer.ReadObject(stream);
					Assert.Equal(oldValues, exported);
				}
				Assert.Same(configuration, values[AppPlacementSettings.ConfigurationKey]);
			});
		}

		[Theory]
		[InlineData("not xml")]
		[InlineData("<wrong />")]
		[InlineData("<!DOCTYPE x [<!ENTITY value 'x'>]><x>&value;</x>")]
		public async Task InvalidStructuredStringCannotReplaceActiveSettings(string payload)
		{
			await WithTemporaryDirectory(async root =>
			{
				var active = new FileInfo(Path.Combine(root, "active.xml"));
				var provider = new ExplicitPathSettingsProvider(active);
				await provider.LoadAsync();
				provider.SetValue(AppPlacementSettings.ConfigurationKey, AppPlacementConfiguration.Empty);
				await provider.SaveAsync();
				var before = File.ReadAllBytes(active.FullName);
				var invalid = Path.Combine(root, "invalid.xml");
				using (var stream = File.Create(invalid))
					new DataContractSerializer(typeof(IDictionary<string, object>), KnownTypes).WriteObject(stream,
						new Dictionary<string, object> { [AppPlacementSettings.ConfigurationKey] = payload });
				await Assert.ThrowsAnyAsync<Exception>(() => provider.PrepareImportAsync(invalid));
				Assert.False(provider.ImportTransactionActive);
				Assert.Equal(before, File.ReadAllBytes(active.FullName));
				Assert.True(provider.TryGetValue<AppPlacementConfiguration>(AppPlacementSettings.ConfigurationKey, out var preserved));
				Assert.False(preserved.Enabled);
				var valid = await provider.PrepareImportAsync(active.FullName);
				provider.DiscardStagedImport(valid);
			});
		}

		private static string SerializeValue<T>(T value)
		{
			using (var stream = new MemoryStream())
			{
				new DataContractSerializer(typeof(T)).WriteObject(stream, value);
				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		private static string ComputeHash(string path)
		{
			using (var stream = File.OpenRead(path))
			using (var sha = SHA256.Create())
				return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
		}

		private static async Task WithTemporaryDirectory(Func<string, Task> action)
		{
			var root = Path.Combine(Path.GetTempPath(), "SylphyHornPlus-SettingsFileStore-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try { await action(root); }
			finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
		}

		private sealed class ExplicitPathSettingsProvider : DictionaryProvider
		{
			private readonly FileInfo _file;

			internal ExplicitPathSettingsProvider(FileInfo file)
			{
				this._file = file ?? throw new ArgumentNullException(nameof(file));
			}

			protected override Task SaveAsyncCore(IDictionary<string, object> dic)
				=> AtomicSettingsFile.WriteAsync(dic, this._file, this.KnownTypes);

			protected override Task SaveAsyncCore(IDictionary<string, object> dic, string path)
				=> AtomicSettingsFile.WriteAsync(dic, new FileInfo(path), this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore()
				=> AtomicSettingsFile.ReadAsync(this._file, this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore(string path)
				=> AtomicSettingsFile.ReadAsync(new FileInfo(path), this.KnownTypes);

			protected override Task<string> GetContentHashAsyncCore()
				=> AtomicSettingsFile.HashAsync(this._file);
		}
	}
}
