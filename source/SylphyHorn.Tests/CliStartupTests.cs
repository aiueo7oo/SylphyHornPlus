#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;
using SylphyHorn.Services.Commands;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class CliStartupTests
	{
		[Theory]
		[InlineData(false, true, "normal")]
		[InlineData(true, false, "elevated")]
		[InlineData(true, true, "disabled")]
		public async Task ModeChangesAreExclusiveAndRepeatedRequestsAreIdempotent(bool normal, bool elevated, string mode)
		{
			var registration = new Registration { Normal = normal, Elevated = elevated, Administrator = true };
			var service = new CliStartupService(registration, () => true, _ => { });
			var response = await Run(service, mode);
			Assert.True(response.Success);
			Assert.Equal(mode, response.Data.Startup.Mode);
			if (mode == "normal") Assert.Equal(new[] { "normal+", "task-" }, registration.Calls);
			if (mode == "elevated") Assert.Equal(new[] { "task+", "normal-" }, registration.Calls);
			if (mode == "disabled") Assert.Equal(new[] { "task-", "normal-" }, registration.Calls);
			var count = registration.Calls.Count;
			Assert.False((await Run(service, mode)).Data.Changed);
			Assert.Equal(count, registration.Calls.Count);
		}

		[Fact]
		public async Task InsufficientPrivilegeOrUnexpectedTargetDoesNotChangeEitherRegistration()
		{
			var registration = new Registration { Elevated = true };
			var service = new CliStartupService(registration, () => true, _ => { });
			Assert.Equal("elevation_required", (await Run(service, "normal")).Error.Code);
			Assert.Empty(registration.Calls);
			registration.Administrator = true;
			registration.Matches = false;
			Assert.Equal("startup_target_mismatch", (await Run(service, "disabled")).Error.Code);
			Assert.Empty(registration.Calls);
		}

		[Fact]
		public async Task FailedTransitionPreservesOldRegistrationAndDoesNotReportSuccess()
		{
			var registration = new Registration { Normal = true, Administrator = true, FailTask = true };
			var service = new CliStartupService(registration, () => true, _ => { });
			Assert.Equal("result_unconfirmed", (await Run(service, "elevated")).Error.Code);
			Assert.True(registration.Normal);
			Assert.Equal(new[] { "task+" }, registration.Calls);
			registration.FailRead = true;
			var status = await service.ExecuteAsync(CliCommand.Parse(new[] { "startup", "status" }), CancellationToken.None);
			Assert.Equal("startup_query_failed", status.Error.Code);
			Assert.False(status.Success);
		}

		[Fact]
		public async Task BusyAndCancelledChangesDoNotWriteAndStatusCanReportMixedState()
		{
			var registration = new Registration { Normal = true, Elevated = true };
			var service = new CliStartupService(registration, () => false, _ => { });
			Assert.Equal("host_busy", (await Run(service, "disabled")).Error.Code);
			Assert.Empty(registration.Calls);
			var status = await service.ExecuteAsync(CliCommand.Parse(new[] { "startup", "status" }), CancellationToken.None);
			Assert.Equal("mixed", status.Data.Startup.Mode);
			var cancelled = await service.ExecuteAsync(CliCommand.Parse(new[] { "startup", "configure", "--mode", "disabled" }), new CancellationToken(true));
			Assert.Equal("request_cancelled", cancelled.Error.Code);
			Assert.Empty(registration.Calls);
		}

		private static Task<CliResponse> Run(CliStartupService service, string mode)
			=> service.ExecuteAsync(CliCommand.Parse(new[] { "startup", "configure", "--mode", mode }), CancellationToken.None);

		private sealed class Registration : ICliStartupRegistration
		{
			internal bool Normal;
			internal bool Elevated;
			internal bool Administrator;
			internal bool Matches = true;
			internal bool FailTask;
			internal bool FailRead;
			internal List<string> Calls { get; } = new List<string>();

			public CliStartup Read()
			{
				if (this.FailRead) throw new InvalidOperationException();
				return new CliStartup
				{
					NormalRegistered = this.Normal, ElevatedRegistered = this.Elevated,
					Administrator = this.Administrator, TargetMatches = this.Matches,
					Mode = this.Normal ? this.Elevated ? "mixed" : "normal" : this.Elevated ? "elevated" : "disabled",
				};
			}

			public void CreateNormal() { this.Calls.Add("normal+"); this.Normal = true; }
			public void RemoveNormal() { this.Calls.Add("normal-"); this.Normal = false; }
			public Task SetElevatedAsync(bool enabled, CancellationToken cancellation)
			{
				this.Calls.Add(enabled ? "task+" : "task-");
				if (this.FailTask) throw new InvalidOperationException();
				this.Elevated = enabled;
				return Task.CompletedTask;
			}
		}
	}
}
#endif
