using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using WindowsDesktop;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	[Collection(WindowsHookCollection.Name)]
	public sealed class PlacementPackageMovementTests
	{
		private const string CalculatorAppId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
		private readonly ITestOutputHelper _output;

		public PlacementPackageMovementTests(ITestOutputHelper output) => this._output = output;

		[WpfFact(Timeout = 60000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task NewCalculatorInstanceIsIdentifiedAndMovedThroughNativeCandidates()
		{
			Assert.SkipUnless(Environment.GetEnvironmentVariable("SYLPHYHORN_PACKAGE_MOVE_TESTS") == "1", "Opt in to launching and moving a new Calculator instance.");
			await this.VerifyPackageMovement(CalculatorAppId, "CalculatorApp", false);
		}

		[WpfFact(Timeout = 60000)]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task NewPaintInstanceUsesWinUiAndOwnsItsMovedWindow()
		{
			Assert.SkipUnless(Environment.GetEnvironmentVariable("SYLPHYHORN_WINUI3_MOVE_TESTS") == "1", "Opt in to launching and moving a new Paint instance.");
			await this.VerifyPackageMovement("Microsoft.Paint_8wekyb3d8bbwe!App", "mspaint", true);
		}

		private async Task VerifyPackageMovement(string appId, string processName, bool requireDirectWinUi)
		{
			Assert.SkipUnless(new PlacementPackageCatalog().Contains(appId), "The package app is not installed.");
			ActivatedPackageWindows.RequireNoExistingInstance(processName);
			using (var environment = await PlacementTestEnvironment.Create(this._output))
			{
				// Also prove an ordinary fixture cannot match the package rule.
				var ordinary = await environment.Show();
				var destination = environment.Configuration().Rules[0].Destination;
				var configuration = new AppPlacementConfiguration(
					true,
					new[] { new AppPlacementRule(Guid.NewGuid(), true,
						new PlacementAppIdentity(PlacementAppKind.PackageAppId, appId), destination) });
				Assert.Null(configuration.FindEnabledRule(new PlacementWindowReader().Read(ordinary).Identity.App));
				var monitor = PlacementWindowMonitor.Start();
				ActivatedPackageWindows windows = null;
				try
				{
					Assert.True(await monitor.Ready);
					windows = ActivatedPackageWindows.Activate(appId, processName);
					bool Current(PlacementCandidate candidate)
					{
						monitor.Events.ProcessBatch();
						return monitor.Events.IsCurrent(candidate);
					}
					var processor = new PlacementProcessor(
						configuration,
						windows,
						(target, deadline) => new PlacementAuthorization(environment.Map.Resolve(target), new PlacementMovePermit()),
						Current,
						Now);
					var pending = new List<PlacementWorkItem>();
					PlacementWorkItem moved = null;
					var watch = Stopwatch.StartNew();
					while (moved == null && watch.Elapsed < TimeSpan.FromSeconds(12))
					{
						monitor.Events.ProcessBatch();
						Assert.Equal(PlacementMonitorState.Running, monitor.Events.State);
						PlacementCandidate candidate;
						while (pending.Count < 256 && (candidate = monitor.Events.TakeCandidate()) != null) pending.Add(new PlacementWorkItem(candidate));
						foreach (var work in pending.ToArray())
						{
							if (work.NextAt > Now()) continue;
							processor.Step(work);
							if (work.Result == null) continue;
							if (work.Identity?.App.Value == appId)
							{
								this._output.WriteLine(
									"Package candidate: {0}; {1}; {2}; owner={3}; app process={4}",
									work.Candidate.Window,
									work.Result.Outcome,
									work.Result.Reason,
									work.Identity.Owner.Id,
									work.Identity.AppProcess?.Id);
							}
							if (work.Result.Outcome == PlacementOutcome.Moved)
							{
								moved = work;
							}
							monitor.Events.Complete(work.Candidate);
							pending.Remove(work);
						}
						if (moved == null)
						{
							await Task.Delay(20, TestContext.Current.CancellationToken);
						}
					}
					Assert.NotNull(moved);
					Assert.Equal(appId, moved.Identity.App.Value);
					Assert.Equal(PlacementAppKind.PackageAppId, moved.Identity.App.Kind);
					Assert.True(windows.Owns(moved.Identity));
					if (requireDirectWinUi)
					{
						Assert.True(moved.Identity.Owner.SameProcess(moved.Identity.AppProcess));
						Assert.Equal(appId, moved.Identity.Owner.AppId);
						windows.VerifyWinUiRuntime(this._output);
					}
					var after = new PlacementWindowReader().Read(moved.Candidate.Window);
					this._output.WriteLine(
						"After move: status={0}; reason={1}; owner={2}; app={3}; app process={4}; prior thread={5}; current thread={6}",
						after.Status,
						after.Reason,
						after.Identity?.Owner.Id,
						after.Identity?.App.Value,
						after.Identity?.AppProcess?.Id,
						moved.Identity.Thread,
						after.Identity?.Thread);
					Assert.NotNull(after.Identity);
					Assert.Equal(PlacementAppKind.PackageAppId, after.Identity.App.Kind);
					Assert.Equal(appId, after.Identity.App.Value);
					Assert.True(moved.Identity.Owner.SameProcess(after.Identity.Owner));
					Assert.Equal(moved.Identity.Thread, after.Identity.Thread);
					// UWP may detach its child from the frame after moving off the current desktop.
					// Keep verified app identity, but do not pretend the missing process proves continuity.
					if (after.Identity.AppProcess == null)
					{
						Assert.False(moved.Identity.SameInstance(after.Identity));
					}
					else
					{
						Assert.True(windows.Owns(after.Identity));
					}
					if (requireDirectWinUi)
					{
						Assert.True(moved.Identity.SameInstance(after.Identity));
					}
					Assert.Equal(environment.Target, VirtualDesktop.FromHwnd(moved.Candidate.Window).Id);
					Assert.Equal(environment.Source, environment.Location(ordinary));
					Assert.Equal(1, windows.MoveRequests);
					environment.AssertDesktopUnchanged();
					this._output.WriteLine(
						"Verified package={0}; owner={1}; app={2}; elapsed={3}ms; move requests={4}",
						appId,
						moved.Identity.Owner.Path,
						moved.Identity.AppProcess.Path,
						watch.ElapsedMilliseconds,
						windows.MoveRequests);
				}
				finally
				{
					try
					{
						await monitor.StopAsync();
					}
					finally
					{
						monitor.Dispose();
						windows?.Dispose();
					}
				}
			}
		}

		// Safety boundary for an installed application: only the newly activated process may move.
		// The real reader, pin checks, identity revalidation, move and readback remain unchanged.
		private sealed class ActivatedPackageWindows : IPlacementWindows, IDisposable
		{
			private readonly PlacementWindows _inner = new PlacementWindows();
			private Process _process;
			private string _appId;
			private long _created;
			internal int MoveRequests;

			internal void VerifyWinUiRuntime(ITestOutputHelper output)
			{
				var module = this._process.Modules.Cast<ProcessModule>().SingleOrDefault(value => string.Equals(value.ModuleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
				Assert.NotNull(module);
				Assert.True(module.FileName.IndexOf("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) >= 0, module.FileName);
				output.WriteLine("Loaded WinUI: {0}; version={1}", module.FileName, module.FileVersionInfo.FileVersion);
			}

			internal static void RequireNoExistingInstance(string processName)
			{
				var existing = Process.GetProcessesByName(processName);
				try
				{
					Assert.SkipUnless(existing.Length == 0, "An existing " + processName + " instance must not be reused or affected.");
				}
				finally
				{
					foreach (var process in existing) process.Dispose();
				}
			}

			internal static ActivatedPackageWindows Activate(string appId, string processName)
			{
				RequireNoExistingInstance(processName);
				var started = DateTime.UtcNow;
				var activation = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
				uint pid;
				try
				{
					Marshal.ThrowExceptionForHR(activation.ActivateApplication(appId, null, 2, out pid));
				}
				finally
				{
					Marshal.ReleaseComObject(activation);
				}
				var process = Process.GetProcessById(checked((int)pid));
				try
				{
					var handle = process.Handle; // Retain the actual process handle, including during cleanup.
					Assert.Equal(processName, process.ProcessName, ignoreCase: true);
					Assert.True(process.StartTime.ToUniversalTime() >= started, "Activation did not return a newly created process; it will not be moved or terminated.");
					return new ActivatedPackageWindows
					{
						_appId = appId,
						_process = process,
						_created = process.StartTime.ToFileTimeUtc()
					};
				}
				catch
				{
					process.Dispose();
					throw;
				}
			}

			internal bool Owns(PlacementWindowIdentity identity) => !this._process.HasExited && identity?.App.Value == this._appId
				&& identity.AppProcess?.Id == (uint)this._process.Id && identity.AppProcess.CreatedAt == this._created;

			public PlacementWindowInspection Inspect(IntPtr window)
			{
				var result = this._inner.Inspect(window);
				if (result.Identity == null) return result;
				if (this.Owns(result.Identity)) return result;
				if (result.Identity.App.Value == this._appId && result.Identity.AppProcess == null)
				{
					return new PlacementWindowInspection(PlacementInspectionStatus.NotReady, "FixtureAppProcessNotReady");
				}
				return new PlacementWindowInspection(PlacementInspectionStatus.Excluded, "NotOwnedByFixture");
			}

			public PlacementWindowLocation Locate(IntPtr window) => this._inner.Locate(window);

			public PlacementMoveStatus Move(PlacementWindowIdentity expected, Guid source, Guid target,
				PlacementMovePermit permit, Func<bool> stillCurrent, Action beforeMove = null)
			{
				Assert.True(this.Owns(expected));
				this.MoveRequests++;
				return this._inner.Move(expected, source, target, permit, () => this.Owns(expected) && stillCurrent(), beforeMove);
			}

			public void Dispose()
			{
				try
				{
					if (!this._process.HasExited)
					{
						this._process.Kill();
					}
					Assert.True(this._process.WaitForExit(5000));
				}
				finally
				{
					this._process.Dispose();
				}
			}
		}

		private static long Now() => checked((long)GetTickCount64());

		[DllImport("kernel32.dll")]
		private static extern ulong GetTickCount64();

		[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IApplicationActivationManager
		{
			[PreserveSig]
			int ActivateApplication(
				[MarshalAs(UnmanagedType.LPWStr)] string appId,
				[MarshalAs(UnmanagedType.LPWStr)] string arguments,
				uint options,
				out uint processId);
		}
	}
}
