using System.Net;
using System.Net.Http.Json;
using AdventureWorks.Api;
using AdventureWorks.Api.Authentication;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AdventureWorks.Api.Tests;

public sealed class SqlServerIntegrationFactAttribute : FactAttribute
{
	public SqlServerIntegrationFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("ADVENTUREWORKS_RUN_SQL_INTEGRATION_TESTS") != "1" ||
			string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerReadAdapterConfiguration.CredentialFileEnvironmentVariable)) &&
			!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdventureWorks", "test-reader.credential")))
		{
			Skip = "SQL clone integration is opt-in and requires the protected read-only connection environment.";
		}
	}
}

public sealed class SqlServerReadStoreIntegrationTests
{
	private readonly ITestOutputHelper output;

	public SqlServerReadStoreIntegrationTests(ITestOutputHelper output)
	{
		this.output = output;
	}

	[SqlServerIntegrationFact]
	public async Task Aw034_ReadOnly_AssessesCurrentOfferPairsAndOfferOne()
	{
		var configuration = SqlServerReadAdapterConfiguration.FromEnvironment();
		Assert.True(configuration.IsConfigured);
		var settings = new SqlConnectionStringBuilder(configuration.ConnectionString);
		Assert.Equal("AdventureWorks2022_Test", settings.InitialCatalog);
		Assert.Equal("AdventureWorksTestReader", settings.UserID);
		Assert.False(settings.IntegratedSecurity);

		await using var connection = new SqlConnection(configuration.ConnectionString);
		await connection.OpenAsync();
		Assert.Equal("AdventureWorks2022_Test", connection.Database);
		var evaluationUtc = DateTime.UtcNow;

		await using (var summaryCommand = connection.CreateCommand())
		{
			summaryCommand.CommandText = """
				SELECT COUNT(*) AS OfferCount,
				       SUM(CASE WHEN StartDate <= @evaluationUtc AND EndDate > @evaluationUtc THEN 1 ELSE 0 END) AS DateEligibleCount,
				       SUM(CASE WHEN StartDate <= @evaluationUtc AND EndDate > @evaluationUtc
				                     AND MinQty <= 32767
				                     AND (MaxQty IS NULL OR MaxQty >= CASE WHEN MinQty > 1 THEN MinQty ELSE 1 END)
				                THEN 1 ELSE 0 END) AS DateAndQuantityEligibleCount,
				       MAX(EndDate) AS LatestStoredEndDate
				FROM Sales.SpecialOffer;
				""";
			summaryCommand.Parameters.Add("@evaluationUtc", System.Data.SqlDbType.DateTime2).Value = evaluationUtc;
			await using var reader = await summaryCommand.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			output.WriteLine(
				$"Evaluation UTC={evaluationUtc:O}; offers={reader.GetInt32(0)}; date-eligible={reader.GetInt32(1)}; date-and-positive-smallint-quantity-eligible={reader.GetInt32(2)}; latest-stored-end={reader.GetDateTime(3):O}");
		}

		await using (var offerCommand = connection.CreateCommand())
		{
			offerCommand.CommandText = """
				SELECT offer.Description, offer.DiscountPct, offer.StartDate, offer.EndDate,
			       offer.MinQty, offer.MaxQty,
			       COUNT(sop.ProductID) AS LinkedProductCount,
			       SUM(CASE WHEN product.FinishedGoodsFlag = 1 THEN 1 ELSE 0 END) AS FinishedGoodsCount,
			       SUM(CASE WHEN product.FinishedGoodsFlag = 0 THEN 1 ELSE 0 END) AS NonFinishedCount
			FROM Sales.SpecialOffer AS offer
			LEFT JOIN Sales.SpecialOfferProduct AS sop ON sop.SpecialOfferID = offer.SpecialOfferID
			LEFT JOIN Production.Product AS product ON product.ProductID = sop.ProductID
			WHERE offer.SpecialOfferID = 1
			GROUP BY offer.Description, offer.DiscountPct, offer.StartDate, offer.EndDate, offer.MinQty, offer.MaxQty;
			""";
			await using var reader = await offerCommand.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			var endDate = reader.GetDateTime(3);
			var isDateEligible = reader.GetDateTime(2) <= evaluationUtc && endDate > evaluationUtc;
			output.WriteLine(
				$"Offer 1={reader.GetString(0)}; discount={reader.GetDecimal(1)}; stored-window=[{reader.GetDateTime(2):O}, {endDate:O}); minQty={reader.GetInt32(4)}; maxQty={(reader.IsDBNull(5) ? "NULL" : reader.GetInt32(5))}; linked={reader.GetInt32(6)}; finished={reader.GetInt32(7)}; non-finished={reader.GetInt32(8)}; date-eligible={isDateEligible}");
		}

		await using (var productCommand = connection.CreateCommand())
		{
			productCommand.CommandText = """
				SELECT COUNT(DISTINCT product.ProductID) AS ProductCount,
			       COUNT(DISTINCT CASE WHEN offer.SpecialOfferID IS NOT NULL THEN product.ProductID END) AS ProductsWithEligiblePair,
			       COUNT(offer.SpecialOfferID) AS EligibleLinkedPairCount
			FROM Production.Product AS product
			LEFT JOIN Sales.SpecialOfferProduct AS sop ON sop.ProductID = product.ProductID
			LEFT JOIN Sales.SpecialOffer AS offer
			  ON offer.SpecialOfferID = sop.SpecialOfferID
			 AND offer.StartDate <= @evaluationUtc
			 AND offer.EndDate > @evaluationUtc
			 AND offer.MinQty <= 32767
			 AND (offer.MaxQty IS NULL OR offer.MaxQty >= CASE WHEN offer.MinQty > 1 THEN offer.MinQty ELSE 1 END);
			""";
			productCommand.Parameters.Add("@evaluationUtc", System.Data.SqlDbType.DateTime2).Value = evaluationUtc;
			await using var reader = await productCommand.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			output.WriteLine(
				$"Products={reader.GetInt32(0)}; products-with-any-date-and-quantity-eligible-linked-pair={reader.GetInt32(1)}; eligible-linked-pairs={reader.GetInt32(2)}");
		}

		await using (var schemaCommand = connection.CreateCommand())
		{
			schemaCommand.CommandText = """
				SELECT COLUMN_NAME, DATA_TYPE, DATETIME_PRECISION, IS_NULLABLE
				FROM INFORMATION_SCHEMA.COLUMNS
				WHERE (TABLE_SCHEMA = N'Sales' AND TABLE_NAME = N'SpecialOffer' AND COLUMN_NAME IN (N'StartDate', N'EndDate'))
				   OR (TABLE_SCHEMA = N'Sales' AND TABLE_NAME = N'SalesOrderDetail' AND COLUMN_NAME IN (N'SpecialOfferID', N'ProductID'))
				ORDER BY TABLE_NAME, ORDINAL_POSITION;
				""";
			await using var reader = await schemaCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				output.WriteLine(
					$"Column={reader.GetString(0)}; SQL type={reader.GetString(1)}; datetime precision={(reader.IsDBNull(2) ? "n/a" : reader.GetInt16(2).ToString())}; nullable={reader.GetString(3)}");
			}
		}

