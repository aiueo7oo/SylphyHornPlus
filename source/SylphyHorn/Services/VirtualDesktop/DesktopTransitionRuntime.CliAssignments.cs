#if !NETFRAMEWORK
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Commands;
using SylphyHorn.Services.AppPlacement;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal sealed partial class DesktopTransitionRuntime
	{
		// Assignment results register window IDs without the listing limit, so keep headroom below it.
		private const int CliAssignmentWindowHeadroom = 256;

		internal async Task<CliResponse> ResumeCliPlacementAsync(CliCommand command, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			var submitted = false;
			try
			{
				this.EnsureCliAvailable(cancellation);
				var status = this.PlacementStatus;
				if (status == PlacementStatuses.Stopping || status == PlacementStatuses.Suspended)
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Placement is being stopped or suspended.", retryable: true);
				}
				if (status == PlacementStatuses.Disabled || status == PlacementStatuses.NoRules)
				{
					return CliResponse.Fail(command.Operation, "assignment_unavailable",
						"Enable automatic placement and configure an enabled rule or closing target first.");
				}
				if (status == PlacementStatuses.Paused)
				{
					submitted = true;
					await this.RestartPlacementAsync().WaitAsync(cancellation);
					this.EnsureCliAvailable(cancellation);
					status = this.PlacementStatus;
					if (status != PlacementStatuses.Active && status != PlacementStatuses.Preparing)
					{
						return CliResponse.Fail(command.Operation, "assignment_unavailable", "Monitoring did not resume. Read assignment status and logs before retrying.");
					}
				}
				return CliResponse.Ok(command.Operation, new CliData { Changed = submitted, AssignmentStatus = status.ToLowerInvariant() });
			}
			catch (CliFailure failure)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : failure.Code, failure.Message, !submitted && failure.Retryable);
			}
			catch (OperationCanceledException)
			{
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "request_cancelled", "Read assignment status before retrying.");
			}
			catch (Exception ex)
			{
				this.ReportFault(new DesktopRuntimeFault("Cli.ResumePlacement", ex.GetType()));
				return CliResponse.Fail(command.Operation, submitted ? "result_unconfirmed" : "operation_failed", "Monitoring could not be resumed.");
			}
		}

		internal async Task<CliResponse> ApplyCliAssignmentsAsync(CliCommand command, CancellationToken cancellation)
		{
			this.EnsureOwnerAccess();
			var submitted = false;
			try
			{
				PlacementAppIdentity app = null;
				if (command.AppPath != null)
				{
					try
					{
						app = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, command.AppPath);
					}
					catch (SerializationException)
					{
						return CliResponse.Fail(command.Operation, "invalid_arguments", "Specify a valid absolute executable path.");
					}
				}
				await this.RefreshCliStateAsync(cancellation);
				if (command.RuleId != null)
				{
					var id = Guid.Parse(command.RuleId);
					var rule = this._placementConfiguration.Rules.SingleOrDefault(item => item.Id == id);
					if (rule == null)
					{
						return CliResponse.Fail(command.Operation, "assignment_not_found", "The saved rule no longer exists.");
					}
					if (!rule.Enabled)
					{
						return CliResponse.Fail(command.Operation, "assignment_disabled", "The selected rule is disabled.");
					}
					app = rule.App;
				}
				if (this.PlacementStatus != PlacementStatuses.Active)
				{
					return CliResponse.Fail(command.Operation, "assignment_unavailable", "Automatic app placement must be enabled and monitoring.");
				}
				if (app != null && this._placementConfiguration.FindEnabledRule(app) == null)
				{
					return CliResponse.Fail(command.Operation, "assignment_not_found", "No enabled assignment matches this executable path.");
				}
				this.RemoveExpiredCliWindows();
				if (this._cliWindows.Count > CliWindowLimit - CliAssignmentWindowHeadroom)
				{
					return CliResponse.Fail(command.Operation, "host_busy", "Too many unexpired window identifiers. Retry after they expire.", retryable: true);
				}
				var request = this._placementSession.ApplyRulesAsync(this.PlacementDestinations, app, command.DryRun, cancellation);
				submitted = !command.DryRun;
				var application = await request;
				var results = application.Preview.Items.Select(item => this.CliAssignmentInfo(item, application)).ToArray();
				if (!command.DryRun && results.Any(result => !IsSettledCliAssignment(result.Outcome)))
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
			catch (PlacementRequestRejectedException ex) when (!submitted)
			{
				return CliResponse.Fail(command.Operation, "host_busy", ex.Message, retryable: true);
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

		private CliAssignmentResult CliAssignmentInfo(PlacementPreviewItem item, PlacementRuleApplication application)
		{
			var key = this.RegisterCliWindow(item.Identity, this.FindCliWindowKey(item.Identity));
			var applied = application.Results.FirstOrDefault(result => result.Window == item.Candidate.Window && result.Rule == item.Rule.Id);
			return new CliAssignmentResult
			{
				WindowId = key.ToString(),
				RuleId = item.Rule.Id.ToString(),
				Title = item.Title,
				SourceDesktopId = item.Source == Guid.Empty ? null : item.Source.ToString(),
				TargetDesktopId = item.Target?.ToString(),
				CanApply = item.CanApply,
				Outcome = CliAssignmentOutcome(item, applied),
			};
		}

		private static string CliAssignmentOutcome(PlacementPreviewItem item, PlacementResult applied)
		{
			if (applied != null) return CliPlacementOutcome(applied.Outcome);
			if (item.Excluded.HasValue) return CliPlacementOutcome(item.Excluded.Value);
			return "would_move";
		}

		private static bool IsSettledCliAssignment(string outcome)
			=> outcome == "moved" || outcome == "already_placed" || outcome == "excluded";

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
