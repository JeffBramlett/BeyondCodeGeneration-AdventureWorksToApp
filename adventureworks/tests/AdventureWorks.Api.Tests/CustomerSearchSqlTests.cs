using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Controllers;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class CustomerSearchSqlTests
{
	[Fact]
	public void CustomerDto_ExposesOnlySelectionFields()
	{
		Assert.Equal(
			new[]
			{
				nameof(CustomerDto.CustomerId),
				nameof(CustomerDto.DisplayName),
				nameof(CustomerDto.CustomerKind)
			},
			typeof(CustomerDto).GetProperties().Select(property => property.Name));
	}

	[Fact]
	public void Search_UsesVerifiedIdentityJoinsAndOnlyMinimalFields()
	{
		var plan = CustomerSearchSql.BuildSearch(new CustomerSearchRequest());

		Assert.Contains("person.BusinessEntityID = c.PersonID", plan.CommandText);
		Assert.Contains("store.BusinessEntityID = c.StoreID", plan.CommandText);
		Assert.Contains("person.BusinessEntityID IS NOT NULL AND store.BusinessEntityID IS NULL", plan.CommandText);
		Assert.Contains("c.CustomerID,", plan.CommandText);
		Assert.Contains("AS DisplayName", plan.CommandText);
		Assert.Contains("AS CustomerKind", plan.CommandText);
		Assert.DoesNotContain("Person.FirstName", plan.CommandText);
		Assert.DoesNotContain("Address", plan.CommandText);
		Assert.DoesNotContain("EmailAddress", plan.CommandText);
		Assert.DoesNotContain("Phone", plan.CommandText);
	}

	[Fact]
	public void Search_BindsSearchAndBoundsPagingWithAllowlistedSort()
	{
		const string search = "Northwind' OR 1=1 --";
		var plan = CustomerSearchSql.BuildSearch(new CustomerSearchRequest
		{
			Page = 4,
			PageSize = 50,
			Search = search,
			SortBy = "customerKind",
			SortDirection = "desc"
		});

		Assert.Contains("ORDER BY CASE WHEN person.BusinessEntityID IS NOT NULL THEN N'Person' ELSE N'Store' END DESC", plan.CommandText);
		Assert.Contains("OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY", plan.CommandText);
		Assert.DoesNotContain(search, plan.CommandText);
		Assert.Equal(search, plan.Parameters["@search"]);
		Assert.Equal(150L, plan.Parameters["@offset"]);
		Assert.Equal(51, plan.Parameters["@take"]);
	}

	[Fact]
	public void Search_RejectsUnallowlistedSortAndOutOfBoundsPaging()
	{
		Assert.Throws<ArgumentException>(() => CustomerSearchSql.BuildSearch(
			new CustomerSearchRequest { SortBy = "Name; DROP TABLE Sales.Customer" }));
		Assert.Throws<ArgumentOutOfRangeException>(() => CustomerSearchSql.BuildSearch(
			new CustomerSearchRequest { PageSize = 101 }));
	}

	[Fact]
	public void SearchRequest_RejectsInvalidBoundsAndWhitespaceSearch()
	{
		var invalidBoundsRequest = new CustomerSearchRequest { Page = 0, PageSize = 101 };
		var validationResults = new List<ValidationResult>();

		var isValid = Validator.TryValidateObject(
			invalidBoundsRequest,
			new ValidationContext(invalidBoundsRequest),
			validationResults,
			validateAllProperties: true);

		Assert.False(isValid);

		var whitespaceRequest = new CustomerSearchRequest { Search = "  " };
		validationResults.Clear();
		isValid = Validator.TryValidateObject(
			whitespaceRequest,
			new ValidationContext(whitespaceRequest),
			validationResults,
			validateAllProperties: true);

		Assert.False(isValid);
		Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(CustomerSearchRequest.Search)));
	}

	[Fact]
	public async Task Search_Returns503WhenNoApprovedReadStoreIsConfigured()
	{
		var controller = new CustomersController(new UnconfiguredCustomerReadStore());

		var result = await controller.Search(new CustomerSearchRequest(), CancellationToken.None);

		var unavailable = Assert.IsType<ObjectResult>(result);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
		Assert.Contains("approved read-only", Assert.IsType<ProblemDetails>(unavailable.Value).Detail);
	}

	[Fact]
	public async Task OrderOptions_Return503UntilApprovedStoreExists()
	{
		var controller = new CustomersController(new UnconfiguredCustomerReadStore());

		var result = await controller.GetOrderOptions(42, CancellationToken.None);

		var unavailable = Assert.IsType<ObjectResult>(result);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
		Assert.Contains("order-options permissions", Assert.IsType<ProblemDetails>(unavailable.Value).Detail);
	}

	[Fact]
	public async Task OrderOptions_Returns404WhenCustomerDoesNotExist()
	{
		var controller = new CustomersController(new OrderOptionsCustomerReadStore(null));

		var result = await controller.GetOrderOptions(42, CancellationToken.None);

		var notFound = Assert.IsType<NotFoundObjectResult>(result);
		Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ProblemDetails>(notFound.Value).Status);
	}

	[Fact]
	public async Task OrderOptions_ReturnsStoreDtoForExistingCustomer()
	{
		var expected = new CustomerOrderOptionsDto(
			[new CustomerAddressOptionDto(7, "1 Example Road", null, "Example City", "00000", ["Home"] )],
			[new ShipMethodOptionDto(2, "Ground")]);
		var controller = new CustomersController(new OrderOptionsCustomerReadStore(expected));

		var result = await controller.GetOrderOptions(42, CancellationToken.None);

		var ok = Assert.IsType<OkObjectResult>(result);
		Assert.Same(expected, ok.Value);
	}

	private sealed class OrderOptionsCustomerReadStore(CustomerOrderOptionsDto? options) : ICustomerReadStore
	{
		public bool IsConfigured => true;

		public Task<CustomerPageDto> SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken) =>
			Task.FromResult(new CustomerPageDto([], request.Page, request.PageSize, false));

		public Task<CustomerOrderOptionsDto?> GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken) =>
			Task.FromResult(options);
	}
}