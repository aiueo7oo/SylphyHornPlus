#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class CliRuntimeTests : IDisposable
	{
		[Theory]
		[InlineData("--name", "work")]
		[InlineData("--number", "1")]
		[InlineData("--id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
		public async Task CurrentDestinationIsSuccessfulWithoutSwitching(string option, string value)
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", option, value }), CancellationToken.None);
			Assert.True(response.Success);
			Assert.False(response.Data.Changed);
			Assert.Equal(A.ToString(), response.Data.Desktop.Id);
			Assert.Empty(harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task PreviousAtFirstDesktopDoesNotWrapImplicitly()
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", "--previous" }), CancellationToken.None);
			Assert.Equal("no_previous_desktop", response.Error.Code);
			Assert.Empty(harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task ExplicitWrapConfirmsActualDestination()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, B, Entry(A, 0, "work", ""), Entry(B, 1, "work", "")));
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", "--previous", "--wrap" }), CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Changed);
			Assert.Equal(B.ToString(), response.Data.Desktop.Id);
			Assert.Equal(new[] { B }, harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task MissingDesktopDoesNotCreateOne()
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", "--number", "3" }), CancellationToken.None);
			Assert.Equal("desktop_not_found", response.Error.Code);
			Assert.Equal(0, harness.Operations.CreateCalls);
		}

		[Fact]
		public async Task UnconfirmedSwitchIsNotReportedAsFailureSafeToRetry()
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", "--next" }), CancellationToken.None);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
			Assert.Equal(new[] { B }, harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task CancelledRequestNeverQueuesASwitch()
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "switch", "--next" }), new CancellationToken(true));
			harness.Owner.Drain();
			Assert.Equal("request_cancelled", response.Error.Code);
			Assert.Empty(harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task CreateReturnsConfirmedDesktop()
		{
			var harness = await Create();
			harness.Operations.Creating = () => C;
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "work", ""), Entry(B, 1, "work", ""), Entry(C, 2, "new", "")));
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "create", "--name", "new" }), CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Changed);
			Assert.Equal(C.ToString(), response.Data.Desktop.Id);
			Assert.Equal(3, response.Data.Desktop.Number);
			Assert.Equal(new[] { "new" }, harness.Operations.NameValues);
		}

		[Fact]
		public async Task CreateAndSwitchWaitsForTheNewDesktopToBecomeCurrent()
		{
			var harness = await Create();
			harness.Operations.Creating = () => C;
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "work", ""), Entry(B, 1, "work", ""), Entry(C, 2, "", "")));
			harness.Provider.EnqueueResult(Batch(1, 4, C, Entry(A, 0, "work", ""), Entry(B, 1, "work", ""), Entry(C, 2, "", "")));
			var command = CliCommand.Parse(new[] { "desktop", "create", "--switch" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Desktop.Current);
			Assert.Equal(C.ToString(), response.Data.Desktop.Id);
			Assert.Equal(new[] { C }, harness.Operations.DesktopOperationIds);
		}

		[Fact]
		public async Task RenameWaitsForConfirmedName()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "renamed", ""), Entry(B, 1, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "rename", "--id", A.ToString(), "--name", "renamed" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal("renamed", response.Data.Desktop.Name);
			Assert.Equal(new[] { "renamed" }, harness.Operations.NameValues);
		}

		[Fact]
		public async Task EmptyNameCanBeConfirmed()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "", ""), Entry(B, 1, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "rename", "--id", A.ToString(), "--name", "" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal(string.Empty, response.Data.Desktop.Name);
			Assert.Equal(new[] { string.Empty }, harness.Operations.NameValues);
		}

		[Fact]
		public async Task ReorderWaitsForConfirmedPosition()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(B, 0, "work", ""), Entry(A, 1, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "reorder", "--id", A.ToString(), "--number", "2" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal(2, response.Data.Desktop.Number);
			Assert.Equal(new[] { "MoveRight" }, harness.Operations.DesktopOperationNames);
		}

		[Fact]
		public async Task DeleteWaitsForConfirmedRemovalAndReturnsRemainingDesktops()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, B, Entry(B, 0, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--id", A.ToString() });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Changed);
			Assert.Equal(B.ToString(), Assert.Single(response.Data.Desktops).Id);
			Assert.Equal(new[] { A }, harness.Operations.RemovedIds);
			Assert.Null(Assert.Single(harness.Operations.RemovalFallbackIds));
		}

		[Theory]
		[InlineData("--fallback-id", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
		[InlineData("--fallback-number", "2")]
		public async Task DeletePassesResolvedFallbackAndConfirmsCurrentDestination(string option, string value)
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, B, Entry(B, 0, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--number", "1", option, value });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal(new[] { A }, harness.Operations.RemovedIds);
			Assert.Equal(B, Assert.Single(harness.Operations.RemovalFallbackIds));
			Assert.True(Assert.Single(response.Data.Desktops).Current);
		}

		[Theory]
		[InlineData("--fallback-id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "invalid_arguments")]
		[InlineData("--fallback-number", "1", "invalid_arguments")]
		[InlineData("--fallback-id", "cccccccc-cccc-cccc-cccc-cccccccccccc", "desktop_not_found")]
		[InlineData("--fallback-number", "3", "desktop_not_found")]
		public async Task InvalidFallbackDoesNotRemoveAnyDesktop(string option, string value, string error)
		{
			var harness = await Create();
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--number", "1", option, value });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.False(response.Success);
			Assert.Equal(error, response.Error.Code);
			Assert.Empty(harness.Operations.RemovedIds);
		}

		[Fact]
		public async Task DeleteDoesNotConfirmSuccessIfDisplayDidNotReachFallback()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, C, Entry(B, 0, "work", ""), Entry(C, 1, "other", "")));
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--number", "1", "--fallback-number", "2" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.False(response.Success);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
			Assert.Equal(B, Assert.Single(harness.Operations.RemovalFallbackIds));
		}

		[Fact]
		public async Task DeleteByNumberResolvesCurrentDesktopOrder()
		{
			var harness = await Create();
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--number", "2" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal(new[] { B }, harness.Operations.RemovedIds);
			Assert.Equal(A.ToString(), Assert.Single(response.Data.Desktops).Id);
		}

		[Fact]
		public async Task WallpaperPathWaitsForProviderConfirmation()
		{
			var harness = await Create();
			var image = this.CreateImage();
			harness.Provider.EnqueueResult(Batch(1, 3, A, Entry(A, 0, "work", image), Entry(B, 1, "work", "")));
			var command = CliCommand.Parse(new[] { "desktop", "wallpaper", "--number", "1", "--path", image });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Changed);
			Assert.Equal(image, response.Data.Desktop.WallpaperPath);
			Assert.True(response.Data.Desktop.WallpaperPathConfirmed);
			Assert.Equal(1, harness.Operations.WallpaperCalls);
		}

		[Theory]
		[InlineData("relative")]
		[InlineData("missing")]
		[InlineData("unreadable")]
		[InlineData("empty")]
		public async Task InvalidWallpaperDoesNotMutateDesktopOrSettings(string kind)
		{
			var harness = await Create();
			if (kind == "unreadable")
			{
				File.WriteAllText(this._imagePath, "not an image");
			}
			var path = kind == "relative" ? "wallpaper.bmp" : kind == "empty" ? "" : this._imagePath;
			var before = harness.Runtime.State.Records[A].WallpaperPath.Value;
			var response = await harness.Runtime.ExecuteCliAsync(
				CliCommand.Parse(new[] { "desktop", "wallpaper", "--number", "1", "--path", path }), CancellationToken.None);
			Assert.Equal("invalid_arguments", response.Error.Code);
			Assert.Equal(0, harness.Operations.WallpaperCalls);
			Assert.Equal(before, harness.Runtime.State.Records[A].WallpaperPath.Value);
		}

		[Fact]
		public async Task LegacyWallpaperCanStillBeCleared()
		{
			var batch = Batch(1, 1, A, WallpaperUnsupported(A, 0, "work"));
			var harness = Harness.Create(batch);
			await harness.Runtime.InitializeAsync(false, CancellationToken.None);
			harness.Provider.EnqueueResult(Batch(1, 2, A, WallpaperUnsupported(A, 0, "work")));
			var response = await harness.Runtime.ExecuteCliAsync(
				CliCommand.Parse(new[] { "desktop", "wallpaper", "--number", "1", "--path", "" }), CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal("", response.Data.Desktop.WallpaperPath);
		}

		[Fact]
		public async Task WallpaperPositionUsesExistingLocalEdit()
		{
			var harness = await Create();
			var command = CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", A.ToString(), "--position", "fit" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.True(response.Data.Changed);
			Assert.Equal("fit", response.Data.Desktop.WallpaperPosition);
			Assert.Equal(new[] { A }, harness.Operations.AppliedWallpaperIds);
		}

		[Fact]
		public async Task FailedWallpaperWriteIsNotReportedAsSuccess()
		{
			var harness = await Create();
			var image = this.CreateImage();
			harness.Operations.FailWallpaperValue = image;
			var command = CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", A.ToString(), "--path", image });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.False(response.Success);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
		}

		[Fact]
		public async Task UnconfirmedDeleteCannotBeBlindlyRetried()
		{
			var harness = await Create();
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--id", A.ToString() });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
			Assert.Equal(new[] { A }, harness.Operations.RemovedIds);
		}

		[Fact]
		public async Task DeletingTheLastDesktopReturnsItsReplacement()
		{
			var harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "work", "")));
			await harness.Runtime.InitializeAsync(false, CancellationToken.None);
			harness.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "work", "")));
			harness.Provider.EnqueueResult(Batch(1, 3, B, Entry(B, 0, "", "")));
			var command = CliCommand.Parse(new[] { "desktop", "delete", "--id", A.ToString() });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.True(response.Success);
			Assert.Equal(B.ToString(), Assert.Single(response.Data.Desktops).Id);
			Assert.True(response.Data.Desktops[0].Current);
		}

		[Fact]
		public async Task UnconfirmedCreateCannotBeBlindlyRetried()
		{
			var harness = await Create();
			var response = await harness.Runtime.ExecuteCliAsync(CliCommand.Parse(new[] { "desktop", "create" }), CancellationToken.None);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
			Assert.Equal(1, harness.Operations.CreateCalls);
		}

		[Fact]
		public async Task UnconfirmedReorderStopsBeforeAnotherMove()
		{
			var harness = await Create();
			var command = CliCommand.Parse(new[] { "desktop", "reorder", "--id", A.ToString(), "--number", "2" });
			var response = await harness.Runtime.ExecuteCliAsync(command, CancellationToken.None);
			Assert.Equal("result_unconfirmed", response.Error.Code);
			Assert.False(response.Error.Retryable);
			Assert.Equal(new[] { "MoveRight" }, harness.Operations.DesktopOperationNames);
		}

		private readonly string _imagePath = Path.Combine(Path.GetTempPath(), "SylphyHorn-cli-wallpaper-" + Guid.NewGuid().ToString("N") + ".bmp");

		private string CreateImage()
		{
			var bytes = new byte[58];
			bytes[0] = 66; bytes[1] = 77; bytes[2] = 58; bytes[10] = 54; bytes[14] = 40;
			bytes[18] = 1; bytes[22] = 1; bytes[26] = 1; bytes[28] = 24; bytes[34] = 4;
			File.WriteAllBytes(this._imagePath, bytes);
			return this._imagePath;
		}

		public void Dispose() => File.Delete(this._imagePath);

		private static async Task<Harness> Create()
		{
			var batch = Batch(1, 1, A, Entry(A, 0, "work", ""), Entry(B, 1, "work", ""));
			var harness = Harness.Create(batch);
			await harness.Runtime.InitializeAsync(false, CancellationToken.None);
			harness.Provider.EnqueueResult(Batch(1, 2, A, Entry(A, 0, "work", ""), Entry(B, 1, "work", "")));
			return harness;
		}
	}
}
#endif
