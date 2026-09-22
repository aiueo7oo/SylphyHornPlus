using System;
using SylphyHorn.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliCommandTests
	{
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
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "rename", "--name", "work" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "reorder", "--id", id, "--number", "0" }));
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
	}
}
