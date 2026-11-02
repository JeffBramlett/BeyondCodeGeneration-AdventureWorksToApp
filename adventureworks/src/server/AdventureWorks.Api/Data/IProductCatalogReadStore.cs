using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public interface IProductCatalogReadStore
{
	bool IsConfigured { get; }

	Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken);

	Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken);

	Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken);

	Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(int productId, CancellationToken cancellationToken);

	Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken);
}

public sealed record ProductOfferEligibilityReadResult(
	int ProductId,
	IReadOnlyList<ProductOfferEligibilityRow> Offers);

public sealed record ProductOfferEligibilityRow(
	int SpecialOfferId,
	string Description,
	DateTimeOffset StartDate,
	DateTimeOffset EndDate,
	int MinQty,
	int? MaxQty,
	decimal DiscountPct = 0m);

public sealed class UnconfiguredProductCatalogReadStore : IProductCatalogReadStore
{
	public bool IsConfigured => false;

	public Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks catalog store is configured.");

	public Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks catalog store is configured.");

	public Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks catalog store is configured.");

	public Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(int productId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks catalog store is configured.");

	public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks catalog store is configured.");
}