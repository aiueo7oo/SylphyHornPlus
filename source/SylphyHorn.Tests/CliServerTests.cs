#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Services.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliServerTests
	{
		[Fact]
		public async Task InvalidFrameAndDisconnectedClientDoNotStopNextRequest()
		{
			var name = "SylphyHorn.Tests." + Guid.NewGuid();
			var calls = 0;
			var server = new CliServer(name, (command, token) =>
			{
				Interlocked.Increment(ref calls);
				return Task.FromResult(CliResponse.Ok(command.Operation, new CliData { Desktops = Array.Empty<CliDesktop>() }));
			}, () => throw new InvalidOperationException("Unexpected shutdown."));
			using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
			{
				try
				{
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await client.WriteAsync(new byte[4], deadline.Token);
						var invalid = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
						Assert.Equal("invalid_arguments", invalid.Error.Code);
					}
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await CliProtocol.WriteAsync(client, new CliRequest { Args = new[] { "exit", "--force" } }, deadline.Token);
						var invalid = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
						Assert.Equal("invalid_arguments", invalid.Error.Code);
					}
					using (var client = Client(name)) await client.ConnectAsync(deadline.Token);
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await CliProtocol.WriteAsync(client, new CliRequest { Args = new[] { "desktop", "list" } }, deadline.Token);
						var response = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
						Assert.True(response.Success);
						Assert.Equal("desktop list", response.Command);
						Assert.Equal(1, calls);
					}
				}
				finally { await server.StopAsync().WaitAsync(deadline.Token); }
			}
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task AcceptedExitStopsServerEvenIfClientDisconnects(bool disconnect)
		{
			var name = "SylphyHorn.Tests." + Guid.NewGuid();
			var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var server = new CliServer(name, async (command, token) =>
			{
				accepted.SetResult(true);
				await release.Task;
				return CliResponse.Ok(command.Operation, new CliData { Accepted = true });
			}, () => shutdown.SetResult(true));
			using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
			{
				try
				{
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await CliProtocol.WriteAsync(client, new CliRequest { Args = new[] { "exit" } }, deadline.Token);
						await accepted.Task.WaitAsync(deadline.Token);
						Assert.False(shutdown.Task.IsCompleted);
						if (disconnect)
						{
							client.Dispose();
						}
						release.SetResult(true);
						if (!disconnect)
						{
							var response = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
							Assert.Equal("exit", response.Command);
							Assert.True(response.Success);
							Assert.True(response.Data.Accepted);
						}
						await shutdown.Task.WaitAsync(deadline.Token);
					}
				}
				finally
				{
					release.TrySetResult(true);
					await server.StopAsync().WaitAsync(deadline.Token);
				}
			}
		}

		[Fact]
		public async Task LongestNotificationConfigureReachesHostAndOversizedRequestKeepsCommand()
		{
			var name = "SylphyHorn.Tests." + Guid.NewGuid();
			CliCommand received = null;
			var server = new CliServer(name, (command, token) =>
			{
				received = command;
				return Task.FromResult(CliResponse.Ok(command.Operation, new CliData { Changed = true }));
			}, () => throw new InvalidOperationException("Unexpected shutdown."));
			var args = new List<string> { "notification", "configure" };
			foreach (var argument in CliSpecCatalog.Find("notification configure").Arguments)
			{
				args.Add(argument.Name);
				args.Add(argument.Values?[0] ?? (argument.Type == "integer" ? "10" : argument.Name == "--monitor" ? "current" : "Segoe UI"));
			}
			Assert.Equal(48, args.Count);
			Assert.True(args.Count <= CliSpecCatalog.MaximumArgumentCount);
			using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
			{
				try
				{
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await CliProtocol.WriteAsync(client, new CliRequest { Args = args.ToArray() }, deadline.Token);
						var response = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
						Assert.True(response.Success);
						Assert.Equal("notification configure", response.Command);
						Assert.Equal(10, received.DurationMs);
						Assert.Equal("current", received.Monitor);
					}
					received = null;
					var oversized = args.Concat(Enumerable.Repeat("--duration-ms", CliSpecCatalog.MaximumArgumentCount)).ToArray();
					using (var client = Client(name))
					{
						await client.ConnectAsync(deadline.Token);
						await CliProtocol.WriteAsync(client, new CliRequest { Args = oversized }, deadline.Token);
						var response = await CliProtocol.ReadAsync<CliResponse>(client, deadline.Token);
						Assert.False(response.Success);
						Assert.Equal("notification configure", response.Command);
						Assert.Equal("invalid_arguments", response.Error.Code);
						Assert.Null(received);
					}
				}
				finally { await server.StopAsync().WaitAsync(deadline.Token); }
			}
		}

		private static NamedPipeClientStream Client(string name)
			=> new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
	}
}
#endif
