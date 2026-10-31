using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public static class CustomerSearchSql
{
	private static readonly IReadOnlyDictionary<string, string> SortColumns =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["customerId"] = "c.CustomerID",
			["displayName"] = "CASE WHEN store.BusinessEntityID IS NOT NULL THEN store.Name ELSE N'Person customer' END",
			["customerKind"] = "CASE WHEN person.BusinessEntityID IS NOT NULL THEN N'Person' ELSE N'Store' END"
		};

	public static SqlQueryPlan BuildSearch(CustomerSearchRequest request)
	{
		if (request.Page is < 1 or > 10_000 || request.PageSize is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(request), "Search bounds are invalid.");
		}

		if (!SortColumns.TryGetValue(request.SortBy, out var sortColumn))
		{
			throw new ArgumentException("Unsupported customer sort field.", nameof(request));
		}

		var direction = request.SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase)
			? "DESC"
			: request.SortDirection.Equals("asc", StringComparison.OrdinalIgnoreCase)
				? "ASC"
				: throw new ArgumentException("Unsupported sort direction.", nameof(request));

		var offset = (long)(request.Page - 1) * request.PageSize;
		var sql = $"""
			SELECT c.CustomerID,
			       CASE WHEN store.BusinessEntityID IS NOT NULL THEN store.Name ELSE N'Person customer' END AS DisplayName,
			       CASE WHEN person.BusinessEntityID IS NOT NULL THEN N'Person' ELSE N'Store' END AS CustomerKind
			FROM Sales.Customer AS c
			LEFT JOIN Person.Person AS person
				ON person.BusinessEntityID = c.PersonID
			LEFT JOIN Sales.Store AS store
				ON store.BusinessEntityID = c.StoreID
			WHERE ((person.BusinessEntityID IS NOT NULL AND store.BusinessEntityID IS NULL)
			    OR (person.BusinessEntityID IS NULL AND store.BusinessEntityID IS NOT NULL))
			  AND (@search IS NULL
			       OR CHARINDEX(@search, CONVERT(nvarchar(11), c.CustomerID)) > 0
			       OR (store.BusinessEntityID IS NOT NULL AND CHARINDEX(@search, store.Name) > 0))
			ORDER BY {sortColumn} {direction}, c.CustomerID ASC
			OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?>
		{
			["@search"] = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
			["@offset"] = offset,
			["@take"] = request.PageSize + 1
		});
	}
}