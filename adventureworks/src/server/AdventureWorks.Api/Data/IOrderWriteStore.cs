namespace AdventureWorks.Api.Data;

public interface IOrderWriteStore
{
	bool IsConfigured { get; }

	/// <summary>
	/// Inserts one Sales.SalesOrderHeader and its Sales.SalesOrderDetail rows in a single transaction and returns
	/// the persisted (post-trigger) values. Throws <see cref="OrderWriteUnavailableException"/> on any failure;
	/// nothing is committed in that case.
	/// </summary>
	Task<PersistedOrder> CreateOrderAsync(OrderWriteCommand command, CancellationToken cancellationToken);
}

public sealed record OrderWriteCommand(
	DateTime OrderDate,
	DateTime DueDate,
	int CustomerId,
	int BillToAddressId,
	int ShipToAddressId,
	int ShipMethodId,
	decimal TaxAmt,
	decimal Freight,
	IReadOnlyList<OrderWriteLine> Lines);

public sealed record OrderWriteLine(
	int ProductId,
	short Quantity,
	int SpecialOfferId,
	decimal UnitPrice,
	decimal UnitPriceDiscount);

public sealed record PersistedOrder(
	int SalesOrderId,
	string SalesOrderNumber,
	DateTime OrderDate,
	DateTime DueDate,
	decimal SubTotal,
	decimal TaxAmt,
	decimal Freight,
	decimal TotalDue,
	IReadOnlyList<PersistedOrderLine> Lines);

public sealed record PersistedOrderLine(
	int ProductId,
	int Quantity,
	int SpecialOfferId,
	decimal UnitPrice,
	decimal UnitPriceDiscount,
	decimal LineTotal);

public sealed class OrderWriteUnavailableException : Exception
{
	public OrderWriteUnavailableException()
	{
	}

	public OrderWriteUnavailableException(string message) : base(message)
	{
	}
}

public sealed class UnconfiguredOrderWriteStore : IOrderWriteStore
{
	public bool IsConfigured => false;

	public Task<PersistedOrder> CreateOrderAsync(OrderWriteCommand command, CancellationToken cancellationToken) =>
		throw new OrderWriteUnavailableException("No demo order writer credential is configured.");
}
