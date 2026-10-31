using System.Data;
using Microsoft.Data.SqlClient;

namespace AdventureWorks.Api.Data;

public static class OrderWriteSql
{
	public const string IdentityCheck = "SELECT DB_NAME(), SUSER_SNAME();";

	// Sales.SalesOrderHeader/Detail have triggers; OUTPUT clauses are intentionally not used.
	public const string InsertHeader = """
		INSERT INTO Sales.SalesOrderHeader
			(OrderDate, DueDate, CustomerID, BillToAddressID, ShipToAddressID, ShipMethodID, TaxAmt, Freight)
		VALUES
			(@OrderDate, @DueDate, @CustomerID, @BillToAddressID, @ShipToAddressID, @ShipMethodID, @TaxAmt, @Freight);
		SELECT CONVERT(int, SCOPE_IDENTITY());
		""";

	public const string InsertDetail = """
		INSERT INTO Sales.SalesOrderDetail
			(SalesOrderID, OrderQty, ProductID, SpecialOfferID, UnitPrice, UnitPriceDiscount)
		VALUES
			(@SalesOrderID, @OrderQty, @ProductID, @SpecialOfferID, @UnitPrice, @UnitPriceDiscount);
		""";

	public const string ReadBackHeader = """
		SELECT SalesOrderNumber, OrderDate, DueDate, SubTotal, TaxAmt, Freight, TotalDue
		FROM Sales.SalesOrderHeader
		WHERE SalesOrderID = @SalesOrderID;
		""";

	public const string ReadBackDetails = """
		SELECT ProductID, OrderQty, SpecialOfferID, UnitPrice, UnitPriceDiscount, LineTotal
		FROM Sales.SalesOrderDetail
		WHERE SalesOrderID = @SalesOrderID
		ORDER BY SalesOrderDetailID;
		""";
}

