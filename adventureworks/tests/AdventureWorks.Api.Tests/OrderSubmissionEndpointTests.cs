using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdventureWorks.Api.Application;
using AdventureWorks.Api.Authentication;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderSubmissionEndpointTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 13, 43, 55, 789, TimeSpan.Zero);

	private static CustomerOrderOptionsDto Options => new(
		[
			new CustomerAddressOptionDto(501, "1 Main St", null, "Seattle", "98101", ["Main Office"]),
			new CustomerAddressOptionDto(502, "2 Dock Rd", null, "Seattle", "98101", ["Shipping"])
		],
		[new ShipMethodOptionDto(5, "CARGO TRANSPORT 5")]);

	private static OrderSubmissionCatalogFake Catalog()
	{
		var catalog = new OrderSubmissionCatalogFake();
		catalog.AddProduct(707, "Sport-100 Helmet, Red", 34.99m, noOfferSupported: true);
		catalog.AddProduct(712, "AWC Logo Cap", 8.99m, noOfferSupported: true);
		catalog.AddProduct(900, "Offer-only product", 10.00m, noOfferSupported: false);
		catalog.AddProduct(901, "Unpriced product", null, noOfferSupported: true);
		catalog.AddProduct(680, "Tax rounding product", 1.00m, noOfferSupported: true);
		catalog.AddOffer(707, new ProductOfferEligibilityRow(2, "Volume Discount 2 to 14", Now.AddDays(-1), Now.AddDays(1), 2, 14), 0.02m);
		return catalog;
	}

	private static object Body(string? token = null, int shipTo = 502, params object[] lines) => new
	{
		submissionToken = token ?? Guid.NewGuid().ToString(),
		customerId = 42,
		billToAddressId = 501,
		shipToAddressId = shipTo,
		shipMethodId = 5,
		lines
	};

	private static object Line(int productId, int quantity, int? offerId) => new { productId, quantity, offerId };

	[Fact]
	public async Task Submit_HappyPath_Returns201WithServerAuthoritativeFinancials()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(707, 3, 2), Line(712, 2, null)));

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Assert.Equal("/api/orders/SO90001", response.Headers.Location?.OriginalString);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var root = json.RootElement;
		Assert.Equal("SO90001", root.GetProperty("orderReference").GetString());
		Assert.Equal("Recorded", root.GetProperty("status").GetString());
		Assert.Equal(42, root.GetProperty("customerId").GetInt32());
		Assert.Equal(501, root.GetProperty("billToAddressId").GetInt32());
		Assert.Equal(502, root.GetProperty("shipToAddressId").GetInt32());
		Assert.Equal(5, root.GetProperty("shipMethodId").GetInt32());

		var lines = root.GetProperty("lines");
		Assert.Equal(2, lines.GetArrayLength());
		var discounted = lines[0];
		Assert.Equal(707, discounted.GetProperty("productId").GetInt32());
		Assert.Equal("Sport-100 Helmet, Red", discounted.GetProperty("productName").GetString());
		Assert.Equal(3, discounted.GetProperty("quantity").GetInt32());
		Assert.Equal(2, discounted.GetProperty("offerId").GetInt32());
		Assert.Equal("Volume Discount 2 to 14", discounted.GetProperty("offerDescription").GetString());
		Assert.Equal(34.99m, discounted.GetProperty("unitPrice").GetDecimal());
		Assert.Equal(0.02m, discounted.GetProperty("unitPriceDiscount").GetDecimal());
		Assert.Equal(102.8706m, discounted.GetProperty("lineTotal").GetDecimal());
		var noOffer = lines[1];
		Assert.Equal(1, noOffer.GetProperty("offerId").GetInt32());
		Assert.Equal(JsonValueKind.Null, noOffer.GetProperty("offerDescription").ValueKind);
		Assert.Equal(0m, noOffer.GetProperty("unitPriceDiscount").GetDecimal());
		Assert.Equal(17.98m, noOffer.GetProperty("lineTotal").GetDecimal());

		// 102.8706 + 17.98 = 120.8506; tax = round(9.063795, 2) = 9.06; freight 25.00.
		Assert.Equal(120.8506m, root.GetProperty("subTotal").GetDecimal());
		Assert.Equal(9.06m, root.GetProperty("taxAmt").GetDecimal());
		Assert.Equal(25.00m, root.GetProperty("freight").GetDecimal());
		Assert.Equal(154.9106m, root.GetProperty("totalDue").GetDecimal());

		var command = Assert.Single(writer.Commands);
		Assert.Equal(9.06m, command.TaxAmt);
		Assert.Equal(25.00m, command.Freight);
		Assert.Equal(0, command.OrderDate.Millisecond);
		Assert.Equal(command.OrderDate.AddDays(7), command.DueDate);
		Assert.Equal(new OrderWriteLine(707, 3, 2, 34.99m, 0.02m), command.Lines[0]);
		Assert.Equal(new OrderWriteLine(712, 2, 1, 8.99m, 0m), command.Lines[1]);
	}

	[Fact]
	public async Task Submit_TaxUsesAwayFromZeroMidpointRounding()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		// 3 x 1.00 = 3.00; 3.00 x 0.075 = 0.225 -> 0.23 (banker's rounding would give 0.22).
		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(680, 3, null)));

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<OrderSubmissionConfirmationDto>();
		Assert.Equal(3.00m, body!.SubTotal);
		Assert.Equal(0.23m, body.TaxAmt);
		Assert.Equal(28.23m, body.TotalDue);
	}

	[Fact]
	public async Task Submit_InvalidShape_Returns400WithFieldKeysAndNoWrite()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", new
		{
			submissionToken = "not-a-guid",
			customerId = 0,
			billToAddressId = 501,
			shipToAddressId = 502,
			shipMethodId = 5,
			lines = new[] { Line(707, 0, null), Line(707, 1, null) }
		});

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.BadRequest);
		Assert.Contains("submissionToken", problem.Errors.Keys);
		Assert.Contains("customerId", problem.Errors.Keys);
		Assert.Contains("lines[0].quantity", problem.Errors.Keys);
		Assert.Contains("lines[1].productId", problem.Errors.Keys);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_EmptyLines_Returns400()
	{
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), new FakeOrderWriteStore());
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.BadRequest);
		Assert.Contains("lines", problem.Errors.Keys);
	}

	[Fact]
	public async Task Submit_AddressNotOwnedByCustomer_Returns422()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 999, Line(712, 1, null)));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.UnprocessableEntity);
		Assert.Equal(new[] { "shipToAddressId" }, problem.Errors.Keys);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_UnknownCustomer_Returns422OnCustomerId()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(null, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null)));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.UnprocessableEntity);
		Assert.Equal(new[] { "customerId" }, problem.Errors.Keys);
		Assert.Equal(0, writer.CallCount);
	}

	[Theory]
	[InlineData(1, 2)]   // below MinQty 2
	[InlineData(15, 2)]  // above MaxQty 14
	[InlineData(3, 99)]  // not linked to the product
	public async Task Submit_IneligibleOffer_Returns422OnLineOfferId(int quantity, int offerId)
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null), Line(707, quantity, offerId)));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.UnprocessableEntity);
		Assert.Equal(new[] { "lines[1].offerId" }, problem.Errors.Keys);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_NoOfferForProductWithoutOfferOneLink_Returns422AskingForEligibleOffer()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(900, 1, null)));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.UnprocessableEntity);
		var message = Assert.Single(problem.Errors["lines[0].offerId"]);
		Assert.Contains("eligible offer", message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_ProductMissingOrUnpriced_Returns422OnLineProductId()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(901, 1, null), Line(12345, 1, null)));

		var problem = await AssertValidationProblemAsync(response, HttpStatusCode.UnprocessableEntity);
		Assert.Contains("lines[0].productId", problem.Errors.Keys);
		Assert.Contains("lines[1].productId", problem.Errors.Keys);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_ReplayedToken_Returns200WithSameBodyAndWritesOnce()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();
		var body = Body(Guid.NewGuid().ToString(), 502, Line(707, 3, 2));

		using var first = await client.PostAsJsonAsync("/api/orders", body);
		using var second = await client.PostAsJsonAsync("/api/orders", body);

		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
		Assert.Equal(1, writer.CallCount);
	}

	[Fact]
	public async Task Submit_TokenInFlight_Returns409()
	{
		var writer = new FakeOrderWriteStore { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();
		var body = Body(Guid.NewGuid().ToString(), 502, Line(712, 1, null));

		var firstTask = client.PostAsJsonAsync("/api/orders", body);
		await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		using var second = await client.PostAsJsonAsync("/api/orders", body);

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
		Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
		var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
		Assert.Equal("Submission already in progress", problem!.Title);

		writer.Gate.SetResult();
		using var first = await firstTask;
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		Assert.Equal(1, writer.CallCount);
	}

	[Fact]
	public async Task Submit_WriterFailure_Returns503AndReleasesTokenForRetry()
	{
		var writer = new FakeOrderWriteStore { FailNextCall = true };
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();
		var body = Body(Guid.NewGuid().ToString(), 502, Line(712, 1, null));

		using var failed = await client.PostAsJsonAsync("/api/orders", body);
		await AssertProblemAsync(failed, HttpStatusCode.ServiceUnavailable, "Order submission unavailable");
		var failedText = await failed.Content.ReadAsStringAsync();
		Assert.DoesNotContain("Password", failedText, StringComparison.OrdinalIgnoreCase);

		using var retried = await client.PostAsJsonAsync("/api/orders", body);
		Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
		Assert.Equal(2, writer.CallCount);
	}

	[Fact]
	public async Task Submit_ValidationFailure_ReleasesTokenForRetry()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer);
		using var client = factory.CreateClient();
		var token = Guid.NewGuid().ToString();

		using var rejected = await client.PostAsJsonAsync("/api/orders", Body(token, 999, Line(712, 1, null)));
		Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

		using var corrected = await client.PostAsJsonAsync("/api/orders", Body(token, 502, Line(712, 1, null)));
		Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
	}

	[Fact]
	public async Task Submit_WriterUnconfigured_Returns503()
	{
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), new UnconfiguredOrderWriteStore());
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null)));

		await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Order submission unavailable");
	}

	[Fact]
	public async Task Submit_ReaderUnconfigured_Returns503()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer, offerPricingOverride: new UnconfiguredOfferPricingReadStore());
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null)));

		await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Order submission unavailable");
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_AnonymousCaller_Returns401WithoutWrite()
	{
		var writer = new FakeOrderWriteStore();
		using var factory = new OrderSubmissionApiFactory(Options, Catalog(), writer, authenticateCaller: false);
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null)));

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Equal(0, writer.CallCount);
	}

	[Fact]
	public async Task Submit_AnonymousCallerOnProductionHost_Returns401()
	{
		using var factory = new OfflineApiFactory();
		using var client = factory.CreateClient();

		using var response = await client.PostAsJsonAsync("/api/orders", Body(null, 502, Line(712, 1, null)));

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private static async Task<ValidationProblemDetails> AssertValidationProblemAsync(HttpResponseMessage response, HttpStatusCode status)
	{
		Assert.Equal(status, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
		Assert.NotNull(problem);
		Assert.Equal((int)status, problem.Status);
		return problem;
	}

	private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
	{
		Assert.Equal(status, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
		Assert.Equal(title, problem!.Title);
	}

	private sealed class OrderSubmissionApiFactory(
		CustomerOrderOptionsDto? options,
		OrderSubmissionCatalogFake catalog,
		IOrderWriteStore writer,
		IOfferPricingReadStore? offerPricingOverride = null,
		bool authenticateCaller = true) : OfflineApiFactory
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);
			builder.UseEnvironment("Development");
			builder.UseUrls("http://127.0.0.1:5080");
			builder.UseSetting(ApiAuthentication.DevelopmentAuthenticationEnvironmentVariable, "true");
			builder.ConfigureTestServices(services =>
			{
				services.AddAuthentication(authentication =>
				{
					authentication.DefaultAuthenticateScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
					authentication.DefaultChallengeScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
					authentication.DefaultForbidScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
				}).AddScheme<TestDevelopmentStaffAuthenticationOptions, TestDevelopmentStaffAuthenticationHandler>(
					TestDevelopmentStaffAuthenticationHandler.SchemeName,
					schemeOptions => schemeOptions.AuthenticateCaller = authenticateCaller);
				services.RemoveAll<IAuthenticationSchemeProvider>();
				services.AddSingleton<IAuthenticationSchemeProvider>(serviceProvider =>
				{
					var schemeProvider = new AuthenticationSchemeProvider(
						serviceProvider.GetRequiredService<IOptions<AuthenticationOptions>>());
					schemeProvider.RemoveScheme(ApiAuthentication.DevelopmentScheme);
					return schemeProvider;
				});

				services.RemoveAll<ICustomerReadStore>();
				services.AddSingleton<ICustomerReadStore>(new SpyCustomerReadStore(options));
				services.RemoveAll<IProductCatalogReadStore>();
				services.AddSingleton<IProductCatalogReadStore>(catalog);
				services.RemoveAll<IOfferPricingReadStore>();
				services.AddSingleton(offerPricingOverride ?? catalog);
				services.RemoveAll<IOrderWriteStore>();
				services.AddSingleton(writer);
				services.RemoveAll<TimeProvider>();
				services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
			});
		}
	}
}

