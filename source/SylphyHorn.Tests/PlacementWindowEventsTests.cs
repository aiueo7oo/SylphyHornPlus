using System;
using System.Collections.Generic;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class PlacementWindowEventsTests
	{
		private static readonly IntPtr Window = new IntPtr(101);

		[Fact]
		public void ReentrantCallbacksPreserveCreateShowAndDestroyOrder()
		{
			var state = Running();
			PlacementWindowEventReceiver receiver = null;
			receiver = new PlacementWindowEventReceiver(value =>
			{
				if (value.Kind == PlacementWindowEventKind.Create)
				{
					receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Show, value.Window, 102));
				}
				state.Receive(value);
			}, () => state.Pause("EventCapacity"), 32);

			receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Create, Window, 101));
			state.ProcessBatch();
			var candidate = Assert.IsType<PlacementCandidate>(state.TakeCandidate());
			Assert.Equal(Window, candidate.Window);
			Assert.True(state.IsCurrent(candidate));
			receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Destroy, Window, 103));
			state.ProcessBatch();
			Assert.False(state.IsCurrent(candidate));
			Assert.Equal(0, state.TrackedCount);
		}

		[Fact]
		public void ReentrantOverflowInvalidatesTheMonitorAndDoesNotResume()
		{
			var state = Running();
			PlacementWindowEventReceiver receiver = null;
			receiver = new PlacementWindowEventReceiver(value =>
			{
				for (var time = 102; time < 106; time++)
					receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Show, value.Window, time));
				state.Receive(value);
			}, () => state.Pause("EventCapacity"), 2);

			receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Create, Window, 101));
			Assert.Equal(PlacementMonitorState.Paused, state.State);
			Assert.Equal("EventCapacity", state.PauseReason);
			Assert.Equal(0, state.BufferedCount);
			Assert.Equal(0, state.TrackedCount);
			receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Create, Window, 106));
			Assert.Equal(0, state.BufferedCount);
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void ReentrantDestroyDoesNotLeaveAStaleLifetimeOrCandidate()
		{
			var state = Running();
			PlacementWindowEventReceiver receiver = null;
			receiver = new PlacementWindowEventReceiver(value =>
			{
				if (value.Kind == PlacementWindowEventKind.Create)
				{
					receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Show, value.Window, 102));
					receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Destroy, value.Window, 103));
				}
				state.Receive(value);
			}, () => state.Pause("EventCapacity"), 32);

			receiver.Receive(new PlacementWindowEvent(PlacementWindowEventKind.Create, Window, 101));
			state.ProcessBatch();
			Assert.Equal(PlacementMonitorState.Running, state.State);
			Assert.Equal(0, state.TrackedCount);
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void ExplicitLifetimeAllowsBaselineButRejectsReuseAmbiguityAndPendingAutomaticWork()
		{
			var state = new PlacementWindowEvents(32, 16, 4);
			state.Ready(new[] { Window }, 100);
			var original = Assert.Single(state.Existing(101));
			Assert.True(state.IsExistingCurrent(original));
			Send(state, PlacementWindowEventKind.Destroy, 102);
			Assert.False(state.IsExistingCurrent(original));
			state.ProcessBatch();
			var automatic = Admit(state, 103);
			Assert.False(state.IsExistingCurrent(original));
			Assert.Empty(state.Existing(105));
			state.Complete(automatic);
			var replacement = Assert.Single(state.Existing(106));
			Assert.True(state.IsExistingCurrent(replacement));
			Send(state, PlacementWindowEventKind.Create, 107);
			state.ProcessBatch();
			Assert.Empty(state.Existing(108));
			Assert.False(state.IsExistingCurrent(replacement));
			state.Stop();
			Assert.False(state.IsExistingCurrent(original));
		}

		private static PlacementWindowEvents Running(int events = 32, int tracking = 16, int candidates = 4)
		{
			var state = new PlacementWindowEvents(events, tracking, candidates);
			Assert.True(state.Ready(new IntPtr[0], 100));
			return state;
		}

		private static void Send(PlacementWindowEvents state, PlacementWindowEventKind kind, long time, int window = 101)
			=> state.Receive(new PlacementWindowEvent(kind, new IntPtr(window), time));

		private static PlacementCandidate Admit(PlacementWindowEvents state, long time = 101, int window = 101)
		{
			Send(state, PlacementWindowEventKind.Create, time, window);
			Send(state, PlacementWindowEventKind.Show, time + 1, window);
			state.ProcessBatch();
			return Assert.IsType<PlacementCandidate>(state.TakeCandidate());
		}

		[Fact]
		public void PreparationAndVisibleBaselineWindowsAreNeverAdmittedEvenWithLateShow()
		{
			var state = new PlacementWindowEvents(32, 16, 4);
			Send(state, PlacementWindowEventKind.Create, 99, 102);
			Send(state, PlacementWindowEventKind.Show, 100, 102);
			Assert.Null(state.TakeCandidate());
			Assert.True(state.Ready(new[] { Window }, 100));
			Send(state, PlacementWindowEventKind.Show, 101);
			Send(state, PlacementWindowEventKind.Show, 102, 102);
			Send(state, PlacementWindowEventKind.Create, 100, 103);
			Send(state, PlacementWindowEventKind.Show, 103, 103);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
			Assert.Equal(PlacementMonitorState.Running, state.State);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void OnlyInitiallyHiddenBaselineWindowsAreAdmittedOnFirstShow(bool hidden)
		{
			var state = new PlacementWindowEvents(32, 16, 4);
			var hiddenWindows = new HashSet<IntPtr>();
			if (hidden)
			{
				hiddenWindows.Add(Window);
			}
			Assert.True(state.Ready(new[] { Window }, 100, hiddenWindows));
			Send(state, PlacementWindowEventKind.Show, 10101);
			state.ProcessBatch();
			var candidate = state.TakeCandidate();
			if (hidden)
			{
				Assert.NotNull(candidate);
				Assert.Equal(15101, new PlacementWorkItem(candidate).Deadline);
				state.Complete(candidate);
			}
			else
			{
				Assert.Null(candidate);
			}
			Send(state, PlacementWindowEventKind.Hide, 10200);
			Send(state, PlacementWindowEventKind.Show, 10300);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void DelayedFirstShowStartsThePlacementDeadlineWhenTheWindowAppears()
		{
			var state = Running();
			Send(state, PlacementWindowEventKind.Create, 101);
			Send(state, PlacementWindowEventKind.Show, 10101);
			state.ProcessBatch();
			var candidate = Assert.IsType<PlacementCandidate>(state.TakeCandidate());
			Assert.Equal(10101, candidate.ObservedAt);
			Assert.Equal(15101, new PlacementWorkItem(candidate).Deadline);
			Assert.True(state.IsCurrent(candidate));
		}

		[Fact]
		public void HiddenThenShownWindowIsAdmittedOnceAndRestorationDoesNotRepeat()
		{
			var state = Running();
			Send(state, PlacementWindowEventKind.Create, 101);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
			Send(state, PlacementWindowEventKind.Show, 102);
			state.ProcessBatch();
			var first = Assert.IsType<PlacementCandidate>(state.TakeCandidate());
			Assert.Equal(Window, first.Window);
			Assert.Equal(102, first.ObservedAt);
			Assert.True(state.IsCurrent(first));
			state.Complete(first);
			state.Complete(first);
			Send(state, PlacementWindowEventKind.Hide, 103);
			Send(state, PlacementWindowEventKind.Show, 104);
			state.ProcessBatch();
			Assert.False(state.IsCurrent(first));
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void QueuedDestroyBlocksAdmissionAndHandleReuseGetsNewLifetime()
		{
			var state = Running();
			var first = Admit(state);
			Send(state, PlacementWindowEventKind.Destroy, 103);
			Assert.False(state.IsCurrent(first));
			Assert.Null(state.TakeCandidate());
			state.ProcessBatch();
			Assert.Equal(0, state.TrackedCount);
			var second = Admit(state, 104);
			Assert.NotEqual(first.Lifetime, second.Lifetime);
			Assert.False(state.IsCurrent(first));
			Assert.True(state.IsCurrent(second));
		}

		[Fact]
		public void DestroyBeforeConsumptionRemovesQueuedCandidate()
		{
			var state = Running();
			Send(state, PlacementWindowEventKind.Create, 101);
			Send(state, PlacementWindowEventKind.Show, 102);
			Send(state, PlacementWindowEventKind.Destroy, 103);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
			Assert.Equal(0, state.TrackedCount);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void AmbiguousCreateCancelsBothQueuedAndOutstandingWork(bool consumed)
		{
			var state = Running();
			Send(state, PlacementWindowEventKind.Create, 101);
			Send(state, PlacementWindowEventKind.Show, 102);
			state.ProcessBatch();
			var first = consumed ? state.TakeCandidate() : null;
			Send(state, PlacementWindowEventKind.Create, 103);
			Send(state, PlacementWindowEventKind.Show, 104);
			state.ProcessBatch();
			Assert.False(state.IsCurrent(first));
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void PendingLimitIncludesOutstandingWorkAndSkippedLifetimeIsNotRetried()
		{
			var state = Running(candidates: 1);
			var first = Admit(state);
			Send(state, PlacementWindowEventKind.Create, 103, 102);
			Send(state, PlacementWindowEventKind.Show, 104, 102);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
			Assert.Equal(1, state.SkippedCandidates);
			state.Complete(first);
			Send(state, PlacementWindowEventKind.Show, 105, 102);
			state.ProcessBatch();
			Assert.Null(state.TakeCandidate());
			Assert.NotNull(Admit(state, 106, 103));
			Assert.Equal(PlacementMonitorState.Running, state.State);
		}

		[Fact]
		public void RepeatedShowsAreCoalescedWithoutUnboundedBufferGrowth()
		{
			var state = Running(events: 2);
			Send(state, PlacementWindowEventKind.Create, 101);
			for (var i = 0; i < 100000; i++) Send(state, PlacementWindowEventKind.Show, 102 + i);
			Assert.Equal(2, state.BufferedCount);
			Assert.Equal(PlacementMonitorState.Running, state.State);
			state.ProcessBatch(1);
			Assert.Null(state.TakeCandidate());
			state.ProcessBatch(1);
			Assert.NotNull(state.TakeCandidate());
			Assert.Null(state.TakeCandidate());
		}

		[Fact]
		public void EventOverflowInvalidatesOutstandingWorkAndRequiresFreshMonitor()
		{
			var state = Running(events: 2);
			var first = Admit(state);
			Send(state, PlacementWindowEventKind.Hide, 103);
			Send(state, PlacementWindowEventKind.Show, 104);
			Send(state, PlacementWindowEventKind.Destroy, 105);
			Assert.Equal(PlacementMonitorState.Paused, state.State);
			Assert.Equal("EventCapacity", state.PauseReason);
			Assert.False(state.IsCurrent(first));
			Assert.Equal(0, state.TrackedCount);
			Assert.False(state.Ready(new IntPtr[0], 106));
			Assert.Null(state.TakeCandidate());
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void LifetimeCapacityPausesInsteadOfEvictingHistory(bool duringBaseline)
		{
			var state = new PlacementWindowEvents(8, 1, 1);
			if (duringBaseline)
			{
				Assert.False(state.Ready(new[] { Window, new IntPtr(102) }, 100));
			}
			else
			{
				Assert.True(state.Ready(new[] { Window }, 100));
				Send(state, PlacementWindowEventKind.Create, 101, 102);
				state.ProcessBatch();
			}
			Assert.Equal(PlacementMonitorState.Paused, state.State);
			Assert.Equal("TrackingCapacity", state.PauseReason);
			Assert.Equal(0, state.TrackedCount);
		}

		[Fact]
		public void ReorderedEventsPauseAndStopIsTerminal()
		{
			var state = Running();
			var first = Admit(state);
			Send(state, PlacementWindowEventKind.Hide, 104);
			Send(state, PlacementWindowEventKind.Show, 103);
			state.ProcessBatch();
			Assert.Equal("EventOrder", state.PauseReason);
			Assert.False(state.IsCurrent(first));
			state.Stop();
			state.Pause("LateFailure");
			Send(state, PlacementWindowEventKind.Create, 105);
			Assert.Equal(PlacementMonitorState.Stopped, state.State);
			Assert.Equal(0, state.BufferedCount);
			Assert.False(state.Ready(new IntPtr[0], 106));
		}

		[Fact]
		public void StopInvalidatesOutstandingCandidateImmediately()
		{
			var state = Running();
			var first = Admit(state);
			state.Stop();
			Assert.False(state.IsCurrent(first));
			Assert.Equal(0, state.TrackedCount);
		}

		[Fact]
		public void FreshMonitorCannotAcceptCandidateFromPreviousMonitor()
		{
			var previous = Running();
			var oldCandidate = Admit(previous);
			previous.Stop();
			var current = Running();
			var newCandidate = Admit(current);
			Assert.Equal(oldCandidate.Window, newCandidate.Window);
			Assert.Equal(oldCandidate.Lifetime, newCandidate.Lifetime);
			Assert.False(current.IsCurrent(oldCandidate));
			current.Complete(oldCandidate);
			Assert.True(current.IsCurrent(newCandidate));
		}

		[Fact]
		public void NativeTimestampWrapIsNormalizedButStaleOrFutureTimeIsRejected()
		{
			var now = (1L << 32) + 10;
			Assert.Equal(now - 20, PlacementWindowMonitor.NormalizeRecentTime(uint.MaxValue - 9, now, 5000));
			Assert.Equal(now, PlacementWindowMonitor.NormalizeRecentTime(10, now, 5000));
			Assert.Null(PlacementWindowMonitor.NormalizeRecentTime(11, now, 5000));
			Assert.Null(PlacementWindowMonitor.NormalizeRecentTime(1, 10000, 5000));
		}
	}
}