/// <summary>
/// Local-demo order writer using the clone-only AdventureWorksTestWriter login against AdventureWorks2022_Test.
/// </summary>
public sealed class SqlServerOrderWriteStore(
	SqlServerOrderWriterConfiguration configuration,
	ILogger<SqlServerOrderWriteStore> logger) : IOrderWriteStore
{
	public bool IsConfigured => configuration.IsConfigured;

	public async Task<PersistedOrder> CreateOrderAsync(OrderWriteCommand command, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(command);
		if (!configuration.IsConfigured)
		{
			throw new OrderWriteUnavailableException("No demo order writer credential is configured.");
		}

		var stage = "connection configuration";
		try
		{
			var builder = new SqlConnectionStringBuilder(configuration.ConnectionString);
			if (builder.IntegratedSecurity ||
				!string.Equals(builder.InitialCatalog, SqlServerOrderWriterConfiguration.ExpectedDatabase, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(builder.UserID, SqlServerOrderWriterConfiguration.ExpectedLogin, StringComparison.OrdinalIgnoreCase))
			{
				logger.LogWarning("Demo order writer rejected its configured target or login.");
				throw new OrderWriteUnavailableException();
			}

			builder.PersistSecurityInfo = false;
			await using var connection = new SqlConnection(builder.ConnectionString);
			stage = "open connection";
			await connection.OpenAsync(cancellationToken);

			stage = "identity check";
			await VerifyIdentityAsync(connection, cancellationToken);

			stage = "begin transaction";
			await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
			try
			{
				stage = "insert header";
				var salesOrderId = await InsertHeaderAsync(connection, transaction, command, cancellationToken);

				stage = "insert detail";
				foreach (var line in command.Lines)
				{
					await InsertDetailAsync(connection, transaction, salesOrderId, line, cancellationToken);
				}

				stage = "read back";
				var persisted = await ReadBackAsync(connection, transaction, salesOrderId, cancellationToken);

				stage = "commit";
				await transaction.CommitAsync(cancellationToken);
				logger.LogInformation(
					"Demo order {SalesOrderNumber} recorded with {LineCount} lines.",
					persisted.SalesOrderNumber,
					persisted.Lines.Count);
				return persisted;
			}
			catch
			{
				await TryRollbackAsync(transaction, stage);
				throw;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (OrderWriteUnavailableException)
		{
			throw;
		}
		catch (SqlException exception)
		{
			logger.LogWarning(
				"Demo order writer failed during {FailureStage} with provider error {SqlErrorNumber}: {SqlErrorMessage}",
				stage,
				exception.Number,
				exception.Message);
			throw new OrderWriteUnavailableException();
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or InvalidCastException)
		{
			logger.LogWarning(
				"Demo order writer failed during {FailureStage} with exception type {ExceptionType}.",
				stage,
				exception.GetType().Name);
			throw new OrderWriteUnavailableException();
		}
	}

	private async Task VerifyIdentityAsync(SqlConnection connection, CancellationToken cancellationToken)
	{
		await using var identity = connection.CreateCommand();
		identity.CommandText = OrderWriteSql.IdentityCheck;
		await using var reader = await identity.ExecuteReaderAsync(cancellationToken);
		if (!await reader.ReadAsync(cancellationToken) ||
			reader.IsDBNull(0) || reader.IsDBNull(1) ||
			!string.Equals(reader.GetString(0), SqlServerOrderWriterConfiguration.ExpectedDatabase, StringComparison.Ordinal) ||
			!string.Equals(reader.GetString(1), SqlServerOrderWriterConfiguration.ExpectedLogin, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogWarning("Demo order writer connected to an unexpected database or login; failing closed.");
			throw new OrderWriteUnavailableException();
		}
	}

	private static async Task<int> InsertHeaderAsync(
		SqlConnection connection,
		SqlTransaction transaction,
		OrderWriteCommand command,
		CancellationToken cancellationToken)
	{
		await using var header = connection.CreateCommand();
		header.Transaction = transaction;
		header.CommandText = OrderWriteSql.InsertHeader;
		header.Parameters.Add("@OrderDate", SqlDbType.DateTime).Value = command.OrderDate;
		header.Parameters.Add("@DueDate", SqlDbType.DateTime).Value = command.DueDate;
		header.Parameters.Add("@CustomerID", SqlDbType.Int).Value = command.CustomerId;
		header.Parameters.Add("@BillToAddressID", SqlDbType.Int).Value = command.BillToAddressId;
		header.Parameters.Add("@ShipToAddressID", SqlDbType.Int).Value = command.ShipToAddressId;
		header.Parameters.Add("@ShipMethodID", SqlDbType.Int).Value = command.ShipMethodId;
		header.Parameters.Add("@TaxAmt", SqlDbType.Money).Value = command.TaxAmt;
		header.Parameters.Add("@Freight", SqlDbType.Money).Value = command.Freight;
		var result = await header.ExecuteScalarAsync(cancellationToken);
		if (result is not int salesOrderId || salesOrderId <= 0)
		{
			throw new InvalidOperationException("Order header identity was not returned.");
		}

		return salesOrderId;
	}

	private static async Task InsertDetailAsync(
		SqlConnection connection,
		SqlTransaction transaction,
		int salesOrderId,
		OrderWriteLine line,
		CancellationToken cancellationToken)
	{
		await using var detail = connection.CreateCommand();
		detail.Transaction = transaction;
		detail.CommandText = OrderWriteSql.InsertDetail;
		detail.Parameters.Add("@SalesOrderID", SqlDbType.Int).Value = salesOrderId;
		detail.Parameters.Add("@OrderQty", SqlDbType.SmallInt).Value = line.Quantity;
		detail.Parameters.Add("@ProductID", SqlDbType.Int).Value = line.ProductId;
		detail.Parameters.Add("@SpecialOfferID", SqlDbType.Int).Value = line.SpecialOfferId;
		detail.Parameters.Add("@UnitPrice", SqlDbType.Money).Value = line.UnitPrice;
		detail.Parameters.Add("@UnitPriceDiscount", SqlDbType.Money).Value = line.UnitPriceDiscount;
		var affected = await detail.ExecuteNonQueryAsync(cancellationToken);
		if (affected < 1)
		{
			throw new InvalidOperationException("Order detail insert did not affect a row.");
		}
	}

	private static async Task<PersistedOrder> ReadBackAsync(
		SqlConnection connection,
		SqlTransaction transaction,
		int salesOrderId,
		CancellationToken cancellationToken)
	{
		string salesOrderNumber;
		DateTime orderDate;
		DateTime dueDate;
		decimal subTotal;
		decimal taxAmt;
		decimal freight;
		decimal totalDue;
		await using (var headerCommand = connection.CreateCommand())
		{
			headerCommand.Transaction = transaction;
			headerCommand.CommandText = OrderWriteSql.ReadBackHeader;
			headerCommand.Parameters.Add("@SalesOrderID", SqlDbType.Int).Value = salesOrderId;
			await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
			if (!await reader.ReadAsync(cancellationToken))
			{
				throw new InvalidOperationException("Inserted order header could not be read back.");
			}

			salesOrderNumber = reader.GetString(0);
			orderDate = reader.GetDateTime(1);
			dueDate = reader.GetDateTime(2);
			subTotal = reader.GetDecimal(3);
			taxAmt = reader.GetDecimal(4);
			freight = reader.GetDecimal(5);
			totalDue = reader.GetDecimal(6);
		}

		var lines = new List<PersistedOrderLine>();
		await using (var detailCommand = connection.CreateCommand())
		{
			detailCommand.Transaction = transaction;
			detailCommand.CommandText = OrderWriteSql.ReadBackDetails;
			detailCommand.Parameters.Add("@SalesOrderID", SqlDbType.Int).Value = salesOrderId;
			await using var reader = await detailCommand.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				lines.Add(new PersistedOrderLine(
					reader.GetInt32(0),
					reader.GetInt16(1),
					reader.GetInt32(2),
					reader.GetDecimal(3),
					reader.GetDecimal(4),
					reader.GetDecimal(5)));
			}
		}

		return new PersistedOrder(salesOrderId, salesOrderNumber, orderDate, dueDate, subTotal, taxAmt, freight, totalDue, lines);
	}

	private async Task TryRollbackAsync(SqlTransaction transaction, string stage)
	{
		try
		{
			if (transaction.Connection is not null)
			{
				await transaction.RollbackAsync(CancellationToken.None);
			}
		}
		catch (Exception exception) when (exception is SqlException or InvalidOperationException)
		{
			logger.LogWarning(
				"Demo order writer rollback after {FailureStage} reported {ExceptionType}.",
				stage,
				exception.GetType().Name);
		}
	}
}
