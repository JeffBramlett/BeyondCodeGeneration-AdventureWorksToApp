using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderDraftPreflightTests
{
	private static readonly DateTimeOffset EvaluationInstant = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task InvalidDraft_DoesNotReadCustomerOptionsOrCatalog()
	{
		var customers = new FakeCustomerReadStore();
		var catalog = new FakeProductCatalogReadStore();
		var service = CreateService(customers, catalog);

		var result = await service.ReviewAsync(ValidRequest() with { Lines = [] }, CancellationToken.None);

		Assert.Equal(OrderDraftPreflightStatus.InvalidDraft, result.Status);
		Assert.Null(result.Draft);
		Assert.Equal(0, customers.OrderOptionsReadCalls);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task AvailableOptionsAndCurrentOffer_CompleteOnlyReadOnlyPreflight()
	{
		var customers = new FakeCustomerReadStore { OrderOptions = ValidOptions() };
		var catalog = new FakeProductCatalogReadStore();
		catalog.MarkerSupportByProduct[7] = true;
		var service = CreateService(customers, catalog);

		var result = await service.ReviewAsync(ValidRequest(), CancellationToken.None);

		Assert.Equal(OrderDraftPreflightStatus.ReadOnlyPreflightReviewed, result.Status);
		Assert.Equal(1, customers.OrderOptionsReadCalls);
		Assert.Equal(42, customers.LastCustomerId);
		Assert.Equal(OfferReviewStatus.Current, result.OfferReview!.Status);
		Assert.Equal(OrderDraftPriceReviewStatus.NotPerformed, result.PriceReviewStatus);
		Assert.Contains("No reviewed price snapshot", result.PriceReviewGap);
		Assert.Contains("changed-price review was not performed", result.PriceReviewGap);
		Assert.False(result.IsAtomic);
		Assert.False(result.IsSubmissionAuthorized);
		Assert.Equal(1, catalog.MarkerReadCalls);
	}

	[Theory]
	[InlineData(10, 12, 2, "billToAddressId")]
	[InlineData(11, 99, 2, "shipToAddressId")]
	[InlineData(11, 12, 99, "shipMethodId")]
	public async Task MissingSelection_IsRejectedBeforeOfferReview(
		int billToAddressId,
		int shipToAddressId,
		int shipMethodId,
		string expectedError)
	{
		var customers = new FakeCustomerReadStore { OrderOptions = ValidOptions() };
		var catalog = new FakeProductCatalogReadStore();
		var service = CreateService(customers, catalog);

		var request = ValidRequest() with
		{
			BillToAddressId = billToAddressId,
			ShipToAddressId = shipToAddressId,
			ShipMethodId = shipMethodId
		};
		var result = await service.ReviewAsync(request, CancellationToken.None);

		Assert.Equal(OrderDraftPreflightStatus.SelectionRequiresReview, result.Status);
		Assert.Contains(expectedError, result.Errors.Keys);
		Assert.Null(result.OfferReview);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task MissingCustomerOptions_FailClosedWithoutOfferReview()
	{
		var customers = new FakeCustomerReadStore();
		var catalog = new FakeProductCatalogReadStore();
		var service = CreateService(customers, catalog);

		var result = await service.ReviewAsync(ValidRequest(), CancellationToken.None);

		Assert.Equal(OrderDraftPreflightStatus.CustomerOptionsUnavailable, result.Status);
		Assert.Contains("customerId", result.Errors.Keys);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task StaleOfferReview_IsDelegatedAndRequiresReview()
	{
		var customers = new FakeCustomerReadStore { OrderOptions = ValidOptions() };
		var catalog = new FakeProductCatalogReadStore();
		catalog.EligibilityByProduct[7] = new ProductOfferEligibilityReadResult(7, []);
		var service = CreateService(customers, catalog);
		var request = ValidRequest() with { Lines = [new OrderDraftLineRequest(7, 1, 3)] };

		var result = await service.ReviewAsync(request, CancellationToken.None);

		Assert.Equal(OrderDraftPreflightStatus.OffersRequireReview, result.Status);
		Assert.Equal(OfferReviewStatus.ReviewRequired, result.OfferReview!.Status);
		Assert.Contains("lines[0].offerId", result.Errors.Keys);
		Assert.Equal(1, catalog.EligibilityReadCalls);
	}

	[Fact]
	public async Task Cancellation_IsPropagatedBeforeCustomerRead()
	{
		var customers = new FakeCustomerReadStore { OrderOptions = ValidOptions() };
		var catalog = new FakeProductCatalogReadStore();
		var service = CreateService(customers, catalog);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await service.ReviewAsync(ValidRequest(), cancellation.Token));

		Assert.Equal(0, customers.OrderOptionsReadCalls);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	[Fact]
	public async Task CancellationDuringCustomerRead_StopsBeforeOfferReview()
	{
		using var cancellation = new CancellationTokenSource();
		var customers = new FakeCustomerReadStore
		{
			OrderOptions = ValidOptions(),
			OnOrderOptionsRead = cancellation.Cancel
		};
		var catalog = new FakeProductCatalogReadStore();
		var service = CreateService(customers, catalog);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await service.ReviewAsync(ValidRequest(), cancellation.Token));

		Assert.Equal(1, customers.OrderOptionsReadCalls);
		Assert.Equal(0, catalog.TotalReadCalls);
	}

	private static OrderDraftPreflightService CreateService(
		FakeCustomerReadStore customers,
		FakeProductCatalogReadStore catalog) =>
		new(customers, new OfferReviewService(catalog, new FixedTimeProvider(EvaluationInstant)));

	private static OrderDraftRequest ValidRequest() => new(
		CustomerId: 42,
		BillToAddressId: 11,
		ShipToAddressId: 12,
		ShipMethodId: 2,
		Lines: [new OrderDraftLineRequest(7, 1, null)]);

	private static CustomerOrderOptionsDto ValidOptions() => new(
		[
			new CustomerAddressOptionDto(11, "1 Main St", null, "Seattle", "98101", ["Billing"]),
			new CustomerAddressOptionDto(12, "2 Main St", null, "Seattle", "98101", ["Shipping"])
		],
		[new ShipMethodOptionDto(2, "Ground")]);

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}

	private sealed class FakeCustomerReadStore : ICustomerReadStore
	{
		public CustomerOrderOptionsDto? OrderOptions { get; init; }
		public Action? OnOrderOptionsRead { get; init; }
		public int OrderOptionsReadCalls { get; private set; }
		public int LastCustomerId { get; private set; }
		public bool IsConfigured => true;

		public Task<CustomerOrderOptionsDto?> GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			OrderOptionsReadCalls++;
			LastCustomerId = customerId;
			OnOrderOptionsRead?.Invoke();
			return Task.FromResult(OrderOptions);
		}

		public Task<CustomerPageDto> SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
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
			cancellationToken.ThrowIfCancellationRequested();
			EligibilityReadCalls++;
			EligibilityByProduct.TryGetValue(productId, out var result);
			return Task.FromResult(result);
		}

		public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			MarkerReadCalls++;
			MarkerSupportByProduct.TryGetValue(productId, out var result);
			return Task.FromResult(result);
		}
	}
}