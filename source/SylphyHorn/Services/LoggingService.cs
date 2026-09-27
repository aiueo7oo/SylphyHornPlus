using System;
using System.Linq;
using System.Collections.Generic;
using MetroTrilithon.Lifetime;
using MetroTrilithon.Threading.Tasks;

namespace SylphyHorn.Services
{
	public interface ILog
	{
		DateTimeOffset DateTime { get; }
		string Header { get; }
		string Content { get; }
	}

	public readonly struct LogEntry
	{
		public long Sequence { get; }

		public ILog Log { get; }

		public LogEntry(long sequence, ILog log)
		{
			this.Sequence = sequence;
			this.Log = log;
		}
	}

	public class LoggingService
	{
		private readonly object _gate = new object();
		private readonly List<ILog> _logs = new List<ILog>();
		private readonly List<Action<LogEntry>> _handlers = new List<Action<LogEntry>>();
		private long _sequence;

		public static LoggingService Instance { get; } = new LoggingService();

		private LoggingService() { }

		internal LogEntry[] GetRecent(int limit, out int totalCount)
		{
			if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
			lock (this._gate)
			{
				totalCount = this._logs.Count;
				var start = Math.Max(0, totalCount - limit);
				return this._logs.Skip(start).Select((log, index) => new LogEntry(start + index + 1L, log)).ToArray();
			}
		}

		public void Register(ILog log)
		{
			lock (this._gate)
			{
				var entry = new LogEntry(++this._sequence, log);
				this._logs.Add(log);
				foreach (var handler in this._handlers.ToArray()) handler(entry);
			}
		}

		public IDisposable Subscribe(Action<LogEntry[]> initialize, Action<LogEntry> handler)
		{
			if (initialize == null) throw new ArgumentNullException(nameof(initialize));
			if (handler == null) throw new ArgumentNullException(nameof(handler));

			lock (this._gate)
			{
				var snapshot = this._logs
					.Select((log, index) => new LogEntry(index + 1L, log))
					.ToArray();
				initialize(snapshot);
				this._handlers.Add(handler);
			}

			return Disposable.Create(() =>
			{
				lock (this._gate)
				{
					this._handlers.Remove(handler);
				}
			});
		}

		public void Register(Exception exception)
		{
			if (exception is AggregateException aggregateException)
			{
				foreach (var innerException in aggregateException.InnerExceptions) this.Register(new Log(innerException));
			}
			else
			{
				this.Register(new Log(exception));
			}
		}

		public void Register(TaskLog log)
		{
			this.Register(new Log(log));
		}

		private class Log : ILog
		{
			public DateTimeOffset DateTime { get; } = DateTimeOffset.Now;

			public string Header { get; }

			public string Content { get; }

			public Log(Exception ex)
			{
				this.Header = ex.GetType().Name;
				this.Content = ex.ToString();
			}

			public Log(TaskLog log)
			{
				this.Header = log.Exception.GetType().Name;
				this.Content = $@"Unhandled exception was thrown by Task<T>.
{log.CallerMemberName} ({System.IO.Path.GetFileName(log.CallerFilePath)}#{log.CallerLineNumber})
-----
{log.Exception}";
			}
		}
	}
}
