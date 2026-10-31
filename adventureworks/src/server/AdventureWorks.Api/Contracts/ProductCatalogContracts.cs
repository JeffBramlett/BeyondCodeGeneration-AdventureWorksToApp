using System.ComponentModel.DataAnnotations;

namespace AdventureWorks.Api.Contracts;

public sealed class ProductSearchRequest : IValidatableObject
{
	[Range(1, 10_000)]
	public int Page { get; init; } = 1;

	[Range(1, 100)]
	public int PageSize { get; init; } = 25;

	[StringLength(100)]
	public string? Search { get; init; }

	[RegularExpression("^(productId|name|productNumber)$",
		MatchTimeoutInMilliseconds = 1000)]
	public string SortBy { get; init; } = "name";

	[RegularExpression("^(asc|desc)$", MatchTimeoutInMilliseconds = 1000)]
	public string SortDirection { get; init; } = "asc";

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (Search is not null && Search.Trim().Length == 0)
		{
			yield return new ValidationResult("Search must contain a non-whitespace character.", [nameof(Search)]);
		}
	}
}

public sealed record ProductDto(
	int ProductId,
	string Name,
	string ProductNumber,
	string? SubcategoryName,
	string? CategoryName);

public sealed record ProductDetailDto(
	int ProductId,
	string Name,
	string ProductNumber,
	string? SubcategoryName,
	string? CategoryName,
	decimal? ListPrice);

public sealed record ProductOfferDto(int SpecialOfferId, string Description);

/// <summary>DiscountPct is a decimal fraction from Sales.SpecialOffer.DiscountPct (e.g. 0.15), for demo line-amount preview.</summary>
public sealed record ProductEligibleOfferDto(int SpecialOfferId, string Description, decimal DiscountPct);

public sealed record ProductNoOfferPersistenceDto(bool IsSupported);

public sealed record ProductPageDto(
	IReadOnlyList<ProductDto> Items,
	int Page,
	int PageSize,
	bool HasMore);