		await using (var dateMetadataCommand = connection.CreateCommand())
		{
			dateMetadataCommand.CommandText = """
				SELECT columnInfo.name, extendedProperty.name, CONVERT(nvarchar(4000), extendedProperty.value)
				FROM sys.columns AS columnInfo
			LEFT JOIN sys.extended_properties AS extendedProperty
			  ON extendedProperty.class = 1
			 AND extendedProperty.major_id = columnInfo.object_id
			 AND extendedProperty.minor_id = columnInfo.column_id
			WHERE columnInfo.object_id = OBJECT_ID(N'Sales.SpecialOffer')
			  AND columnInfo.name IN (N'StartDate', N'EndDate')
			ORDER BY columnInfo.name, extendedProperty.name;
			""";
			await using var reader = await dateMetadataCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				output.WriteLine(
					$"Date-column metadata={reader.GetString(0)}; property={(reader.IsDBNull(1) ? "none visible" : reader.GetString(1))}; value={(reader.IsDBNull(2) ? "none" : reader.GetString(2))}");
			}
		}

		await using (var dateDefaultsCommand = connection.CreateCommand())
		{
			dateDefaultsCommand.CommandText = """
				SELECT columnInfo.name, defaultConstraint.definition
				FROM sys.columns AS columnInfo
			LEFT JOIN sys.default_constraints AS defaultConstraint
			  ON defaultConstraint.parent_object_id = columnInfo.object_id
			 AND defaultConstraint.parent_column_id = columnInfo.column_id
			WHERE columnInfo.object_id = OBJECT_ID(N'Sales.SpecialOffer')
			  AND columnInfo.name IN (N'StartDate', N'EndDate')
			ORDER BY columnInfo.name;
			""";
			await using var reader = await dateDefaultsCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				output.WriteLine(
					$"Date-column default={reader.GetString(0)}; expression={(reader.IsDBNull(1) ? "none visible" : reader.GetString(1))}");
			}
		}

		await using (var constraintCommand = connection.CreateCommand())
		{
			constraintCommand.CommandText = """
				SELECT foreignKey.name, foreignKey.is_disabled, foreignKey.is_not_trusted,
				       parentColumn.name, referencedColumn.name, foreignKeyColumn.constraint_column_id
				FROM sys.foreign_keys AS foreignKey
				INNER JOIN sys.foreign_key_columns AS foreignKeyColumn ON foreignKeyColumn.constraint_object_id = foreignKey.object_id
				INNER JOIN sys.columns AS parentColumn
				  ON parentColumn.object_id = foreignKeyColumn.parent_object_id
				 AND parentColumn.column_id = foreignKeyColumn.parent_column_id
				INNER JOIN sys.columns AS referencedColumn
				  ON referencedColumn.object_id = foreignKeyColumn.referenced_object_id
				 AND referencedColumn.column_id = foreignKeyColumn.referenced_column_id
				WHERE foreignKey.parent_object_id = OBJECT_ID(N'Sales.SalesOrderDetail')
				  AND foreignKey.referenced_object_id = OBJECT_ID(N'Sales.SpecialOfferProduct')
				ORDER BY foreignKeyColumn.constraint_column_id;
				""";
			await using var reader = await constraintCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				output.WriteLine(
					$"FK={reader.GetString(0)}; disabled={reader.GetBoolean(1)}; untrusted={reader.GetBoolean(2)}; order-detail-column={reader.GetString(3)}; referenced-column={reader.GetString(4)}; key-position={reader.GetByte(5)}");
			}
		}
	}

	[SqlServerIntegrationFact]
	public async Task SpecialOfferOne_LinkCoverageForFinishedAndNonFinishedProducts()
	{
		var configuration = SqlServerReadAdapterConfiguration.FromEnvironment();
		Assert.True(configuration.IsConfigured);
		var connectionSettings = new SqlConnectionStringBuilder(configuration.ConnectionString);
		Assert.Equal("AdventureWorks2022_Test", connectionSettings.InitialCatalog);
		Assert.Equal("AdventureWorksTestReader", connectionSettings.UserID);
		Assert.False(connectionSettings.IntegratedSecurity);

		await using var connection = new SqlConnection(configuration.ConnectionString);
		await connection.OpenAsync();
		Assert.Equal("AdventureWorks2022_Test", connection.Database);

		await using (var offerCommand = new SqlCommand(
			"SELECT SpecialOfferID, Description FROM Sales.SpecialOffer WHERE SpecialOfferID = 1;",
			connection))
		await using (var offerReader = await offerCommand.ExecuteReaderAsync())
		{
			Assert.True(await offerReader.ReadAsync());
			Assert.Equal(1, offerReader.GetInt32(0));
			output.WriteLine($"Offer: {offerReader.GetInt32(0)} ({offerReader.GetString(1)})");
		}

		const string coverageSql = """
			SELECT p.FinishedGoodsFlag,
			       COUNT(*) AS ProductCount,
			       SUM(CASE WHEN sop.ProductID IS NOT NULL THEN 1 ELSE 0 END) AS LinkedCount,
			       SUM(CASE WHEN sop.ProductID IS NULL THEN 1 ELSE 0 END) AS UnlinkedCount
			FROM Production.Product AS p
			LEFT JOIN Sales.SpecialOfferProduct AS sop
			  ON sop.SpecialOfferID = 1
			 AND sop.ProductID = p.ProductID
			GROUP BY p.FinishedGoodsFlag
			ORDER BY p.FinishedGoodsFlag DESC;

			SELECT COALESCE(pc.Name, N'Uncategorized') AS CategoryName,
			       p.FinishedGoodsFlag,
			       COUNT(*) AS ProductCount,
			       SUM(CASE WHEN sop.ProductID IS NOT NULL THEN 1 ELSE 0 END) AS LinkedCount,
			       MIN(CASE WHEN sop.ProductID IS NOT NULL THEN p.ProductID END) AS LinkedProductId,
			       MIN(CASE WHEN sop.ProductID IS NULL THEN p.ProductID END) AS UnlinkedProductId
			FROM Production.Product AS p
			LEFT JOIN Production.ProductSubcategory AS ps
			  ON ps.ProductSubcategoryID = p.ProductSubcategoryID
			LEFT JOIN Production.ProductCategory AS pc
			  ON pc.ProductCategoryID = ps.ProductCategoryID
			LEFT JOIN Sales.SpecialOfferProduct AS sop
			  ON sop.SpecialOfferID = 1
			 AND sop.ProductID = p.ProductID
			GROUP BY COALESCE(pc.Name, N'Uncategorized'), p.FinishedGoodsFlag
			ORDER BY CategoryName, p.FinishedGoodsFlag DESC;
			""";

		await using var coverageCommand = new SqlCommand(coverageSql, connection);
		await using var coverageReader = await coverageCommand.ExecuteReaderAsync();
		var grandTotal = 0;
		var grandLinked = 0;
		var groupCount = 0;
		while (await coverageReader.ReadAsync())
		{
			var finishedGoods = coverageReader.GetBoolean(0);
			var productCount = coverageReader.GetInt32(1);
			var linkedCount = coverageReader.GetInt32(2);
			var unlinkedCount = coverageReader.GetInt32(3);
			Assert.Equal(productCount, linkedCount + unlinkedCount);
			grandTotal += productCount;
			grandLinked += linkedCount;
			groupCount++;
			output.WriteLine($"{(finishedGoods ? "Finished goods" : "Non-finished")}: total={productCount}, linked={linkedCount}, unlinked={unlinkedCount}");
		}

		Assert.Equal(2, groupCount);
		Assert.True(await coverageReader.NextResultAsync());
		var categoryTotal = 0;
		var categoryLinked = 0;
		while (await coverageReader.ReadAsync())
		{
			var categoryName = coverageReader.GetString(0);
			var finishedGoods = coverageReader.GetBoolean(1);
			var productCount = coverageReader.GetInt32(2);
			var linkedCount = coverageReader.GetInt32(3);
			var linkedProductId = coverageReader.IsDBNull(4) ? "none" : coverageReader.GetInt32(4).ToString();
			var unlinkedProductId = coverageReader.IsDBNull(5) ? "none" : coverageReader.GetInt32(5).ToString();
			categoryTotal += productCount;
			categoryLinked += linkedCount;
			output.WriteLine($"Category={categoryName}; {(finishedGoods ? "finished" : "non-finished")}: total={productCount}, linked={linkedCount}, representative-linked-id={linkedProductId}, representative-unlinked-id={unlinkedProductId}");
		}

		Assert.Equal(grandTotal, categoryTotal);
		Assert.Equal(grandLinked, categoryLinked);
		output.WriteLine($"All products: total={grandTotal}, linked={grandLinked}, unlinked={grandTotal - grandLinked}");
	}

	[SqlServerIntegrationFact]
	public async Task CustomerOrderOptions_UsesVerifiedLinksAndUnionsBothEntityReferences()
	{
		var configuration = SqlServerReadAdapterConfiguration.FromEnvironment();
		Assert.True(configuration.IsConfigured);
		var connectionSettings = new SqlConnectionStringBuilder(configuration.ConnectionString);
		Assert.Equal("AdventureWorks2022_Test", connectionSettings.InitialCatalog);
		Assert.Equal("AdventureWorksTestReader", connectionSettings.UserID);
		Assert.False(connectionSettings.IntegratedSecurity);

		await using var connection = new SqlConnection(configuration.ConnectionString);
		await connection.OpenAsync();
		var hasOrderOptionPermissions = await HasOrderOptionPermissionsAsync(connection);

		using var factory = new CloneIntegrationApiFactory();
		using var client = factory.CreateClient();
		if (!hasOrderOptionPermissions)
		{
			using var unavailableResponse = await client.GetAsync("/api/customers/1/order-options");
			Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableResponse.StatusCode);
			return;
		}

		var customerIds = await FindCustomersWithLinkedAddressesAsync(connection);
		Assert.True(customerIds.PersonOnly.HasValue, "Clone needs a person-only customer with a linked address.");
		Assert.True(customerIds.StoreOnly.HasValue, "Clone needs a store-only customer with a linked address.");

		await AssertOrderOptionsAsync(client, connection, customerIds.PersonOnly.Value);
		await AssertOrderOptionsAsync(client, connection, customerIds.StoreOnly.Value);
		if (customerIds.Both.HasValue)
		{
			await AssertOrderOptionsAsync(client, connection, customerIds.Both.Value);
		}
	}

	[SqlServerIntegrationFact]
	public async Task ConfiguredEndpoints_ReturnRealProductAndMinimalCustomerDtosFromApprovedClone()
	{
		using var factory = new CloneIntegrationApiFactory();
		using var client = factory.CreateClient();

		using var productResponse = await client.GetAsync("/api/products?pageSize=2");
		Assert.Equal(HttpStatusCode.OK, productResponse.StatusCode);
		var products = await productResponse.Content.ReadFromJsonAsync<ProductPageDto>();
		Assert.NotNull(products);
		Assert.NotEmpty(products.Items);
		var product = products.Items[0];
		Assert.True(product.ProductId > 0);
		Assert.False(string.IsNullOrWhiteSpace(product.Name));
		Assert.False(string.IsNullOrWhiteSpace(product.ProductNumber));

		using var detailResponse = await client.GetAsync($"/api/products/{product.ProductId}");
		Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
		var detail = await detailResponse.Content.ReadFromJsonAsync<ProductDetailDto>();
		Assert.Equal(product.ProductId, detail?.ProductId);
		Assert.NotNull(detail?.ListPrice);

		var configuration = SqlServerReadAdapterConfiguration.FromEnvironment();
		Assert.True(configuration.IsConfigured);
		await using var connection = new SqlConnection(configuration.ConnectionString);
		await connection.OpenAsync();
		await using var priceCommand = connection.CreateCommand();
		priceCommand.CommandText = "SELECT ListPrice FROM Production.Product WHERE ProductID = @productId;";
		priceCommand.Parameters.Add("@productId", System.Data.SqlDbType.Int).Value = product.ProductId;
		var storedListPrice = await priceCommand.ExecuteScalarAsync();
		Assert.IsType<decimal>(storedListPrice);
		Assert.Equal((decimal)storedListPrice, detail?.ListPrice);

		using var offersResponse = await client.GetAsync($"/api/products/{product.ProductId}/offers");
		Assert.Equal(HttpStatusCode.OK, offersResponse.StatusCode);
		Assert.NotNull(await offersResponse.Content.ReadFromJsonAsync<ProductOfferDto[]>());

		using var customerResponse = await client.GetAsync("/api/customers?pageSize=2");
		Assert.Equal(HttpStatusCode.OK, customerResponse.StatusCode);
		var customers = await customerResponse.Content.ReadFromJsonAsync<CustomerPageDto>();
		Assert.NotNull(customers);
		Assert.NotEmpty(customers.Items);
		Assert.All(customers.Items, customer =>
		{
			Assert.True(customer.CustomerId > 0);
			Assert.Contains(customer.CustomerKind, new[] { "Person", "Store" });
		});
		Assert.Equal(
			new[] { "CustomerId", "DisplayName", "CustomerKind" },
			typeof(CustomerDto).GetProperties().Select(property => property.Name));
	}

	[SqlServerIntegrationFact]
	public async Task Aw034_EligibleOffersEndpointDistinguishesEmptyEligibilityFromMissingProduct()
	{
		using var factory = new CloneIntegrationApiFactory();
		using var client = factory.CreateClient();

		using var existingResponse = await client.GetAsync("/api/products/1/eligible-offers?quantity=1");
		Assert.Equal(HttpStatusCode.OK, existingResponse.StatusCode);
		var eligibleOffers = await existingResponse.Content.ReadFromJsonAsync<ProductEligibleOfferDto[]>();
		Assert.NotNull(eligibleOffers);
		Assert.Empty(eligibleOffers);

		using var missingResponse = await client.GetAsync("/api/products/2147483647/eligible-offers?quantity=1");
		Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
	}

	[SqlServerIntegrationFact]
	public async Task Aw034_NoOfferPersistenceReportsOnlyProductsLinkedToOfferOne()
	{
		using var factory = new CloneIntegrationApiFactory();
		using var client = factory.CreateClient();

		using var linkedProductResponse = await client.GetAsync("/api/products/680/no-offer-persistence");
		Assert.Equal(HttpStatusCode.OK, linkedProductResponse.StatusCode);
		Assert.Equal(
			new ProductNoOfferPersistenceDto(true),
			await linkedProductResponse.Content.ReadFromJsonAsync<ProductNoOfferPersistenceDto>());

		using var unlinkedProductResponse = await client.GetAsync("/api/products/1/no-offer-persistence");
		Assert.Equal(HttpStatusCode.OK, unlinkedProductResponse.StatusCode);
		Assert.Equal(
			new ProductNoOfferPersistenceDto(false),
			await unlinkedProductResponse.Content.ReadFromJsonAsync<ProductNoOfferPersistenceDto>());

		using var missingProductResponse = await client.GetAsync("/api/products/2147483647/no-offer-persistence");
		Assert.Equal(HttpStatusCode.NotFound, missingProductResponse.StatusCode);
	}

	private static async Task<bool> HasOrderOptionPermissionsAsync(SqlConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT CONVERT(int, HAS_PERMS_BY_NAME(N'Person.BusinessEntityAddress', N'OBJECT', N'SELECT')),
			       CONVERT(int, HAS_PERMS_BY_NAME(N'Person.Address', N'OBJECT', N'SELECT')),
			       CONVERT(int, HAS_PERMS_BY_NAME(N'Person.AddressType', N'OBJECT', N'SELECT')),
			       CONVERT(int, HAS_PERMS_BY_NAME(N'Purchasing.ShipMethod', N'OBJECT', N'SELECT'));
			""";
		await using var reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		for (var index = 0; index < reader.FieldCount; index++)
		{
			if (reader.IsDBNull(index) || reader.GetInt32(index) != 1)
			{
				return false;
			}
		}

		return true;
	}

	private static async Task<(int? PersonOnly, int? StoreOnly, int? Both)> FindCustomersWithLinkedAddressesAsync(
		SqlConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT
			(
				SELECT TOP (1) customer.CustomerID
				FROM Sales.Customer AS customer
				INNER JOIN Person.Person AS person ON person.BusinessEntityID = customer.PersonID
				WHERE customer.StoreID IS NULL
				  AND EXISTS
				  (
					SELECT 1
					FROM Person.BusinessEntityAddress AS link
					INNER JOIN Person.Address AS address ON address.AddressID = link.AddressID
					INNER JOIN Person.AddressType AS addressType ON addressType.AddressTypeID = link.AddressTypeID
					WHERE link.BusinessEntityID = person.BusinessEntityID
				  )
				ORDER BY customer.CustomerID
			),
			(
				SELECT TOP (1) customer.CustomerID
				FROM Sales.Customer AS customer
				INNER JOIN Sales.Store AS store ON store.BusinessEntityID = customer.StoreID
				WHERE customer.PersonID IS NULL
				  AND EXISTS
				  (
					SELECT 1
					FROM Person.BusinessEntityAddress AS link
					INNER JOIN Person.Address AS address ON address.AddressID = link.AddressID
					INNER JOIN Person.AddressType AS addressType ON addressType.AddressTypeID = link.AddressTypeID
					WHERE link.BusinessEntityID = store.BusinessEntityID
				  )
				ORDER BY customer.CustomerID
			),
			(
				SELECT TOP (1) customer.CustomerID
				FROM Sales.Customer AS customer
				INNER JOIN Person.Person AS person ON person.BusinessEntityID = customer.PersonID
				INNER JOIN Sales.Store AS store ON store.BusinessEntityID = customer.StoreID
				WHERE EXISTS
				  (
					SELECT 1
					FROM Person.BusinessEntityAddress AS link
					INNER JOIN Person.Address AS address ON address.AddressID = link.AddressID
					INNER JOIN Person.AddressType AS addressType ON addressType.AddressTypeID = link.AddressTypeID
					WHERE link.BusinessEntityID = person.BusinessEntityID
				  )
				  AND EXISTS
				  (
					SELECT 1
					FROM Person.BusinessEntityAddress AS link
					INNER JOIN Person.Address AS address ON address.AddressID = link.AddressID
					INNER JOIN Person.AddressType AS addressType ON addressType.AddressTypeID = link.AddressTypeID
					WHERE link.BusinessEntityID = store.BusinessEntityID
				  )
				ORDER BY customer.CustomerID
			);
			""";
		await using var reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		return (
			reader.IsDBNull(0) ? null : reader.GetInt32(0),
			reader.IsDBNull(1) ? null : reader.GetInt32(1),
			reader.IsDBNull(2) ? null : reader.GetInt32(2));
	}

	private static async Task AssertOrderOptionsAsync(HttpClient client, SqlConnection connection, int customerId)
	{
		using var response = await client.GetAsync($"/api/customers/{customerId}/order-options");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var options = await response.Content.ReadFromJsonAsync<CustomerOrderOptionsDto>();
		Assert.NotNull(options);
		Assert.True(options.Addresses.Count > 0, "Expected at least one verifiably linked address.");
		Assert.True(options.ShipMethods.Count > 0, "Expected existing ship methods.");
		Assert.True(options.ShipMethods.Count <= CustomerOrderOptionsSql.MaximumShipMethods);
		Assert.Equal(options.Addresses.Count, options.Addresses.Select(address => address.AddressId).Distinct().Count());
		Assert.All(options.Addresses, address => Assert.NotEmpty(address.AddressTypes));
		Assert.All(options.ShipMethods, method => Assert.True(method.ShipMethodId > 0));

		var expectedAddressIds = await FindAddressIdsAsync(connection, customerId);
		Assert.Equal(expectedAddressIds, options.Addresses.Select(address => address.AddressId).Order().ToArray());
	}

	private static async Task<int[]> FindAddressIdsAsync(SqlConnection connection, int customerId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT DISTINCT address.AddressID
			FROM Sales.Customer AS customer
			CROSS APPLY (VALUES (customer.PersonID), (customer.StoreID)) AS entity(BusinessEntityID)
			INNER JOIN Person.BusinessEntityAddress AS link ON link.BusinessEntityID = entity.BusinessEntityID
			INNER JOIN Person.Address AS address ON address.AddressID = link.AddressID
			INNER JOIN Person.AddressType AS addressType ON addressType.AddressTypeID = link.AddressTypeID
			WHERE customer.CustomerID = @customerId
			  AND entity.BusinessEntityID IS NOT NULL
			ORDER BY address.AddressID;
			""";
		command.Parameters.Add("@customerId", System.Data.SqlDbType.Int).Value = customerId;
		await using var reader = await command.ExecuteReaderAsync();
		var addressIds = new List<int>();
		while (await reader.ReadAsync())
		{
			addressIds.Add(reader.GetInt32(0));
		}

		return addressIds.ToArray();
	}
}

public sealed class CloneIntegrationApiFactory : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Development");
		builder.UseUrls("http://127.0.0.1:5080");
		builder.UseSetting(ApiAuthentication.DevelopmentAuthenticationEnvironmentVariable, "true");
	}
}