namespace AdventureWorks.Api.Data;

/// <summary>
/// Reads the DiscountPct/Description of a specific product-linked offer for demo order pricing.
/// Kept separate from IProductCatalogReadStore so browsing/eligibility queries stay free of discount data.
/// </summary>
public interface IOfferPricingReadStore
{
	bool IsConfigured { get; }

	Task<OfferPricingRow?> GetOfferPricingAsync(int productId, int specialOfferId, CancellationToken cancellationToken);
}

public sealed record OfferPricingRow(int SpecialOfferId, string Description, decimal DiscountPct);

public sealed class UnconfiguredOfferPricingReadStore : IOfferPricingReadStore
{
	public bool IsConfigured => false;

	public Task<OfferPricingRow?> GetOfferPricingAsync(int productId, int specialOfferId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks offer pricing store is configured.");
}
