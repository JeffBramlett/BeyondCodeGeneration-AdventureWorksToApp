using AdventureWorks.Api.Application;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OfferEligibilityEvaluatorTests
{
	private static readonly DateTimeOffset StartDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset EndDate = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public void StartDate_IsInclusive()
	{
		Assert.True(Evaluate(evaluationInstant: StartDate));
	}

	[Fact]
	public void EndDate_IsExclusive()
	{
		Assert.False(Evaluate(evaluationInstant: EndDate));
	}

	[Fact]
	public void MinQty_IsInclusive()
	{
		Assert.True(Evaluate(requestedQuantity: 5, minQty: 5));
	}

	[Fact]
	public void MaxQty_IsInclusive()
	{
		Assert.True(Evaluate(requestedQuantity: 5, maxQty: 5));
	}

	[Fact]
	public void NullMaxQty_HasNoUpperLimitWithinSupportedQuantityRange()
	{
		Assert.True(Evaluate(requestedQuantity: short.MaxValue, maxQty: null));
	}

	[Fact]
	public void UnlinkedProductOffer_IsIneligible()
	{
		Assert.False(Evaluate(isProductLinked: false));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	[InlineData(32768)]
	public void OutOfRangeRequestedQuantity_IsIneligible(int requestedQuantity)
	{
		Assert.False(Evaluate(requestedQuantity: requestedQuantity));
	}

	[Fact]
	public void DateTimeOffsets_AreComparedByInstant()
	{
		var equivalentStartInstant = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));
		var beforeStartInstant = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(2));

		Assert.True(Evaluate(evaluationInstant: equivalentStartInstant));
		Assert.False(Evaluate(evaluationInstant: beforeStartInstant));
	}

	private static bool Evaluate(
		DateTimeOffset? evaluationInstant = null,
		int requestedQuantity = 1,
		int minQty = 1,
		int? maxQty = null,
		bool isProductLinked = true) =>
		OfferEligibilityEvaluator.IsEligible(
			StartDate,
			EndDate,
			evaluationInstant ?? StartDate.AddHours(1),
			requestedQuantity,
			minQty,
			maxQty,
			isProductLinked);
}