public sealed class OrderSubmissionCatalogFake : IProductCatalogReadStore, IOfferPricingReadStore
{
	private readonly Dictionary<int, ProductDetailDto> products = [];
	private readonly Dictionary<int, bool> noOfferSupport = [];
	private readonly Dictionary<int, List<(ProductOfferEligibilityRow Row, decimal DiscountPct)>> offers = [];

	public bool IsConfigured => true;

	public void AddProduct(int productId, string name, decimal? listPrice, bool noOfferSupported)
	{
		products[productId] = new ProductDetailDto(productId, name, $"PN-{productId}", null, null, listPrice);
		noOfferSupport[productId] = noOfferSupported;
		offers.TryAdd(productId, []);
	}

	public void AddOffer(int productId, ProductOfferEligibilityRow row, decimal discountPct) =>
		offers[productId].Add((row, discountPct));

	public Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken) =>
		Task.FromResult(new ProductPageDto([], request.Page, request.PageSize, false));

	public Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken) =>
		Task.FromResult(products.GetValueOrDefault(productId));

	public Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<ProductOfferDto>>([]);

	public Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(int productId, CancellationToken cancellationToken) =>
		Task.FromResult(products.ContainsKey(productId)
			? new ProductOfferEligibilityReadResult(productId, offers[productId].Select(offer => offer.Row).ToArray())
			: null);

	public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken) =>
		Task.FromResult(noOfferSupport.TryGetValue(productId, out var supported) ? supported : (bool?)null);

	public Task<OfferPricingRow?> GetOfferPricingAsync(int productId, int specialOfferId, CancellationToken cancellationToken)
	{
		var match = offers.TryGetValue(productId, out var list)
			? list.FirstOrDefault(offer => offer.Row.SpecialOfferId == specialOfferId)
			: default;
		return Task.FromResult(match.Row is null
			? null
			: new OfferPricingRow(match.Row.SpecialOfferId, match.Row.Description, match.DiscountPct));
	}
}

