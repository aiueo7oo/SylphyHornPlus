#if !NETFRAMEWORK
using System;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliSettingsResetService
	{
		private readonly Func<CancellationToken, Task<SettingsImportCommitResult>> _reset;
		private readonly Func<bool> _available;
		private readonly Func<IDisposable> _suspendInput;
		private readonly Action _refresh;

		internal CliSettingsResetService(Func<CancellationToken, Task<SettingsImportCommitResult>> reset,
			Func<bool> available, Func<IDisposable> suspendInput, Action refresh)
		{
			this._reset = reset;
			this._available = available;
			this._suspendInput = suspendInput;
			this._refresh = refresh;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			if (command.Operation != "settings reset" || !command.ConfirmReset)
			{
				return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify settings reset --yes.");
			}

			var submitted = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (!this._available())
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Settings are changing or input is being edited.", true);
				}

				using (this._suspendInput())
				{
					cancellation.ThrowIfCancellationRequested();
					submitted = true;
					var result = await this._reset(cancellation);
					if (!result.Succeeded)
					{
						return CliResponse.Fail(command.Operation, CliSettingsFileService.CommitFailureCode(result.Status),
							"Settings reset did not complete successfully. Inspect settings before retrying.");
					}
					this._refresh();
					return CliResponse.Ok(command.Operation, new CliData { Reset = true });
				}
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled",
					"Settings reset was cancelled. Inspect settings before retrying if the result is unconfirmed.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "operation_failed",
					"Settings reset could not be confirmed. Inspect settings before retrying.");
			}
		}
	}
}
#endif
