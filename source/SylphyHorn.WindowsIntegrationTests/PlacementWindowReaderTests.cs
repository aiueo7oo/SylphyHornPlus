using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	public sealed class PlacementWindowReaderTests
	{
		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public void ConfiguredPackageWindowCanBeInspectedWithoutActivationOrMovement()
		{
			var handle = Environment.GetEnvironmentVariable("SYLPHYHORN_PLACEMENT_TEST_HWND");
			var appId = Environment.GetEnvironmentVariable("SYLPHYHORN_PLACEMENT_TEST_APPID");
			Assert.SkipWhen(string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(appId), "A read-only package window fixture was not configured.");
			var reader = new PlacementWindowReader();
			var result = reader.Read(new IntPtr(long.Parse(handle, System.Globalization.CultureInfo.InvariantCulture)));
			Assert.True(result.Status == PlacementInspectionStatus.Ready || result.Status == PlacementInspectionStatus.NotReady, result.Reason);
			Assert.NotNull(result.Identity);
			Assert.Equal(PlacementAppKind.PackageAppId, result.Identity.App.Kind);
			Assert.Equal(appId, result.Identity.App.Value);
			Assert.True(result.Identity.SameInstance(reader.Read(result.Identity.Window).Identity));
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public void InstalledCalculatorCatalogCanBeVerifiedWithoutLaunchingIt()
		{
			const string family = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
			uint count = 0, length = 0;
			var status = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
			Assert.True(status == 0 || status == 122);
			Assert.SkipWhen(count == 0, "Windows Calculator is not installed for this user.");
			var catalog = new PlacementPackageCatalog();
			Assert.True(catalog.Contains(family + "!App"));
			Assert.False(catalog.Contains(family + "!UnknownApp"));
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public void HiddenFixtureHasStableProcessIdentityAndClosedFixtureIsRejected()
		{
			var window = CreateWindowEx(0, "STATIC", "Placement identity fixture", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			Assert.NotEqual(IntPtr.Zero, window);
			try
			{
				var reader = new PlacementWindowReader();
				var first = reader.Read(window);
				Assert.Equal("WindowNotVisible", first.Reason);
				Assert.Equal(PlacementInspectionStatus.NotReady, first.Status);
				Assert.Equal(PlacementAppKind.ExecutablePath, first.Identity.App.Kind);
				using (var process = Process.GetCurrentProcess())
				{
					Assert.Equal((uint)process.Id, first.Identity.Owner.Id);
					Assert.Equal(process.MainModule.FileName, first.Identity.App.Value, ignoreCase: true);
				}
				Assert.True(first.Identity.Owner.CreatedAt > 0);
				Assert.True(first.Identity.SameInstance(reader.Read(window).Identity));
			}
			finally
			{
				Assert.True(DestroyWindow(window));
			}
			Assert.Equal("WindowClosed", new PlacementWindowReader().Read(window).Reason);
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public void ToolWindowIsExcludedBeforeProcessOrShellInspection()
		{
			var window = CreateWindowEx(0x80, "STATIC", "Placement auxiliary fixture", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			Assert.NotEqual(IntPtr.Zero, window);
			try
			{
				var result = new PlacementWindowReader().Read(window);
				Assert.Equal(PlacementInspectionStatus.Excluded, result.Status);
				Assert.Equal("AuxiliaryWindow", result.Reason);
				Assert.Null(result.Identity);
			}
			finally
			{
				Assert.True(DestroyWindow(window));
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateWindowEx(
			uint extended,
			string className,
			string title,
			uint style,
			int x,
			int y,
			int width,
			int height,
			IntPtr parent,
			IntPtr menu,
			IntPtr instance,
			IntPtr parameter);

		[DllImport("user32.dll")]
		private static extern bool DestroyWindow(IntPtr window);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
	}
}
