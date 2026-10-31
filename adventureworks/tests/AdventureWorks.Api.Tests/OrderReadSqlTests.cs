using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderReadSqlTests
{
	[Fact]
	public void SearchByReference_IsExactTrimmedParameterizedAndDeterministicallyPaged()
	{
		const string reference = " so-500 ";
		var plan = OrderReadSql.BuildSearch(new OrderSearchRequest
		{
			OrderReference = reference,
			Page = 3,
			PageSize = 25
		});

		Assert.Contains("header.SalesOrderNumber = @orderReference", plan.CommandText);
		Assert.Contains("ORDER BY header.OrderDate ASC, header.SalesOrderID ASC", plan.CommandText);
		Assert.Contains("OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY", plan.CommandText);
		Assert.DoesNotContain(reference, plan.CommandText);
		Assert.Equal("so-500", plan.Parameters["@orderReference"]);
		Assert.Equal(50L, plan.Parameters["@offset"]);
		Assert.Equal(26, plan.Parameters["@take"]);
		Assert.DoesNotContain("Customer", plan.CommandText, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Address", plan.CommandText, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("TotalDue", plan.CommandText, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void SearchByDates_UsesInclusiveDatesAsHalfOpenStoredDatetimeRange()
	{
		var plan = OrderReadSql.BuildSearch(new OrderSearchRequest
		{
			FromDate = new DateOnly(2026, 2, 1),
			ToDate = new DateOnly(2026, 3, 3)
		});

		Assert.Contains("header.OrderDate >= @fromDate AND header.OrderDate < @toDateExclusive", plan.CommandText);
		Assert.Equal(new DateTime(2026, 2, 1), plan.Parameters["@fromDate"]);
		Assert.Equal(new DateTime(2026, 3, 4), plan.Parameters["@toDateExclusive"]);
		Assert.Equal(0L, plan.Parameters["@offset"]);
		Assert.Equal(26, plan.Parameters["@take"]);
	}

	[Fact]
	public void Detail_UsesExactReferenceAndStableInternalLinePagingWithoutExposingOtherFields()
	{
		var plan = OrderReadSql.BuildDetail("SO-500", new OrderLinesRequest { LinePage = 2, LinePageSize = 20 });

		Assert.Contains("header.SalesOrderNumber = @orderReference", plan.CommandText);
		Assert.Contains("ORDER BY detail.SalesOrderDetailID ASC", plan.CommandText);
		Assert.Contains("detail.SalesOrderID = header.SalesOrderID", plan.CommandText);
		Assert.Contains("OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY", plan.CommandText);
		Assert.Equal("SO-500", plan.Parameters["@orderReference"]);
		Assert.Equal(20L, plan.Parameters["@offset"]);
		Assert.Equal(21, plan.Parameters["@take"]);
		Assert.Contains("detail.ProductID, detail.OrderQty", plan.CommandText);
		foreach (var forbidden in new[] { "CustomerID", "AddressID", "Status", "TotalDue", "UnitPrice", "SpecialOfferID" })
		{
			Assert.DoesNotContain(forbidden, plan.CommandText, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void Search_RejectsInvalidModesAndBounds()
	{
		Assert.Throws<ArgumentException>(() => OrderReadSql.BuildSearch(new OrderSearchRequest()));
		Assert.Throws<ArgumentException>(() => OrderReadSql.BuildSearch(new OrderSearchRequest
		{
			OrderReference = "SO-500",
			FromDate = new DateOnly(2026, 1, 1),
			ToDate = new DateOnly(2026, 1, 2)
		}));
		Assert.Throws<ArgumentException>(() => OrderReadSql.BuildSearch(new OrderSearchRequest
		{
			FromDate = new DateOnly(2026, 1, 1),
			ToDate = new DateOnly(2026, 2, 1)
		}));
		Assert.Throws<ArgumentOutOfRangeException>(() => OrderReadSql.BuildSearch(new OrderSearchRequest { PageSize = 101, OrderReference = "SO-500" }));
		Assert.Throws<ArgumentOutOfRangeException>(() => OrderReadSql.BuildDetail("SO-500", new OrderLinesRequest { LinePageSize = 101 }));
	}

	[Fact]
	public void ResponseDtos_AllowOnlyApprovedOrderFieldsAndPagingMetadata()
	{
		Assert.Equal(
			new[] { nameof(OrderSummaryDto.OrderReference), nameof(OrderSummaryDto.OrderDate) },
			typeof(OrderSummaryDto).GetProperties().Select(property => property.Name));
		Assert.Equal(
			new[] { nameof(OrderLineDto.ProductId), nameof(OrderLineDto.OrderQty) },
			typeof(OrderLineDto).GetProperties().Select(property => property.Name));
		Assert.Equal(
			new[] { "Items", "Page", "PageSize", "HasMore" },
			typeof(OrderPageDto).GetProperties().Select(property => property.Name));
		Assert.Equal(
			new[] { "OrderReference", "OrderDate", "Lines", "LinePage", "LinePageSize", "HasMore" },
			typeof(OrderDetailPageDto).GetProperties().Select(property => property.Name));
	}
}
