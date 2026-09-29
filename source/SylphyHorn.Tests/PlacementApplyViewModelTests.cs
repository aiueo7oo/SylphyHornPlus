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
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(3, fixture.Model.Rows.Count);
				Assert.All(fixture.Model.Rows, row => Assert.False(row.Selected));
				Assert.Equal(0, fixture.Session.ApplyCalls);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				var excluded = fixture.Model.Rows[2];
				excluded.Selected = true;
				Assert.False(excluded.Selected);
				Assert.False(excluded.Selectable);
				Assert.DoesNotContain(A.ToString(), fixture.Model.Rows[0].Source);
				Assert.Contains("Development", fixture.Model.Rows[0].Target);
			}
		}

		[Fact]
		public async Task OnlyCheckedWindowsAreSentAndResultsRequireFreshPreviewBeforeReuse()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				var row = fixture.Model.Rows[1];
				row.Selected = true;
				await fixture.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(new[] { row.Item.Id }, fixture.Session.Selection);
				Assert.Equal(fixture.Model.Text["OutcomeMoved"], row.Result);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.All(fixture.Model.Rows, item => Assert.False(item.Selectable));
				await fixture.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(1, fixture.Session.ApplyCalls);
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				Assert.All(fixture.Model.Rows, item => Assert.False(item.Selected));
			}
		}

		[Fact]
		public async Task CancellingInFlightApplyRetainsConfirmedAndCancelledResults()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				foreach (var row in fixture.Model.Rows.Take(2))
				{
					row.Selected = true;
				}
				var gate = new TaskCompletionSource<PlacementResult[]>();
				fixture.Session.ApplyPending = gate.Task;
				var applying = fixture.Model.ApplyCommand.ExecuteAsync(null);
				Assert.True(fixture.Model.IsBusy);
				Assert.False(fixture.Model.RefreshCommand.CanExecute(null));
				fixture.Model.StopCommand.Execute(null);
				Assert.True(fixture.Session.Token.IsCancellationRequested);
				gate.SetResult(new[] { fixture.Session.Result(0, PlacementOutcome.Moved), fixture.Session.Result(1, PlacementOutcome.Cancelled) });
				await applying;
				Assert.Equal(fixture.Model.Text["OutcomeMoved"], fixture.Model.Rows[0].Result);
				Assert.Equal(fixture.Model.Text["OutcomeCancelled"], fixture.Model.Rows[1].Result);
				Assert.False(fixture.Model.IsBusy);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
			}
		}

		[Fact]
		public async Task ClosingDuringApplyCancelsPendingWorkAndDoesNotPublishLateSuccess()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				var row = fixture.Model.Rows[0];
				row.Selected = true;
				var gate = new TaskCompletionSource<PlacementResult[]>();
				fixture.Session.ApplyPending = gate.Task;
				var applying = fixture.Model.ApplyCommand.ExecuteAsync(null);
				fixture.Model.Dispose();
				Assert.True(fixture.Session.Token.IsCancellationRequested);
				gate.SetResult(new[] { fixture.Session.Result(0, PlacementOutcome.Moved) });
				await applying;
				Assert.NotEqual(fixture.Model.Text["OutcomeMoved"], row.Result);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.False(fixture.Model.RefreshCommand.CanExecute(null));
			}
		}

		[Fact]
		public async Task ExpiredOrFailedApplicationIsNotReportedAsSuccessOrRetried()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				fixture.Model.Rows[0].Selected = true;
				fixture.Session.ApplyPending = Task.FromException<PlacementResult[]>(new InvalidOperationException("expired"));
				await fixture.Model.ApplyCommand.ExecuteAsync(null);
				Assert.Equal(fixture.Model.Text["ApplyFailed"], fixture.Model.Status);
				Assert.Equal(fixture.Model.Text["OutcomeUnconfirmed"], fixture.Model.Rows[0].Result);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(1, fixture.Session.ApplyCalls);
			}
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task ClosedOrChangedPreviewCannotPublishLateRows(bool stateChanged)
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				var gate = new TaskCompletionSource<PlacementPreview>();
				fixture.Session.PreviewPending = gate.Task;
				var loading = fixture.Model.RefreshCommand.ExecuteAsync(null);
				if (stateChanged)
				{
					fixture.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Renamed", ""), Entry(B, 1, "Other", "")));
					fixture.Harness.Owner.Drain();
				}
				else
				{
					fixture.Model.Dispose();
				}
				Assert.True(fixture.Session.Token.IsCancellationRequested);
				gate.SetResult(fixture.Session.Preview);
				await loading;
				Assert.Empty(fixture.Model.Rows);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(0, fixture.Session.ApplyCalls);
			}
		}

		[Fact]
		public async Task StoppedMonitoringInvalidatesSelectionAndExplainsWhy()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				fixture.Model.Rows[0].Selected = true;
				await fixture.Harness.Runtime.ConfigurePlacementAsync(AppPlacementConfiguration.Empty);
				fixture.Model.RefreshStatus();
				Assert.Equal(fixture.Model.Text["ApplyDisabled"], fixture.Model.Status);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.False(fixture.Model.RefreshCommand.CanExecute(null));
				Assert.All(fixture.Model.Rows, row => Assert.False(row.Selectable));
				Assert.Equal(0, fixture.Session.ApplyCalls);
			}
		}

		[Fact]
		public async Task EmptyAndFailedPreviewHaveDistinctMessagesAndCannotApply()
		{
			using (var fixture = await PlacementApplyFixture.Create())
			{
				fixture.Session.PreviewPending = Task.FromResult(new PlacementPreview(Array.Empty<PlacementPreviewItem>(), 0));
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(fixture.Model.Text["ApplyEmpty"], fixture.Model.Status);
				fixture.Session.PreviewPending = Task.FromException<PlacementPreview>(new TimeoutException());
				await fixture.Model.RefreshCommand.ExecuteAsync(null);
				Assert.Equal(fixture.Model.Text["ApplyQueryFailed"], fixture.Model.Status);
				Assert.False(fixture.Model.ApplyCommand.CanExecute(null));
				Assert.Equal(0, fixture.Session.ApplyCalls);
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
			var fixture = new PlacementApplyFixture();
			fixture.Harness = Harness.Create(Batch(1, 1, A, Entry(A, 0, "Web", ""), Entry(B, 1, "Development", "")), new Factory(fixture.Session));
			await fixture.Harness.Runtime.InitializeAsync(cancellationToken: CancellationToken.None);
			await fixture.Harness.Runtime.ConfigurePlacementAsync(new AppPlacementConfiguration(true, fixture.Session.Preview.Items.Select(item => item.Rule).GroupBy(rule => rule.Id).Select(group => group.First())));
			fixture.Model = new PlacementApplyViewModel(fixture.Harness.Runtime);
			return fixture;
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
			PlacementPreviewItem EditorWindow(int handle, string title, Guid source, PlacementOutcome? excluded) => new PlacementPreviewItem(
				new PlacementCandidate(new IntPtr(handle), Guid.NewGuid(), handle, 0),
				new PlacementWindowIdentity(new IntPtr(handle), 1, process, process, app),
				rule,
				title,
				source,
				B,
				excluded);
			this.Preview = new PlacementPreview(
				new[]
				{
					EditorWindow(1, "Project notes — Editor", A, null),
					EditorWindow(2, "README.md — Editor", A, null),
					EditorWindow(3, "Settings — Editor", B, PlacementOutcome.AlreadyPlaced)
				},
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
