namespace AdventureWorks.Api.Contracts;

public sealed record OrderReplayLine(int ProductId, int Quantity, int? OfferId);

public sealed class OrderReplaySnapshot
{
	public int SalesOrderId { get; }
	public string SalesOrderNumber { get; }
	public int CustomerId { get; }
	public int BillToAddressId { get; }
	public int ShipToAddressId { get; }
	public int ShipMethodId { get; }
	public IReadOnlyList<OrderReplayLine> Lines { get; }

	public OrderReplaySnapshot(
		int salesOrderId,
		string salesOrderNumber,
		int customerId,
		int billToAddressId,
		int shipToAddressId,
		int shipMethodId,
		IEnumerable<OrderReplayLine> lines)
	{
		ArgumentNullException.ThrowIfNull(salesOrderNumber);
		ArgumentNullException.ThrowIfNull(lines);

		if (salesOrderId <= 0 || customerId <= 0 || billToAddressId <= 0 || shipToAddressId <= 0 || shipMethodId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(salesOrderId), "Snapshot identifiers must be positive.");
		}

		if (string.IsNullOrWhiteSpace(salesOrderNumber))
		{
			throw new ArgumentException("A generated sales order number is required.", nameof(salesOrderNumber));
		}

		var lineSnapshot = lines.ToArray();
		if (lineSnapshot.Length is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(lines), "A replay snapshot must contain between 1 and 100 lines.");
		}

		foreach (var line in lineSnapshot)
		{
			if (line is null || line.ProductId <= 0 || line.Quantity is < 1 or > short.MaxValue || line.OfferId is <= 0)
			{
				throw new ArgumentException("Replay lines must contain valid product, quantity, and optional offer identifiers.", nameof(lines));
			}
		}

		SalesOrderId = salesOrderId;
		SalesOrderNumber = salesOrderNumber;
		CustomerId = customerId;
		BillToAddressId = billToAddressId;
		ShipToAddressId = shipToAddressId;
		ShipMethodId = shipMethodId;
		Lines = Array.AsReadOnly(lineSnapshot);
	}
}