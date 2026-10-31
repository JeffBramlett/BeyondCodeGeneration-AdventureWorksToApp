using System.ComponentModel.DataAnnotations;

namespace AdventureWorks.Api.Contracts;

public sealed class CustomerSearchRequest : IValidatableObject
{
	[Range(1, 10_000)]
	public int Page { get; init; } = 1;

	[Range(1, 100)]
	public int PageSize { get; init; } = 25;

	[StringLength(100)]
	public string? Search { get; init; }

	[RegularExpression("^(customerId|displayName|customerKind)$",
		MatchTimeoutInMilliseconds = 1000)]
	public string SortBy { get; init; } = "displayName";

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

public sealed record CustomerDto(int CustomerId, string DisplayName, string CustomerKind);

public sealed record CustomerPageDto(
	IReadOnlyList<CustomerDto> Items,
	int Page,
	int PageSize,
	bool HasMore);

public sealed record CustomerOrderOptionsDto(
	IReadOnlyList<CustomerAddressOptionDto> Addresses,
	IReadOnlyList<ShipMethodOptionDto> ShipMethods);

public sealed record CustomerAddressOptionDto(
	int AddressId,
	string AddressLine1,
	string? AddressLine2,
	string City,
	string? PostalCode,
	IReadOnlyList<string> AddressTypes);

public sealed record ShipMethodOptionDto(int ShipMethodId, string Name);