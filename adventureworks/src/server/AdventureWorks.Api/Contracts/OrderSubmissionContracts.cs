namespace AdventureWorks.Api.Contracts;

/// <summary>
/// Local-demo order submission request (POST /api/orders). See docs/api/order-submission.md.
/// </summary>
public sealed record OrderSubmissionRequest(
	string? SubmissionToken,
	int CustomerId,
	int BillToAddressId,
	int ShipToAddressId,
	int ShipMethodId,
	IReadOnlyList<OrderDraftLineRequest?>? Lines);

public sealed record OrderSubmissionConfirmationDto(
	string OrderReference,
	string Status,
	DateTime OrderDate,
	DateTime DueDate,
	int CustomerId,
	int BillToAddressId,
	int ShipToAddressId,
	int ShipMethodId,
	IReadOnlyList<OrderSubmissionLineDto> Lines,
	decimal SubTotal,
	decimal TaxAmt,
	decimal Freight,
	decimal TotalDue);

public sealed record OrderSubmissionLineDto(
	int ProductId,
	string ProductName,
	int Quantity,
	int OfferId,
	string? OfferDescription,
	decimal UnitPrice,
	decimal UnitPriceDiscount,
	decimal LineTotal);
