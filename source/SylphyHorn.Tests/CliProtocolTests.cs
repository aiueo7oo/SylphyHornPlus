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
		public void SuccessIncludesCommandAndFalseChangedWithoutError()
		{
			var json = Encoding.UTF8.GetString(CliProtocol.Serialize(CliResponse.Ok("desktop switch", new CliData { Changed = false })));
			Assert.Contains("\"success\":true", json);
			Assert.Contains("\"command\":\"desktop switch\"", json);
			Assert.Contains("\"changed\":false", json);
			Assert.DoesNotContain("\"error\"", json);
			Assert.DoesNotContain("\"ok\"", json);
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
