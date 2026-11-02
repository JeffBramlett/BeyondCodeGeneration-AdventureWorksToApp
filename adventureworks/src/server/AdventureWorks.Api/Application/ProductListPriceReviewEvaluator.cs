namespace AdventureWorks.Api.Application;

public sealed record ProductListPriceSnapshotEntry(int ProductId, decimal? ListPrice);

public sealed class ProductListPriceSnapshot
{
	public bool IsAvailable { get; }
	public IReadOnlyList<ProductListPriceSnapshotEntry?> Entries { get; }

	public ProductListPriceSnapshot(
		bool isAvailable,
		IEnumerable<ProductListPriceSnapshotEntry?>? entries)
	{
		IsAvailable = isAvailable;
		Entries = Array.AsReadOnly(entries?.ToArray() ?? []);
	}
}

public enum ProductListPriceReviewStatus
{
	InvalidNotEligible,
	Current,
	ReviewRequired
}

public sealed record ProductListPriceReviewResult(ProductListPriceReviewStatus Status);

public static class ProductListPriceReviewEvaluator
{
	public static ProductListPriceReviewResult Evaluate(
		ProductListPriceSnapshot? previouslyReviewedSnapshot,
		ProductListPriceSnapshot? freshServerReadSnapshot)
	{
		if (!TryReadKnownPrices(previouslyReviewedSnapshot, allowUnavailablePrices: false, out var reviewedPrices))
		{
			return Result(ProductListPriceReviewStatus.InvalidNotEligible);
		}

		if (freshServerReadSnapshot is null)
		{
			return Result(ProductListPriceReviewStatus.ReviewRequired);
		}

		if (!freshServerReadSnapshot.IsAvailable)
		{
			return freshServerReadSnapshot.Entries.Count == 0
				? Result(ProductListPriceReviewStatus.ReviewRequired)
				: Result(ProductListPriceReviewStatus.InvalidNotEligible);
		}

		if (freshServerReadSnapshot.Entries.Count == 0)
		{
			return Result(ProductListPriceReviewStatus.ReviewRequired);
		}

		if (!TryReadKnownPrices(freshServerReadSnapshot, allowUnavailablePrices: true, out var freshPrices))
		{
			return Result(ProductListPriceReviewStatus.InvalidNotEligible);
		}

		if (reviewedPrices.Count != freshPrices.Count)
		{
			return Result(ProductListPriceReviewStatus.ReviewRequired);
		}

		foreach (var (productId, reviewedPrice) in reviewedPrices)
		{
			if (!freshPrices.TryGetValue(productId, out var freshPrice)
				|| freshPrice is null
				|| freshPrice.Value != reviewedPrice)
			{
				return Result(ProductListPriceReviewStatus.ReviewRequired);
			}
		}

		return Result(ProductListPriceReviewStatus.Current);
	}

	private static bool TryReadKnownPrices(
		ProductListPriceSnapshot? snapshot,
		bool allowUnavailablePrices,
		out Dictionary<int, decimal?> prices)
	{
		prices = [];
		if (snapshot is null || !snapshot.IsAvailable || snapshot.Entries.Count == 0)
		{
			return false;
		}

		foreach (var entry in snapshot.Entries)
		{
			if (entry is null || entry.ProductId <= 0 || !prices.TryAdd(entry.ProductId, entry.ListPrice))
			{
				return false;
			}

			if (entry.ListPrice is null)
			{
				if (!allowUnavailablePrices)
				{
					return false;
				}
			}
			else if (entry.ListPrice < 0m)
			{
				return false;
			}
		}

		return true;
	}

	private static ProductListPriceReviewResult Result(ProductListPriceReviewStatus status) => new(status);
}