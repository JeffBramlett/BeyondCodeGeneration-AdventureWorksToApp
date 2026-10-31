using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;

namespace AdventureWorks.Api.Application;

public enum OrderDraftPreflightStatus
{
	InvalidDraft,
	CustomerOptionsUnavailable,
	SelectionRequiresReview,
	OffersRequireReview,
	ReadOnlyPreflightReviewed
}

public enum OrderDraftPriceReviewStatus
{
	NotPerformed
}

public sealed record OrderDraftPreflightResult(
	OrderDraftPreflightStatus Status,
	ValidatedOrderDraftShape? Draft,
	OfferReviewResult? OfferReview,
	IReadOnlyDictionary<string, string[]> Errors)
{
	public OrderDraftPriceReviewStatus PriceReviewStatus => OrderDraftPriceReviewStatus.NotPerformed;
	public string PriceReviewGap => "No reviewed price snapshot is available; changed-price review was not performed.";
	public bool IsAtomic => false;
	public bool IsSubmissionAuthorized => false;
}

public sealed class OrderDraftPreflightService(
	ICustomerReadStore customers,
	OfferReviewService offerReview)
{
	public async Task<OrderDraftPreflightResult> ReviewAsync(
		OrderDraftRequest? request,
		CancellationToken cancellationToken)
	{
		var validation = OrderDraftRequestValidator.Validate(request);
		if (!validation.IsValid)
		{
			return new OrderDraftPreflightResult(
				OrderDraftPreflightStatus.InvalidDraft,
				null,
				null,
				validation.Errors);
		}

		cancellationToken.ThrowIfCancellationRequested();
		var draft = validation.Draft!;
		var options = await customers.GetOrderOptionsAsync(draft.CustomerId, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		if (options is null)
		{
			return Failed(
				OrderDraftPreflightStatus.CustomerOptionsUnavailable,
				draft,
				"customerId",
				"Customer was not found or order options are unavailable.");
		}

		var errors = new Dictionary<string, string[]>();
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

		if (errors.Count > 0)
		{
			return new OrderDraftPreflightResult(
				OrderDraftPreflightStatus.SelectionRequiresReview,
				draft,
				null,
				errors);
		}

		cancellationToken.ThrowIfCancellationRequested();
		var offerResult = await offerReview.ReviewAsync(request, cancellationToken);
		if (offerResult.Status != OfferReviewStatus.Current)
		{
			return new OrderDraftPreflightResult(
				OrderDraftPreflightStatus.OffersRequireReview,
				draft,
				offerResult,
				offerResult.Errors);
		}

		return new OrderDraftPreflightResult(
			OrderDraftPreflightStatus.ReadOnlyPreflightReviewed,
			draft,
			offerResult,
			new Dictionary<string, string[]>());
	}

	private static OrderDraftPreflightResult Failed(
		OrderDraftPreflightStatus status,
		ValidatedOrderDraftShape draft,
		string field,
		string message) =>
		new(status, draft, null, new Dictionary<string, string[]> { [field] = [message] });

	private static void AddError(Dictionary<string, string[]> errors, string field, string message) =>
		errors.Add(field, [message]);
}