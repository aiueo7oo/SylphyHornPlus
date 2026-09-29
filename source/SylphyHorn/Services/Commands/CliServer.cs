#if !NETFRAMEWORK
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliServer
	{
		private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
		private static readonly TimeSpan LongRunningRequestTimeout = TimeSpan.FromSeconds(40);
		private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(1);

		private readonly string _name;
		private readonly Action _shutdown;
		private readonly Func<CliCommand, CancellationToken, Task<CliResponse>> _execute;
		private readonly CancellationTokenSource _stop = new CancellationTokenSource();
		private readonly Task _completion;

		internal CliServer(string name, Func<CliCommand, CancellationToken, Task<CliResponse>> execute, Action shutdown)
		{
			this._name = name;
			this._shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
			this._execute = execute;
			var first = this.CreatePipe();
			this._completion = Task.Run(() => this.RunAsync(first));
		}

		private NamedPipeServerStream CreatePipe() => new NamedPipeServerStream(this._name, PipeDirection.InOut, 1,
			PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

		internal async Task StopAsync()
		{
			this._stop.Cancel();
			await this._completion.ConfigureAwait(false);
		}

		private async Task RunAsync(NamedPipeServerStream first)
		{
			var pipe = first;
			try
			{
				while (!this._stop.IsCancellationRequested)
				{
					using (pipe)
					{
						await pipe.WaitForConnectionAsync(this._stop.Token).ConfigureAwait(false);
						var response = await this.HandleRequestAsync(pipe).ConfigureAwait(false);
						if (IsAcceptedExit(response))
						{
							this._shutdown();
							return;
						}
					}
					if (!this._stop.IsCancellationRequested)
					{
						pipe = this.CreatePipe();
					}
				}
			}
			catch (OperationCanceledException) when (this._stop.IsCancellationRequested) { }
			catch (Exception ex)
			{
				LoggingService.Instance.Register(ex);
			}
			finally
			{
				pipe.Dispose();
			}
		}

		private async Task<CliResponse> HandleRequestAsync(NamedPipeServerStream pipe)
		{
			using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token))
			{
				deadline.CancelAfter(RequestTimeout);
				string operation = null;
				CliResponse response;
				try
				{
					var request = await CliProtocol.ReadAsync<CliRequest>(pipe, deadline.Token).ConfigureAwait(false);
					operation = request?.Args == null ? null : CliCommand.Recognize(request.Args);
					if (request == null || request.SchemaVersion != 1 || request.Args == null
						|| request.Args.Length > CliSpecCatalog.MaximumArgumentCount)
					{
						throw new ArgumentException("Unsupported request version or arguments.");
					}
					var command = CliCommand.Parse(request.Args);
					if (CliProtocol.IsLongRunning(command.Operation))
					{
						deadline.CancelAfter(LongRunningRequestTimeout);
					}
					response = await this._execute(command, deadline.Token).ConfigureAwait(false);
				}
				catch (ArgumentException ex)
				{
					response = CliResponse.Fail(operation, "invalid_arguments", ex.Message);
				}
				catch (SerializationException)
				{
					response = CliResponse.Fail(operation, "invalid_arguments", "Invalid request JSON.");
				}
				catch (OperationCanceledException)
				{
					response = CliResponse.Fail(operation, "result_unconfirmed", "The request expired. Query current state before retrying.");
				}
				catch (InvalidDataException)
				{
					response = CliResponse.Fail(operation, "invalid_arguments", "Invalid request frame.");
				}
				catch (IOException)
				{
					response = CliResponse.Fail(operation, "invalid_arguments", "The request frame is incomplete or invalid.");
				}
				catch (Exception ex)
				{
					LoggingService.Instance.Register(ex);
					response = CliResponse.Fail(operation, "result_unconfirmed", "The request could not be confirmed. Query current state before retrying.");
				}
				return await this.WriteResponseAsync(pipe, operation, response).ConfigureAwait(false);
			}
		}

		/// <summary>Writes the response, or a size-limit failure in its place, and returns what was sent.</summary>
		private async Task<CliResponse> WriteResponseAsync(NamedPipeServerStream pipe, string operation, CliResponse response)
		{
			try
			{
				using (var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token))
				{
					writeDeadline.CancelAfter(ResponseWriteTimeout);
					if (CliProtocol.Serialize(response).Length > CliProtocol.MaximumFrameBytes)
					{
						response = CliResponse.Fail(operation, "response_too_large", "The result exceeds the protocol size limit.");
					}
					await CliProtocol.WriteAsync(pipe, response, writeDeadline.Token).ConfigureAwait(false);
				}
			}
			catch (IOException) { }
			catch (OperationCanceledException) { }
			return response;
		}

		private static bool IsAcceptedExit(CliResponse response)
			=> response.Command == "exit" && response.Success && response.Data?.Accepted == true;
	}
}
#endif
