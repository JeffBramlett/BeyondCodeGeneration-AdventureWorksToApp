using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Application;

public enum SubmissionTokenState
{
	Started,
	InProgress,
	Completed
}

public sealed record SubmissionTokenBeginResult(SubmissionTokenState State, OrderSubmissionConfirmationDto? Confirmation);

/// <summary>
/// Local-demo duplicate protection: single-use submission tokens held in API process memory.
/// Completed confirmations are retained (bounded by count and age) so a retried token replays the same body.
/// State is lost on API restart; this is an approved demo limitation.
/// </summary>
public sealed class OrderSubmissionTokenRegistry(TimeProvider timeProvider)
{
	public const int MaximumCompletedEntries = 1000;
	public static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(24);

	private readonly object gate = new();
	private readonly HashSet<Guid> inFlight = [];
	private readonly Dictionary<Guid, CompletedEntry> completed = [];
	private readonly LinkedList<Guid> completedOrder = new();

	public SubmissionTokenBeginResult TryBegin(Guid token)
	{
		lock (gate)
		{
			PurgeExpired();
			if (completed.TryGetValue(token, out var entry))
			{
				return new SubmissionTokenBeginResult(SubmissionTokenState.Completed, entry.Confirmation);
			}

			return inFlight.Add(token)
				? new SubmissionTokenBeginResult(SubmissionTokenState.Started, null)
				: new SubmissionTokenBeginResult(SubmissionTokenState.InProgress, null);
		}
	}

	public void Complete(Guid token, OrderSubmissionConfirmationDto confirmation)
	{
		ArgumentNullException.ThrowIfNull(confirmation);
		lock (gate)
		{
			inFlight.Remove(token);
			if (completed.Remove(token, out var existing))
			{
				completedOrder.Remove(existing.Node);
			}

			var node = completedOrder.AddLast(token);
			completed[token] = new CompletedEntry(confirmation, timeProvider.GetUtcNow(), node);
			while (completed.Count > MaximumCompletedEntries && completedOrder.First is { } oldest)
			{
				completed.Remove(oldest.Value);
				completedOrder.RemoveFirst();
			}
		}
	}

	public void Release(Guid token)
	{
		lock (gate)
		{
			inFlight.Remove(token);
		}
	}

	public int CompletedCount
	{
		get
		{
			lock (gate)
			{
				return completed.Count;
			}
		}
	}

	private void PurgeExpired()
	{
		var cutoff = timeProvider.GetUtcNow() - CompletedRetention;
		while (completedOrder.First is { } oldest && completed[oldest.Value].CompletedAt <= cutoff)
		{
			completed.Remove(oldest.Value);
			completedOrder.RemoveFirst();
		}
	}

	private sealed record CompletedEntry(
		OrderSubmissionConfirmationDto Confirmation,
		DateTimeOffset CompletedAt,
		LinkedListNode<Guid> Node);
}