public sealed class FakeOrderWriteStore : IOrderWriteStore
{
	private int callCount;
	private int nextOrderId = 90000;

	public bool IsConfigured => true;
	public bool FailNextCall { get; set; }
	public TaskCompletionSource? Gate { get; init; }
	public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public List<OrderWriteCommand> Commands { get; } = [];
	public int CallCount => Volatile.Read(ref callCount);

	public async Task<PersistedOrder> CreateOrderAsync(OrderWriteCommand command, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref callCount);
		Entered.TrySetResult();
		if (Gate is not null)
		{
			await Gate.Task.WaitAsync(cancellationToken);
		}

		if (FailNextCall)
		{
			FailNextCall = false;
			throw new OrderWriteUnavailableException();
		}

		lock (Commands)
		{
			Commands.Add(command);
		}

		var orderId = Interlocked.Increment(ref nextOrderId);
		var lines = command.Lines
			.Select(line => new PersistedOrderLine(
				line.ProductId,
				line.Quantity,
				line.SpecialOfferId,
				line.UnitPrice,
				line.UnitPriceDiscount,
				DemoOrderFinancials.LineTotal(line.UnitPrice, line.UnitPriceDiscount, line.Quantity)))
			.ToArray();
		var subTotal = DemoOrderFinancials.Subtotal(lines.Select(line => line.LineTotal));
		return new PersistedOrder(
			orderId,
			$"SO{orderId}",
			command.OrderDate,
			command.DueDate,
			subTotal,
			command.TaxAmt,
			command.Freight,
			DemoOrderFinancials.TotalDue(subTotal, command.TaxAmt, command.Freight),
			lines);
	}
}
