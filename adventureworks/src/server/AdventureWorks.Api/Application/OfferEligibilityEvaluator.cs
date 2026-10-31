namespace AdventureWorks.Api.Application;

public static class OfferEligibilityEvaluator
{
	public static DateTimeOffset InterpretSqlDateTimeAsUtc(DateTime value) =>
		new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

	public static bool IsEligible(
		DateTimeOffset startDate,
		DateTimeOffset endDate,
		DateTimeOffset evaluationInstant,
		int requestedQuantity,
		int minQty,
		int? maxQty,
		bool isProductLinked)
	{
		return isProductLinked &&
			startDate <= evaluationInstant &&
			evaluationInstant < endDate &&
			requestedQuantity is >= 1 and <= short.MaxValue &&
			requestedQuantity >= minQty &&
			(maxQty is null || requestedQuantity <= maxQty.Value);
	}
}