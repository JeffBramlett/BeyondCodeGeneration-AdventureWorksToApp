using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AdventureWorks.Api;
using AdventureWorks.Api.Application;
using AdventureWorks.Api.Authentication;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class ApiEndpointTests(OfflineApiFactory factory) : IClassFixture<OfflineApiFactory>, IDisposable
{
	public static TheoryData<decimal?> ProductListPriceCases => new()
	{
		0m,
		null
	};

	private readonly HttpClient client = factory.CreateClient();

	public void Dispose() => client.Dispose();

	[Fact]
	public async Task Health_ReturnsJsonSuccess()
	{
		using var response = await client.GetAsync("/api/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
		Assert.Equal(new[] { "status" }, body.RootElement.EnumerateObject().Select(property => property.Name));
	}

	[Fact]
	public async Task AuthenticatedStaff_CanLoadSpaIndexAndGeneratedAsset()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();

		using var indexResponse = await developmentClient.GetAsync("/");
		Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
		Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
		var index = await indexResponse.Content.ReadAsStringAsync();
		Assert.Contains("<div id=\"root\"></div>", index, StringComparison.Ordinal);

		var assetPath = Regex.Match(index, "src=\"(?<path>/assets/[^\"]+\\.js)\"")
			.Groups["path"].Value;
		Assert.False(string.IsNullOrWhiteSpace(assetPath));

		using var assetResponse = await developmentClient.GetAsync(assetPath);
		Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
		Assert.Contains("javascript", assetResponse.Content.Headers.ContentType?.MediaType);
		Assert.NotEmpty(await assetResponse.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task AnonymousCaller_CannotLoadSpaAssetsOrUnknownApi()
	{
		using var response = await client.GetAsync("/");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

		using var assetResponse = await client.GetAsync("/favicon.svg");
		Assert.Equal(HttpStatusCode.Unauthorized, assetResponse.StatusCode);

		using var missingApiResponse = await client.GetAsync("/api/no-such-endpoint");
		Assert.Equal(HttpStatusCode.Unauthorized, missingApiResponse.StatusCode);
		Assert.NotEqual("text/html", missingApiResponse.Content.Headers.ContentType?.MediaType);

		using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
		using var missingPostResponse = await client.PostAsync("/api/orders", content);
		Assert.Equal(HttpStatusCode.Unauthorized, missingPostResponse.StatusCode);
	}

	[Fact]
	public async Task AuthenticatedStaff_UnknownApiReturnsNotFound()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/no-such-endpoint");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
	}

	[Fact]
	public async Task SpaLoadsWithoutDatabase_WhileBusinessApiFailsClosed()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();
		var productStore = developmentFactory.Services.GetRequiredService<IProductCatalogReadStore>();
		Assert.IsType<UnconfiguredProductCatalogReadStore>(productStore);
		Assert.False(productStore.IsConfigured);

		using var indexResponse = await developmentClient.GetAsync("/");
		Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);

		using var productsResponse = await developmentClient.GetAsync("/api/products");
		await AssertProblemAsync(productsResponse, HttpStatusCode.ServiceUnavailable, "Catalog data source unavailable");
	}

	[Fact]
	public async Task OrderSubmissionRoute_IsHandledByControllerNotCatchAll()
	{
		// Controller POST /api/orders takes precedence over the authenticated /api/{**path} catch-all.
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();
		using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
		using var response = await developmentClient.PostAsync("/api/orders", content);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		var serviceInspector = developmentFactory.Services.GetRequiredService<IServiceProviderIsService>();
		Assert.True(serviceInspector.IsService(typeof(OrderSubmissionService)));
		Assert.False(serviceInspector.IsService(typeof(IdempotencyOutcomeClassifier)));
		Assert.False(serviceInspector.IsService(typeof(OrderReplaySnapshot)));
		Assert.False(serviceInspector.IsService(typeof(LocalPriceReviewReceiptStore)));
	}

	[Fact]
	public void DevelopmentIdentity_AddsStaffClaimOnlyToAuthenticatedCaller()
	{
		var caller = new ClaimsPrincipal(new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, "TEST\\windows-caller")],
			NegotiateDefaults.AuthenticationScheme));
		Assert.True(DevelopmentStaffIdentity.AddInternalStaffClaim(caller));
		Assert.Equal(
			ApiAuthentication.InternalStaffClaimValue,
			caller.FindFirst(ApiAuthentication.InternalStaffClaimType)?.Value);
		Assert.Equal(
			ApiAuthentication.DevelopmentIdentityIssuer,
			caller.FindFirst(ApiAuthentication.InternalStaffClaimType)?.Issuer);
		Assert.Equal("TEST\\windows-caller", caller.Identity?.Name);

		var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
		Assert.False(DevelopmentStaffIdentity.AddInternalStaffClaim(anonymous));
		Assert.Null(anonymous.FindFirst(ApiAuthentication.InternalStaffClaimType));
	}

	[Fact]
	public async Task DevelopmentAuthentication_RegistersNegotiateAndStaffClaimMapping()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, useTestAuthentication: false);
		var schemeProvider = developmentFactory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
		var scheme = await schemeProvider.GetSchemeAsync(ApiAuthentication.DevelopmentScheme);
		Assert.Equal(typeof(NegotiateHandler), scheme?.HandlerType);
		var authenticationOptions = developmentFactory.Services
			.GetRequiredService<IOptions<AuthenticationOptions>>()
			.Value;
		Assert.Equal(ApiAuthentication.DevelopmentScheme, authenticationOptions.DefaultAuthenticateScheme);
		Assert.Equal(ApiAuthentication.DevelopmentScheme, authenticationOptions.DefaultChallengeScheme);

		var negotiateOptions = developmentFactory.Services
			.GetRequiredService<IOptionsMonitor<NegotiateOptions>>()
			.Get(ApiAuthentication.DevelopmentScheme);
		var negotiateEvents = negotiateOptions.Events;
		Assert.NotNull(negotiateEvents);
		Assert.NotNull(negotiateEvents.OnAuthenticated);
	}

	[Theory]
	[InlineData("/api/products")]
	[InlineData("/api/products/1")]
	[InlineData("/api/products/1/offers")]
	[InlineData("/api/products/1/eligible-offers?quantity=1")]
	[InlineData("/api/products/1/no-offer-persistence")]
	[InlineData("/api/customers")]
	[InlineData("/api/customers/42/order-options")]
	[InlineData("/api/orders?orderReference=SO-501")]
	[InlineData("/api/orders/SO-501")]
	public async Task BusinessEndpoints_RequireAuthentication(string path)
	{
		using var response = await client.GetAsync(path);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task DevelopmentStaffIdentity_ReachesNormalEndpointBehavior()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();
		using var response = await developmentClient.GetAsync("/api/products");

		await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Catalog data source unavailable");
	}

	[Fact]
	public async Task DevelopmentAuthentication_UnauthenticatedCallerReturnsUnauthorized()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, authenticateTestCaller: false);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/products");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task DevelopmentStaffIdentity_CustomerOrderOptionsUsesConfiguredStore()
	{
		var expected = new CustomerOrderOptionsDto(
			[new CustomerAddressOptionDto(7, "1 Example Road", null, "Example City", "00000", ["Home"])],
			[new ShipMethodOptionDto(2, "Ground")]);
		var store = new SpyCustomerReadStore(expected);
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, customerStore: store);
		using var developmentClient = developmentFactory.CreateClient();
		using var response = await developmentClient.GetAsync("/api/customers/42/order-options");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var actual = await response.Content.ReadFromJsonAsync<CustomerOrderOptionsDto>();
		Assert.NotNull(actual);
		Assert.Equal(expected.Addresses[0].AddressId, actual.Addresses[0].AddressId);
		Assert.Equal(expected.Addresses[0].AddressLine1, actual.Addresses[0].AddressLine1);
		Assert.Equal(expected.Addresses[0].AddressTypes, actual.Addresses[0].AddressTypes);
		Assert.Equal(expected.ShipMethods[0], actual.ShipMethods[0]);
		Assert.Equal(1, store.OrderOptionsCallCount);
	}

	[Theory]
	[MemberData(nameof(ProductListPriceCases))]
	public async Task ProductDetail_PreservesZeroVersusUnavailableListPrice(decimal? listPrice)
	{
		var expected = new ProductDetailDto(42, "Demo product", "DEMO-42", null, null, listPrice);
		var store = new SpyProductCatalogReadStore(null, productDetail: expected);
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, productStore: store);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/products/42");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var actual = await response.Content.ReadFromJsonAsync<ProductDetailDto>();
		Assert.Equal(expected, actual);
		Assert.Equal(listPrice, actual?.ListPrice);
	}

	[Fact]
	public async Task DevelopmentAuthenticationDisabled_ReturnsUnauthorized()
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: false);
		using var developmentClient = developmentFactory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
		request.Headers.Add("X-Internal-Staff", "true");
		request.Headers.Add("X-User", "local-development-staff");
		using var response = await developmentClient.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public void DevelopmentAuthenticationOutsideDevelopment_IsRejected()
	{
		using var productionFactory = new OutsideDevelopmentApiFactory();
		Assert.ThrowsAny<InvalidOperationException>(() => productionFactory.CreateClient());
	}

	[Fact]
	public async Task AuthenticatedNonStaffIdentity_ReturnsForbidden()
	{
		using var nonStaffFactory = new NonStaffApiFactory();
		using var nonStaffClient = nonStaffFactory.CreateClient();
		using var response = await nonStaffClient.GetAsync("/api/products");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task AuthenticatedNonStaff_CustomerOrderOptionsReturnsForbiddenWithoutInvokingStore()
	{
		var store = new SpyCustomerReadStore(new CustomerOrderOptionsDto([], []));
		using var nonStaffFactory = new NonStaffApiFactory(store);
		using var nonStaffClient = nonStaffFactory.CreateClient();
		using var response = await nonStaffClient.GetAsync("/api/customers/42/order-options");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(0, store.InvocationCount);
	}

	[Fact]
	public void DevelopmentAuthentication_RejectsNonLoopbackConfiguredOrListeningAddress()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:5080;http://0.0.0.0:5081" })
			.Build();
		Assert.Throws<InvalidOperationException>((Action)(() => DevelopmentLoopbackAddressGuard.ValidateConfiguredListeners(configuration)));
		Assert.Throws<InvalidOperationException>((Action)(() => DevelopmentLoopbackAddressGuard.ValidateListeningAddresses(
			new[] { "http://127.0.0.1:5080", "http://192.168.1.10:5081" })));
		DevelopmentLoopbackAddressGuard.ValidateListeningAddresses(["http://localhost:5080", "http://[::1]:5081"]);
	}

	[Fact]
	public void DevelopmentAuthentication_RejectsWildcardBind()
	{
		using var nonLoopbackFactory = new DevelopmentApiFactory(enabled: true, urls: "http://0.0.0.0:5080");
		Assert.ThrowsAny<InvalidOperationException>(() => nonLoopbackFactory.CreateClient());
	}

	[Theory]
	[InlineData("/api/products?page=0")]
	[InlineData("/api/customers?pageSize=101")]
	public async Task Search_WithInvalidBounds_ReturnsValidationProblem(string path)
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();
		using var response = await developmentClient.GetAsync(path);

		await AssertProblemAsync(response, HttpStatusCode.BadRequest, "One or more validation errors occurred.");
	}

	[Theory]
	[InlineData("/api/products/0", "Invalid product ID")]
	[InlineData("/api/products/0/offers", "Invalid product ID")]
	[InlineData("/api/products/0/no-offer-persistence", "Invalid product ID")]
	[InlineData("/api/customers/0/order-options", "Invalid customer ID")]
	public async Task Route_WithInvalidId_ReturnsBadRequestProblem(string path, string title)
	{
		using var developmentFactory = new DevelopmentApiFactory(enabled: true);
		using var developmentClient = developmentFactory.CreateClient();
		using var response = await developmentClient.GetAsync(path);

		await AssertProblemAsync(response, HttpStatusCode.BadRequest, title);
	}

	[Theory]
	[InlineData("/api/products/0/eligible-offers?quantity=1")]
	[InlineData("/api/products/-1/eligible-offers?quantity=1")]
	[InlineData("/api/products/1/eligible-offers?quantity=0")]
	[InlineData("/api/products/1/eligible-offers?quantity=32768")]
	public async Task EligibleOffers_InvalidProductOrQuantityReturnsBadRequestWithoutStoreCall(string path)
	{
		var store = new SpyProductCatalogReadStore(null);
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, productStore: store);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync(path);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(0, store.EligibilityCallCount);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(32767)]
	public async Task EligibleOffers_AcceptsQuantityStorageEndpoints(int quantity)
	{
		var store = new SpyProductCatalogReadStore(new ProductOfferEligibilityReadResult(42, []));
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, productStore: store);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync($"/api/products/42/eligible-offers?quantity={quantity}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(1, store.EligibilityCallCount);
	}

	[Fact]
	public async Task EligibleOffers_ReturnsOnlyOffersMatchingUtcDateAndQuantityRules()
	{
		var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
		var start = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
		var store = new SpyProductCatalogReadStore(new ProductOfferEligibilityReadResult(42,
		[
			new(1, "Eligible", start, start.AddHours(1), 2, 4, 0.15m),
			new(6, "Inclusive quantity bounds", start, start.AddHours(1), 3, 3, 0.02m),
			new(2, "End is exclusive", start.AddHours(-1), now, 1, null, 0.10m),
			new(3, "Future start", now.AddTicks(1), now.AddHours(1), 1, null),
			new(4, "Minimum not met", start, start.AddHours(1), 4, null),
			new(5, "Maximum exceeded", start, start.AddHours(1), 1, 2)
		]));
		using var developmentFactory = new DevelopmentApiFactory(
			enabled: true,
			productStore: store,
			timeProvider: new FixedTimeProvider(now));
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/products/42/eligible-offers?quantity=3");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(new[] { "specialOfferId", "description", "discountPct" },
			json.RootElement[0].EnumerateObject().Select(property => property.Name));
		Assert.Equal(0.15m, json.RootElement[0].GetProperty("discountPct").GetDecimal());
		var offers = await response.Content.ReadFromJsonAsync<ProductEligibleOfferDto[]>();
		Assert.NotNull(offers);
		Assert.Equal(
			new[] { new ProductEligibleOfferDto(1, "Eligible", 0.15m), new ProductEligibleOfferDto(6, "Inclusive quantity bounds", 0.02m) },
			offers);
		Assert.Equal(1, store.EligibilityCallCount);
	}

	[Fact]
	public async Task EligibleOffers_EmptyListForExistingProductDiffersFromMissingProduct()
	{
		using var existingFactory = new DevelopmentApiFactory(
			enabled: true,
			productStore: new SpyProductCatalogReadStore(new ProductOfferEligibilityReadResult(42, [])));
		using var existingClient = existingFactory.CreateClient();
		using var existingResponse = await existingClient.GetAsync("/api/products/42/eligible-offers?quantity=1");
		Assert.Equal(HttpStatusCode.OK, existingResponse.StatusCode);
		Assert.Empty(await existingResponse.Content.ReadFromJsonAsync<ProductEligibleOfferDto[]>() ?? []);

		using var missingFactory = new DevelopmentApiFactory(
			enabled: true,
			productStore: new SpyProductCatalogReadStore(null));
		using var missingClient = missingFactory.CreateClient();
		using var missingResponse = await missingClient.GetAsync("/api/products/42/eligible-offers?quantity=1");
		Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task NoOfferPersistence_ReturnsPerProductSupportWithoutExposingOffer(bool isSupported)
	{
		var store = new SpyProductCatalogReadStore(null, isSupported);
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, productStore: store);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/products/42/no-offer-persistence");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var capability = await response.Content.ReadFromJsonAsync<ProductNoOfferPersistenceDto>();
		Assert.Equal(new ProductNoOfferPersistenceDto(isSupported), capability);
		Assert.Equal(1, store.NoOfferPersistenceCallCount);
	}

	[Fact]
	public async Task NoOfferPersistence_ReturnsNotFoundForMissingProduct()
	{
		var store = new SpyProductCatalogReadStore(null);
		using var developmentFactory = new DevelopmentApiFactory(enabled: true, productStore: store);
		using var developmentClient = developmentFactory.CreateClient();

		using var response = await developmentClient.GetAsync("/api/products/42/no-offer-persistence");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(1, store.NoOfferPersistenceCallCount);
	}

	private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedTitle)
	{
		Assert.Equal(expectedStatus, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
		Assert.NotNull(problem);
		Assert.Equal((int)expectedStatus, problem.Status);
		Assert.Equal(expectedTitle, problem.Title);
	}
}

