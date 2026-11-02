using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class IdempotencyOutcomeClassifierTests
{
	private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string OtherFingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

	[Fact]
	public void AbsentClaim_IsOnlyNewAttemptEligible()
	{
		var result = Classify(new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Absent));

		Assert.Equal(IdempotencyOutcome.NewAttemptEligible, result.Outcome);
		Assert.Null(result.ReplaySnapshot);
	}

	[Fact]
	public void MatchingCommittedClaim_ReplaysTheOriginalSnapshotReference()
	{
		var snapshot = Snapshot();
		var result = IdempotencyOutcomeClassifier.Classify(
			Fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Committed, Fingerprint, snapshot));

		Assert.Equal(IdempotencyOutcome.ReplayCommitted, result.Outcome);
		Assert.Same(snapshot, result.ReplaySnapshot);
	}

	[Fact]
	public void ReplayInFakeHarness_DoesNotExecuteAnotherOrder()
	{
		var snapshot = Snapshot();
		var orderExecutionCount = 1;
		var result = Classify(new IdempotencyClaimObservation(
			IdempotencyClaimObservationKind.Committed,
			Fingerprint,
			snapshot));

		if (result.Outcome == IdempotencyOutcome.NewAttemptEligible)
		{
			orderExecutionCount++;
		}

		Assert.Equal(IdempotencyOutcome.ReplayCommitted, result.Outcome);
		Assert.Same(snapshot, result.ReplaySnapshot);
		Assert.Equal(1, orderExecutionCount);
	}

	[Fact]
	public void MismatchingCommittedClaim_IsConflictAndDoesNotExposeSnapshot()
	{
		var result = IdempotencyOutcomeClassifier.Classify(
			Fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Committed, OtherFingerprint, Snapshot()));

		Assert.Equal(IdempotencyOutcome.Conflict, result.Outcome);
		Assert.Null(result.ReplaySnapshot);
	}

	[Fact]
	public void ProcessingObservation_IsInProgress()
	{
		var result = Classify(new IdempotencyClaimObservation(
			IdempotencyClaimObservationKind.ProcessingObservable,
			Fingerprint));

		Assert.Equal(IdempotencyOutcome.InProgress, result.Outcome);
	}

	[Fact]
	public void MismatchingProcessingObservation_IsConflict()
	{
		var result = Classify(new IdempotencyClaimObservation(
			IdempotencyClaimObservationKind.ProcessingObservable,
			OtherFingerprint));

		Assert.Equal(IdempotencyOutcome.Conflict, result.Outcome);
	}

	[Fact]
	public void UnknownObservation_IsAmbiguous()
	{
		var result = Classify(new IdempotencyClaimObservation(IdempotencyClaimObservationKind.OutcomeUnknown));

		Assert.Equal(IdempotencyOutcome.Ambiguous, result.Outcome);
	}

	[Fact]
	public void ReplaySnapshot_PreservesHeaderValuesLineOrderAndNullOffers()
	{
		var snapshot = Snapshot();

		Assert.Equal(501, snapshot.SalesOrderId);
		Assert.Equal("SO-501", snapshot.SalesOrderNumber);
		Assert.Equal(42, snapshot.CustomerId);
		Assert.Equal(11, snapshot.BillToAddressId);
		Assert.Equal(12, snapshot.ShipToAddressId);
		Assert.Equal(2, snapshot.ShipMethodId);
		Assert.Collection(
			snapshot.Lines,
			line => Assert.Equal(new OrderReplayLine(7, 3, null), line),
			line => Assert.Equal(new OrderReplayLine(8, 1, 5), line));
	}

	[Fact]
	public void ReplaySnapshot_CopiesAndExposesLinesAsReadOnly()
	{
		var source = new List<OrderReplayLine>
		{
			new(7, 3, null),
			new(8, 1, 5)
		};
		var snapshot = CreateSnapshot(source);
		source[0] = new OrderReplayLine(99, 1, null);
		source.Add(new OrderReplayLine(10, 2, null));

		Assert.Equal(new OrderReplayLine(7, 3, null), snapshot.Lines[0]);
		Assert.Equal(2, snapshot.Lines.Count);
		var exposedList = Assert.IsAssignableFrom<IList<OrderReplayLine>>(snapshot.Lines);
		Assert.Throws<NotSupportedException>(() => exposedList[0] = new OrderReplayLine(99, 1, null));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
	[InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
	public void InvalidIncomingFingerprint_FailsClosed(string? fingerprint)
	{
		var result = IdempotencyOutcomeClassifier.Classify(
			fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Absent));

		Assert.Equal(IdempotencyOutcome.InvalidState, result.Outcome);
		Assert.Null(result.ReplaySnapshot);
	}

	[Theory]
	[InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("bad")]
	public void InvalidStoredFingerprint_FailsClosed(string storedFingerprint)
	{
		var result = IdempotencyOutcomeClassifier.Classify(
			Fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Committed, storedFingerprint, Snapshot()));

		Assert.Equal(IdempotencyOutcome.InvalidState, result.Outcome);
	}

	[Fact]
	public void ProcessingObservationWithoutFingerprint_FailsClosed()
	{
		var result = Classify(new IdempotencyClaimObservation(IdempotencyClaimObservationKind.ProcessingObservable));

		Assert.Equal(IdempotencyOutcome.InvalidState, result.Outcome);
	}

	[Fact]
	public void MatchingCommittedClaimWithoutSnapshot_FailsClosed()
	{
		var result = IdempotencyOutcomeClassifier.Classify(
			Fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Committed, Fingerprint));

		Assert.Equal(IdempotencyOutcome.InvalidState, result.Outcome);
		Assert.Null(result.ReplaySnapshot);
	}

	[Fact]
	public void AbsentObservationWithUnexpectedStoredState_FailsClosed()
	{
		var result = IdempotencyOutcomeClassifier.Classify(
			Fingerprint,
			new IdempotencyClaimObservation(IdempotencyClaimObservationKind.Absent, Fingerprint));

		Assert.Equal(IdempotencyOutcome.InvalidState, result.Outcome);
	}

	[Fact]
	public void ReplaySnapshot_RejectsInvalidIdentifiersAndLines()
	{
		Assert.Throws<ArgumentException>(() => CreateSnapshot([new OrderReplayLine(0, 1, null)]));
		Assert.Throws<ArgumentException>(() => CreateSnapshot([new OrderReplayLine(7, 0, null)]));
		Assert.Throws<ArgumentException>(() => CreateSnapshot([new OrderReplayLine(7, 1, 0)]));
		Assert.Throws<ArgumentOutOfRangeException>(() => new OrderReplaySnapshot(0, "SO-501", 42, 11, 12, 2, [new(7, 1, null)]));
	}

	[Fact]
	public void NewTypes_ExposeNoPersistenceRouteDiWriterOrMoneyContract()
	{
		var surfaceTypes = new[]
		{
			typeof(OrderReplayLine),
			typeof(OrderReplaySnapshot),
			typeof(IdempotencyClaimObservation),
			typeof(IdempotencyClassification),
			typeof(IdempotencyOutcomeClassifier)
		};
		var assembly = typeof(IdempotencyOutcomeClassifier).Assembly;

		// Project Owner scope change (2026-10-30) added a demo order writer; it must stay off the idempotency surface.
		Assert.DoesNotContain(assembly.GetTypes(), type =>
			type.Name.Contains("OrderWriter", StringComparison.Ordinal) &&
			type.Namespace != "AdventureWorks.Api.Data");
		foreach (var type in surfaceTypes)
		{
			var publicTypes = type.GetConstructors()
				.SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
				.Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)))
				.Append(type.BaseType ?? typeof(object));
			Assert.DoesNotContain(publicTypes, IsPersistenceOrWebBoundaryType);
		}

		Assert.Equal(
			new[] { "BillToAddressId", "CustomerId", "Lines", "SalesOrderId", "SalesOrderNumber", "ShipMethodId", "ShipToAddressId" },
			typeof(OrderReplaySnapshot).GetProperties().Select(property => property.Name).Order().ToArray());
	}

	private static IdempotencyClassification Classify(IdempotencyClaimObservation observation) =>
		IdempotencyOutcomeClassifier.Classify(Fingerprint, observation);

	private static OrderReplaySnapshot Snapshot() => CreateSnapshot(
	[
		new OrderReplayLine(7, 3, null),
		new OrderReplayLine(8, 1, 5)
	]);

	private static OrderReplaySnapshot CreateSnapshot(IEnumerable<OrderReplayLine> lines) => new(
		salesOrderId: 501,
		salesOrderNumber: "SO-501",
		customerId: 42,
		billToAddressId: 11,
		shipToAddressId: 12,
		shipMethodId: 2,
		lines: lines);

	private static bool IsPersistenceOrWebBoundaryType(Type type) =>
		type.Namespace?.StartsWith("System.Data", StringComparison.Ordinal) == true
		|| type.Namespace?.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal) == true
		|| type.Namespace?.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) == true
		|| type.Namespace?.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.Ordinal) == true;
}