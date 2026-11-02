using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Application;

public enum IdempotencyClaimObservationKind
{
	Absent,
	Committed,
	ProcessingObservable,
	OutcomeUnknown
}

public sealed record IdempotencyClaimObservation(
	IdempotencyClaimObservationKind Kind,
	string? StoredFingerprint = null,
	OrderReplaySnapshot? ReplaySnapshot = null);

public enum IdempotencyOutcome
{
	InvalidState,
	NewAttemptEligible,
	ReplayCommitted,
	Conflict,
	InProgress,
	Ambiguous
}

public sealed record IdempotencyClassification(
	IdempotencyOutcome Outcome,
	OrderReplaySnapshot? ReplaySnapshot = null);

public static class IdempotencyOutcomeClassifier
{
	public static IdempotencyClassification Classify(
		string? incomingFingerprint,
		IdempotencyClaimObservation? observation)
	{
		if (!IsValidFingerprint(incomingFingerprint) || observation is null)
		{
			return InvalidState();
		}

		switch (observation.Kind)
		{
			case IdempotencyClaimObservationKind.Absent:
				return observation.StoredFingerprint is null && observation.ReplaySnapshot is null
					? new IdempotencyClassification(IdempotencyOutcome.NewAttemptEligible)
					: InvalidState();

			case IdempotencyClaimObservationKind.Committed:
				if (!IsValidFingerprint(observation.StoredFingerprint))
				{
					return InvalidState();
				}

				if (!string.Equals(incomingFingerprint, observation.StoredFingerprint, StringComparison.Ordinal))
				{
					return new IdempotencyClassification(IdempotencyOutcome.Conflict);
				}

				return observation.ReplaySnapshot is null
					? InvalidState()
					: new IdempotencyClassification(IdempotencyOutcome.ReplayCommitted, observation.ReplaySnapshot);

			case IdempotencyClaimObservationKind.ProcessingObservable:
				if (!IsValidFingerprint(observation.StoredFingerprint) || observation.ReplaySnapshot is not null)
				{
					return InvalidState();
				}

				return string.Equals(incomingFingerprint, observation.StoredFingerprint, StringComparison.Ordinal)
					? new IdempotencyClassification(IdempotencyOutcome.InProgress)
					: new IdempotencyClassification(IdempotencyOutcome.Conflict);

			case IdempotencyClaimObservationKind.OutcomeUnknown:
				return observation.StoredFingerprint is null && observation.ReplaySnapshot is null
					? new IdempotencyClassification(IdempotencyOutcome.Ambiguous)
					: InvalidState();

			default:
				return InvalidState();
		}
	}

	private static bool IsValidFingerprint(string? fingerprint)
	{
		if (fingerprint is null || fingerprint.Length != 64)
		{
			return false;
		}

		foreach (var character in fingerprint)
		{
			if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
			{
				return false;
			}
		}

		return true;
	}

	private static IdempotencyClassification InvalidState() =>
		new(IdempotencyOutcome.InvalidState);
}