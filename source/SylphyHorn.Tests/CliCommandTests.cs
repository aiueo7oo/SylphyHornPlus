using System;
using SylphyHorn.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliCommandTests
	{
		[Fact]
		public void AssignmentCommandsRecognizeThreeWordsAndPreservePaths()
		{
			var args = new[] { "app", "assignment", "set", "--path", @"C:\My Apps\Editor.exe", "--desktop-name", "Development" };
			var command = CliCommand.Parse(args);
			Assert.Equal("app assignment set", CliCommand.Recognize(args));
			Assert.Equal(@"C:\My Apps\Editor.exe", command.AppPath);
			Assert.Equal("name", command.TargetKind);
			Assert.Equal("Development", command.TargetValue);
		}

		[Theory]
		[InlineData("app assignment")]
		[InlineData("app assignment list --path C:\\App.exe")]
		[InlineData("app assignment remove")]
		[InlineData("app assignment set --path C:\\App.exe")]
		[InlineData("app assignment set --path C:\\App.exe --desktop-number 0")]
		[InlineData("app assignment set --path C:\\App.exe --desktop-number 2 --desktop-name work")]
		[InlineData("app assignment remove --path C:\\App.exe --all")]
		public void AssignmentCommandsRejectMissingConflictingAndUnrelatedOptions(string args)
			=> Assert.Throws<ArgumentException>(() => CliCommand.Parse(args.Split(' ')));

		[Theory]
		[InlineData("app assignment apply")]
		[InlineData("app assignment apply --dry-run")]
		[InlineData("app assignment apply --all --path C:\\App.exe")]
		[InlineData("app assignment apply --all --desktop-number 2")]
		[InlineData("app assignment list --dry-run")]
		public void ApplyRequiresAnExplicitScope(string args)
			=> Assert.Throws<ArgumentException>(() => CliCommand.Parse(args.Split(' ')));

		[Fact]
		public void DryRunUsesTheSameApplyCommandWithAnExplicitFlag()
		{
			var command = CliCommand.Parse(new[] { "app", "assignment", "apply", "--all", "--dry-run" });
			Assert.Equal("app assignment apply", command.Operation);
			Assert.True(command.All);
			Assert.True(command.DryRun);
			Assert.Null(command.AppPath);
		}

		[Theory]
		[InlineData("--number", "2", "number")]
		[InlineData("--name", "開発 作業", "name")]
		[InlineData("--id", "569cbb93-d9c7-4ce3-b508-ef94942eb34f", "id")]
		public void SwitchPreservesExplicitSelector(string option, string value, string kind)
		{
			var command = CliCommand.Parse(new[] { "desktop", "switch", option, value });
			Assert.Equal(kind, command.TargetKind);
			Assert.Equal(value, command.TargetValue);
			Assert.False(command.Wrap);
		}

		[Theory]
		[InlineData("--next")]
		[InlineData("--previous")]
		public void RelativeSwitchCanExplicitlyWrap(string option)
		{
			var command = CliCommand.Parse(new[] { "desktop", "switch", option, "--wrap" });
			Assert.Equal(option.Substring(2), command.TargetKind);
			Assert.True(command.Wrap);
		}

		[Theory]
		[InlineData("--number", "0")]
		[InlineData("--number", "-1")]
		[InlineData("--number", "2147483648")]
		[InlineData("--name", " ")]
		[InlineData("--id", "00000000-0000-0000-0000-000000000000")]
		[InlineData("--id", "2")]
		public void InvalidSelectorsAreRejected(string option, string value)
			=> Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "switch", option, value }));

		[Theory]
		[InlineData("desktop switch --next --previous")]
		[InlineData("desktop switch --next --next")]
		[InlineData("desktop switch --number 2 --wrap")]
		[InlineData("desktop switch --name")]
		[InlineData("desktop switch --next --follow")]
		[InlineData("desktop list --next")]
		[InlineData("window move --desktop-number 2")]
		[InlineData("window list --all")]
		[InlineData("desktop remove --number 2")]
		public void ConflictingMissingAndUnknownOptionsAreRejected(string args)
			=> Assert.Throws<ArgumentException>(() => CliCommand.Parse(args.Split(' ')));

		[Fact]
		public void MoveKeepsWindowAndDesktopIdentifiersDistinctAndDoesNotFollowByDefault()
		{
			var window = Guid.NewGuid().ToString();
			var desktop = Guid.NewGuid().ToString();
			var command = CliCommand.Parse(new[] { "window", "move", "--id", window, "--desktop-id", desktop });
			Assert.Equal(window, command.WindowId);
			Assert.Equal(desktop, command.TargetValue);
			Assert.False(command.Follow);
		}

		[Fact]
		public void DesktopMutationsRequireExplicitIdentifiersAndValues()
		{
			var id = Guid.NewGuid().ToString();
			Assert.Equal("work", CliCommand.Parse(new[] { "desktop", "create", "--name", "work" }).Name);
			var rename = CliCommand.Parse(new[] { "desktop", "rename", "--id", id, "--name", "new work" });
			Assert.Equal(id, rename.TargetValue);
			Assert.Equal("new work", rename.Name);
			var clearName = CliCommand.Parse(new[] { "desktop", "rename", "--id", id, "--name", string.Empty });
			Assert.Equal(string.Empty, clearName.Name);
			var reorder = CliCommand.Parse(new[] { "desktop", "reorder", "--id", id, "--number", "2" });
			Assert.Equal(2, reorder.Number);
			var delete = CliCommand.Parse(new[] { "desktop", "delete", "--id", id });
			Assert.Equal(id, delete.TargetValue);
			var deleteByNumber = CliCommand.Parse(new[] { "desktop", "delete", "--number", "2" });
			Assert.Equal("number", deleteByNumber.TargetKind);
			Assert.Equal("2", deleteByNumber.TargetValue);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "rename", "--name", "work" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "reorder", "--id", id, "--number", "0" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "delete" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "delete", "--id", id, "--number", "2" }));
		}

		[Fact]
		public void WallpaperRequiresOneSelectorAndOneEdit()
		{
			var id = Guid.NewGuid().ToString();
			var path = CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", id, "--path", "C:\\Images\\wall.jpg" });
			Assert.Equal(id, path.TargetValue);
			Assert.Equal("C:\\Images\\wall.jpg", path.WallpaperPath);
			var position = CliCommand.Parse(new[] { "desktop", "wallpaper", "--number", "2", "--position", "fit" });
			Assert.Equal("number", position.TargetKind);
			Assert.Equal("fit", position.WallpaperPosition);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", id }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", id, "--path", "a", "--position", "fit" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "wallpaper", "--number", "0", "--path", "a" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "wallpaper", "--id", id, "--position", "invalid" }));
		}

		[Fact]
		public void PinScopeMustBeExplicit()
		{
			var id = Guid.NewGuid().ToString();
			Assert.Equal("window", CliCommand.Parse(new[] { "window", "pin", "--id", id, "--scope", "window" }).Scope);
			Assert.Equal("app", CliCommand.Parse(new[] { "window", "unpin", "--id", id, "--scope", "app" }).Scope);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "window", "pin", "--id", id }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "window", "unpin", "--id", id, "--scope", "all" }));
		}

		[Theory]
		[InlineData("task-view")]
		[InlineData("window-switch")]
		[InlineData("settings")]
		[InlineData("notification-toggle")]
		public void UiCommandsRequireNoArguments(string action)
		{
			var args = new[] { "ui", action };
			Assert.Equal("ui " + action, CliCommand.Recognize(args));
			Assert.Equal("ui " + action, CliCommand.Parse(args).Operation);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "ui", action, "--unexpected" }));
		}

		[Fact]
		public void CreateSwitchIsExplicitAndRejectsUnrelatedOptions()
		{
			Assert.False(CliCommand.Parse(new[] { "desktop", "create" }).SwitchAfterCreate);
			Assert.True(CliCommand.Parse(new[] { "desktop", "create", "--switch" }).SwitchAfterCreate);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "create", "--switch", "--switch" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "list", "--switch" }));
		}

		[Theory]
		[InlineData("--desktop-next", "next")]
		[InlineData("--desktop-previous", "previous")]
		[InlineData("--desktop-last-used", "last-used")]
		[InlineData("--desktop-new", "new")]
		public void MoveParsesRelativeAndNewDestinations(string option, string kind)
		{
			var id = Guid.NewGuid().ToString();
			var command = CliCommand.Parse(new[] { "window", "move", "--id", id, option });
			Assert.Equal(kind, command.TargetKind);
			Assert.False(command.Wrap);
			Assert.False(command.Follow);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "window", "move", "--id", id, option, "--desktop-number", "2" }));
			if (kind == "next" || kind == "previous")
				Assert.True(CliCommand.Parse(new[] { "window", "move", "--id", id, option, "--wrap" }).Wrap);
			else
				Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "window", "move", "--id", id, option, "--wrap" }));
		}

		[Fact]
		public void LastUsedSwitchDoesNotAcceptWrap()
		{
			Assert.Equal("last-used", CliCommand.Parse(new[] { "desktop", "switch", "--last-used" }).TargetKind);
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "switch", "--last-used", "--wrap" }));
		}
	}
}
