using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;

namespace AdventureWorks.Api.Application;

public enum OrderSubmissionOutcome
{
	Created,
	Replayed,
	InvalidShape,
	InProgress,
	RequiresReview,
	Unavailable
}

public sealed record OrderSubmissionResult(
	OrderSubmissionOutcome Outcome,
	OrderSubmissionConfirmationDto? Confirmation,
	IReadOnlyDictionary<string, string[]> Errors)
{
	public static OrderSubmissionResult Unavailable { get; } =
		new(OrderSubmissionOutcome.Unavailable, null, new Dictionary<string, string[]>());
}

/// <summary>
/// Local-demo order submission (Project Owner decision 2026-10-30): shape validation, single-use token,
/// read-store revalidation, server-authoritative demo pricing, and one-transaction persistence to the clone.
/// </summary>
public sealed class OrderSubmissionService(
	ICustomerReadStore customers,
	IProductCatalogReadStore catalog,
	IOfferPricingReadStore offerPricing,
	IOrderWriteStore writer,
	OrderSubmissionTokenRegistry tokens,
	TimeProvider timeProvider,
	ILogger<OrderSubmissionService> logger)
{
	public const string RecordedStatus = "Recorded";
	public static readonly TimeSpan DueDateOffset = TimeSpan.FromDays(7);

	public async Task<OrderSubmissionResult> SubmitAsync(OrderSubmissionRequest? request, CancellationToken cancellationToken)
	{
		var shape = ValidateShape(request, out var token, out var draft);
		if (shape.Count > 0)
		{
			return new OrderSubmissionResult(OrderSubmissionOutcome.InvalidShape, null, shape);
		}

		var begin = tokens.TryBegin(token);
		switch (begin.State)
		{
			case SubmissionTokenState.Completed:
				return new OrderSubmissionResult(OrderSubmissionOutcome.Replayed, begin.Confirmation, NoErrors());
			case SubmissionTokenState.InProgress:
				return new OrderSubmissionResult(OrderSubmissionOutcome.InProgress, null, NoErrors());
		}

		var completed = false;
		try
		{
			var result = await ProcessAsync(draft!, cancellationToken);
			if (result.Outcome == OrderSubmissionOutcome.Created && result.Confirmation is not null)
			{
				tokens.Complete(token, result.Confirmation);
				completed = true;
			}

			return result;
		}
		finally
		{
			if (!completed)
			{
				tokens.Release(token);
			}
		}
	}

	private async Task<OrderSubmissionResult> ProcessAsync(ValidatedOrderDraftShape draft, CancellationToken cancellationToken)
	{
		if (!customers.IsConfigured || !catalog.IsConfigured || !offerPricing.IsConfigured || !writer.IsConfigured)
		{
			return OrderSubmissionResult.Unavailable;
		}

		PricedOrder priced;
		try
		{
			var review = await ReviewAndPriceAsync(draft, cancellationToken);
			if (review.Errors.Count > 0)
			{
				return new OrderSubmissionResult(OrderSubmissionOutcome.RequiresReview, null, review.Errors);
			}

			priced = review.Order!;
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return OrderSubmissionResult.Unavailable;
		}

		var orderDate = TruncateToSeconds(timeProvider.GetLocalNow().DateTime);
		var command = new OrderWriteCommand(
			orderDate,
			orderDate.Add(DueDateOffset),
			draft.CustomerId,
			draft.BillToAddressId,
			draft.ShipToAddressId,
			draft.ShipMethodId,
			priced.TaxAmt,
			DemoOrderFinancials.FreightPerOrder,
			priced.Lines.Select(line => new OrderWriteLine(
				line.ProductId,
				(short)line.Quantity,
				line.SpecialOfferId,
				line.UnitPrice,
				line.UnitPriceDiscount)).ToArray());

		PersistedOrder persisted;
		try
		{
			persisted = await writer.CreateOrderAsync(command, cancellationToken);
		}
		catch (OrderWriteUnavailableException)
		{
			return OrderSubmissionResult.Unavailable;
		}

		var persistedByProduct = persisted.Lines.ToDictionary(line => line.ProductId);
		var lines = new List<OrderSubmissionLineDto>(priced.Lines.Count);
		foreach (var line in priced.Lines)
		{
			if (!persistedByProduct.TryGetValue(line.ProductId, out var stored))
			{
				// Committed but read-back is inconsistent; surface as unavailable rather than a partial confirmation.
				logger.LogError("Demo order {SalesOrderNumber} read-back is missing product line {ProductId}.", persisted.SalesOrderNumber, line.ProductId);
				return OrderSubmissionResult.Unavailable;
			}

			lines.Add(new OrderSubmissionLineDto(
				line.ProductId,
				line.ProductName,
				stored.Quantity,
				stored.SpecialOfferId,
				line.OfferDescription,
				stored.UnitPrice,
				stored.UnitPriceDiscount,
				stored.LineTotal));
		}

		var confirmation = new OrderSubmissionConfirmationDto(
			persisted.SalesOrderNumber,
			RecordedStatus,
			persisted.OrderDate,
			persisted.DueDate,
			draft.CustomerId,
			draft.BillToAddressId,
			draft.ShipToAddressId,
			draft.ShipMethodId,
			lines,
			persisted.SubTotal,
			persisted.TaxAmt,
			persisted.Freight,
			persisted.TotalDue);
		return new OrderSubmissionResult(OrderSubmissionOutcome.Created, confirmation, NoErrors());
	}

	private async Task<PriceReview> ReviewAndPriceAsync(ValidatedOrderDraftShape draft, CancellationToken cancellationToken)
	{
		var errors = new Dictionary<string, List<string>>();
		var options = await customers.GetOrderOptionsAsync(draft.CustomerId, cancellationToken);
		if (options is null)
		{
			AddError(errors, "customerId", "Customer was not found or order options are unavailable.");
			return PriceReview.Failed(errors);
		}

		if (!options.Addresses.Any(address => address.AddressId == draft.BillToAddressId))
		{
			AddError(errors, "billToAddressId", "Selected billing address is not available for this customer.");
		}

		if (!options.Addresses.Any(address => address.AddressId == draft.ShipToAddressId))
		{
			AddError(errors, "shipToAddressId", "Selected shipping address is not available for this customer.");
		}

		if (!options.ShipMethods.Any(method => method.ShipMethodId == draft.ShipMethodId))
		{
			AddError(errors, "shipMethodId", "Selected shipping method is not available.");
		}

		var evaluationInstant = timeProvider.GetUtcNow();
		var pricedLines = new List<PricedLine>(draft.Lines.Count);
		for (var index = 0; index < draft.Lines.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = draft.Lines[index];
			var productField = $"lines[{index}].productId";
			var offerField = $"lines[{index}].offerId";

			var product = await catalog.GetByIdAsync(line.ProductId, cancellationToken);
			if (product is null)
			{
				AddError(errors, productField, "Product was not found.");
				continue;
			}

			if (product.ListPrice is not { } listPrice || listPrice < 0m || listPrice > PocOrderFinancialCalculator.SqlMoneyMaximum)
			{
				AddError(errors, productField, "Product has no usable list price and cannot be ordered.");
				continue;
			}

			int specialOfferId;
			decimal discount;
			string? offerDescription;
			if (line.OfferId is null)
			{
				var noOfferSupported = await catalog.GetNoOfferPersistenceSupportAsync(line.ProductId, cancellationToken);
				if (noOfferSupported is not true)
				{
					AddError(errors, offerField, "This product cannot be ordered without an offer. Select one of the product's eligible offers.");
					continue;
				}

				specialOfferId = DemoOrderFinancials.NoOfferSpecialOfferId;
				discount = 0m;
				offerDescription = null;
			}
			else
			{
				// Same eligibility rule as GET /api/products/{id}/eligible-offers.
				var eligibility = await catalog.GetOfferEligibilityDataAsync(line.ProductId, cancellationToken);
				var selected = eligibility?.ProductId == line.ProductId
					? eligibility.Offers.FirstOrDefault(offer => offer.SpecialOfferId == line.OfferId)
					: null;
				if (selected is null || !OfferEligibilityEvaluator.IsEligible(
						selected.StartDate,
						selected.EndDate,
						evaluationInstant,
						line.Quantity,
						selected.MinQty,
						selected.MaxQty,
						isProductLinked: true))
				{
					AddError(errors, offerField, selected is null
						? "Selected offer is not linked to this product or is unavailable. Select one of the product's eligible offers."
						: "Selected offer is not eligible for this product and quantity. Select one of the product's eligible offers.");
					continue;
				}

				var pricing = await offerPricing.GetOfferPricingAsync(line.ProductId, line.OfferId.Value, cancellationToken);
				if (pricing is null || pricing.DiscountPct is < 0m or > 1m)
				{
					AddError(errors, offerField, "Selected offer pricing is unavailable. Select one of the product's eligible offers.");
					continue;
				}

				specialOfferId = pricing.SpecialOfferId;
				discount = pricing.DiscountPct;
				offerDescription = pricing.Description;
			}

			pricedLines.Add(new PricedLine(
				line.ProductId,
				product.Name,
				line.Quantity,
				specialOfferId,
				offerDescription,
				listPrice,
				discount,
				DemoOrderFinancials.LineTotal(listPrice, discount, line.Quantity)));
		}

		if (errors.Count > 0)
		{
			return PriceReview.Failed(errors);
		}

		var subtotal = DemoOrderFinancials.Subtotal(pricedLines.Select(line => line.LineTotal));
		if (subtotal > PocOrderFinancialCalculator.SqlMoneyMaximum / 2)
		{
			AddError(errors, "lines", "Order total exceeds the supported money range.");
			return PriceReview.Failed(errors);
		}

		var tax = DemoOrderFinancials.Tax(subtotal);
		return new PriceReview(new PricedOrder(pricedLines, subtotal, tax), new Dictionary<string, string[]>());
	}

	private static Dictionary<string, string[]> ValidateShape(
		OrderSubmissionRequest? request,
		out Guid token,
		out ValidatedOrderDraftShape? draft)
	{
		token = Guid.Empty;
		draft = null;
		if (request is null)
		{
			return new Dictionary<string, string[]> { ["request"] = ["A request is required."] };
		}

		var errors = new Dictionary<string, string[]>();
		if (string.IsNullOrWhiteSpace(request.SubmissionToken) ||
			!Guid.TryParse(request.SubmissionToken.Trim(), out token) ||
			token == Guid.Empty)
		{
			errors["submissionToken"] = ["A non-empty GUID submission token is required."];
		}

		var validation = OrderDraftRequestValidator.Validate(new OrderDraftRequest(
			request.CustomerId,
			request.BillToAddressId,
			request.ShipToAddressId,
			request.ShipMethodId,
			request.Lines));
		foreach (var entry in validation.Errors)
		{
			errors[entry.Key] = entry.Value;
		}

		if (errors.Count == 0)
		{
			draft = validation.Draft;
		}

		return errors;
	}

	private static DateTime TruncateToSeconds(DateTime value) =>
		new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

	private static Dictionary<string, string[]> NoErrors() => [];

	private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
	{
		if (!errors.TryGetValue(field, out var messages))
		{
			messages = [];
			errors.Add(field, messages);
		}

		messages.Add(message);
	}

	private sealed record PricedLine(
		int ProductId,
		string ProductName,
		int Quantity,
		int SpecialOfferId,
		string? OfferDescription,
		decimal UnitPrice,
		decimal UnitPriceDiscount,
		decimal LineTotal);

	private sealed record PricedOrder(IReadOnlyList<PricedLine> Lines, decimal Subtotal, decimal TaxAmt);

	private sealed record PriceReview(PricedOrder? Order, IReadOnlyDictionary<string, string[]> Errors)
	{
		public static PriceReview Failed(Dictionary<string, List<string>> errors) =>
			new(null, errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));
	}
}
