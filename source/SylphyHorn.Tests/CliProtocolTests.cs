using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliProtocolTests
	{
		[Fact]
		public void LogsPreservesEmptyResultsAndMultilineContents()
		{
			var data = new CliData { Logs = Array.Empty<CliLog>(), TotalCount = 0, OmittedCount = 0 };
			var empty = CliProtocol.Deserialize<CliResponse>(CliProtocol.Serialize(CliResponse.Ok("logs", data)));
			Assert.Empty(empty.Data.Logs);
			Assert.Equal(0, empty.Data.TotalCount);
			Assert.Equal(0, empty.Data.OmittedCount);

			data.Logs = new[]
			{
				new CliLog { Timestamp = "2026-09-27T12:00:00.0000000+09:00", Header = "エラー", Content = "first\nsecond" },
			};
			data.TotalCount = 4;
			data.OmittedCount = 3;
			var result = CliProtocol.Deserialize<CliResponse>(CliProtocol.Serialize(CliResponse.Ok("logs", data)));
			Assert.Equal("logs", result.Command);
			Assert.Equal(data.Logs[0].Timestamp, result.Data.Logs[0].Timestamp);
			Assert.Equal("エラー", result.Data.Logs[0].Header);
			Assert.Equal("first\nsecond", result.Data.Logs[0].Content);
			Assert.Equal(4, result.Data.TotalCount);
			Assert.Equal(3, result.Data.OmittedCount);
		}

		[Fact]
		public void AssignmentStatusPreservesDisabledSettingsAndBothClosingTargetKinds()
		{
			var response = CliResponse.Ok("app assignment status", new CliData
			{
				AssignmentStatus = "preparing",
				AssignmentEnabled = true,
				CreateMissingDesktops = false,
				CloseCreatedDesktops = false,
				ClosingTargets = new[]
				{
					new CliAssignmentTarget { DesktopName = "work" },
					new CliAssignmentTarget { DesktopNumber = 3 },
				},
			});
			var result = CliProtocol.Deserialize<CliResponse>(CliProtocol.Serialize(response));
			Assert.Equal("preparing", result.Data.AssignmentStatus);
			Assert.True(result.Data.AssignmentEnabled);
			Assert.False(result.Data.CreateMissingDesktops);
			Assert.False(result.Data.CloseCreatedDesktops);
			Assert.Equal("work", result.Data.ClosingTargets[0].DesktopName);
			Assert.Equal(3, result.Data.ClosingTargets[1].DesktopNumber);
			Assert.Null(result.Data.Assignments);
		}

		[Fact]
		public void PartialAssignmentResultsKeepConfirmedMovesInsideTheErrorEnvelope()
		{
			var response = CliResponse.Fail("app assignment apply", "result_unconfirmed", "Inspect results.");
			response.Error.Results = new[]
			{
				new CliAssignmentResult { WindowId = "first", Outcome = "moved" },
				new CliAssignmentResult { WindowId = "second", Outcome = "unconfirmed" },
			};
			var roundTrip = CliProtocol.Deserialize<CliResponse>(CliProtocol.Serialize(response));
			Assert.False(roundTrip.Success);
			Assert.Null(roundTrip.Data);
			Assert.Equal(5, roundTrip.ExitCode);
			Assert.Equal("moved", roundTrip.Error.Results[0].Outcome);
			Assert.Equal("second", roundTrip.Error.Results[1].WindowId);
			Assert.False(roundTrip.Error.Retryable);
		}

		[Fact]
		public void SuccessIncludesCommandAndFalseChangedWithoutError()
		{
			var json = Encoding.UTF8.GetString(CliProtocol.Serialize(CliResponse.Ok("desktop switch", new CliData { Changed = false })));
			Assert.Contains("\"success\":true", json);
			Assert.Contains("\"command\":\"desktop switch\"", json);
			Assert.Contains("\"changed\":false", json);
			Assert.DoesNotContain("\"error\"", json);
			Assert.DoesNotContain("\"ok\"", json);
		}

		[Fact]
		public void UiSuccessIncludesAnEmptyDataObject()
		{
			var bytes = CliProtocol.Serialize(CliResponse.Ok("ui settings", new CliData()));
			var response = CliProtocol.Deserialize<CliResponse>(bytes);
			Assert.True(response.Success);
			Assert.Equal("ui settings", response.Command);
			Assert.NotNull(response.Data);
			Assert.Contains("\"data\":{}", Encoding.UTF8.GetString(bytes));
		}

		[Fact]
		public void WindowPinScopesRemainDistinctInJson()
		{
			var window = new CliWindow { Pinned = true, WindowPinned = false, AppPinned = true };
			var bytes = CliProtocol.Serialize(CliResponse.Ok("window list", new CliData { Windows = new[] { window } }));
			var response = CliProtocol.Deserialize<CliResponse>(bytes);
			Assert.False(response.Data.Windows[0].WindowPinned);
			Assert.True(response.Data.Windows[0].AppPinned);
			Assert.True(response.Data.Windows[0].Pinned);
		}

		[Fact]
		public void DesktopWallpaperStateRoundTripsInJson()
		{
			var desktop = new CliDesktop
			{
				WallpaperPath = "C:\\Images\\wall.jpg",
				WallpaperPathAvailable = true,
				WallpaperPathConfirmed = false,
				WallpaperPosition = "fit"
			};
			var bytes = CliProtocol.Serialize(CliResponse.Ok("desktop list", new CliData { Desktops = new[] { desktop } }));
			var response = CliProtocol.Deserialize<CliResponse>(bytes);
			Assert.Equal(desktop.WallpaperPath, response.Data.Desktops[0].WallpaperPath);
			Assert.True(response.Data.Desktops[0].WallpaperPathAvailable);
			Assert.False(response.Data.Desktops[0].WallpaperPathConfirmed);
			Assert.Equal("fit", response.Data.Desktops[0].WallpaperPosition);
		}

		[Theory]
		[InlineData("invalid_arguments", 2)]
		[InlineData("host_unavailable", 3)]
		[InlineData("desktop_not_found", 4)]
		[InlineData("result_unconfirmed", 5)]
		public void FailureRoundTripPreservesCodeAndNullCommand(string code, int exitCode)
		{
			var bytes = CliProtocol.Serialize(CliResponse.Fail(null, code, "説明"));
			var response = CliProtocol.Deserialize<CliResponse>(bytes);
			Assert.False(response.Success);
			Assert.Null(response.Command);
			Assert.Null(response.Data);
			Assert.Equal("説明", response.Error.Message);
			Assert.Equal(exitCode, response.ExitCode);
			Assert.Contains("\"command\":null", Encoding.UTF8.GetString(bytes));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		[InlineData(1048577)]
		public async Task InvalidFrameLengthIsRejectedBeforeReadingPayload(int length)
		{
			using (var stream = new MemoryStream(BitConverter.GetBytes(length)))
				await Assert.ThrowsAsync<InvalidDataException>(() => CliProtocol.ReadAsync<CliRequest>(stream, CancellationToken.None));
		}

		[Fact]
		public async Task TruncatedFrameDoesNotBecomeAnEmptyRequest()
		{
			using (var stream = new MemoryStream(new byte[] { 5, 0, 0, 0, 123 }))
				await Assert.ThrowsAsync<EndOfStreamException>(() => CliProtocol.ReadAsync<CliRequest>(stream, CancellationToken.None));
		}

		[Fact]
		public async Task FragmentedFramesRoundTripUnicodeArguments()
		{
			using (var stream = new FragmentedStream())
			{
				var args = new[] { "desktop", "switch", "--name", "開発 作業" };
				await CliProtocol.WriteAsync(stream, new CliRequest { Args = args }, CancellationToken.None);
				stream.Position = 0;
				var request = await CliProtocol.ReadAsync<CliRequest>(stream, CancellationToken.None);
				Assert.Equal(args, request.Args);
			}
		}

		private sealed class FragmentedStream : MemoryStream
		{
			public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
				=> base.ReadAsync(buffer, offset, Math.Min(count, 1), token);
		}
	}
}
