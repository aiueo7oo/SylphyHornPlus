#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MetroTrilithon.Desktop;
using SylphyHorn.Commands;

namespace SylphyHorn.Services.Commands
{
	internal interface ICliStartupRegistration
	{
		CliStartup Read();
		void CreateNormal();
		void RemoveNormal();
		Task SetElevatedAsync(bool enabled, CancellationToken cancellation);
	}

	internal sealed class CliStartupService
	{
		private readonly ICliStartupRegistration _registration;
		private readonly Func<bool> _available;
		private readonly Action<CliStartup> _refresh;

		internal CliStartupService(ICliStartupRegistration registration, Func<bool> available, Action<CliStartup> refresh)
		{
			this._registration = registration;
			this._available = available;
			this._refresh = refresh;
		}

		internal async Task<CliResponse> ExecuteAsync(CliCommand command, CancellationToken cancellation)
		{
			var submitted = false;
			try
			{
				cancellation.ThrowIfCancellationRequested();
				var before = this._registration.Read();
				if (command.Operation == "startup status")
				{
					return CliResponse.Ok(command.Operation, new CliData { Startup = before });
				}
				if (!this._available())
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Settings are being changed.", true);
				}
				if (!before.TargetMatches)
				{
					return CliResponse.Fail(command.Operation, "startup_target_mismatch", "An existing registration points elsewhere. No registration was changed.");
				}
				var normal = command.StartupMode == "normal";
				var elevated = command.StartupMode == "elevated";
				if (before.ElevatedRegistered != elevated && !before.Administrator)
				{
					return CliResponse.Fail(command.Operation, "elevation_required", "Run both SylphyHorn and the CLI as administrator. No UAC prompt was opened.");
				}

				var changed = before.NormalRegistered != normal || before.ElevatedRegistered != elevated;
				if (changed)
				{
					// Establish the new registration before removing the old one.
					if (normal && !before.NormalRegistered)
					{
						submitted = true;
						this._registration.CreateNormal();
						var created = this._registration.Read();
						if (!created.NormalRegistered || !created.TargetMatches)
						{
							throw new InvalidOperationException("Normal registration was not confirmed.");
						}
					}
					if (before.ElevatedRegistered != elevated)
					{
						cancellation.ThrowIfCancellationRequested();
						submitted = true;
						await this._registration.SetElevatedAsync(elevated, cancellation);
						var taskState = this._registration.Read();
						if (taskState.ElevatedRegistered != elevated || !taskState.TargetMatches)
						{
							throw new InvalidOperationException("Task registration was not confirmed.");
						}
					}
					if (!normal && before.NormalRegistered)
					{
						cancellation.ThrowIfCancellationRequested();
						submitted = true;
						this._registration.RemoveNormal();
					}
				}
				var after = this._registration.Read();
				if (!after.TargetMatches || after.NormalRegistered != normal || after.ElevatedRegistered != elevated)
				{
					throw new InvalidOperationException("Startup registration was not confirmed.");
				}
				if (changed)
				{
					this._refresh(after);
				}
				return CliResponse.Ok(command.Operation, new CliData { Startup = after, Changed = changed });
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled",
					"Startup configuration was cancelled. Query startup status before retrying.");
			}
			catch (Exception)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "startup_query_failed",
					"Startup registration could not be confirmed. Failure does not mean it is unregistered.");
			}
		}
	}

	internal sealed class WindowsStartupRegistration : ICliStartupRegistration
	{
		private readonly string _appPath;
		private readonly string _shortcutPath;
		private readonly string _taskName;

		internal WindowsStartupRegistration(string appPath, string startupDirectory = null)
		{
			this._appPath = Path.GetFullPath(appPath);
			var name = Path.GetFileNameWithoutExtension(appPath);
			this._shortcutPath = Path.Combine(startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), name + ".lnk");
			this._taskName = name + " Startup";
		}

		public CliStartup Read()
		{
			var objects = new List<object>();
			try
			{
				var result = new CliStartup
				{
					TargetMatches = true,
					Administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
				};
				try
				{
					File.GetAttributes(this._shortcutPath);
					dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
					objects.Add(shell);
					dynamic link = shell.CreateShortcut(this._shortcutPath);
					objects.Add(link);
					result.NormalRegistered = true;
					result.NormalTarget = (string)link.TargetPath;
					result.TargetMatches &= this.Matches(result.NormalTarget) && string.IsNullOrEmpty((string)link.Arguments);
				}
				catch (FileNotFoundException) { }
				catch (DirectoryNotFoundException) { }

				dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
				objects.Add(scheduler);
				scheduler.Connect();
				dynamic folder = scheduler.GetFolder(@"\");
				objects.Add(folder);
				dynamic task = null;
				try { task = folder.GetTask(this._taskName); }
				catch (Exception ex) when ((ex is COMException || ex is FileNotFoundException)
					&& ex.HResult == unchecked((int)0x80070002)) { }
				if (task != null)
				{
					objects.Add(task);
					dynamic definition = task.Definition;
					objects.Add(definition);
					dynamic actions = definition.Actions;
					objects.Add(actions);
					dynamic principal = definition.Principal;
					objects.Add(principal);
					result.ElevatedRegistered = true;
					if ((int)actions.Count != 1 || (int)principal.RunLevel != 1 || !(bool)task.Enabled)
					{
						throw new InvalidDataException("The startup task has an unexpected definition.");
					}
					dynamic action = actions.Item(1);
					objects.Add(action);
					if ((int)action.Type != 0)
					{
						throw new InvalidDataException("The startup task does not execute an application.");
					}
					result.ElevatedTarget = (string)action.Path;
					result.TargetMatches &= this.Matches(result.ElevatedTarget) && string.IsNullOrEmpty((string)action.Arguments);
				}
				result.Mode = result.NormalRegistered ? result.ElevatedRegistered ? "mixed" : "normal"
					: result.ElevatedRegistered ? "elevated" : "disabled";
				return result;
			}
			finally
			{
				for (var index = objects.Count - 1; index >= 0; index--)
					if (Marshal.IsComObject(objects[index]))
					{
						Marshal.FinalReleaseComObject(objects[index]);
					}
			}
		}

		private bool Matches(string path)
			=> !string.IsNullOrWhiteSpace(path) && string.Equals(
				Path.GetFullPath(path.Trim('"')), this._appPath, StringComparison.OrdinalIgnoreCase);

		public void CreateNormal() => ShellLink.Create(this._shortcutPath, this._appPath);
		public void RemoveNormal() => File.Delete(this._shortcutPath);

		public async Task SetElevatedAsync(bool enabled, CancellationToken cancellation)
		{
			var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(this._appPath), "SchedulerManager.exe"))
			{
				UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
			};
			info.ArgumentList.Add(enabled ? "register" : "unregister");
			info.ArgumentList.Add(this._appPath);
			using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
			using (var process = Process.Start(info))
			{
				deadline.CancelAfter(TimeSpan.FromSeconds(20));
				try { await process.WaitForExitAsync(deadline.Token); }
				catch (OperationCanceledException)
				{
					if (!process.HasExited)
					{
						process.Kill();
					}
					throw;
				}
				if (process.ExitCode != 0)
				{
					throw new InvalidOperationException("Startup task registration failed.");
				}
			}
		}
	}
}
#endif
