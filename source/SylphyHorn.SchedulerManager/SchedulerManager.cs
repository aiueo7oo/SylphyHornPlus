using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.TaskScheduler;

namespace SylphyHorn
{
	class SchedulerManager
	{
		static void Main(string[] args)
		{
			if (args.Length < 1)
			{
				Environment.Exit(-1);
			}

			var command = args[0];
			var process = args.Length > 1 ? new SchedulerProcess(appPath: args[1]) : new SchedulerProcess();
			switch (command)
			{
				case "register":
					Environment.Exit(process.Register());
					break;
				case "unregister":
					Environment.Exit(process.Unregister());
					break;
				case "start":
					Environment.Exit(process.Start());
					break;
				case "stop":
					Environment.Exit(process.Stop());
					break;
				case "restart":
					Environment.Exit(process.Restart());
					break;
				case "hastask":
					Environment.Exit(Convert.ToInt32(process.HasTask));
					break;
				case "isrunning":
					Environment.Exit(Convert.ToInt32(process.IsRunning));
					break;
			}
		}
	}

	class SchedulerProcess
	{
		private const string _defaultAppName = "SylphyHorn";
		private const string _defaultAppExtension = ".exe";
		private readonly string _taskName;
		private readonly string _appPath;
		private readonly WindowsPrincipal _principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());

		public bool HasTask
		{
			get
			{
				using (var task = this.FindStartupTask())
				{
					return task != null;
				}
			}
		}

		public bool IsRunning
		{
			get
			{
				using (var task = this.FindStartupTask())
				{
					return task?.State == TaskState.Running;
				}
			}
		}

		public bool IsAdministrator => _principal.IsInRole(WindowsBuiltInRole.Administrator);

		public SchedulerProcess()
			: this(GetDefaultApplicationPath())
		{
		}

		public SchedulerProcess(string appPath)
		{
			if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
			{
				throw new ArgumentNullException($"Error: app path ({appPath}) is invalid.");
			}

			this._appPath = appPath;
			this._taskName = Path.GetFileNameWithoutExtension(appPath) + " Startup";
		}

		public int Register()
		{
			if (!this.IsAdministrator)
			{
				return -1;
			}

			try
			{
				using (var taskService = new TaskService())
				using (var taskDefinition = this.CreateTaskDefinition(taskService))
				{
					taskService.RootFolder.RegisterTaskDefinition(
						this._taskName,
						taskDefinition,
						TaskCreation.CreateOrUpdate,
						null,
						null,
						TaskLogonType.InteractiveToken);
				}
				return 0;
			}
			catch (UnauthorizedAccessException)
			{
				return -1;
			}
			catch (Exception e)
			{
				return e.HResult;
			}
		}

		public int Unregister()
		{
			if (!this.IsAdministrator)
			{
				return -1;
			}
			if (!this.HasTask)
			{
				return 0;
			}

			try
			{
				using (var taskService = new TaskService())
				{
					taskService.RootFolder.DeleteTask(this._taskName);
				}
				return 0;
			}
			catch (UnauthorizedAccessException)
			{
				return -1;
			}
			catch (Exception e)
			{
				return e.HResult;
			}
		}

		public int Start()
		{
			using (var task = this.FindStartupTask())
			{
				if (task == null)
				{
					return -1;
				}
				task.Run();
				return 0;
			}
		}

		public int Stop()
		{
			using (var task = this.FindStartupTask())
			{
				if (task == null)
				{
					return -1;
				}
				task.Stop();
				return 0;
			}
		}

		public int Restart()
		{
			using (var task = this.FindStartupTask())
			{
				if (task == null || task.State == TaskState.Disabled)
				{
					return -1;
				}

				var appPath = task.Definition.Actions
					.OfType<ExecAction>()
					.FirstOrDefault()?.Path ?? this._appPath;
				appPath = Regex.Replace(appPath, @"^""(.+)""$", "$1");
				if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
				{
					throw new FileNotFoundException($"Error: app path ({appPath}) is invalid.");
				}
				if (task.State == TaskState.Running)
				{
					task.Stop();
				}

				var appName = Path.GetFileNameWithoutExtension(appPath);
				var totalTime = 0;
				const int timeout = 30000;
				const int interval = 100;
				while (Process.GetProcessesByName(appName).Length > 0)
				{
					Thread.Sleep(interval);
					totalTime += interval;
					if (totalTime >= timeout)
					{
						return -1;
					}
				}

				task.Run();
				return 0;
			}
		}

		private TaskDefinition CreateTaskDefinition(TaskService taskService)
		{
			var taskDefinition = taskService.NewTask();
			taskDefinition.RegistrationInfo.Author = "";
			taskDefinition.RegistrationInfo.Description = "";
			taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;
			taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
			taskDefinition.Settings.DisallowStartIfOnBatteries = false;
			taskDefinition.Settings.StopIfGoingOnBatteries = false;
			taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
			taskDefinition.Settings.Compatibility = TaskCompatibility.V2;
			taskDefinition.Settings.Hidden = false;
			taskDefinition.Settings.Priority = ProcessPriorityClass.Normal;
			taskDefinition.Triggers.Add(new LogonTrigger
			{
				Enabled = true,
				Delay = TimeSpan.FromSeconds(10),
			});
			taskDefinition.Actions.Add(new ExecAction(this._appPath));
			return taskDefinition;
		}

		private Microsoft.Win32.TaskScheduler.Task FindStartupTask()
		{
			try
			{
				using (var taskService = new TaskService())
				{
					return taskService.GetTask(this._taskName);
				}
			}
			catch (UnauthorizedAccessException)
			{
				return null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string GetDefaultApplicationPath()
		{
			var appDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
			return Path.Combine(appDir, _defaultAppName + _defaultAppExtension);
		}
	}
}
