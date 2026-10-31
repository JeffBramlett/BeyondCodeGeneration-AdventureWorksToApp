using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OfferReviewServiceTests
{
	private static readonly DateTimeOffset EvaluationInstant = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task InvalidDraft_IsRejectedWithoutCatalogReads()
	{
		var catalog = new FakeProductCatalogReadStore();
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));

		var result = await service.ReviewAsync(ValidRequest() with { Lines = [] }, CancellationToken.None);

		Assert.Equal(OfferReviewStatus.InvalidDraft, result.Status);
		Assert.Null(result.EvaluationInstant);
		Assert.Empty(result.Lines);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task DuplicateProductIds_AreRejectedBeforeCatalogReads()
	{
		var catalog = new FakeProductCatalogReadStore();
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));
		var request = ValidRequest() with { Lines = [new(7, 1, null), new(7, 2, null)] };

		var result = await service.ReviewAsync(request, CancellationToken.None);

		Assert.Equal(OfferReviewStatus.InvalidDraft, result.Status);
		Assert.Contains("lines[1].productId", result.Errors.Keys);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task SelectedEligibleOffer_IsCurrentAtOneCapturedInstant()
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.EligibilityByProduct[7] = new ProductOfferEligibilityReadResult(7,
		[
			new ProductOfferEligibilityRow(3, "Offer", EvaluationInstant, EvaluationInstant.AddHours(1), 2, 4)
		]);
		var timeProvider = new CountingTimeProvider(EvaluationInstant);
		var service = new OfferReviewService(catalog, timeProvider);

		var result = await service.ReviewAsync(
			ValidRequest() with { Lines = [new OrderDraftLineRequest(7, 3, 3)] },
			CancellationToken.None);

		Assert.Equal(OfferReviewStatus.Current, result.Status);
		Assert.Equal(EvaluationInstant, result.EvaluationInstant);
		Assert.Equal(1, timeProvider.CallCount);
		Assert.Equal(OfferLineReviewStatus.SelectedOfferCurrent, Assert.Single(result.Lines).Status);
	}

	[Theory]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	public async Task SelectedOfferThatIsAbsentOrIneligible_RequiresReview(int offerId)
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.EligibilityByProduct[7] = new ProductOfferEligibilityReadResult(7,
		[
			new ProductOfferEligibilityRow(3, "Offer", EvaluationInstant.AddHours(1), EvaluationInstant.AddHours(2), 1, 4),
			new ProductOfferEligibilityRow(5, "Expired", EvaluationInstant.AddHours(-2), EvaluationInstant, 1, null),
			new ProductOfferEligibilityRow(6, "Quantity mismatch", EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1), 1, 2)
		]);
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));

		var result = await service.ReviewAsync(
			ValidRequest() with { Lines = [new OrderDraftLineRequest(7, offerId == 6 ? 3 : 1, offerId)] },
			CancellationToken.None);

		Assert.Equal(OfferReviewStatus.ReviewRequired, result.Status);
		var line = Assert.Single(result.Lines);
		Assert.Equal(OfferLineReviewStatus.ReviewRequired, line.Status);
		Assert.Equal("lines[0].offerId", line.ErrorField);
		Assert.Contains("lines[0].offerId", result.Errors.Keys);
	}

	[Fact]
	public async Task SelectedOfferNotLinkedToProduct_RequiresReviewWithoutSubstitution()
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.EligibilityByProduct[7] = new ProductOfferEligibilityReadResult(7,
		[
			new ProductOfferEligibilityRow(11, "Other offer", EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1), 1, null)
		]);
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));

		var result = await service.ReviewAsync(
			ValidRequest() with { Lines = [new OrderDraftLineRequest(7, 1, 12)] },
			CancellationToken.None);

		Assert.Equal(OfferReviewStatus.ReviewRequired, result.Status);
		Assert.Equal(12, Assert.Single(result.Lines).OfferId);
		Assert.Equal(OfferLineReviewStatus.ReviewRequired, result.Lines[0].Status);
		Assert.DoesNotContain(result.Lines, line => line.Status == OfferLineReviewStatus.SelectedOfferCurrent);
	}

	[Theory]
	[InlineData(true, OfferLineReviewStatus.NoOfferMarkerSupported, OfferReviewStatus.Current)]
	[InlineData(false, OfferLineReviewStatus.ReviewRequired, OfferReviewStatus.ReviewRequired)]
	public async Task OmittedOffer_UsesPerProductMarkerCapability(
		bool markerSupported,
		OfferLineReviewStatus expectedLineStatus,
		OfferReviewStatus expectedStatus)
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.MarkerSupportByProduct[7] = markerSupported;
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));

		var result = await service.ReviewAsync(ValidRequest(), CancellationToken.None);

		Assert.Equal(expectedStatus, result.Status);
		Assert.Equal(expectedLineStatus, Assert.Single(result.Lines).Status);
		Assert.Equal(1, catalog.MarkerReadCalls);
		Assert.Equal(0, catalog.EligibilityReadCalls);
	}

	[Fact]
	public async Task MissingProduct_RequiresReviewOnProductField()
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.MarkerSupportByProduct[7] = null;
		var service = new OfferReviewService(catalog, new CountingTimeProvider(EvaluationInstant));

		var result = await service.ReviewAsync(ValidRequest(), CancellationToken.None);

		Assert.Equal(OfferReviewStatus.ReviewRequired, result.Status);
		Assert.Equal("lines[0].productId", Assert.Single(result.Errors.Keys));
	}

	[Fact]
	public async Task MultipleLinesShareOneEvaluationInstant_AndResultIsNotOrderReady()
	{
		var catalog = new FakeProductCatalogReadStore();
		catalog.MarkerSupportByProduct[7] = true;
		catalog.EligibilityByProduct[8] = new ProductOfferEligibilityReadResult(8,
		[
			new ProductOfferEligibilityRow(2, "Offer", EvaluationInstant.AddMinutes(-1), EvaluationInstant.AddMinutes(1), 1, null)
		]);
		var timeProvider = new CountingTimeProvider(EvaluationInstant);
		var service = new OfferReviewService(catalog, timeProvider);
		var request = ValidRequest() with { Lines = [new(7, 1, null), new(8, 1, 2)] };

		var result = await service.ReviewAsync(request, CancellationToken.None);

		Assert.Equal(OfferReviewStatus.Current, result.Status);
		Assert.Equal(EvaluationInstant, result.EvaluationInstant);
		Assert.Equal(1, timeProvider.CallCount);
		Assert.Equal(2, result.Lines.Count);
		Assert.DoesNotContain("ReadyToSubmit", Enum.GetNames<OfferReviewStatus>());
		Assert.DoesNotContain("Valid", Enum.GetNames<OfferReviewStatus>());
	}

	private static OrderDraftRequest ValidRequest() => new(
		CustomerId: 42,
		BillToAddressId: 11,
		ShipToAddressId: 12,
		ShipMethodId: 2,
		Lines: [new OrderDraftLineRequest(7, 1, null)]);

	private sealed class CountingTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public int CallCount { get; private set; }

		public override DateTimeOffset GetUtcNow()
		{
			CallCount++;
			return utcNow;
		}
	}

	private sealed class FakeProductCatalogReadStore : IProductCatalogReadStore
	{
		public Dictionary<int, ProductOfferEligibilityReadResult?> EligibilityByProduct { get; } = [];
		public Dictionary<int, bool?> MarkerSupportByProduct { get; } = [];
		public int EligibilityReadCalls { get; private set; }
		public int MarkerReadCalls { get; private set; }
		public int TotalReadCalls => EligibilityReadCalls + MarkerReadCalls;
		public bool IsConfigured => true;

		public Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(
			int productId,
			CancellationToken cancellationToken)
		{
			EligibilityReadCalls++;
			EligibilityByProduct.TryGetValue(productId, out var result);
			return Task.FromResult(result);
		}

		public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken)
		{
			MarkerReadCalls++;
			MarkerSupportByProduct.TryGetValue(productId, out var result);
			return Task.FromResult(result);
		}
	}
}