using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SylphyHorn.Services;
using SylphyHorn.UI.Bindings;
using Xunit;

namespace SylphyHorn.Tests
{
	[CollectionDefinition(Name, DisableParallelization = true)]
	public sealed class LoggingServiceTestCollection
	{
		public const string Name = "LoggingService";
	}

	[Collection(LoggingServiceTestCollection.Name)]
	public class LoggingServiceTests
	{
		private const int TimeoutMilliseconds = 10000;

		[Fact]
		public void RecentSnapshotReturnsNewestEntriesInOrderWithoutClearingHistory()
		{
			var service = LoggingService.Instance;
			var first = new TestLog("recent-first");
			var second = new TestLog("recent-second");
			service.Register(first);
			service.Register(second);

			var entries = service.GetRecent(2, out var total);
			Assert.Equal(new[] { first, second }, entries.Select(entry => entry.Log));
			var latest = service.GetRecent(1, out var unchangedTotal);
			Assert.Equal(total, unchangedTotal);
			Assert.Same(second, Assert.Single(latest).Log);
			Assert.Equal(total, service.GetRecent(int.MaxValue, out _).Length);
			Assert.Equal(entries[1].Sequence, latest[0].Sequence);
		}

		[Fact]
		public void SubscribeIncludesPreexistingLogsExactlyOnceInSnapshot()
		{
			var service = LoggingService.Instance;
			var first = new TestLog("snapshot-first");
			var second = new TestLog("snapshot-second");
			service.Register(first);
			service.Register(second);

			LogEntry[] snapshot = null;
			using (service.Subscribe(entries => snapshot = entries, entry => { }))
			{
				var selected = snapshot.Where(entry => ReferenceEquals(entry.Log, first) || ReferenceEquals(entry.Log, second)).ToArray();
				Assert.Equal(new[] { first, second }, selected.Select(entry => entry.Log));
				Assert.True(selected[0].Sequence < selected[1].Sequence);
			}
		}

		[Fact]
		public void SubscribeBootstrapAndConcurrentRegisterHaveNoGapOrDuplicate()
		{
			var service = LoggingService.Instance;
			var before = new TestLog("bootstrap-before");
			var after = new TestLog("bootstrap-after");
			service.Register(before);

			var initializeEntered = new ManualResetEventSlim();
			var releaseInitialize = new ManualResetEventSlim();
			var registerStarted = new ManualResetEventSlim();
			LogEntry[] snapshot = null;
			var live = new ConcurrentQueue<LogEntry>();
			IDisposable subscription = null;
			Exception subscribeFailure = null;
			var subscribeThread = new Thread(() =>
			{
				try
				{
					subscription = service.Subscribe(
						entries =>
						{
							snapshot = entries;
							initializeEntered.Set();
							if (!releaseInitialize.WaitHandle.WaitOne(TimeoutMilliseconds)) throw new TimeoutException("Initialize was not released.");
						},
						live.Enqueue);
				}
				catch (Exception exception)
				{
					subscribeFailure = exception;
				}
			});
			subscribeThread.Start();

			Assert.True(initializeEntered.WaitHandle.WaitOne(TimeoutMilliseconds));
			Exception registerFailure = null;
			var registerThread = new Thread(() =>
			{
				try
				{
					registerStarted.Set();
					service.Register(after);
				}
				catch (Exception exception)
				{
					registerFailure = exception;
				}
			});
			registerThread.Start();
			Assert.True(registerStarted.WaitHandle.WaitOne(TimeoutMilliseconds));
			releaseInitialize.Set();
			Assert.True(subscribeThread.Join(TimeoutMilliseconds));
			Assert.True(registerThread.Join(TimeoutMilliseconds));
			Assert.Null(subscribeFailure);
			Assert.Null(registerFailure);

			using (subscription)
			{
				var selected = snapshot
					.Concat(live)
					.Where(entry => ReferenceEquals(entry.Log, before) || ReferenceEquals(entry.Log, after))
					.ToArray();
				Assert.Equal(new[] { before, after }, selected.Select(entry => entry.Log));
				Assert.True(selected[0].Sequence < selected[1].Sequence);
			}
		}

		[Fact]
		public void ConcurrentRegisterNotificationsFollowSequenceOrder()
		{
			const int count = 32;
			var service = LoggingService.Instance;
			var logs = Enumerable.Range(0, count).Select(index => new TestLog($"concurrent-{index}")).ToArray();
			var expected = new HashSet<ILog>(logs);
			var received = new List<LogEntry>();
			var ready = new CountdownEvent(count);
			var start = new ManualResetEventSlim();

			using (service.Subscribe(entries => { }, entry =>
			{
				if (expected.Contains(entry.Log)) received.Add(entry);
			}))
			{
				var threads = logs.Select(log => new Thread(() =>
				{
					ready.Signal();
					if (!start.WaitHandle.WaitOne(TimeoutMilliseconds)) throw new TimeoutException("Concurrent register was not started.");
					service.Register(log);
				})).ToArray();
				foreach (var thread in threads) thread.Start();
				Assert.True(ready.WaitHandle.WaitOne(TimeoutMilliseconds));
				start.Set();
				foreach (var thread in threads) Assert.True(thread.Join(TimeoutMilliseconds));
			}

			Assert.Equal(count, received.Count);
			Assert.Equal(received.OrderBy(entry => entry.Sequence).Select(entry => entry.Sequence), received.Select(entry => entry.Sequence));
			Assert.True(expected.SetEquals(received.Select(entry => entry.Log)));
		}

		[Fact]
		public void DrainGateDoesNotLoseOwnerAtReleaseAndQueueRecheckBoundary()
		{
			var gate = new SingleDrainGate();
			var recheckEntered = new ManualResetEventSlim();
			var releaseRecheck = new ManualResetEventSlim();
			Assert.True(gate.TryAcquire());

			var reacquiredByDrain = false;
			Exception drainFailure = null;
			var drainThread = new Thread(() =>
			{
				try
				{
					reacquiredByDrain = gate.ReleaseAndTryAcquireIf(() =>
					{
						recheckEntered.Set();
						if (!releaseRecheck.WaitHandle.WaitOne(TimeoutMilliseconds)) throw new TimeoutException("Queue recheck was not released.");
						return true;
					});
				}
				catch (Exception exception)
				{
					drainFailure = exception;
				}
			});
			drainThread.Start();

			Assert.True(recheckEntered.WaitHandle.WaitOne(TimeoutMilliseconds));
			Assert.True(gate.TryAcquire());
			releaseRecheck.Set();
			Assert.True(drainThread.Join(TimeoutMilliseconds));
			Assert.Null(drainFailure);
			Assert.False(reacquiredByDrain);
			gate.Release();
		}

		private sealed class TestLog : ILog
		{
			public DateTimeOffset DateTime { get; } = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

			public string Header { get; }

			public string Content => this.Header;

			internal TestLog(string header)
			{
				this.Header = header;
			}
		}
	}
}
