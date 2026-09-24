using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Security.Principal;
using System.Windows.Forms;

namespace SylphyHorn.PlacementTestHost
{
	// Separate process: production placement deliberately excludes its own windows.
	internal static class Program
	{
		[STAThread]
		private static void Main(string[] args)
		{
			if (args.Length != 0)
			{
				if (args.Length != 1 || args[0] != "--elevated-window") return;
				using (var identity = WindowsIdentity.GetCurrent())
					if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
				// No IPC, file writes, commands or child processes in the elevated fixture.
				// A thread-pool watchdog also exits if the UI message loop stalls.
				using (var watchdog = new System.Threading.Timer(_ => Environment.Exit(0), null, 20000, Timeout.Infinite))
				using (var window = new FixtureWindow
				{
					Text = "SylphyHorn elevated placement test (20 seconds)",
					Size = new Size(300, 90)
				})
					Application.Run(window);
				return;
			}
			using (var dispatcher = new Control())
			using (var lifetime = new System.Windows.Forms.Timer { Interval = 120000 })
			{
				var handle = dispatcher.Handle;
				var windows = new List<Form>();
				Action close = () =>
				{
					foreach (var window in windows) window.Dispose();
					Application.ExitThread();
				};
				lifetime.Tick += (_, __) => close();
				lifetime.Start();
				var reader = new Thread(() =>
				{
					while (true)
					{
						var command = Console.ReadLine();
						dispatcher.BeginInvoke(new Action(() =>
						{
							if (command != "show" && command != "show-active")
							{
								close();
								return;
							}
							var window = new FixtureWindow
							{
								ActivateOnShow = command == "show-active",
								Text = "SylphyHorn placement test " + (windows.Count + 1),
								Size = new Size(300, 90),
								StartPosition = FormStartPosition.Manual,
								Location = new Point(20, 20)
							};
							windows.Add(window);
							window.Show();
							if (window.ActivateOnShow) window.Activate();
							Console.WriteLine(window.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
							Console.Out.Flush();
						}));
						if (command != "show" && command != "show-active") return;
					}
				})
				{ IsBackground = true };
				reader.Start();
				Application.Run();
			}
		}

		private sealed class FixtureWindow : Form
		{
			internal bool ActivateOnShow;

			protected override bool ShowWithoutActivation => !this.ActivateOnShow;
		}
	}
}
