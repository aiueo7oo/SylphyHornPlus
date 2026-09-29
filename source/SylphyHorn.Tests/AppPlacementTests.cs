using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using SylphyHorn.AppPlacement;
using SylphyHorn.Serialization;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class AppPlacementTests
	{
		[Theory]
		[InlineData(true, null, false)]
		[InlineData(false, true, false)]
		[InlineData(true, false, false)]
		[InlineData(false, false, true)]
		public void FollowPreferencesRoundTripAndLegacyDefaultsArePreserved(bool global, bool? follow, bool legacy)
		{
			var rule = Rule();
			var config = new AppPlacementConfiguration(true, new[] { new AppPlacementRule(rule.Id, rule.Enabled,
				rule.App, rule.Destination, followForeground: follow) }, followForeground: global);
			var serializer = new DataContractSerializer(typeof(AppPlacementConfiguration));
			using (var stream = new MemoryStream())
			{
				serializer.WriteObject(stream, config);
				stream.Position = 0;
				var xml = XDocument.Load(stream);
				if (legacy)
				{
					xml.Descendants().Where(element => element.Name.LocalName == "FollowForeground").Remove();
				}
				using (var reader = xml.CreateReader())
				{
					var restored = (AppPlacementConfiguration)serializer.ReadObject(reader);
					Assert.Equal(legacy || global, restored.FollowForeground);
					Assert.Equal(legacy ? null : follow, Assert.Single(restored.Rules).FollowForeground);
				}
			}
		}

		private const string Key = "AppPlacementSettings.Configuration";
		private static readonly Guid RuleId = new Guid("70c72736-00bd-49c4-9d6a-b859dadc2273");
		private static readonly Guid Work = new Guid("9f9b1ee6-16ee-442b-b341-88f60dfb4bf7");
		private static readonly Guid Other = new Guid("2f85c661-9c03-4e69-ad01-85df359a1a00");

		private static PlacementAppIdentity Exe(string path = @"C:\Apps\Editor.exe") => new PlacementAppIdentity(PlacementAppKind.ExecutablePath, path);

		private static AppPlacementRule Rule(PlacementDestination target = null, bool enabled = true, PlacementAppIdentity app = null)
			=> new AppPlacementRule(RuleId, enabled, app ?? Exe(), target ?? PlacementDestination.ByName("仕事"), "Editor", @"C:\Apps\Editor.exe");

		private static PlacementDesktopMap Map(params PlacementDesktop[] entries) => new PlacementDesktopMap(entries);

		private static PlacementDesktop Desktop(Guid id, string name) => new PlacementDesktop(id, name, true);

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void CreationOptionRoundTripsAndLegacyConfigurationDefaultsToOff(bool create)
		{
			var serializer = new DataContractSerializer(typeof(AppPlacementConfiguration));
			using (var stream = new MemoryStream())
			{
				serializer.WriteObject(stream, new AppPlacementConfiguration(true, new[] { Rule() }, create));
				stream.Position = 0;
				var xml = XDocument.Load(stream);
				Assert.Equal(create, xml.Descendants().Any(element => element.Name.LocalName == "CreateMissingDesktops"));
				using (var reader = xml.CreateReader())
				{
					var restored = (AppPlacementConfiguration)serializer.ReadObject(reader);
					Assert.Equal(create, restored.CreateMissingDesktops);
					Assert.False(restored.CloseCreatedDesktops);
					Assert.Empty(restored.ClosingTargets);
					Assert.Single(restored.Rules);
				}
			}
		}

		[Fact]
		public void NameFollowsRecreationButNotRenameAndNumberFollowsOrder()
		{
			var name = PlacementDestination.ByName("仕事");
			var number = PlacementDestination.ByNumber(2);
			var first = Map(Desktop(Work, "仕事"), Desktop(Other, "私用"));
			Assert.Equal(Work, first.Resolve(name).DesktopId);
			Assert.Equal(Other, first.Resolve(number).DesktopId);
			var reordered = Map(Desktop(Other, "私用"), Desktop(Work, "仕事"));
			Assert.Equal(Work, reordered.Resolve(name).DesktopId);
			Assert.Equal(Work, reordered.Resolve(number).DesktopId);
			Assert.Equal(PlacementResolutionStatus.Missing, Map(Desktop(Work, "業務")).Resolve(name).Status);
			Assert.Equal(PlacementResolutionStatus.Missing, Map().Resolve(number).Status);
			Assert.Equal(Other, Map(Desktop(Other, "仕事")).Resolve(name).DesktopId);
			Assert.Equal("仕事", name.Name);
		}

		[Fact]
		public void DuplicateNamesChooseLowestNumberAndFollowReordering()
		{
			var name = PlacementDestination.ByName("仕事");
			var first = Map(Desktop(Work, "仕事"), Desktop(Other, "仕事")).Resolve(name);
			Assert.Equal(PlacementResolutionStatus.Resolved, first.Status);
			Assert.Equal(Work, first.DesktopId);
			Assert.Equal(Other, Map(Desktop(Other, "仕事"), Desktop(Work, "仕事")).Resolve(name).DesktopId);
			Assert.Equal(Other, Map(Desktop(Other, "仕事")).Resolve(name).DesktopId);
		}

		[Fact]
		public void MissingUnknownAndUnnamedDesktopsAreDistinct()
		{
			var name = PlacementDestination.ByName("仕事");
			var partial = Map(Desktop(Work, "仕事"), new PlacementDesktop(Other, null, false));
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, partial.Resolve(name).Status);
			Assert.Equal(Other, partial.Resolve(PlacementDestination.ByNumber(2)).DesktopId);
			Assert.Equal(PlacementResolutionStatus.StateUnavailable, PlacementDesktopMap.Unavailable.Resolve(name).Status);
			var unnamed = Map(Desktop(Work, null));
			Assert.Equal(Work, unnamed.Resolve(PlacementDestination.ByNumber(1)).DesktopId);
			Assert.Equal(PlacementResolutionStatus.Missing, unnamed.Resolve(PlacementDestination.ByName("デスクトップ1")).Status);
			Assert.Throws<ArgumentException>(() => Map(Desktop(Work, "a"), Desktop(Work, "b")));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		public void DesktopNamesAreExact(int variant)
		{
			// Keep runner metadata ASCII; the compared desktop names remain Unicode.
			var requested = new[] { "仕事 ", " 仕事", "work" }[variant];
			Assert.Equal(
				PlacementResolutionStatus.Missing,
				Map(Desktop(Work, "仕事"), Desktop(Other, "Work")).Resolve(PlacementDestination.ByName(requested)).Status);
		}

		[Fact]
		public void RulesUseFullIdentityAndNeverAnArbitraryDuplicate()
		{
			var rule = Rule();
			var config = new AppPlacementConfiguration(true, new[] { rule });
			Assert.Same(rule, config.FindEnabledRule(Exe(@"c:/apps/./EDITOR.exe")));
			Assert.Null(config.FindEnabledRule(Exe(@"D:\Apps\Editor.exe")));
			Assert.Null(new AppPlacementConfiguration(false, new[] { rule }).FindEnabledRule(Exe()));
			Assert.Null(new AppPlacementConfiguration(true, new[] { Rule(enabled: false) }).FindEnabledRule(Exe()));
			var duplicate = new AppPlacementRule(Guid.NewGuid(), true, Exe(@"c:\APPS\editor.exe"), PlacementDestination.ByNumber(1));
			Assert.Throws<SerializationException>(() => new AppPlacementConfiguration(true, new[] { rule, duplicate }));
			Assert.Throws<SerializationException>(() => new AppPlacementConfiguration(true, new[] { rule, rule }));
			var package = new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Family_publisher!App");
			Assert.False(package.Equals(new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Family_publisher!OtherApp")));
			Assert.NotNull(new AppPlacementConfiguration(true, new[] { Rule(app: package) }).FindEnabledRule(package));
		}

		[Theory]
		[InlineData("Editor.exe")]
		[InlineData("C:Editor.exe")]
		[InlineData("\\Apps\\Editor.exe")]
		[InlineData("C:\\Apps\\*.exe")]
		[InlineData("C:\\Apps\\document.txt")]
		public void RelativeAndNonExecutablePathsAreRejected(string path)
		{
			Assert.Throws<SerializationException>(() => Exe(path));
		}

		[Fact]
		public void InvalidTargetsAndIdentityKindsAreRejected()
		{
			Assert.Throws<SerializationException>(() => PlacementDestination.ByNumber(0));
			Assert.Throws<SerializationException>(() => PlacementDestination.ByNumber(-1));
			Assert.Throws<SerializationException>(() => PlacementDestination.ByName(" "));
			Assert.Throws<SerializationException>(() => new PlacementAppIdentity((PlacementAppKind)99, "value"));
			Assert.Throws<SerializationException>(() => new PlacementAppIdentity(PlacementAppKind.PackageAppId, "FamilyOnly"));
			Assert.Throws<SerializationException>(() => new AppPlacementRule(Guid.Empty, true, Exe(), PlacementDestination.ByNumber(1)));
		}

		[Fact]
		public async Task LegacySettingsDefaultToDisabledAndEmptyWithoutChangingOtherValues()
		{
			var provider = new MemoryDictionaryProvider(new Dictionary<string, object> { ["GeneralSettings.LoopDesktop"] = true });
			await provider.InitializeAsync();
			var settings = new AppPlacementSettings(provider);
			Assert.False(settings.Configuration.Value.Enabled);
			Assert.Empty(settings.Configuration.Value.Rules);
			Assert.True(provider.TryGetValue<bool>("GeneralSettings.LoopDesktop", out var loop) && loop);
		}

		[Fact]
		public async Task RuleGraphCannotBeMutatedThroughInputOrStagedSnapshots()
		{
			var rules = new[] { Rule() };
			var config = new AppPlacementConfiguration(true, rules);
			rules[0] = Rule(PlacementDestination.ByNumber(3));
			Assert.Equal("仕事", config.Rules[0].Destination.Name);
			Assert.Throws<NotSupportedException>(() => ((IList<AppPlacementRule>)config.Rules)[0] = rules[0]);
			var provider = new MemoryDictionaryProvider(new Dictionary<string, object> { [Key] = config });
			await provider.InitializeAsync();
			var stage = await provider.PrepareImportAsync("memory");
			var commit = stage.CreateCommitDictionary();
			commit[Key] = AppPlacementConfiguration.Empty;
			Assert.True(((AppPlacementConfiguration)stage.Settings[Key]).Enabled);
			Assert.True(provider.TryGetValue<AppPlacementConfiguration>(Key, out var active) && active.Enabled);
			provider.DiscardStagedImport(stage);
		}

		[Fact]
		public async Task FingerprintIncludesAllRuleFieldsAndIsStableAcrossEquivalentGraphs()
		{
			var rule = Rule();
			var baseline = new AppPlacementConfiguration(true, new[] { rule });
			var expected = await Fingerprint(baseline);
			Assert.Equal(expected, await Fingerprint(new AppPlacementConfiguration(true, new[] { Rule() })));
			var variants = new[]
			{
				new AppPlacementConfiguration(false, new[] { rule }),
				new AppPlacementConfiguration(true, new[] { rule }, true),
				new AppPlacementConfiguration(true, new[] { rule }, closeCreatedDesktops: true),
				new AppPlacementConfiguration(true, new[] { rule }, closingTargets: new[] { PlacementDestination.ByName("work") }),
				new AppPlacementConfiguration(true, new[] { rule }, closingTargets: new[] { PlacementDestination.ByNumber(2) }),
				new AppPlacementConfiguration(true, Array.Empty<AppPlacementRule>()),
				new AppPlacementConfiguration(true, new[] { Rule(enabled: false) }),
				new AppPlacementConfiguration(true, new[] { Rule(PlacementDestination.ByNumber(2)) }),
				new AppPlacementConfiguration(true, new[] { Rule(PlacementDestination.ByName("業務")) }),
				new AppPlacementConfiguration(true, new[] { Rule(app: Exe(@"D:\Editor.exe")) }),
				new AppPlacementConfiguration(true, new[] { new AppPlacementRule(Guid.NewGuid(), true, rule.App, rule.Destination, rule.DisplayName, rule.DisplayExecutablePath) }),
				new AppPlacementConfiguration(true, new[] { new AppPlacementRule(RuleId, true, rule.App, rule.Destination, "Renamed", rule.DisplayExecutablePath) }),
				new AppPlacementConfiguration(true, new[] { new AppPlacementRule(RuleId, true, rule.App, rule.Destination, rule.DisplayName, @"D:\Display.exe") }),
			};
			foreach (var variant in variants)
			{
				Assert.NotEqual(expected, await Fingerprint(variant));
			}
		}

		[Fact]
		public async Task XmlRoundTripImportResetAndAtomicStorePreserveConfiguration()
		{
			await WithFiles(async directory =>
			{
				var provider = new TestFileProvider(Path.Combine(directory, "active.xml"));
				await provider.LoadAsync();
				var packageRule = new AppPlacementRule(
					Guid.NewGuid(),
					true,
					new PlacementAppIdentity(PlacementAppKind.PackageAppId, "Family_publisher!App"),
					PlacementDestination.ByNumber(2),
					"Package app");
				var configuration = new AppPlacementConfiguration(true, new[] { Rule(), packageRule }, true, true,
					new[] { PlacementDestination.ByName("work"), PlacementDestination.ByNumber(3) });
				new AppPlacementSettings(provider).CreatedDesktopGroups.Value = new[] { new PlacementCreatedGroup(new[] { Work, Other }, true) };
				new AppPlacementSettings(provider).Configuration.Value = configuration;
				provider.SetValue("Future.Unknown", 42);
				Assert.True((await provider.SaveWithResultAsync()).Succeeded);
				var export = Path.Combine(directory, "export.xml");
				await provider.ExportAsync(export);
				var reader = new TestFileProvider(Path.Combine(directory, "imported.xml"));
				await reader.LoadAsync();
				await reader.ImportAsync(export);
				var loaded = new AppPlacementSettings(reader).Configuration.Value;
				Assert.Equal(await Fingerprint(configuration), await Fingerprint(loaded));
				Assert.True(reader.TryGetValue<int>("Future.Unknown", out var unknown) && unknown == 42);
				Assert.NotNull(loaded.FindEnabledRule(packageRule.App));
				Assert.True(loaded.CloseCreatedDesktops);
				Assert.Equal(2, loaded.ClosingTargets.Count);
				var created = Assert.Single(new AppPlacementSettings(reader).CreatedDesktopGroups.Value);
				Assert.True(created.Used);
				Assert.Equal(new[] { Work, Other }, created.Desktops);
				var reset = await reader.PrepareResetAsync();
				Assert.True((await reader.CommitStagedImportAsync(reset, reset.CreateCommitDictionary())).Succeeded);
				reader.PublishCommittedImport();
				Assert.False(new AppPlacementSettings(reader).Configuration.Value.Enabled);
				Assert.Empty(new AppPlacementSettings(reader).CreatedDesktopGroups.Value);
				Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
			});
		}

		[Theory]
		[InlineData("Number", "0")]
		[InlineData("Kind", "UnknownKind")]
		[InlineData("Value", "relative.exe")]
		[InlineData("Id", "00000000-0000-0000-0000-000000000000")]
		public async Task InvalidXmlImportLeavesActiveMemoryAndFileUntouched(string member, string invalid)
		{
			await WithFiles(async directory =>
			{
				var activePath = Path.Combine(directory, "active.xml");
				var provider = new TestFileProvider(activePath);
				await provider.LoadAsync();
				provider.SetValue(Key, new AppPlacementConfiguration(true, new[] { Rule(PlacementDestination.ByNumber(2)) }));
				await provider.SaveAsync();
				var original = File.ReadAllBytes(activePath);
				var document = XDocument.Load(activePath);
				var value = document.Descendants().Single(e => e.Name.LocalName == "Key" && e.Value == Key)
					.Parent.Elements().Single(e => e.Name.LocalName == "Value");
				var payload = XDocument.Parse(value.Value);
				payload.Descendants().First(e => e.Name.LocalName == member).Value = invalid;
				value.Value = payload.ToString(SaveOptions.DisableFormatting);
				var bad = Path.Combine(directory, "bad.xml");
				document.Save(bad);
				await Assert.ThrowsAnyAsync<Exception>(() => provider.PrepareImportAsync(bad));
				Assert.False(provider.ImportTransactionActive);
				Assert.Equal(original, File.ReadAllBytes(activePath));
				Assert.True(provider.TryGetValue<AppPlacementConfiguration>(Key, out var config));
				Assert.Equal(2, config.Rules[0].Destination.Number);
				var valid = await provider.PrepareImportAsync(activePath);
				provider.DiscardStagedImport(valid);
			});
		}

		[Fact]
		public async Task DuplicateEnabledRulesInXmlAreRejectedBeforePublication()
		{
			await WithFiles(async directory =>
			{
				var path = Path.Combine(directory, "active.xml");
				var provider = new TestFileProvider(path);
				await provider.LoadAsync();
				provider.SetValue(Key, new AppPlacementConfiguration(true, new[] { Rule() }));
				await provider.SaveAsync();
				var before = File.ReadAllBytes(path);
				var document = XDocument.Load(path);
				var value = document.Descendants().Single(e => e.Name.LocalName == "Key" && e.Value == Key)
					.Parent.Elements().Single(e => e.Name.LocalName == "Value");
				var payload = XDocument.Parse(value.Value);
				var list = payload.Descendants().Single(e => e.Name.LocalName == "Rules");
				var duplicate = new XElement(list.Elements().Single());
				duplicate.Elements().Single(e => e.Name.LocalName == "Id").Value = Guid.NewGuid().ToString();
				list.Add(duplicate);
				value.Value = payload.ToString(SaveOptions.DisableFormatting);
				var imported = Path.Combine(directory, "duplicate.xml");
				document.Save(imported);
				await Assert.ThrowsAsync<SerializationException>(() => provider.PrepareImportAsync(imported));
				Assert.False(provider.ImportTransactionActive);
				Assert.Equal(before, File.ReadAllBytes(path));
				Assert.True(provider.TryGetValue<AppPlacementConfiguration>(Key, out var active));
				Assert.Single(active.Rules);
			});
		}

		[Fact]
		public async Task WrongConfigurationTypeCannotBeLoadedSetOrCommitted()
		{
			var bad = new MemoryDictionaryProvider(new Dictionary<string, object> { [Key] = "incorrect" });
			await Assert.ThrowsAsync<SerializationException>(() => bad.InitializeAsync());
			var provider = new MemoryDictionaryProvider();
			await provider.InitializeAsync();
			Assert.Throws<SerializationException>(() => provider.SetValue<object>(Key, null));
			var stage = await provider.PrepareResetAsync();
			var commit = stage.CreateCommitDictionary();
			commit[Key] = "incorrect";
			var result = await provider.CommitStagedImportAsync(stage, commit);
			Assert.Equal(SettingsImportCommitStatus.PublishFailed, result.Status);
			Assert.Equal(SettingsSaveErrorCategory.Serialization, result.SaveResult.ErrorCategory);
			Assert.False(provider.ImportTransactionActive);
		}

		private static async Task<string> Fingerprint(AppPlacementConfiguration configuration)
		{
			var provider = new MemoryDictionaryProvider(new Dictionary<string, object> { [Key] = configuration });
			await provider.InitializeAsync();
			var stage = await provider.PrepareResetAsync();
			provider.DiscardStagedImport(stage);
			return stage.ActiveFingerprint;
		}

		private static async Task WithFiles(Func<string, Task> action)
		{
			var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SylphyHornPlus-AppPlacement-" + Guid.NewGuid().ToString("N")));
			Directory.CreateDirectory(directory);
			try
			{
				await action(directory);
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		private sealed class TestFileProvider : DictionaryProvider
		{
			private readonly FileInfo _file;

			internal TestFileProvider(string path)
			{
				this._file = new FileInfo(path);
			}

			protected override Task SaveAsyncCore(IDictionary<string, object> values) => AtomicSettingsFile.WriteAsync(values, this._file, this.KnownTypes);

			protected override Task SaveAsyncCore(IDictionary<string, object> values, string path) => AtomicSettingsFile.WriteAsync(values, new FileInfo(path), this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore() => AtomicSettingsFile.ReadAsync(this._file, this.KnownTypes);

			protected override Task<IDictionary<string, object>> LoadAsyncCore(string path) => AtomicSettingsFile.ReadAsync(new FileInfo(path), this.KnownTypes);

			protected override Task<string> GetContentHashAsyncCore() => AtomicSettingsFile.HashAsync(this._file);
		}
	}
}
