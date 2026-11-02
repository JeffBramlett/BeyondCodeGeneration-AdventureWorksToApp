using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public sealed record SqlQueryPlan(string CommandText, IReadOnlyDictionary<string, object?> Parameters);

public static class ProductCatalogSql
{
	private static readonly IReadOnlyDictionary<string, string> SortColumns =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["productId"] = "p.ProductID",
			["name"] = "p.Name",
			["productNumber"] = "p.ProductNumber"
		};

	public static SqlQueryPlan BuildSearch(ProductSearchRequest request)
	{
		if (request.Page is < 1 or > 10_000 || request.PageSize is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(request), "Search bounds are invalid.");
		}

		if (!SortColumns.TryGetValue(request.SortBy, out var sortColumn))
		{
			throw new ArgumentException("Unsupported product sort field.", nameof(request));
		}

		var direction = request.SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase)
			? "DESC"
			: request.SortDirection.Equals("asc", StringComparison.OrdinalIgnoreCase)
				? "ASC"
				: throw new ArgumentException("Unsupported sort direction.", nameof(request));

		var offset = (long)(request.Page - 1) * request.PageSize;
		var sql = $"""
			SELECT p.ProductID, p.Name, p.ProductNumber,
			       subcategory.Name AS SubcategoryName,
			       category.Name AS CategoryName
			FROM Production.Product AS p
			LEFT JOIN Production.ProductSubcategory AS subcategory
				ON subcategory.ProductSubcategoryID = p.ProductSubcategoryID
			LEFT JOIN Production.ProductCategory AS category
				ON category.ProductCategoryID = subcategory.ProductCategoryID
			WHERE (@search IS NULL OR CHARINDEX(@search, p.Name) > 0
			       OR CHARINDEX(@search, p.ProductNumber) > 0)
			ORDER BY {sortColumn} {direction}, p.ProductID ASC
			OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?>
		{
			["@search"] = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
			["@offset"] = offset,
			["@take"] = request.PageSize + 1
		});
	}

	public static SqlQueryPlan BuildDetail(int productId)
	{
		if (productId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(productId));
		}

		var sql = """
			SELECT p.ProductID, p.Name, p.ProductNumber,
			       subcategory.Name AS SubcategoryName,
			       category.Name AS CategoryName,
			       p.ListPrice
			FROM Production.Product AS p
			LEFT JOIN Production.ProductSubcategory AS subcategory
				ON subcategory.ProductSubcategoryID = p.ProductSubcategoryID
			LEFT JOIN Production.ProductCategory AS category
				ON category.ProductCategoryID = subcategory.ProductCategoryID
			WHERE p.ProductID = @productId;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@productId"] = productId });
	}

	public static SqlQueryPlan BuildOffers(int productId)
	{
		if (productId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(productId));
		}

		var sql = """
			SELECT offer.SpecialOfferID, offer.Description
			FROM Sales.SpecialOfferProduct AS offerProduct
			INNER JOIN Sales.SpecialOffer AS offer
				ON offer.SpecialOfferID = offerProduct.SpecialOfferID
			WHERE offerProduct.ProductID = @productId
			ORDER BY offer.SpecialOfferID;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@productId"] = productId });
	}

	public static SqlQueryPlan BuildOfferEligibilityData(int productId)
	{
		if (productId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(productId));
		}

		var sql = """
			SELECT product.ProductID, offer.SpecialOfferID, offer.Description,
			       offer.StartDate, offer.EndDate, offer.MinQty, offer.MaxQty,
			       offer.DiscountPct
			FROM Production.Product AS product
			LEFT JOIN Sales.SpecialOfferProduct AS offerProduct
				ON offerProduct.ProductID = product.ProductID
			LEFT JOIN Sales.SpecialOffer AS offer
				ON offer.SpecialOfferID = offerProduct.SpecialOfferID
			WHERE product.ProductID = @productId
			ORDER BY offer.SpecialOfferID;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@productId"] = productId });
	}

	public static SqlQueryPlan BuildOfferPricing(int productId, int specialOfferId)
	{
		if (productId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(productId));
		}

		if (specialOfferId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(specialOfferId));
		}

		var sql = """
			SELECT offer.SpecialOfferID, offer.Description, offer.DiscountPct
			FROM Sales.SpecialOfferProduct AS offerProduct
			INNER JOIN Sales.SpecialOffer AS offer
				ON offer.SpecialOfferID = offerProduct.SpecialOfferID
			WHERE offerProduct.ProductID = @productId
			  AND offerProduct.SpecialOfferID = @specialOfferId;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?>
		{
			["@productId"] = productId,
			["@specialOfferId"] = specialOfferId
		});
	}

	public static SqlQueryPlan BuildNoOfferPersistenceSupport(int productId)
	{
		if (productId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(productId));
		}

		var sql = """
			SELECT product.ProductID,
			       CONVERT(bit, CASE WHEN offerProduct.ProductID IS NULL THEN 0 ELSE 1 END) AS IsSupported
			FROM Production.Product AS product
			LEFT JOIN Sales.SpecialOfferProduct AS offerProduct
				ON offerProduct.ProductID = product.ProductID
				AND offerProduct.SpecialOfferID = 1
			WHERE product.ProductID = @productId;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@productId"] = productId });
	}
}