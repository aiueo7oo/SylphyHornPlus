#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.Services.Commands;
using Xunit;

using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class CliSettingsResetTests
	{
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task ResetUsesRuntimeTransactionAndPreservesDesktops(bool saveFails)
		{
			var harness = await Harness.Initialized();
			var settings = new GeneralSettings(harness.Settings.Provider);
			settings.LoopDesktop.Value = true;
			harness.Runtime.EditWallpaperPosition(A, WallpaperPosition.Tile);
			if (saveFails)
			{
				harness.Settings.Provider.SaveFailure = new IOException("synthetic");
			}
			var suspended = false;
			var refreshed = false;
			var service = new CliSettingsResetService(harness.Runtime.ResetSettingsAsync, () => true,
				() =>
				{
					suspended = true;
					return new Cleanup(() => suspended = false);
				}, () => refreshed = true);

			var response = await service.ExecuteAsync(Command(), TestContext.Current.CancellationToken);

			Assert.Equal(!saveFails, response.Success);
			Assert.Equal(saveFails, settings.LoopDesktop.Value);
			Assert.Equal(!saveFails, refreshed);
			Assert.False(suspended);
			Assert.False(harness.Settings.Provider.ImportTransactionActive);
			Assert.Equal(new[] { A }, harness.Runtime.State.Order);
			Assert.Equal("name", harness.Runtime.State.Records[A].Name.Value);
			Assert.Equal("wall", harness.Runtime.State.Records[A].WallpaperPath.Value);
			Assert.Equal(saveFails ? WallpaperPosition.Tile : WallpaperPosition.Fill,
				harness.Runtime.State.Records[A].WallpaperPosition);
			Assert.Equal(0, harness.Operations.CreateCalls);
			Assert.Equal(0, harness.Operations.NameCalls);
			Assert.Empty(harness.Operations.RemovedIds);
			if (saveFails)
			{
				Assert.Equal("result_unconfirmed", response.Error.Code);
			}
			else
			{
				Assert.True(response.Data.Reset);
			}
		}

		[Fact]
		public async Task MissingConfirmationBusyHostAndEarlyCancellationDoNotStartReset()
		{
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "settings", "reset" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "settings", "reset", "--yes", "--path", "file.xml" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "settings", "get", "--yes" }));
			var service = new CliSettingsResetService(_ => throw new Exception("Reset must not run."), () => false,
				() => throw new Exception("Input must not be suspended."), () => throw new Exception("No refresh."));
			var busy = await service.ExecuteAsync(Command(), CancellationToken.None);
			Assert.Equal("host_busy", busy.Error.Code);
			var cancelled = await service.ExecuteAsync(Command(), new CancellationToken(true));
			Assert.Equal("request_cancelled", cancelled.Error.Code);
		}

		[Theory]
		[InlineData(SettingsImportCommitStatus.Conflict, "state_changed")]
		[InlineData(SettingsImportCommitStatus.CompletedWithFailures, "partial_failure")]
		[InlineData(SettingsImportCommitStatus.FailedWithoutStableState, "result_unconfirmed")]
		public async Task IncompleteResetIsNotSuccessAndReleasesInput(SettingsImportCommitStatus status, string error)
		{
			var released = false;
			var service = new CliSettingsResetService(_ => Task.FromResult(new SettingsImportCommitResult(status, null)),
				() => true, () => new Cleanup(() => released = true), () => throw new Exception("No refresh."));
			var response = await service.ExecuteAsync(Command(), CancellationToken.None);
			Assert.False(response.Success);
			Assert.Equal(error, response.Error.Code);
			Assert.True(released);
		}

		[Fact]
		public async Task CancellationAfterSubmissionIsUnconfirmedAndReleasesInput()
		{
			var released = false;
			var service = new CliSettingsResetService(_ => throw new OperationCanceledException(),
				() => true, () => new Cleanup(() => released = true), () => throw new Exception("No refresh."));
			var response = await service.ExecuteAsync(Command(), CancellationToken.None);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.True(released);
		}

		private static CliCommand Command() => CliCommand.Parse(new[] { "settings", "reset", "--yes" });

		private sealed class Cleanup : IDisposable
		{
			private readonly Action _action;

			internal Cleanup(Action action) => this._action = action;

			public void Dispose() => this._action();
		}
	}
}
#endif
