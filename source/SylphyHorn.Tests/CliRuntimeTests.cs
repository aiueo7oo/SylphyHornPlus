#if !NETFRAMEWORK
using System;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	public sealed class CliRuntimeTests
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