public class OfflineApiFactory : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Production");
		ConfigureOfflineStores(builder);
	}

	protected static void ConfigureOfflineStores(IWebHostBuilder builder)
	{
		builder.ConfigureServices(services =>
		{
			services.RemoveAll<IProductCatalogReadStore>();
			services.RemoveAll<ICustomerReadStore>();
			services.RemoveAll<IOrderReadStore>();
			services.RemoveAll<IOfferPricingReadStore>();
			services.RemoveAll<IOrderWriteStore>();
			services.AddSingleton<IProductCatalogReadStore, UnconfiguredProductCatalogReadStore>();
			services.AddSingleton<ICustomerReadStore, UnconfiguredCustomerReadStore>();
			services.AddSingleton<IOrderReadStore, UnconfiguredOrderReadStore>();
			services.AddSingleton<IOfferPricingReadStore, UnconfiguredOfferPricingReadStore>();
			services.AddSingleton<IOrderWriteStore, UnconfiguredOrderWriteStore>();
		});
	}
}

public sealed class SpyCustomerReadStore(CustomerOrderOptionsDto? options) : ICustomerReadStore
{
	private int invocationCount;
	private int orderOptionsCallCount;

	public int InvocationCount => Volatile.Read(ref invocationCount);
	public int OrderOptionsCallCount => Volatile.Read(ref orderOptionsCallCount);

