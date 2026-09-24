#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using SylphyHorn.Commands;
using SylphyHorn.Serialization;

namespace SylphyHorn.Services.Commands
{
	internal sealed class CliSettingsFileService
	{
		private readonly DictionaryProvider _provider;
		private readonly string _settingsPath;
		private readonly Func<bool> _available;
		private readonly Func<IDisposable> _suspendInput;
		private readonly Func<StagedSettingsImport, bool, CancellationToken, Task<SettingsImportCommitResult>> _commit;
		private readonly Action _refresh;
		private readonly bool _nameSupported;

		internal CliSettingsFileService(DictionaryProvider provider, string settingsPath, Func<bool> available,
			Func<IDisposable> suspendInput,
			Func<StagedSettingsImport, bool, CancellationToken, Task<SettingsImportCommitResult>> commit,
			Action refresh, bool nameSupported)
		{
			this._provider = provider;
			this._settingsPath = settingsPath;
			this._available = available;
			this._suspendInput = suspendInput;
			this._commit = commit;
			this._refresh = refresh;
			this._nameSupported = nameSupported;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var submitted = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				if (!this._available()) return CliResponse.Fail(command.Operation, "host_busy", "Settings are changing or input is being edited.", true);
				if (!System.IO.Path.IsPathFullyQualified(command.FilePath))
					return CliResponse.Fail(command.Operation, "invalid_arguments", "The host requires an absolute file path.");
				var path = System.IO.Path.GetFullPath(command.FilePath);
				if (string.Equals(path, System.IO.Path.GetFullPath(this._settingsPath), StringComparison.OrdinalIgnoreCase))
					return CliResponse.Fail(command.Operation, "invalid_arguments", "Use a separate settings backup file.");

				if (command.Operation == "settings export")
				{
					if (!command.Overwrite && File.Exists(path))
						return CliResponse.Fail(command.Operation, "file_exists", "Use --overwrite to replace an existing backup.");
					var directory = System.IO.Path.GetDirectoryName(path);
					Directory.CreateDirectory(directory);
					var temporary = System.IO.Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".xml");
					try
					{
						await this._provider.ExportAsync(temporary);
						cancellation.ThrowIfCancellationRequested();
						File.Move(temporary, path, command.Overwrite);
					}
					finally
					{
						if (File.Exists(temporary)) File.Delete(temporary);
					}
					return CliResponse.Ok(command.Operation, new CliData { Path = path });
				}
				if (command.Operation != "settings import")
					return CliResponse.Fail(command.Operation, "invalid_arguments", "Unknown settings file command.");
				if (command.ApplyDesktops == true && !this._nameSupported)
					return CliResponse.Fail(command.Operation, "unsupported", "Applying a saved desktop layout is unavailable on this Windows build.");

				// Keep the source present and immutable while the existing provider reads it.
				using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
				using (this._suspendInput())
				{
					var stage = await this._provider.PrepareImportAsync(path);
					try
					{
						cancellation.ThrowIfCancellationRequested();
						submitted = true;
						var result = await this._commit(stage, command.ApplyDesktops.Value, cancellation);
						if (!result.Succeeded)
						{
							var code = result.Status == SettingsImportCommitStatus.Conflict ? "state_changed"
								: result.Status == SettingsImportCommitStatus.CompletedWithFailures ? "partial_failure"
								: "result_unconfirmed";
							var failure = CliResponse.Fail(command.Operation, code,
								"Import did not complete successfully. Inspect settings and desktops before retrying.");
							failure.Error.ImportStatus = result.Status.ToString();
							return failure;
						}
						this._refresh();
						return CliResponse.Ok(command.Operation, new CliData { Path = path, ApplyDesktops = command.ApplyDesktops });
					}
					finally
					{
						this._provider.DiscardStagedImport(stage);
					}
				}
			}
			catch (FileNotFoundException) when (!submitted)
			{
				return CliResponse.Fail(command.Operation, "file_not_found", "The settings file does not exist.");
			}
			catch (Exception ex) when (!submitted && (ex is SerializationException || ex is XmlException || ex is ArgumentException))
			{
				return CliResponse.Fail(command.Operation, "invalid_settings_file", "The settings file or path is invalid.");
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled", "The settings operation was cancelled.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "operation_failed",
					"The settings file operation could not be confirmed.");
			}
		}
	}
}
#endif
