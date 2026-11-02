using AdventureWorks.Api.Application;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class ProductListPriceReviewEvaluatorTests
{
	[Fact]
	public void Evaluate_EqualKnownPricesIncludingZero_AreCurrent()
	{
		var result = Evaluate(
			Snapshot((7, 0m), (8, 12.3456m)),
			Snapshot((8, 12.3456m), (7, 0m)));

		Assert.Equal(ProductListPriceReviewStatus.Current, result.Status);
	}

	[Fact]
	public void Evaluate_ChangedDecimalPrice_RequiresReviewWithoutRounding()
	{
		var result = Evaluate(Snapshot((7, 1.0000m)), Snapshot((7, 1.0001m)));

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Fact]
	public void Evaluate_PreviouslyKnownPriceNowUnavailable_RequiresReview()
	{
		var result = Evaluate(Snapshot((7, 1m)), Snapshot((7, null)));

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Fact]
	public void Evaluate_MissingFreshProductId_RequiresReview()
	{
		var result = Evaluate(Snapshot((7, 1m), (8, 2m)), Snapshot((7, 1m)));

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Fact]
	public void Evaluate_EmptyFreshSnapshot_RequiresReviewForMissingProducts()
	{
		var result = Evaluate(Snapshot((7, 1m)), Snapshot());

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Fact]
	public void Evaluate_UnreviewedFreshProductId_RequiresReview()
	{
		var result = Evaluate(Snapshot((7, 1m)), Snapshot((7, 1m), (8, 2m)));

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Fact]
	public void Evaluate_UnavailableFreshSnapshot_RequiresReview()
	{
		var result = ProductListPriceReviewEvaluator.Evaluate(
			Snapshot((7, 1m)),
			new ProductListPriceSnapshot(isAvailable: false, entries: []));

		Assert.Equal(ProductListPriceReviewStatus.ReviewRequired, result.Status);
	}

	[Theory]
	[MemberData(nameof(InvalidReviewedSnapshots))]
	public void Evaluate_InvalidPreviouslyReviewedSnapshot_IsNotEligible(ProductListPriceSnapshot snapshot)
	{
		var result = Evaluate(snapshot, Snapshot((7, 1m)));

		Assert.Equal(ProductListPriceReviewStatus.InvalidNotEligible, result.Status);
	}

	[Theory]
	[MemberData(nameof(InvalidFreshSnapshots))]
	public void Evaluate_InvalidFreshSnapshot_IsNotEligible(ProductListPriceSnapshot snapshot)
	{
		var result = Evaluate(Snapshot((7, 1m)), snapshot);

		Assert.Equal(ProductListPriceReviewStatus.InvalidNotEligible, result.Status);
	}

	[Fact]
	public void Snapshot_CopiesEntriesIntoReadOnlyCollection()
	{
		var entries = new List<ProductListPriceSnapshotEntry?> { new(7, 1m) };
		var snapshot = new ProductListPriceSnapshot(isAvailable: true, entries);
		entries[0] = new ProductListPriceSnapshotEntry(7, 2m);

		Assert.Equal(1m, snapshot.Entries[0]!.ListPrice);
		Assert.IsAssignableFrom<IReadOnlyList<ProductListPriceSnapshotEntry?>>(snapshot.Entries);
		Assert.Throws<NotSupportedException>(() => ((IList<ProductListPriceSnapshotEntry?>)snapshot.Entries)[0] = new(7, 2m));
	}

	public static TheoryData<ProductListPriceSnapshot> InvalidReviewedSnapshots => new()
	{
		new ProductListPriceSnapshot(isAvailable: false, entries: []),
		Snapshot(),
		Snapshot((0, 1m)),
		Snapshot((7, null)),
		Snapshot((7, -1m)),
		Snapshot((7, 1m), (7, 1m)),
		new ProductListPriceSnapshot(true, [null])
	};

	public static TheoryData<ProductListPriceSnapshot> InvalidFreshSnapshots => new()
	{
		Snapshot((0, 1m)),
		Snapshot((7, -1m)),
		Snapshot((7, 1m), (7, 1m)),
		new ProductListPriceSnapshot(true, [null]),
		new ProductListPriceSnapshot(false, [new ProductListPriceSnapshotEntry(7, 1m)])
	};

	private static ProductListPriceReviewResult Evaluate(
		ProductListPriceSnapshot previouslyReviewed,
		ProductListPriceSnapshot fresh) =>
		ProductListPriceReviewEvaluator.Evaluate(previouslyReviewed, fresh);

	private static ProductListPriceSnapshot Snapshot(params (int ProductId, decimal? ListPrice)[] entries) =>
		new(true, entries.Select(entry => new ProductListPriceSnapshotEntry(entry.ProductId, entry.ListPrice)));
}