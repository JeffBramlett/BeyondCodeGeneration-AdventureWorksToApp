using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderEndpointTests
{
	private static readonly DateTime StoredOrderDate = new(2026, 4, 17, 13, 45, 12, DateTimeKind.Unspecified);

	[Fact]
	public async Task SearchByReference_UsesDevelopmentStaffAndReturnsAllowlistedResponse()
	{
		var store = new FakeOrderReadStore();
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: store);
		using var client = factory.CreateClient();

		using var response = await client.GetAsync("/api/orders?orderReference=%20so-500%20&page=2&pageSize=10");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("so-500", store.LastReference);
		Assert.Equal(2, store.LastSearch?.Page);
		Assert.Equal(10, store.LastSearch?.PageSize);
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var summary = body.RootElement.GetProperty("items")[0];
		Assert.Equal("SO-500", summary.GetProperty("orderReference").GetString());
		Assert.Equal("2026-04-17T13:45:12", summary.GetProperty("orderDate").GetString());
		Assert.Equal(new[] { "orderReference", "orderDate" }, summary.EnumerateObject().Select(property => property.Name));
		Assert.False(summary.TryGetProperty("salesOrderNumber", out _));
		Assert.DoesNotContain("salesOrderId", summary.ToString(), StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("customer", summary.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task SearchByDates_UsesCompleteRangeAndDefaultPaging()
	{
		var store = new FakeOrderReadStore();
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: store);
		using var client = factory.CreateClient();

		using var response = await client.GetAsync("/api/orders?fromDate=2026-04-01&toDate=2026-04-30");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(new DateOnly(2026, 4, 1), store.LastSearch?.FromDate);
		Assert.Equal(new DateOnly(2026, 4, 30), store.LastSearch?.ToDate);
		Assert.Equal(1, store.LastSearch?.Page);
		Assert.Equal(25, store.LastSearch?.PageSize);
	}

	[Theory]
	[InlineData("/api/orders")]
	[InlineData("/api/orders?orderReference=%20%20")]
	[InlineData("/api/orders?orderReference=SO-500&fromDate=2026-01-01&toDate=2026-01-02")]
	[InlineData("/api/orders?fromDate=2026-01-01")]
	[InlineData("/api/orders?fromDate=2026-01-01&toDate=2026-02-01")]
	[InlineData("/api/orders?orderReference=12345678901234567890123456")]
	[InlineData("/api/orders?orderReference=SO-500&pageSize=101")]
	[InlineData("/api/orders?orderReference=SO-500&page=0")]
	[InlineData("/api/orders?fromDate=not-a-date&toDate=2026-01-02")]
	public async Task Search_RejectsMissingEmptyConflictingOrOutOfBoundsInput(string path)
	{
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: new FakeOrderReadStore());
		using var client = factory.CreateClient();

		using var response = await client.GetAsync(path);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Detail_TrimsReferenceAndReturnsOnlyApprovedLineFieldsAndStoredDate()
	{
		var store = new FakeOrderReadStore();
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: store);
		using var client = factory.CreateClient();

		using var response = await client.GetAsync("/api/orders/%20SO-500%20?linePage=2&linePageSize=20");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("SO-500", store.LastDetailReference);
		Assert.Equal(2, store.LastLinesRequest?.LinePage);
		Assert.Equal(20, store.LastLinesRequest?.LinePageSize);
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("SO-500", body.RootElement.GetProperty("orderReference").GetString());
		Assert.Equal("2026-04-17T13:45:12", body.RootElement.GetProperty("orderDate").GetString());
		var line = body.RootElement.GetProperty("lines")[0];
		Assert.Equal(42, line.GetProperty("productId").GetInt32());
		Assert.Equal(3, line.GetProperty("orderQty").GetInt32());
		Assert.Equal(new[] { "productId", "orderQty" }, line.EnumerateObject().Select(property => property.Name));
		Assert.False(body.RootElement.TryGetProperty("salesOrderNumber", out _));
	}

	[Theory]
	[InlineData("/api/orders/SO-500?linePage=0")]
	[InlineData("/api/orders/SO-500?linePageSize=101")]
	[InlineData("/api/orders/%20%20")]
	[InlineData("/api/orders/12345678901234567890123456")]
	public async Task Detail_RejectsInvalidReferenceOrLinePaging(string path)
	{
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: new FakeOrderReadStore());
		using var client = factory.CreateClient();

		using var response = await client.GetAsync(path);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Detail_Returns404WhenReferenceDoesNotExist()
	{
		using var factory = new DevelopmentApiFactory(enabled: true, orderStore: new FakeOrderReadStore(missingDetail: true));
		using var client = factory.CreateClient();

		using var response = await client.GetAsync("/api/orders/SO-missing");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Endpoints_ReturnSanitized503WhenUnconfiguredOrReadAdapterFails()
	{
		using var unconfiguredFactory = new DevelopmentApiFactory(enabled: true);
		using var unconfiguredClient = unconfiguredFactory.CreateClient();
		using var unconfiguredResponse = await unconfiguredClient.GetAsync("/api/orders?orderReference=SO-500");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfiguredResponse.StatusCode);

		using var failingFactory = new DevelopmentApiFactory(
			enabled: true,
			orderStore: new FakeOrderReadStore(exception: new SqlReadAdapterUnavailableException()));
		using var failingClient = failingFactory.CreateClient();
		using var failingResponse = await failingClient.GetAsync("/api/orders/SO-500");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, failingResponse.StatusCode);
		var body = await failingResponse.Content.ReadAsStringAsync();
		Assert.Contains("exact order-column permissions", body);
		Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("SalesOrderHeader", body, StringComparison.OrdinalIgnoreCase);
	}

	private sealed class FakeOrderReadStore(
		OrderDetailPageDto? detail = null,
		Exception? exception = null,
		bool missingDetail = false) : IOrderReadStore
	{
		public bool IsConfigured => true;
		public OrderSearchRequest? LastSearch { get; private set; }
		public string? LastReference { get; private set; }
		public string? LastDetailReference { get; private set; }
		public OrderLinesRequest? LastLinesRequest { get; private set; }

		public Task<OrderPageDto> SearchAsync(OrderSearchRequest request, CancellationToken cancellationToken)
		{
			LastSearch = request;
			LastReference = request.OrderReference?.Trim();
			if (exception is not null)
			{
				throw exception;
			}

			return Task.FromResult(new OrderPageDto(
				[new OrderSummaryDto("SO-500", StoredOrderDate)],
				request.Page,
				request.PageSize,
				false));
		}

		public Task<OrderDetailPageDto?> GetByReferenceAsync(
			string orderReference,
			OrderLinesRequest request,
			CancellationToken cancellationToken)
		{
			LastDetailReference = orderReference;
			LastLinesRequest = request;
			if (exception is not null)
			{
				throw exception;
			}

			if (missingDetail)
			{
				return Task.FromResult<OrderDetailPageDto?>(null);
			}

			return Task.FromResult<OrderDetailPageDto?>(detail ?? new OrderDetailPageDto(
				"SO-500",
				StoredOrderDate,
				[new OrderLineDto(42, 3)],
				request.LinePage,
				request.LinePageSize,
				false));
		}
	}
}
