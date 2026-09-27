using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliSpecTests
	{
		[Fact]
		public async Task EveryCommandHasAnOfflineSerializableSpecWithValidExamplesAndReadOnlyQueries()
		{
			var overview = await CliSpecService.ExecuteAsync(new[] { "spec" }, NeverQuery);
			Assert.True(overview.Success);
			Assert.Equal(53, overview.Data.Commands.Length);
			var allowedQueries = new[] { "desktop list", "desktop settings", "window list", "app list", "app assignment list",
				"app assignment status", "desktop autoclose list", "monitor list", "notification settings", "tray settings",
				"desktop creation wallpaper list", "settings get", "startup status", "shortcut list", "shortcut keys" };
			foreach (var entry in overview.Data.Commands)
			{
				Assert.Null(entry.Arguments);
				var response = await CliSpecService.ExecuteAsync(new[] { "spec" }.Concat(entry.Name.Split(' ')).ToArray(), NeverQuery);
				Assert.True(response.Success);
				Assert.Equal(entry.Name, response.Data.Target);
				var spec = response.Data.Specification;
				Assert.NotEmpty(spec.Prerequisites);
				Assert.NotEmpty(spec.Effects);
				Assert.Contains(response.Data.Errors, item => item.Code == "launcher_failure");
				Assert.Contains(response.Data.Errors, item => item.Code == "response_too_large");
				if (entry.Name == "app assignment apply") Assert.DoesNotContain("possible-create", spec.Effects);
				Assert.Equal(spec.Arguments.Length, spec.Arguments.Select(argument => argument.Name).Distinct().Count());
				Assert.Contains(response.Data.ResultSchema, type => type.Name == "CliData");
				foreach (var example in spec.Examples)
				{
					var args = Substitute(example);
					Assert.Equal(spec.Name, CliCommand.Parse(args).Operation);
					foreach (var argument in spec.Arguments.Where(argument => argument.Required))
					{
						var index = Array.IndexOf(args, argument.Name);
						Assert.True(index >= 0, spec.Name + ": " + argument.Name);
						var missing = args.Take(index).Concat(args.Skip(index + (argument.Type == "flag" ? 1 : 2))).ToArray();
						Assert.Throws<ArgumentException>(() => CliCommand.Parse(missing));
					}
				}
				foreach (var query in spec.Queries)
					Assert.Contains(CliCommand.Parse(query).Operation, allowedQueries);
				var json = Encoding.UTF8.GetString(CliProtocol.Serialize(response));
				Assert.Contains("\"specification\"", json);
				Assert.Contains("\"success\":true", json);
			}
		}

		[Fact]
		public async Task EnumsAndMutuallyExclusiveSelectorsMatchTheParser()
		{
			foreach (var spec in CliSpecCatalog.All)
			{
				var baseline = Substitute(spec.Examples[0]).ToList();
				foreach (var argument in spec.Arguments.Where(argument => argument.Values != null))
				{
					foreach (var value in argument.Values)
					{
						var args = baseline.ToList();
						var index = args.IndexOf(argument.Name);
						if (index < 0)
						{
							args.Add(argument.Name);
							args.Add(value);
						}
						else args[index + 1] = value;
						Assert.Equal(spec.Name, CliCommand.Parse(args.ToArray()).Operation);
					}
				}
			}
			var response = await CliSpecService.ExecuteAsync(new[] { "spec", "desktop", "switch" }, NeverQuery);
			Assert.Contains(response.Data.Specification.Constraints, item => item.Kind == "exactlyOne" && item.Arguments.Contains("--name"));
			Assert.Contains(response.Data.Specification.Constraints, item => item.WhenPresent == "--wrap" && item.Arguments.Contains("--previous"));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "switch", "--name", "Work", "--number", "2" }));
			Assert.Throws<ArgumentException>(() => CliCommand.Parse(new[] { "desktop", "switch", "--number", "2", "--wrap" }));
			Assert.All(CliSpecCatalog.Find("desktop configure").Arguments, item => Assert.Equal("preserve-current", item.Omission));
		}

		[Fact]
		public async Task ResolutionReturnsSourceIdsAndPreservesPartialFailures()
		{
			var queries = new List<string>();
			var response = await CliSpecService.ExecuteAsync(new[] { "spec", "window", "move", "--resolve" }, args =>
			{
				var command = CliCommand.Recognize(args);
				queries.Add(command);
				return Task.FromResult(command == "window list"
					? CliResponse.Ok(command, new CliData { Windows = new[] { new CliWindow { Id = "observed-id" } }, Complete = true })
					: CliResponse.Fail(command, "state_unavailable", "synthetic"));
			});
			Assert.True(response.Success);
			Assert.Equal(new[] { "window list", "desktop list" }, queries);
			Assert.Equal("partial", response.Data.Resolution.Status);
			Assert.Equal("observed-id", response.Data.Resolution.Sources[0].Response.Data.Windows[0].Id);
			Assert.Contains("observed-id", Encoding.UTF8.GetString(CliProtocol.Serialize(response)));
			Assert.Equal("state_unavailable", response.Data.Resolution.Sources[1].Response.Error.Code);
			Assert.NotNull(response.Data.Specification);
		}

		[Fact]
		public async Task MissingHostIsNotEmptyCandidatesAndStopsRepeatedConnectionAttempts()
		{
			var calls = 0;
			var response = await CliSpecService.ExecuteAsync(new[] { "spec", "window", "move", "--resolve" }, args =>
			{
				calls++;
				return Task.FromResult(CliResponse.Fail(CliCommand.Recognize(args), "host_unavailable", "synthetic"));
			});
			Assert.True(response.Success);
			Assert.Equal(1, calls);
			Assert.Equal("unavailable", response.Data.Resolution.Status);
			Assert.NotNull(response.Data.Specification);
			var staticOnly = await CliSpecService.ExecuteAsync(new[] { "spec", "settings", "reset", "--resolve" }, NeverQuery);
			Assert.Equal("not-applicable", staticOnly.Data.Resolution.Status);
		}

		[Fact]
		public async Task IncompleteWindowEnumerationIsReportedAsPartial()
		{
			var response = await CliSpecService.ExecuteAsync(new[] { "spec", "window", "list", "--resolve" },
				args => Task.FromResult(CliResponse.Ok("window list", new CliData
				{
					Windows = Array.Empty<CliWindow>(), Complete = false, UnavailableCount = 1,
				})));
			Assert.Equal("partial", response.Data.Resolution.Status);
			Assert.Equal(1, response.Data.Resolution.Sources[0].Response.Data.UnavailableCount);
		}

		[Theory]
		[InlineData("spec --resolve")]
		[InlineData("spec desktop switch --number 2")]
		[InlineData("spec desktop unknown")]
		[InlineData("spec desktop switch --resolve --resolve")]
		public async Task InvalidSpecRequestsNeverReachHost(string input)
		{
			var response = await CliSpecService.ExecuteAsync(input.Split(' '), NeverQuery);
			Assert.False(response.Success);
			Assert.Equal("invalid_arguments", response.Error.Code);
			Assert.Equal(2, response.ExitCode);
		}

		private static Task<CliResponse> NeverQuery(string[] args) => throw new InvalidOperationException("Offline spec must not query the host.");

		private static string[] Substitute(string[] args)
			=> args.Select(arg => arg.StartsWith("<", StringComparison.Ordinal) ? "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" : arg).ToArray();
	}
}
