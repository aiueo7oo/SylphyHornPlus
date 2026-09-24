#if !NETFRAMEWORK
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Services.AppPlacement;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		internal async Task<CliResponse> ApplyCliAssignmentsAsync(CliCommand command, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			var submitted = false;
			try
			{
				PlacementAppIdentity app = null;
				if (command.AppPath != null)
				{
					try { app = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, command.AppPath); }
					catch (System.Runtime.Serialization.SerializationException)
					{
						return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a valid absolute executable path.");
					}
				}
				await this.RefreshCliStateAsync(cancellation);
				if (command.RuleId != null)
				{
					var id = Guid.Parse(command.RuleId);
					var rule = this._placementConfiguration.Rules.SingleOrDefault(item => item.Id == id);
					if (rule == null) return CliResponse.Fail(command.Operation, "assignment_not_found", "The saved rule no longer exists.");
					if (!rule.Enabled) return CliResponse.Fail(command.Operation, "assignment_disabled", "The selected rule is disabled.");
					app = rule.App;
				}
				if (this.PlacementStatus != "Active")
					return CliResponse.Fail(command.Operation, "assignment_unavailable", "Automatic app placement must be enabled and monitoring.");
				if (app != null && this._placementConfiguration.FindEnabledRule(app) == null)
					return CliResponse.Fail(command.Operation, "assignment_not_found", "No enabled assignment matches this executable path.");
				foreach (var key in this._cliWindows.Where(pair => pair.Value.ExpiresAt < DateTime.UtcNow).Select(pair => pair.Key).ToArray())
					this._cliWindows.Remove(key);
				if (this._cliWindows.Count > 4096 - 256)
					return CliResponse.Fail(command.Operation, "host_busy", "Too many unexpired window identifiers. Retry after they expire.", true);
				submitted = !command.DryRun;
				var application = await this._placementSession.ApplyRulesAsync(this.PlacementDestinations, app, command.DryRun, cancellation);
				var results = application.Preview.Items.Select(item =>
				{
					var previous = this._cliWindows.FirstOrDefault(pair => pair.Value.Identity.SameInstance(item.Identity));
					var key = previous.Value == null ? Guid.NewGuid() : previous.Key;
					this._cliWindows[key] = new CliWindowEntry { Identity = item.Identity, ExpiresAt = DateTime.UtcNow.AddMinutes(5) };
					var result = application.Results.FirstOrDefault(value => value.Window == item.Candidate.Window && value.Rule == item.Rule.Id);
					return new CliAssignmentResult
					{
						WindowId = key.ToString(),
						RuleId = item.Rule.Id.ToString(),
						Title = item.Title,
						SourceDesktopId = item.Source == Guid.Empty ? null : item.Source.ToString(),
						TargetDesktopId = item.Target?.ToString(),
						CanApply = item.CanApply,
						Outcome = result != null ? CliPlacementOutcome(result.Outcome)
							: item.Excluded.HasValue ? CliPlacementOutcome(item.Excluded.Value) : "would_move",
					};
				}).ToArray();
				if (!command.DryRun && results.Any(result => result.Outcome != "moved" && result.Outcome != "already_placed" && result.Outcome != "excluded"))
				{
					var unconfirmed = results.Any(result => result.Outcome == "unconfirmed");
					var response = CliResponse.Fail(command.Operation, unconfirmed ? "result_unconfirmed" : "assignment_failed",
						"Some assignments could not be applied. Inspect individual results before retrying.");
					response.Error.Results = results;
					return response;
				}
				return CliResponse.Ok(command.Operation, new CliData
				{
					DryRun = command.DryRun,
					Changed = results.Any(result => result.Outcome == "moved"),
					Results = results,
				});
			}
			catch (CliFailure ex)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : ex.Code, ex.Message, !submitted && ex.Retryable);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled",
					"The request expired. Query current state before retrying.");
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("Cli.Assignments", ex.GetType()));
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "operation_failed",
					"Assignments could not be evaluated or their results could not be confirmed.");
			}
		}

		private static string CliPlacementOutcome(PlacementOutcome outcome)
		{
			switch (outcome)
			{
				case PlacementOutcome.AlreadyPlaced: return "already_placed";
				case PlacementOutcome.NoRule: return "no_rule";
				case PlacementOutcome.TimedOut: return "timed_out";
				case PlacementOutcome.DestinationUnavailable: return "destination_unavailable";
				case PlacementOutcome.MoveFailed: return "move_failed";
				case PlacementOutcome.MonitorPaused: return "monitor_paused";
				default: return outcome.ToString().ToLowerInvariant();
			}
		}
	}
}
#endif
