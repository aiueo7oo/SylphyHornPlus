#if !NETFRAMEWORK
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliServer
	{
		private readonly string _name;
		private readonly Func<CliCommand, CancellationToken, Task<CliResponse>> _execute;
		private readonly CancellationTokenSource _stop = new CancellationTokenSource();
		private readonly Task _completion;

		internal CliServer(string name, Func<CliCommand, CancellationToken, Task<CliResponse>> execute)
		{
			this._name = name;
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
						using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token))
						{
							deadline.CancelAfter(TimeSpan.FromSeconds(10));
							string operation = null;
							CliResponse response;
							try
							{
								var request = await CliProtocol.ReadAsync<CliRequest>(pipe, deadline.Token).ConfigureAwait(false);
								if (request == null || request.SchemaVersion != 1 || request.Args == null || request.Args.Length > 32)
									throw new ArgumentException("Unsupported request version or arguments.");
								operation = CliCommand.Recognize(request.Args);
								var command = CliCommand.Parse(request.Args);
								if (command.Operation == "app assignment apply" || command.Operation == "settings import"
									|| command.Operation == "settings reset"
									|| command.Operation.StartsWith("startup ", StringComparison.Ordinal)) deadline.CancelAfter(TimeSpan.FromSeconds(40));
								response = await this._execute(command, deadline.Token).ConfigureAwait(false);
							}
							catch (ArgumentException ex)
							{
								response = CliResponse.Fail(operation, "invalid_arguments", ex.Message);
							}
							catch (System.Runtime.Serialization.SerializationException)
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
							try
							{
								using (var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token))
								{
									writeDeadline.CancelAfter(TimeSpan.FromSeconds(1));
									if (CliProtocol.Serialize(response).Length > CliProtocol.MaximumFrameBytes)
										response = CliResponse.Fail(operation, "response_too_large", "The result exceeds the protocol size limit.");
									await CliProtocol.WriteAsync(pipe, response, writeDeadline.Token).ConfigureAwait(false);
								}
							}
							catch (IOException) { }
							catch (OperationCanceledException) { }
						}
					}
					if (!this._stop.IsCancellationRequested) pipe = this.CreatePipe();
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
	}
}
#endif
