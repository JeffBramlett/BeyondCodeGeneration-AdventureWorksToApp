namespace AdventureWorks.Api.Contracts;

public sealed record OrderDraftRequest(
	int CustomerId,
	int BillToAddressId,
	int ShipToAddressId,
	int ShipMethodId,
	IReadOnlyList<OrderDraftLineRequest?>? Lines);

public sealed record OrderDraftLineRequest(
	int ProductId,
	int Quantity,
	int? OfferId);

public sealed record ValidatedOrderDraftShape
{
	public int CustomerId { get; init; }
	public int BillToAddressId { get; init; }
	public int ShipToAddressId { get; init; }
	public int ShipMethodId { get; init; }
	public IReadOnlyList<ValidatedOrderDraftLineShape> Lines { get; }

	public ValidatedOrderDraftShape(
		int CustomerId,
		int BillToAddressId,
		int ShipToAddressId,
		int ShipMethodId,
		IEnumerable<ValidatedOrderDraftLineShape> Lines)
	{
		ArgumentNullException.ThrowIfNull(Lines);

		this.CustomerId = CustomerId;
		this.BillToAddressId = BillToAddressId;
		this.ShipToAddressId = ShipToAddressId;
		this.ShipMethodId = ShipMethodId;
		this.Lines = Array.AsReadOnly(Lines.ToArray());
	}
}

public sealed record ValidatedOrderDraftLineShape(
	int ProductId,
	int Quantity,
	int? OfferId);