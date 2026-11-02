namespace AdventureWorks.Api.Data;

public static class CustomerOrderOptionsSql
{
	public const int MaximumShipMethods = 100;

	public static readonly IReadOnlyCollection<string> RequiredPermissions =
	[
		"SELECT:Person.BusinessEntityAddress",
		"SELECT:Person.Address",
		"SELECT:Person.AddressType",
		"SELECT:Purchasing.ShipMethod"
	];

	public static SqlQueryPlan BuildAddresses(int customerId)
	{
		if (customerId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(customerId));
		}

		var sql = """
			SELECT c.CustomerID,
			       eligible.AddressID,
			       eligible.AddressLine1,
			       eligible.AddressLine2,
			       eligible.City,
			       eligible.PostalCode,
			       eligible.AddressTypeName
			FROM Sales.Customer AS c
			OUTER APPLY
			(
				SELECT DISTINCT address.AddressID,
				       address.AddressLine1,
				       address.AddressLine2,
				       address.City,
				       address.PostalCode,
				       addressType.Name AS AddressTypeName
				FROM (VALUES (c.PersonID), (c.StoreID)) AS entity(BusinessEntityID)
				INNER JOIN Person.BusinessEntityAddress AS businessEntityAddress
					ON businessEntityAddress.BusinessEntityID = entity.BusinessEntityID
				INNER JOIN Person.Address AS address
					ON address.AddressID = businessEntityAddress.AddressID
				INNER JOIN Person.AddressType AS addressType
					ON addressType.AddressTypeID = businessEntityAddress.AddressTypeID
				WHERE entity.BusinessEntityID IS NOT NULL
			) AS eligible
			WHERE c.CustomerID = @customerId
			ORDER BY eligible.AddressID, eligible.AddressTypeName;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@customerId"] = customerId });
	}

	public static SqlQueryPlan BuildShipMethods()
	{
		var sql = """
			SELECT TOP (@take) ShipMethodID, Name
			FROM Purchasing.ShipMethod
			ORDER BY ShipMethodID;
			""";

		return new SqlQueryPlan(sql, new Dictionary<string, object?> { ["@take"] = MaximumShipMethods });
	}
}