	public bool IsConfigured
	{
		get
		{
			Interlocked.Increment(ref invocationCount);
			return true;
		}
	}

	public Task<CustomerPageDto> SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref invocationCount);
		return Task.FromResult(new CustomerPageDto([], request.Page, request.PageSize, false));
	}

	public Task<CustomerOrderOptionsDto?> GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref invocationCount);
		Interlocked.Increment(ref orderOptionsCallCount);
		return Task.FromResult(options);
	}
}

public sealed class DevelopmentApiFactory(
	bool enabled,
	string urls = "http://127.0.0.1:5080",
	ICustomerReadStore? customerStore = null,
	IProductCatalogReadStore? productStore = null,
	TimeProvider? timeProvider = null,
	IOrderReadStore? orderStore = null,
	bool authenticateTestCaller = true,
	bool useTestAuthentication = true) : OfflineApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);
		builder.UseEnvironment("Development");
		builder.UseUrls(urls);
		builder.UseSetting(ApiAuthentication.DevelopmentAuthenticationEnvironmentVariable, enabled.ToString());
		if (enabled || customerStore is not null || productStore is not null || timeProvider is not null || orderStore is not null)
		{
			builder.ConfigureTestServices(services =>
			{
				if (enabled && useTestAuthentication)
				{
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
						options.DefaultChallengeScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
						options.DefaultForbidScheme = TestDevelopmentStaffAuthenticationHandler.SchemeName;
					}).AddScheme<TestDevelopmentStaffAuthenticationOptions, TestDevelopmentStaffAuthenticationHandler>(
						TestDevelopmentStaffAuthenticationHandler.SchemeName,
						options => options.AuthenticateCaller = authenticateTestCaller);
					services.RemoveAll<IAuthenticationSchemeProvider>();
					services.AddSingleton<IAuthenticationSchemeProvider>(serviceProvider =>
					{
						var schemeProvider = new AuthenticationSchemeProvider(
							serviceProvider.GetRequiredService<IOptions<AuthenticationOptions>>());
						schemeProvider.RemoveScheme(ApiAuthentication.DevelopmentScheme);
						return schemeProvider;
					});
				}

				if (customerStore is not null)
				{
					services.RemoveAll<ICustomerReadStore>();
					services.AddSingleton(customerStore);
				}

				if (productStore is not null)
				{
					services.RemoveAll<IProductCatalogReadStore>();
					services.AddSingleton(productStore);
				}

				if (timeProvider is not null)
				{
					services.RemoveAll<TimeProvider>();
					services.AddSingleton(timeProvider);
				}

				if (orderStore is not null)
				{
					services.RemoveAll<IOrderReadStore>();
					services.AddSingleton(orderStore);
				}
			});
		}
	}
}

