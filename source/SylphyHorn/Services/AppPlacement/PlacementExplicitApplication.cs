using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Services.AppPlacement
{
	internal sealed class PlacementPreviewItem
	{
		internal PlacementPreviewItem(
			PlacementCandidate candidate,
			PlacementWindowIdentity identity,
			AppPlacementRule rule,
			string title,
			Guid source,
			Guid? target,
			PlacementOutcome? excluded)
		{
			this.Id = Guid.NewGuid();
			this.Candidate = candidate;
			this.Identity = identity;
			this.Rule = rule;
			this.Title = title;
			this.Source = source;
			this.Target = target;
			this.Excluded = excluded;
		}

		internal Guid Id { get; }

		internal PlacementCandidate Candidate { get; }

		internal PlacementWindowIdentity Identity { get; }

		internal AppPlacementRule Rule { get; }

		internal string Title { get; }

		internal Guid Source { get; }

		internal Guid? Target { get; }

		internal PlacementOutcome? Excluded { get; }

		internal bool CanApply => !this.Excluded.HasValue;
	}

	internal sealed class PlacementPreview
	{
		internal PlacementPreview(IEnumerable<PlacementPreviewItem> items, long expiresAt)
		{
			this.Items = Array.AsReadOnly(items.ToArray());
			this.ExpiresAt = expiresAt;
		}

		internal IReadOnlyList<PlacementPreviewItem> Items { get; }

		internal long ExpiresAt { get; }
	}

	/// <summary>Preview and explicit application on the session's existing serial worker.</summary>
	internal sealed class PlacementExplicitApplication
	{
		private readonly AppPlacementConfiguration _configuration;
		private readonly IPlacementWindows _windows;
		private readonly Func<PlacementCandidate[]> _existing;
		private readonly Func<PlacementCandidate, bool> _current;
		private readonly Func<IntPtr, string> _title;
		private readonly Func<long> _now;
		private readonly Action<int, CancellationToken> _wait;
		private PlacementPreview _preview;

		internal PlacementExplicitApplication(
			AppPlacementConfiguration configuration,
			IPlacementWindows windows,
			Func<PlacementCandidate[]> existing,
			Func<PlacementCandidate, bool> current,
			Func<IntPtr, string> title,
			Func<long> now,
			Action<int, CancellationToken> wait)
		{
			this._configuration = configuration;
			this._windows = windows;
			this._existing = existing;
			this._current = current;
			this._title = title;
			this._now = now;
			this._wait = wait;
		}

		internal PlacementPreview Preview(PlacementDesktopMap map, CancellationToken cancellation)
		{
			this._preview = null;
			var deadline = this._now() + 5000;
			var items = new List<PlacementPreviewItem>();
			foreach (var candidate in this._existing())
			{
				cancellation.ThrowIfCancellationRequested();
				if (this._now() >= deadline) throw new TimeoutException("Preview exceeded its time limit.");
				if (!this._current(candidate)) continue;
				var inspection = this._windows.Inspect(candidate.Window);
				if (inspection.Identity == null) continue;
				var rule = this._configuration.FindEnabledRule(inspection.Identity.App);
				if (rule == null) continue;
				var location = this._windows.Locate(candidate.Window);
				var target = map.Resolve(rule.Destination);
				PlacementOutcome? excluded = null;
				if (inspection.Status != PlacementInspectionStatus.Ready || location == null) excluded = PlacementOutcome.Unavailable;
				else if (location.Pinned) excluded = PlacementOutcome.Excluded;
				else if (target.Status != PlacementResolutionStatus.Resolved) excluded = PlacementOutcome.DestinationUnavailable;
				else if (location.Desktop == target.DesktopId) excluded = PlacementOutcome.AlreadyPlaced;
				if (!this._current(candidate)) continue;
				if (items.Count == 256) throw new InvalidOperationException("Preview exceeds 256 matching windows.");
				items.Add(new PlacementPreviewItem(candidate, inspection.Identity, rule, this._title(candidate.Window),
					location?.Desktop ?? Guid.Empty, target.DesktopId, excluded));
			}
			cancellation.ThrowIfCancellationRequested();
			if (this._now() >= deadline) throw new TimeoutException("Preview exceeded its time limit.");
			return this._preview = new PlacementPreview(items, this._now() + 60000);
		}

		internal PlacementResult[] Apply(
			PlacementPreview preview,
			Guid[] selection,
			Func<PlacementDestination, long, PlacementAuthorization> authorize,
			CancellationToken cancellation)
		{
			if (preview == null || !ReferenceEquals(preview, this._preview) || this._now() >= preview.ExpiresAt)
				throw new InvalidOperationException("Preview is stale or already consumed.");
			if (selection == null || selection.Length == 0 || selection.Length > 256 || selection.Distinct().Count() != selection.Length)
				throw new ArgumentException("Select distinct preview items.", nameof(selection));
			var selected = new HashSet<Guid>(selection);
			var items = preview.Items.Where(item => selected.Contains(item.Id)).ToArray();
			if (items.Length != selection.Length || items.Any(item => !item.CanApply)) throw new ArgumentException("Selection is not applicable.", nameof(selection));
			this._preview = null; // A reviewed selection is single-use, even when execution is cancelled.
			var results = new List<PlacementResult>();
			var batchDeadline = this._now() + 30000;
			var processor = new PlacementProcessor(
				this._configuration,
				this._windows,
				authorize,
				candidate => !cancellation.IsCancellationRequested && this._now() < batchDeadline && this._current(candidate),
				this._now,
				cancellation);
			foreach (var item in items)
			{
				var work = new PlacementWorkItem(new PlacementCandidate(item.Candidate.Window, item.Candidate.Epoch, item.Candidate.Lifetime, this._now()))
				{
					Identity = item.Identity,
					Rule = item.Rule,
					Source = item.Source,
					ExpectedTarget = item.Target
				};
				while (work.Result == null)
				{
					processor.Step(work);
					if (work.Result == null) this._wait((int)Math.Max(0, Math.Min(work.NextAt, batchDeadline) - this._now()), cancellation);
				}
				results.Add(work.Result);
			}
			return results.ToArray();
		}
	}
}
