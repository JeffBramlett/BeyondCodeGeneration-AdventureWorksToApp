using System.ComponentModel.DataAnnotations;

namespace AdventureWorks.Api.Contracts;

public sealed class OrderSearchRequest : IValidatableObject
{
	public string? OrderReference { get; init; }
	public DateOnly? FromDate { get; init; }
	public DateOnly? ToDate { get; init; }

	[Range(1, 10_000)]
	public int Page { get; init; } = 1;

	[Range(1, 100)]
	public int PageSize { get; init; } = 25;

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		var hasReference = OrderReference is not null;
		var hasDate = FromDate.HasValue || ToDate.HasValue;
		if (hasReference == hasDate)
		{
			yield return new ValidationResult(
				"Provide either an orderReference or both fromDate and toDate.",
				[nameof(OrderReference), nameof(FromDate), nameof(ToDate)]);
			yield break;
		}

		if (hasReference)
		{
			var normalized = OrderReference!.Trim();
			if (normalized.Length is < 1 or > 25)
			{
				yield return new ValidationResult(
					"Order reference must contain 1 to 25 non-whitespace characters.",
					[nameof(OrderReference)]);
			}

			yield break;
		}

		if (!FromDate.HasValue || !ToDate.HasValue)
		{
			yield return new ValidationResult(
				"Both fromDate and toDate are required for date-range search.",
				[nameof(FromDate), nameof(ToDate)]);
			yield break;
		}

		if (ToDate.Value < FromDate.Value)
		{
			yield return new ValidationResult("toDate must be on or after fromDate.", [nameof(ToDate)]);
		}
		else if (ToDate.Value == DateOnly.MaxValue)
		{
			yield return new ValidationResult("toDate must leave room for the half-open date interval.", [nameof(ToDate)]);
		}
		else if (ToDate.Value.DayNumber - FromDate.Value.DayNumber + 1 > 31)
		{
			yield return new ValidationResult("Date range cannot exceed 31 inclusive calendar days.", [nameof(ToDate)]);
		}
	}
}

public sealed class OrderLinesRequest
{
	[Range(1, 10_000)]
	public int LinePage { get; init; } = 1;

	[Range(1, 100)]
	public int LinePageSize { get; init; } = 50;
}

public sealed record OrderSummaryDto(string OrderReference, DateTime OrderDate);

public sealed record OrderPageDto(
	IReadOnlyList<OrderSummaryDto> Items,
	int Page,
	int PageSize,
	bool HasMore);

public sealed record OrderLineDto(int ProductId, int OrderQty);

public sealed record OrderDetailPageDto(
	string OrderReference,
	DateTime OrderDate,
	IReadOnlyList<OrderLineDto> Lines,
	int LinePage,
	int LinePageSize,
	bool HasMore);
