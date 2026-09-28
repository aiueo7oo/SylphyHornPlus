using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using SylphyHorn.UI.Bindings;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[Collection(PlacementUiCollection.Name)]
	public sealed class PlacementApplyViewModelTests
	{
		[Fact]
		public async Task PreviewDoesNotApplyOrPreselectAndExcludedRowsCannotBeChecked()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(3, f.Model.Rows.Count);
				Assert.All(f.Model.Rows, row => Assert.False(row.Selected));
				Assert.Equal(0, f.Session.ApplyCalls);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				var excluded = f.Model.Rows[2];
				excluded.Selected = true;
				Assert.False(excluded.Selected);
				Assert.False(excluded.Selectable);
				Assert.DoesNotContain(A.ToString(), f.Model.Rows[0].Source);
				Assert.Contains("Development", f.Model.Rows[0].Target);
			}
		}

		[Fact]
		public async Task OnlyCheckedWindowsAreSentAndResultsRequireFreshPreviewBeforeReuse()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				var row = f.Model.Rows[1];
				row.Selected = true;
				await f.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(new[] { row.Item.Id }, f.Session.Selection);
				Assert.Equal(f.Model.Text["OutcomeMoved"], row.Result);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.All(f.Model.Rows, item => Assert.False(item.Selectable));
				await f.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(1, f.Session.ApplyCalls);
				await f.Model.RefreshCommand.ExecuteAsync(null);
				Assert.All(f.Model.Rows, item => Assert.False(item.Selected));
			}
		}

		[Fact]
		public async Task CancellingInFlightApplyRetainsConfirmedAndCancelledResults()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				foreach (var row in f.Model.Rows.Take(2)) row.Selected = true;
				var gate = new TaskCompletionSource<PlacementResult[]>();
				f.Session.ApplyPending = gate.Task;
				var applying = f.Model.ApplyCommand.ExecuteAsync(null);
				Assert.True(f.Model.IsBusy);
				Assert.False(f.Model.RefreshCommand.CanExecute(null));
				f.Model.StopCommand.Execute(null);
				Assert.True(f.Session.Token.IsCancellationRequested);
				gate.SetResult(new[] { f.Session.Result(0, PlacementOutcome.Moved), f.Session.Result(1, PlacementOutcome.Cancelled) });
				await applying;
				Assert.Equal(f.Model.Text["OutcomeMoved"], f.Model.Rows[0].Result);
				Assert.Equal(f.Model.Text["OutcomeCancelled"], f.Model.Rows[1].Result);
				Assert.False(f.Model.IsBusy);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
			}
		}

		[Fact]
		public async Task ClosingDuringApplyCancelsPendingWorkAndDoesNotPublishLateSuccess()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				var row = f.Model.Rows[0];
				row.Selected = true;
				var gate = new TaskCompletionSource<PlacementResult[]>();
				f.Session.ApplyPending = gate.Task;
				var applying = f.Model.ApplyCommand.ExecuteAsync(null);
				f.Model.Dispose();
				Assert.True(f.Session.Token.IsCancellationRequested);
				gate.SetResult(new[] { f.Session.Result(0, PlacementOutcome.Moved) });
				await applying;
				Assert.NotEqual(f.Model.Text["OutcomeMoved"], row.Result);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.False(f.Model.RefreshCommand.CanExecute(null));
			}
		}

		[Fact]
		public async Task ExpiredOrFailedApplicationIsNotReportedAsSuccessOrRetried()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				f.Model.Rows[0].Selected = true;
				f.Session.ApplyPending = Task.FromException<PlacementResult[]>(new InvalidOperationException("expired"));
				await f.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(f.Model.Text["ApplyFailed"], f.Model.Status);
				Assert.Equal(f.Model.Text["OutcomeUnconfirmed"], f.Model.Rows[0].Result);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(1, f.Session.ApplyCalls);
			}
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task ClosedOrChangedPreviewCannotPublishLateRows(bool stateChanged)
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				var gate = new TaskCompletionSource<PlacementPreview>();
				f.Session.PreviewPending = gate.Task;
				var loading = f.Model.RefreshCommand.ExecuteAsync(null);
				if (stateChanged)
				{
					f.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Renamed", ""), Entry(B, 1, "Other", "")));
					f.Harness.Owner.Drain();
				}
				else
				{
					f.Model.Dispose();
				}
				Assert.True(f.Session.Token.IsCancellationRequested);
				gate.SetResult(f.Session.Preview);
				await loading;
				Assert.Empty(f.Model.Rows);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(0, f.Session.ApplyCalls);
			}
		}

		[Fact]
		public async Task StoppedMonitoringInvalidatesSelectionAndExplainsWhy()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				await f.Model.RefreshCommand.ExecuteAsync(null);
				f.Model.Rows[0].Selected = true;
				await f.Harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
				f.Model.RefreshStatus();
				Assert.Equal(f.Model.Text["ApplyDisabled"], f.Model.Status);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.False(f.Model.RefreshCommand.CanExecute(null));
				Assert.All(f.Model.Rows, row => Assert.False(row.Selectable));
				Assert.Equal(0, f.Session.ApplyCalls);
			}
		}

		[Fact]
		public async Task EmptyAndFailedPreviewHaveDistinctMessagesAndCannotApply()
		{
			using (var f = await PlacementApplyFixture.Create())
			{
				f.Session.PreviewPending = Task.FromResult(new PlacementPreview(Array.Empty<PlacementPreviewItem>(), 0));
				await f.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(f.Model.Text["ApplyEmpty"], f.Model.Status);
				f.Session.PreviewPending = Task.FromException<PlacementPreview>(new TimeoutException());
				await f.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(f.Model.Text["ApplyQueryFailed"], f.Model.Status);
				Assert.False(f.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(0, f.Session.ApplyCalls);
			}
		}
	}

	internal sealed class PlacementApplyFixture : IDisposable
	{
		internal Harness Harness;
		internal PlacementApplyViewModel Model;
		internal readonly PlacementApplySession Session = new PlacementApplySession();

		internal static async Task<PlacementApplyFixture> Create()
		{
			var f = new PlacementApplyFixture();
			f.Harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "Web", ""), Entry(B, 1, "Development", "")), new Factory(f.Session));
			await f.Harness.Runtime.InitializeAsync(cancellationToken: CancellationToken.None);
			await f.Harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, f.Session.Preview.Items.Select(item => item.Rule).GroupBy(rule => rule.Id).Select(group => group.First())));
			f.Model = new PlacementApplyViewModel(f.Harness.Runtime);
			return f;
		}

		public void Dispose()
		{
			this.Model.Dispose();
			this.Session.StopAsync();
		}

		private sealed class Factory : IPlacementSessionFactory
		{
			private readonly PlacementApplySession _session;

			internal Factory(PlacementApplySession session) => this._session = session;

			public IPlacementSession Start(
				AppPlacementConfiguration configuration,
				Func<PlacementDestination, bool, CancellationToken, Task<PlacementAuthorization>> authorize,
				PlacementHistory history,
				Func<PlacementOccupancyObservation, CancellationToken, Task<bool>> closeDesktops = null) => this._session;
		}
	}

	internal sealed class PlacementApplySession : IPlacementSession
	{
		private readonly TaskCompletionSource<bool> _end = new TaskCompletionSource<bool>();
		internal readonly PlacementPreview Preview;
		internal Task<PlacementPreview> PreviewPending;
		internal Task<PlacementResult[]> ApplyPending;
		internal Task<PlacementRuleApplication> RuleApplicationPending { get; set; }
		internal Guid[] Selection;
		internal int ApplyCalls;
		internal CancellationToken Token;
		internal Func<bool, PlacementRuleApplication> RuleApplication { get; set; }
		internal PlacementAppIdentity RequestedApp { get; private set; }

		internal PlacementApplySession()
		{
			var process = new PlacementProcessIdentity(42, 1, @"C:\Applications\Editor\Editor.exe", null, null);
			var app = new PlacementAppIdentity(PlacementAppKind.ExecutablePath, process.Path);
			var rule = new AppPlacementRule(Guid.NewGuid(), true, app, PlacementDestination.ByName("Development"), "Editor", process.Path);
			this.Preview = new PlacementPreview(
				Enumerable.Range(1, 3).Select(n => new PlacementPreviewItem(
					new PlacementCandidate(new IntPtr(n), Guid.NewGuid(), n, 0),
					new PlacementWindowIdentity(new IntPtr(n), 1, process, process, app),
					rule,
					n == 1 ? "Project notes — Editor" : n == 2 ? "README.md — Editor" : "Settings — Editor",
					n == 3 ? B : A,
					B,
					n == 3 ? PlacementOutcome.AlreadyPlaced : (PlacementOutcome?)null)),
				long.MaxValue);
		}

		public Task<PlacementRuleApplication> ApplyRulesAsync(PlacementDesktopMap map, PlacementAppIdentity app, bool dryRun, CancellationToken cancellation)
		{
			this.RequestedApp = app;
			return this.RuleApplicationPending ?? Task.FromResult(this.RuleApplication?.Invoke(dryRun) ?? throw new NotSupportedException());
		}

		public bool IsReady => true;

		public void DesktopChanged() { }

			public Task Completion => this._end.Task;

		public Task StopAsync()
		{
			this._end.TrySetResult(true);
			return this.Completion;
		}

		public Task<PlacementPreview> PreviewAsync(PlacementDesktopMap map, CancellationToken cancellation)
		{
			this.Token = cancellation;
			return this.PreviewPending ?? Task.FromResult(this.Preview);
		}

		public Task<PlacementResult[]> ApplyAsync(PlacementPreview preview, Guid[] selection, CancellationToken cancellation)
		{
			this.ApplyCalls++;
			this.Selection = selection;
			this.Token = cancellation;
			return this.ApplyPending ?? Task.FromResult(preview.Items.Where(item => selection.Contains(item.Id)).Select(item => new PlacementResult(item.Candidate.Window, item.Rule.Id, PlacementOutcome.Moved, null)).ToArray());
		}

		internal PlacementResult Result(int index, PlacementOutcome outcome) => new PlacementResult(this.Preview.Items[index].Candidate.Window, this.Preview.Items[index].Rule.Id, outcome, null);
	}
}
