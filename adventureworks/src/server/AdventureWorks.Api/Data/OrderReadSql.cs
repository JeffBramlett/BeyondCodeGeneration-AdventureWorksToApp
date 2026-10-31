using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public static class OrderReadSql
{
	public static readonly string[] RequiredPermissions =
	[
		"SELECT:Sales.SalesOrderHeader.SalesOrderID",
		"SELECT:Sales.SalesOrderHeader.SalesOrderNumber",
		"SELECT:Sales.SalesOrderHeader.OrderDate",
		"SELECT:Sales.SalesOrderDetail.SalesOrderID",
		"SELECT:Sales.SalesOrderDetail.SalesOrderDetailID",
		"SELECT:Sales.SalesOrderDetail.ProductID",
		"SELECT:Sales.SalesOrderDetail.OrderQty"
	];

	public static SqlQueryPlan BuildSearch(OrderSearchRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Page is < 1 or > 10_000 || request.PageSize is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(request), "Order page bounds are invalid.");
		}

		var offset = (long)(request.Page - 1) * request.PageSize;
		var parameters = new Dictionary<string, object?>
		{
			["@offset"] = offset,
			["@take"] = request.PageSize + 1
		};

		string predicate;
		if (request.OrderReference is not null)
		{
			var reference = request.OrderReference.Trim();
			if (reference.Length is < 1 or > 25 || request.FromDate.HasValue || request.ToDate.HasValue)
			{
				throw new ArgumentException("Order reference search is invalid.", nameof(request));
			}

			predicate = "header.SalesOrderNumber = @orderReference";
			parameters["@orderReference"] = reference;
		}
		else if (request.FromDate.HasValue && request.ToDate.HasValue &&
			request.ToDate.Value >= request.FromDate.Value &&
			request.ToDate.Value < DateOnly.MaxValue &&
			request.ToDate.Value.DayNumber - request.FromDate.Value.DayNumber + 1 <= 31)
		{
			predicate = "header.OrderDate >= @fromDate AND header.OrderDate < @toDateExclusive";
			parameters["@fromDate"] = request.FromDate.Value.ToDateTime(TimeOnly.MinValue);
			parameters["@toDateExclusive"] = request.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
		}
		else
		{
			throw new ArgumentException("Provide one exact order reference or a valid inclusive date range.", nameof(request));
		}

		var sql = $"""
			SELECT header.SalesOrderNumber, header.OrderDate
			FROM Sales.SalesOrderHeader AS header
			WHERE {predicate}
			ORDER BY header.OrderDate ASC, header.SalesOrderID ASC
			OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
			""";

		return new SqlQueryPlan(sql, parameters);
	}

	public static SqlQueryPlan BuildDetail(string normalizedOrderReference, OrderLinesRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (normalizedOrderReference.Length is < 1 or > 25)
		{
			throw new ArgumentException("Order reference is invalid.", nameof(normalizedOrderReference));
		}

		if (request.LinePage is < 1 or > 10_000 || request.LinePageSize is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(request), "Order-line page bounds are invalid.");
		}

		var sql = """
			WITH MatchingOrder AS
			(
				SELECT header.SalesOrderID, header.SalesOrderNumber, header.OrderDate
				FROM Sales.SalesOrderHeader AS header
				WHERE header.SalesOrderNumber = @orderReference
			),
			PagedDetails AS
			(
				SELECT detail.SalesOrderID, detail.SalesOrderDetailID, detail.ProductID, detail.OrderQty
				FROM Sales.SalesOrderDetail AS detail
				INNER JOIN MatchingOrder AS header ON header.SalesOrderID = detail.SalesOrderID
				ORDER BY detail.SalesOrderDetailID ASC
				OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
			)
			SELECT header.SalesOrderNumber, header.OrderDate, detail.ProductID, detail.OrderQty
			FROM MatchingOrder AS header
			LEFT JOIN PagedDetails AS detail ON detail.SalesOrderID = header.SalesOrderID
			ORDER BY detail.SalesOrderDetailID ASC;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?>
		{
			["@orderReference"] = normalizedOrderReference,
			["@offset"] = (long)(request.LinePage - 1) * request.LinePageSize,
			["@take"] = request.LinePageSize + 1
		});
	}
}
