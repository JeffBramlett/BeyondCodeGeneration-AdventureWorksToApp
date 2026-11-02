using Xunit;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Controllers;
using AdventureWorks.Api.Data;
using AdventureWorks.Api.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace AdventureWorks.Api.Tests;

public sealed class ProductCatalogSqlTests
{
	[Fact]
	public void Search_UsesBoundedPagingAndAllowlistedSortColumns()
	{
		var plan = ProductCatalogSql.BuildSearch(new ProductSearchRequest
		{
			Page = 4,
			PageSize = 50,
			SortBy = "productNumber",
			SortDirection = "desc"
		});

		Assert.Contains("ORDER BY p.ProductNumber DESC", plan.CommandText);
		Assert.Equal(150L, plan.Parameters["@offset"]);
		Assert.Equal(51, plan.Parameters["@take"]);
		Assert.DoesNotContain("ListPrice", plan.CommandText);
	}

	[Fact]
	public void Detail_ReturnsListPriceOnlyOnTheDetailProjection()
	{
		var plan = ProductCatalogSql.BuildDetail(42);

		Assert.Contains("p.ListPrice", plan.CommandText);
		Assert.DoesNotContain("ProductListPriceHistory", plan.CommandText);
		Assert.Equal(42, plan.Parameters["@productId"]);
	}

	[Fact]
	public void Search_BindsSearchTextInsteadOfInterpolatingIt()
	{
		const string search = "bike' OR 1=1 --";
		var plan = ProductCatalogSql.BuildSearch(new ProductSearchRequest { Search = search });

		Assert.Contains("CHARINDEX(@search", plan.CommandText);
		Assert.DoesNotContain(search, plan.CommandText);
		Assert.Equal(search, plan.Parameters["@search"]);
	}

	[Fact]
	public void Search_RejectsUnallowlistedSortColumns()
	{
		var request = new ProductSearchRequest { SortBy = "Name; DROP TABLE Production.Product" };

		Assert.Throws<ArgumentException>(() => ProductCatalogSql.BuildSearch(request));
	}

	[Fact]
	public void Offers_UsesVerifiedProductOfferBridgeWithoutClaimingEligibility()
	{
		var plan = ProductCatalogSql.BuildOffers(42);

		Assert.Contains("Sales.SpecialOfferProduct AS offerProduct", plan.CommandText);
		Assert.Contains("offer.SpecialOfferID = offerProduct.SpecialOfferID", plan.CommandText);
		Assert.Contains("offerProduct.ProductID = @productId", plan.CommandText);
		Assert.DoesNotContain("DiscountPct", plan.CommandText);
		Assert.Equal(42, plan.Parameters["@productId"]);
	}

	[Fact]
	public void EligibleOffers_AnchorsOnProductAndLeftJoinsLinkedOfferData()
	{
		var plan = ProductCatalogSql.BuildOfferEligibilityData(42);

		Assert.Contains("FROM Production.Product AS product", plan.CommandText);
		Assert.Contains("LEFT JOIN Sales.SpecialOfferProduct AS offerProduct", plan.CommandText);
		Assert.Contains("LEFT JOIN Sales.SpecialOffer AS offer", plan.CommandText);
		Assert.Contains("offerProduct.ProductID = product.ProductID", plan.CommandText);
		Assert.Contains("offer.SpecialOfferID = offerProduct.SpecialOfferID", plan.CommandText);
		Assert.Contains("WHERE product.ProductID = @productId", plan.CommandText);
		Assert.Contains("offer.StartDate", plan.CommandText);
		Assert.Contains("offer.EndDate", plan.CommandText);
		Assert.Contains("offer.MinQty", plan.CommandText);
		Assert.Contains("offer.MaxQty", plan.CommandText);
		Assert.Contains("offer.DiscountPct", plan.CommandText);
		Assert.Equal(42, plan.Parameters["@productId"]);
	}

	[Fact]
	public void EligibleOffers_RejectsNonPositiveProductIds()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => ProductCatalogSql.BuildOfferEligibilityData(0));
	}

	[Fact]
	public void NoOfferPersistence_RequiresProductLinkedToOfferOneWithoutApplyingEligibilityDates()
	{
		var plan = ProductCatalogSql.BuildNoOfferPersistenceSupport(42);

		Assert.Contains("FROM Production.Product AS product", plan.CommandText);
		Assert.Contains("Sales.SpecialOfferProduct AS offerProduct", plan.CommandText);
		Assert.Contains("offerProduct.SpecialOfferID = 1", plan.CommandText);
		Assert.Contains("offerProduct.ProductID = product.ProductID", plan.CommandText);
		Assert.DoesNotContain("StartDate", plan.CommandText);
		Assert.DoesNotContain("EndDate", plan.CommandText);
		Assert.DoesNotContain("DiscountPct", plan.CommandText);
		Assert.Equal(42, plan.Parameters["@productId"]);
	}

	[Fact]
	public void NoOfferPersistence_RejectsNonPositiveProductIds()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => ProductCatalogSql.BuildNoOfferPersistenceSupport(0));
	}

	[Theory]
	[InlineData(DateTimeKind.Unspecified)]
	[InlineData(DateTimeKind.Local)]
	public void SqlDateTimeUtcMapping_PreservesWallClockWithoutLocalConversion(DateTimeKind kind)
	{
		var storedValue = DateTime.SpecifyKind(new DateTime(2026, 10, 27, 12, 34, 56), kind);

		var mapped = OfferEligibilityEvaluator.InterpretSqlDateTimeAsUtc(storedValue);

		Assert.Equal(storedValue.Ticks, mapped.Ticks);
		Assert.Equal(TimeSpan.Zero, mapped.Offset);
		Assert.Equal(DateTimeKind.Utc, mapped.UtcDateTime.Kind);
	}

	[Fact]
	public void Search_RejectsOutOfBoundsPaging()
	{
		var request = new ProductSearchRequest { PageSize = 101 };

		Assert.Throws<ArgumentOutOfRangeException>(() => ProductCatalogSql.BuildSearch(request));
	}

	[Fact]
	public void SearchRequest_RejectsOutOfBoundsPageSize()
	{
		var request = new ProductSearchRequest { PageSize = 101 };
		var validationResults = new List<ValidationResult>();

		var isValid = Validator.TryValidateObject(
			request,
			new ValidationContext(request),
			validationResults,
			validateAllProperties: true);

		Assert.False(isValid);
	}

	[Fact]
	public async Task Search_Returns503WhenNoApprovedReadStoreIsConfigured()
	{
		var controller = new ProductsController(new UnconfiguredProductCatalogReadStore(), TimeProvider.System);

		var result = await controller.Search(new ProductSearchRequest(), CancellationToken.None);

		var unavailable = Assert.IsType<ObjectResult>(result);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
		Assert.Contains("approved read-only", Assert.IsType<ProblemDetails>(unavailable.Value).Detail);
	}
}