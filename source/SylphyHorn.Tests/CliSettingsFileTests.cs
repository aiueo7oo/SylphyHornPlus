#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;
using SylphyHorn.Services.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliSettingsFileTests
	{
		[Fact]
		public async Task ExportPreservesExistingFilesUnlessOverwriteIsExplicitAndRejectsLiveFile()
		{
			using (var files = new Files())
			{
				var provider = new FileProvider(files.Live);
				await provider.LoadAsync();
				provider.SetValue("test", true);
				var service = Create(provider, files.Live);
				var path = Path.Combine(files.Directory, "backup.xml");
				Assert.True((await Run(service, "settings", "export", "--path", path)).Success);
				var original = File.ReadAllBytes(path);
				provider.SetValue("test", false);
				Assert.Equal("file_exists", (await Run(service, "settings", "export", "--path", path)).Error.Code);
				Assert.Equal(original, File.ReadAllBytes(path));
				Assert.True((await Run(service, "settings", "export", "--path", path, "--overwrite")).Success);
				var dictionary = await AtomicSettingsFile.ReadAsync(new FileInfo(path), provider.KnownTypes);
				Assert.Equal(false, dictionary["test"]);
				Assert.Equal("invalid_arguments", (await Run(service, "settings", "export", "--path", files.Live, "--overwrite")).Error.Code);
				Assert.Single(System.IO.Directory.GetFiles(files.Directory));
			}
		}

		[Fact]
		public async Task ImportValidatesSourceReplacesSettingsAndReleasesInputAndStage()
		{
			using (var files = new Files())
			{
				var provider = new FileProvider(files.Live);
				await provider.LoadAsync();
				provider.SetValue("old", true);
				var path = Path.Combine(files.Directory, "input.xml");
				await AtomicSettingsFile.WriteAsync(new Dictionary<string, object> { ["new"] = true }, new FileInfo(path), provider.KnownTypes);
				var suspensions = 0;
				var resumes = 0;
				var refreshes = 0;
				var service = new CliSettingsFileService(provider, files.Live, () => true,
					() => { suspensions++; return new Cleanup(() => resumes++); },
					(stage, apply, token) =>
					{
						Assert.False(apply);
						return provider.CommitStagedImportAsync(stage, new Dictionary<string, object>(stage.Settings));
					}, () => refreshes++, true);
				Assert.True((await Run(service, "settings", "import", "--path", path, "--apply-desktops", "false")).Success);
				Assert.False(provider.TryGetValue<bool>("old", out _));
				Assert.True(provider.TryGetValue<bool>("new", out var value) && value);
				Assert.Equal(1, refreshes);
				Assert.Equal(suspensions, resumes);
				Assert.False(provider.ImportTransactionActive);
				File.WriteAllText(path, "not XML");
				Assert.Equal("invalid_settings_file", (await Run(service, "settings", "import", "--path", path, "--apply-desktops", "false")).Error.Code);
				Assert.False(provider.ImportTransactionActive);
				Assert.Equal(suspensions, resumes);
				Assert.True(provider.TryGetValue<bool>("new", out _));
				File.Delete(path);
				Assert.Equal("file_not_found", (await Run(service, "settings", "import", "--path", path, "--apply-desktops", "false")).Error.Code);
			}
		}

		[Fact]
		public async Task PartialImportIsNotSuccessAndCancellationLeavesNoPreparedStage()
		{
			using (var files = new Files())
			{
				var provider = new FileProvider(files.Live);
				await provider.LoadAsync();
				var path = Path.Combine(files.Directory, "input.xml");
				await provider.ExportAsync(path);
				var service = new CliSettingsFileService(provider, files.Live, () => true, () => new Cleanup(() => { }),
					(stage, apply, token) => Task.FromResult(SettingsImportCommitResult.CompletedWithFailures()), () => { }, true);
				var partial = await Run(service, "settings", "import", "--path", path, "--apply-desktops", "true");
				Assert.Equal("partial_failure", partial.Error.Code);
				Assert.Equal("CompletedWithFailures", partial.Error.ImportStatus);
				Assert.False(provider.ImportTransactionActive);
				var command = CliCommand.Parse(new[] { "settings", "import", "--path", path, "--apply-desktops", "false" });
				Assert.Equal("request_cancelled", (await service.ExecuteAsync(command, new CancellationToken(true))).Error.Code);
				Assert.False(provider.ImportTransactionActive);
			}
		}

		[Theory]
		[InlineData("settings import --path input.xml")]
		[InlineData("settings export --path output.xml --apply-desktops false")]
		[InlineData("startup configure --mode other")]
		[InlineData("startup status --mode normal")]
		public void IncompleteOrMisplacedArgumentsAreRejected(string args)
		{
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(args.Split(' ')));
		}

		private static CliSettingsFileService Create(DictionaryProvider provider, string live)
			=> new CliSettingsFileService(provider, live, () => true, () => new Cleanup(() => { }),
				(stage, apply, token) => provider.CommitStagedImportAsync(stage, new Dictionary<string, object>(stage.Settings)), () => { }, true);

		private static Task<CliResponse> Run(CliSettingsFileService service, params string[] args)
			=> service.ExecuteAsync(CliCommand.Parse(args), CancellationToken.None);

		private sealed class Cleanup : IDisposable
		{
			private readonly Action _action;

			internal Cleanup(Action action) => this._action = action;
			public void Dispose() => this._action();
		}

		private sealed class Files : IDisposable
		{
			internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "SylphyHorn-CliFiles-" + Guid.NewGuid().ToString("N"));
			internal string Live => Path.Combine(this.Directory, "live.xml");
			internal Files() => System.IO.Directory.CreateDirectory(this.Directory);
			public void Dispose() => System.IO.Directory.Delete(this.Directory, true);
		}

		private sealed class FileProvider : DictionaryProvider
		{
			private readonly string _path;

			internal FileProvider(string path) => this._path = path;
			protected override Task SaveAsyncCore(IDictionary<string, object> values)
				=> AtomicSettingsFile.WriteAsync(values, new FileInfo(this._path), this.KnownTypes);
			protected override Task SaveAsyncCore(IDictionary<string, object> values, string path)
				=> AtomicSettingsFile.WriteAsync(values, new FileInfo(path), this.KnownTypes);
			protected override Task<IDictionary<string, object>> LoadAsyncCore()
				=> Task.FromResult<IDictionary<string, object>>(new Dictionary<string, object>());
			protected override Task<IDictionary<string, object>> LoadAsyncCore(string path)
				=> AtomicSettingsFile.ReadAsync(new FileInfo(path), this.KnownTypes);
		}
	}
}
#endif