public sealed class TestDevelopmentStaffAuthenticationOptions : AuthenticationSchemeOptions
{
	public bool AuthenticateCaller { get; set; } = true;
}

public sealed class TestDevelopmentStaffAuthenticationHandler(
	IOptionsMonitor<TestDevelopmentStaffAuthenticationOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder) : AuthenticationHandler<TestDevelopmentStaffAuthenticationOptions>(options, logger, encoder)
{
	public const string SchemeName = "TestDevelopmentStaff";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Options.AuthenticateCaller)
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}

		var identity = new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, "TEST\\windows-caller")],
			Scheme.Name);
		var principal = new ClaimsPrincipal(identity);
		DevelopmentStaffIdentity.AddInternalStaffClaim(principal);
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
	}
}

public sealed class SpyProductCatalogReadStore(
	ProductOfferEligibilityReadResult? eligibilityData,
	bool? noOfferPersistenceSupport = null,
	ProductDetailDto? productDetail = null) : IProductCatalogReadStore
{
	private int eligibilityCallCount;
	private int noOfferPersistenceCallCount;
	public int EligibilityCallCount => Volatile.Read(ref eligibilityCallCount);
	public int NoOfferPersistenceCallCount => Volatile.Read(ref noOfferPersistenceCallCount);
	public bool IsConfigured => true;

	public Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken) =>
		Task.FromResult(new ProductPageDto([], request.Page, request.PageSize, false));

	public Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken) => Task.FromResult(productDetail);

	public Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<ProductOfferDto>>([]);

	public Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(int productId, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref eligibilityCallCount);
		return Task.FromResult(eligibilityData);
	}

	public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref noOfferPersistenceCallCount);
		return Task.FromResult(noOfferPersistenceSupport);
	}
}

public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
	public override DateTimeOffset GetUtcNow() => utcNow;
}

public sealed class OutsideDevelopmentApiFactory : OfflineApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);
		builder.UseSetting(ApiAuthentication.DevelopmentAuthenticationEnvironmentVariable, "true");
	}
}

public sealed class NonStaffApiFactory(ICustomerReadStore? customerStore = null) : OfflineApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);
		builder.ConfigureTestServices(services =>
		{
			if (customerStore is not null)
			{
				services.RemoveAll<ICustomerReadStore>();
				services.AddSingleton(customerStore);
			}

			services.AddAuthentication(options =>
			{
				options.DefaultAuthenticateScheme = "TestNonStaff";
				options.DefaultChallengeScheme = "TestNonStaff";
				options.DefaultForbidScheme = "TestNonStaff";
			}).AddScheme<AuthenticationSchemeOptions, NonStaffAuthenticationHandler>("TestNonStaff", _ => { });
		});
	}
}

public sealed class NonStaffAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Test Non-Staff")], Scheme.Name);
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
	}
}