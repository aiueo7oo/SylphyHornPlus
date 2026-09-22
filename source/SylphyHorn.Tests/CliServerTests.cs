#if !NETFRAMEWORK
using System;
using System.IO.Pipes;
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
			});
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

		private static NamedPipeClientStream Client(string name)
			=> new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
	}
}
#endif
