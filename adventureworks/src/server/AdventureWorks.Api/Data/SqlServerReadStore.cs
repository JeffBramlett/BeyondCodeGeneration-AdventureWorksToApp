using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Application;
using Microsoft.Data.SqlClient;
using System.Data;

namespace AdventureWorks.Api.Data;

public sealed class SqlServerReadStore(
	SqlServerReadAdapterConfiguration configuration,
	ILogger<SqlServerReadStore> logger) :
	IProductCatalogReadStore,
	ICustomerReadStore,
	IOrderReadStore,
	IOfferPricingReadStore
{
	private const string ExpectedDatabase = "AdventureWorks2022_Test";
	private const string ExpectedLogin = "AdventureWorksTestReader";

	public bool IsConfigured => configuration.IsConfigured;

	async Task<OrderPageDto> IOrderReadStore.SearchAsync(OrderSearchRequest request, CancellationToken cancellationToken)
	{
		var plan = OrderReadSql.BuildSearch(request);
		return await ExecuteAsync(plan, async (reader, token) =>
		{
			var items = new List<OrderSummaryDto>();
			while (await reader.ReadAsync(token))
			{
				items.Add(new OrderSummaryDto(reader.GetString(0), reader.GetDateTime(1)));
			}

			var hasMore = items.Count > request.PageSize;
			return new OrderPageDto(items.Take(request.PageSize).ToArray(), request.Page, request.PageSize, hasMore);
		}, cancellationToken, requireOrderPermissions: true);
	}

	async Task<OrderDetailPageDto?> IOrderReadStore.GetByReferenceAsync(
		string orderReference,
		OrderLinesRequest request,
		CancellationToken cancellationToken)
	{
		var plan = OrderReadSql.BuildDetail(orderReference, request);
		return await ExecuteAsync<OrderDetailPageDto?>(plan, async (reader, token) =>
		{
			if (!await reader.ReadAsync(token))
			{
				return null;
			}

			var salesOrderNumber = reader.GetString(0);
			var orderDate = reader.GetDateTime(1);
			var lines = new List<OrderLineDto>();
			do
			{
				if (!reader.IsDBNull(2))
				{
					lines.Add(new OrderLineDto(
						Convert.ToInt32(reader.GetValue(2)),
						Convert.ToInt32(reader.GetValue(3))));
				}
			}
			while (await reader.ReadAsync(token));

			var hasMore = lines.Count > request.LinePageSize;
			return new OrderDetailPageDto(
				salesOrderNumber,
				orderDate,
				lines.Take(request.LinePageSize).ToArray(),
				request.LinePage,
				request.LinePageSize,
				hasMore);
		}, cancellationToken, requireOrderPermissions: true);
	}

	public Task<ProductPageDto> SearchAsync(ProductSearchRequest request, CancellationToken cancellationToken)
	{
		var plan = ProductCatalogSql.BuildSearch(request);
		return ExecuteAsync(plan, async (reader, token) =>
		{
			var items = new List<ProductDto>();
			while (await reader.ReadAsync(token))
			{
				items.Add(ReadProduct(reader));
			}

			var hasMore = items.Count > request.PageSize;
			return new ProductPageDto(items.Take(request.PageSize).ToArray(), request.Page, request.PageSize, hasMore);
		}, cancellationToken);
	}

	async Task<CustomerOrderOptionsDto?> ICustomerReadStore.GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken)
	{
		var addressResult = await ExecuteAsync(
			CustomerOrderOptionsSql.BuildAddresses(customerId),
			ReadCustomerAddressesAsync,
			cancellationToken,
			CustomerOrderOptionsSql.RequiredPermissions);
		if (!addressResult.CustomerExists)
		{
			return null;
		}

		var shipMethods = await ExecuteAsync(
			CustomerOrderOptionsSql.BuildShipMethods(),
			async (reader, token) =>
			{
				var items = new List<ShipMethodOptionDto>();
				while (await reader.ReadAsync(token))
				{
					items.Add(new ShipMethodOptionDto(reader.GetInt32(0), reader.GetString(1)));
				}

				return (IReadOnlyList<ShipMethodOptionDto>)items;
			},
			cancellationToken,
			CustomerOrderOptionsSql.RequiredPermissions);

		return new CustomerOrderOptionsDto(addressResult.Addresses, shipMethods);
	}

	public Task<ProductDetailDto?> GetByIdAsync(int productId, CancellationToken cancellationToken) =>
		ExecuteAsync(ProductCatalogSql.BuildDetail(productId), async (reader, token) =>
			await reader.ReadAsync(token) ? ReadProductDetail(reader) : null, cancellationToken);

	public Task<IReadOnlyList<ProductOfferDto>> GetOffersAsync(int productId, CancellationToken cancellationToken) =>
		ExecuteAsync(ProductCatalogSql.BuildOffers(productId), async (reader, token) =>
		{
			var offers = new List<ProductOfferDto>();
			while (await reader.ReadAsync(token))
			{
				offers.Add(new ProductOfferDto(reader.GetInt32(0), reader.GetString(1)));
			}

			return (IReadOnlyList<ProductOfferDto>)offers;
		}, cancellationToken);

	public Task<ProductOfferEligibilityReadResult?> GetOfferEligibilityDataAsync(int productId, CancellationToken cancellationToken) =>
		ExecuteAsync(ProductCatalogSql.BuildOfferEligibilityData(productId), async (reader, token) =>
		{
			if (!await reader.ReadAsync(token))
			{
				return null;
			}

			var foundProductId = reader.GetInt32(0);
			var offers = new List<ProductOfferEligibilityRow>();
			do
			{
				if (reader.IsDBNull(1))
				{
					continue;
				}

				offers.Add(new ProductOfferEligibilityRow(
					reader.GetInt32(1),
					reader.GetString(2),
					OfferEligibilityEvaluator.InterpretSqlDateTimeAsUtc(reader.GetDateTime(3)),
					OfferEligibilityEvaluator.InterpretSqlDateTimeAsUtc(reader.GetDateTime(4)),
					reader.GetInt32(5),
					reader.IsDBNull(6) ? null : reader.GetInt32(6),
					reader.GetDecimal(7)));
			}
			while (await reader.ReadAsync(token));

			return new ProductOfferEligibilityReadResult(foundProductId, offers);
		}, cancellationToken);

	public Task<OfferPricingRow?> GetOfferPricingAsync(int productId, int specialOfferId, CancellationToken cancellationToken) =>
		ExecuteAsync<OfferPricingRow?>(ProductCatalogSql.BuildOfferPricing(productId, specialOfferId), async (reader, token) =>
			await reader.ReadAsync(token)
				? new OfferPricingRow(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2))
				: null, cancellationToken);

	public Task<bool?> GetNoOfferPersistenceSupportAsync(int productId, CancellationToken cancellationToken) =>
		ExecuteAsync<bool?>(ProductCatalogSql.BuildNoOfferPersistenceSupport(productId), async (reader, token) =>
			await reader.ReadAsync(token) ? reader.GetBoolean(1) : null, cancellationToken);

	Task<CustomerPageDto> ICustomerReadStore.SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken)
	{
		var plan = CustomerSearchSql.BuildSearch(request);
		return ExecuteAsync(plan, async (reader, token) =>
		{
			var items = new List<CustomerDto>();
			while (await reader.ReadAsync(token))
			{
				items.Add(new CustomerDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
			}

			var hasMore = items.Count > request.PageSize;
			return new CustomerPageDto(items.Take(request.PageSize).ToArray(), request.Page, request.PageSize, hasMore);
		}, cancellationToken);
	}

	private async Task<TResult> ExecuteAsync<TResult>(
		SqlQueryPlan plan,
		Func<SqlDataReader, CancellationToken, Task<TResult>> readResult,
		CancellationToken cancellationToken,
		IEnumerable<string>? additionalRequiredPermissions = null,
		bool requireOrderPermissions = false)
	{
		var failureStage = "open connection";
		try
		{
			await using var connection = await OpenValidatedConnectionAsync(
				cancellationToken,
				stage => failureStage = stage,
				additionalRequiredPermissions,
				requireOrderPermissions);
			await using var command = connection.CreateCommand();
			failureStage = "read query";
			command.CommandText = plan.CommandText;
			command.CommandType = CommandType.Text;
			AddParameters(command, plan.Parameters);
			await using var reader = await command.ExecuteReaderAsync(cancellationToken);
			return await readResult(reader, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (SqlServerPrincipalGuardException exception)
		{
			logger.LogWarning(
				"SQL read adapter rejected principal due to {FailureCategory}; unexpected permissions: {UnexpectedPermissions}; missing permissions: {MissingPermissions}.",
				exception.FailureCategory,
				exception.UnexpectedPermissions,
				exception.MissingPermissions);
			throw new SqlReadAdapterUnavailableException();
		}
		catch (SqlException exception)
		{
			logger.LogWarning("SQL read adapter failed during {FailureStage} with provider error {SqlErrorNumber}.", failureStage, exception.Number);
			throw new SqlReadAdapterUnavailableException();
		}
		catch (SqlReadAdapterSafetyException exception)
		{
			logger.LogWarning("SQL read adapter safety check failed: {FailureCategory}.", exception.FailureCategory);
			throw new SqlReadAdapterUnavailableException();
		}
		catch (InvalidOperationException exception)
		{
			logger.LogWarning("SQL read adapter rejected its configured connection or operation at {FailureStage} with exception type {ExceptionType}.", failureStage, exception.GetType().Name);
			throw new SqlReadAdapterUnavailableException();
		}
		catch (ArgumentException exception)
		{
			logger.LogWarning("SQL read adapter rejected its configured connection or query parameters at {FailureStage} with exception type {ExceptionType}.", failureStage, exception.GetType().Name);
			throw new SqlReadAdapterUnavailableException();
		}
	}

	private async Task<SqlConnection> OpenValidatedConnectionAsync(
		CancellationToken cancellationToken,
		Action<string> setFailureStage,
		IEnumerable<string>? additionalRequiredPermissions,
		bool requireOrderPermissions)
	{
		setFailureStage("connection configuration");
		var builder = new SqlConnectionStringBuilder(configuration.ConnectionString);
		if (builder.IntegratedSecurity ||
			!string.Equals(builder.InitialCatalog, ExpectedDatabase, StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(builder.UserID, ExpectedLogin, StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrWhiteSpace(builder.Password))
		{
			throw new SqlReadAdapterSafetyException("configured target or login");
		}

		builder.PersistSecurityInfo = false;
		var connection = new SqlConnection(builder.ConnectionString);
		try
		{
			setFailureStage("open connection");
			await connection.OpenAsync(cancellationToken);
			await VerifyPrincipalAsync(connection, cancellationToken, setFailureStage, additionalRequiredPermissions, requireOrderPermissions);
			return connection;
		}
		catch
		{
			await connection.DisposeAsync();
			throw;
		}
	}

	private static async Task VerifyPrincipalAsync(
		SqlConnection connection,
		CancellationToken cancellationToken,
		Action<string> setFailureStage,
		IEnumerable<string>? additionalRequiredPermissions,
		bool requireOrderPermissions)
	{
		setFailureStage("identity check");
		await using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT DB_NAME(), ORIGINAL_LOGIN(), SUSER_SNAME(),
				       CONVERT(int, IS_SRVROLEMEMBER('sysadmin')),
				       CONVERT(int, IS_ROLEMEMBER('db_owner'));
				""";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken);
			if (!await reader.ReadAsync(cancellationToken) ||
				!string.Equals(reader.GetString(0), ExpectedDatabase, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(reader.GetString(1), ExpectedLogin, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(reader.GetString(2), ExpectedLogin, StringComparison.OrdinalIgnoreCase))
			{
				throw new SqlReadAdapterSafetyException("connected database or login");
			}

			var isSysAdmin = reader.GetInt32(3) == 1;
			var isDbOwner = reader.GetInt32(4) == 1;
			if (isSysAdmin || isDbOwner)
			{
				throw new InvalidOperationException("Elevated SQL principals are not permitted.");
			}
		}

		var permissions = new List<string>();
		setFailureStage("server permissions");
		await ReadPermissionsAsync(connection, "SELECT permission_name FROM sys.fn_my_permissions(NULL, 'SERVER');", permissions, cancellationToken);
		setFailureStage("database permissions");
		await ReadPermissionsAsync(connection, "SELECT permission_name FROM sys.fn_my_permissions(NULL, 'DATABASE');", permissions, cancellationToken);
		setFailureStage("object permissions");
		await ReadPermissionsAsync(connection, SqlServerPrincipalGuard.ObjectPermissionSnapshotSql, permissions, cancellationToken);
		await ReadPermissionsAsync(connection, """
			SELECT permission.permission_name COLLATE DATABASE_DEFAULT + N':SCHEMA:' +
			       schemaInfo.name COLLATE DATABASE_DEFAULT
			FROM sys.schemas AS schemaInfo
			CROSS APPLY sys.fn_my_permissions(QUOTENAME(schemaInfo.name), 'SCHEMA') AS permission
			WHERE schemaInfo.name NOT IN (N'sys', N'INFORMATION_SCHEMA');
			""", permissions, cancellationToken);

		setFailureStage("least-privilege check");
		var snapshot = new SqlServerPrincipalSnapshot(
			IsSysAdmin: false,
			IsDbOwner: false,
			EffectivePermissions: permissions);
		if (requireOrderPermissions)
		{
			setFailureStage("order column permissions");
			await ReadPermissionsAsync(
				connection,
				SqlServerPrincipalGuard.OrderColumnPermissionSnapshotSql,
				permissions,
				cancellationToken);
			snapshot = snapshot with { EffectivePermissions = permissions };
			SqlServerPrincipalGuard.EnsureOrderReadPermissions(snapshot);
		}
		else
		{
			SqlServerPrincipalGuard.EnsureLeastPrivilege(snapshot, additionalRequiredPermissions ?? []);
		}
	}

	private static async Task<CustomerAddressReadResult> ReadCustomerAddressesAsync(
		SqlDataReader reader,
		CancellationToken cancellationToken)
	{
		var grouped = new Dictionary<int, CustomerAddressBuilder>();
		var customerExists = false;
		while (await reader.ReadAsync(cancellationToken))
		{
			customerExists = true;
			if (reader.IsDBNull(1))
			{
				continue;
			}

			var addressId = reader.GetInt32(1);
			if (!grouped.TryGetValue(addressId, out var address))
			{
				address = new CustomerAddressBuilder(
					addressId,
					reader.GetString(2),
					reader.IsDBNull(3) ? null : reader.GetString(3),
					reader.GetString(4),
					reader.IsDBNull(5) ? null : reader.GetString(5));
				grouped.Add(addressId, address);
			}

			address.AddressTypes.Add(reader.GetString(6));
		}

		var addresses = grouped.Values
			.Select(address => new CustomerAddressOptionDto(
				address.AddressId,
				address.AddressLine1,
				address.AddressLine2,
				address.City,
				address.PostalCode,
				address.AddressTypes.Order(StringComparer.Ordinal).ToArray()))
			.ToArray();
		return new CustomerAddressReadResult(customerExists, addresses);
	}

	private static async Task ReadPermissionsAsync(
		SqlConnection connection,
		string commandText,
		ICollection<string> permissions,
		CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			permissions.Add(reader.GetString(0));
		}
	}

	private static void AddParameters(SqlCommand command, IReadOnlyDictionary<string, object?> values)
	{
		foreach (var entry in values)
		{
			var parameter = entry.Value switch
			{
				null => command.Parameters.Add(entry.Key, SqlDbType.NVarChar, 100),
				string => command.Parameters.Add(entry.Key, SqlDbType.NVarChar, 100),
				int => command.Parameters.Add(entry.Key, SqlDbType.Int),
				long => command.Parameters.Add(entry.Key, SqlDbType.BigInt),
				DateTime => command.Parameters.Add(entry.Key, SqlDbType.DateTime2),
				_ => throw new ArgumentException("Unsupported query parameter type.")
			};
			parameter.Value = entry.Value ?? DBNull.Value;
		}
	}

	private static ProductDto ReadProduct(SqlDataReader reader) => new(
		reader.GetInt32(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.IsDBNull(3) ? null : reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4));

	private static ProductDetailDto ReadProductDetail(SqlDataReader reader) => new(
		reader.GetInt32(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.IsDBNull(3) ? null : reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4),
		reader.IsDBNull(5) ? null : reader.GetDecimal(5));

	private sealed record CustomerAddressReadResult(
		bool CustomerExists,
		IReadOnlyList<CustomerAddressOptionDto> Addresses);

	private sealed class CustomerAddressBuilder(
		int addressId,
		string addressLine1,
		string? addressLine2,
		string city,
		string? postalCode)
	{
		public int AddressId { get; } = addressId;
		public string AddressLine1 { get; } = addressLine1;
		public string? AddressLine2 { get; } = addressLine2;
		public string City { get; } = city;
		public string? PostalCode { get; } = postalCode;
		public HashSet<string> AddressTypes { get; } = new(StringComparer.Ordinal);
	}
}

public sealed class SqlReadAdapterUnavailableException : Exception;

internal sealed class SqlReadAdapterSafetyException(string failureCategory) : Exception
{
	public string FailureCategory { get; } = failureCategory;
}