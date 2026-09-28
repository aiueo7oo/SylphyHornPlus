using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.WindowsIntegrationTests
{
	public sealed class PlacementAppCatalogTests
	{
		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task OpenWindowCatalogReadsChoicesWithoutMovingOrActivatingWindows()
		{
			var choices = await new PlacementAppCatalog().ReadAsync(true, TestContext.Current.CancellationToken);
			Assert.All(
				choices,
				choice =>
				{
					Assert.False(string.IsNullOrEmpty(choice.Detail));
					if (choice.Identity == null)
					{
						Assert.Equal("IdentityUnavailable", choice.Problem);
					}
					if (choice.Identity?.Kind == PlacementAppKind.ExecutablePath)
					{
						Assert.Equal(choice.Identity.Value, choice.Path, ignoreCase: true);
					}
					Assert.False(choice.ConfirmPath);
				});
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task InstalledCatalogVerifiesUnstartedApps()
		{
			var choices = await new PlacementAppCatalog().ReadAsync(false, TestContext.Current.CancellationToken);
			Assert.NotEmpty(choices);
			Assert.All(choices.Where(choice => choice.Identity?.Kind == PlacementAppKind.ExecutablePath), choice => Assert.True(choice.ConfirmPath));
			Assert.All(choices.Where(choice => choice.Icon != null), choice => Assert.True(choice.Icon.IsFrozen));
			var calculator = choices.FirstOrDefault(choice => choice.Identity?.Value == "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
			Assert.SkipWhen(calculator == null, "Calculator is not registered for this test user.");
			Assert.False(calculator.ConfirmPath);
			Assert.NotNull(calculator.Icon);
			Assert.True(string.IsNullOrEmpty(calculator.Path) || System.IO.File.Exists(calculator.Path));
		}

		[WpfFact]
		[Trait(IntegrationTestExecutionEnvironment.TraitName, IntegrationTestExecutionEnvironment.InteractiveDesktop)]
		public async Task ExecutableSelectionUsesExactPathAndReturnsFrozenIcon()
		{
			using (var process = Process.GetCurrentProcess())
			{
				var path = process.MainModule.FileName;
				var choice = await new PlacementAppCatalog().ReadExecutableAsync(path, TestContext.Current.CancellationToken);
				Assert.Equal(PlacementAppKind.ExecutablePath, choice.Identity.Kind);
				Assert.Equal(path, choice.Identity.Value, ignoreCase: true);
				Assert.False(choice.ConfirmPath);
				Assert.NotNull(choice.Icon);
				Assert.True(choice.Icon.IsFrozen);
			}
		}
	}
}
