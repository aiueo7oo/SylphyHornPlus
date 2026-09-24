using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MetroTrilithon.Desktop;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	public class StartupShortcutIntegrationTests
	{
#if !NETFRAMEWORK
		[Fact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.HostedCI)]
		public void CliStartupBackendReadsAndRemovesAnIsolatedShortcutWithoutRegisteringATask()
		{
			var directory = Path.Combine(Path.GetTempPath(), "SylphyHorn-CliStartup-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				var executable = Path.Combine(directory, "Probe-" + Guid.NewGuid().ToString("N") + ".exe");
				File.WriteAllBytes(executable, new byte[] { 0 });
				var registration = new SylphyHorn.Services.Commands.WindowsStartupRegistration(executable, directory);
				Assert.Equal("disabled", registration.Read().Mode);
				registration.CreateNormal();
				var state = registration.Read();
				Assert.Equal("normal", state.Mode);
				Assert.True(state.TargetMatches);
				Assert.Equal(executable, state.NormalTarget, StringComparer.OrdinalIgnoreCase);
				Assert.False(state.ElevatedRegistered);
				registration.RemoveNormal();
				Assert.Equal("disabled", registration.Read().Mode);
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}
#endif

		[Fact]
		[Trait(
			IntegrationTestExecutionEnvironment.TraitName,
			IntegrationTestExecutionEnvironment.HostedCI)]
		public void ShellLinkTargetsExplicitExecutablePath()
		{
			var directory = Path.Combine(
				Path.GetTempPath(),
				$"SylphyHornPlus-StartupShortcut-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);

			try
			{
				var shortcutPath = Path.Combine(directory, "SylphyHorn.lnk");
				var executablePath = Process.GetCurrentProcess().MainModule.FileName;

				ShellLink.Create(shortcutPath, executablePath);

				Assert.True(File.Exists(shortcutPath));
				Assert.Equal(
					Path.GetFullPath(executablePath),
					Path.GetFullPath(ReadShortcutTarget(shortcutPath)),
					StringComparer.OrdinalIgnoreCase);
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Fact]
		[Trait(
			IntegrationTestExecutionEnvironment.TraitName,
			IntegrationTestExecutionEnvironment.HostedCI)]
		public void ShellLinkProductionOverloadPreservesShortcutContract()
		{
			var directory = Path.Combine(
				Path.GetTempPath(),
				$"SylphyHornPlus-StartupShortcut-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);

			try
			{
				var shortcutPath = Path.Combine(directory, "SylphyHorn.lnk");
				var executablePath = Process.GetCurrentProcess().MainModule.FileName;

#if NETFRAMEWORK
				ShellLink.Create(shortcutPath);
#else
				ShellLink.Create(shortcutPath, Environment.ProcessPath);
#endif

				Assert.True(File.Exists(shortcutPath));
				var shortcut = ReadShortcut(shortcutPath);
				Assert.Equal(
					Path.GetFullPath(executablePath),
					Path.GetFullPath(shortcut.Target),
					StringComparer.OrdinalIgnoreCase);
				Assert.Equal(string.Empty, shortcut.Arguments);
				Assert.Equal(string.Empty, shortcut.WorkingDirectory);

				File.Delete(shortcutPath);
				Assert.False(File.Exists(shortcutPath));
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		private static string ReadShortcutTarget(string shortcutPath)
			=> ReadShortcut(shortcutPath).Target;

		private static (string Target, string Arguments, string WorkingDirectory) ReadShortcut(
			string shortcutPath)
		{
			var shellLink = (IShellLink)new ShellLinkObject();
			try
			{
				((IPersistFile)shellLink).Load(shortcutPath, 0);

				var targetPath = new StringBuilder(32768);
				shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 4);
				var arguments = new StringBuilder(32768);
				shellLink.GetArguments(arguments, arguments.Capacity);
				var workingDirectory = new StringBuilder(32768);
				shellLink.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
				return (targetPath.ToString(), arguments.ToString(), workingDirectory.ToString());
			}
			finally
			{
				Marshal.FinalReleaseComObject(shellLink);
			}
		}

		[ComImport]
		[Guid("00021401-0000-0000-C000-000000000046")]
		private class ShellLinkObject
		{
		}

		[ComImport]
		[Guid("000214F9-0000-0000-C000-000000000046")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellLink
		{
			void GetPath(
				[Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
				int maximumPathLength,
				IntPtr findData,
				uint flags);

			void GetIDList(out IntPtr itemIdentifierList);

			void SetIDList(IntPtr itemIdentifierList);

			void GetDescription(
				[Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
				int maximumLength);

			void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

			void GetWorkingDirectory(
				[Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder workingDirectory,
				int maximumPathLength);

			void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string workingDirectory);

			void GetArguments(
				[Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
				int maximumLength);
		}
	}
}
