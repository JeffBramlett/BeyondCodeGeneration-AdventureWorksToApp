using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;

namespace AdventureWorks.Api.Application;

public enum OfferReviewStatus
{
	InvalidDraft,
	ReviewRequired,
	Current
}

public enum OfferLineReviewStatus
{
	SelectedOfferCurrent,
	NoOfferMarkerSupported,
	ReviewRequired
}

public sealed record OfferLineReview(
	int LineIndex,
	int ProductId,
	int? OfferId,
	OfferLineReviewStatus Status,
	string? ErrorField,
	string? ErrorMessage);

public sealed record OfferReviewResult(
	OfferReviewStatus Status,
	ValidatedOrderDraftShape? Draft,
	DateTimeOffset? EvaluationInstant,
	IReadOnlyList<OfferLineReview> Lines,
	IReadOnlyDictionary<string, string[]> Errors);

public sealed class OfferReviewService(IProductCatalogReadStore catalog, TimeProvider timeProvider)
{
	public async Task<OfferReviewResult> ReviewAsync(
		OrderDraftRequest? request,
		CancellationToken cancellationToken)
	{
		var validation = OrderDraftRequestValidator.Validate(request);
		if (!validation.IsValid)
		{
			return new OfferReviewResult(
				OfferReviewStatus.InvalidDraft,
				null,
				null,
				[],
				validation.Errors);
		}

		var draft = validation.Draft!;
		var evaluationInstant = timeProvider.GetUtcNow();
		var lineReviews = new List<OfferLineReview>(draft.Lines.Count);
		var errors = new Dictionary<string, string[]>();

		for (var index = 0; index < draft.Lines.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = draft.Lines[index];
			if (line.OfferId is null)
			{
				var markerSupported = await catalog.GetNoOfferPersistenceSupportAsync(line.ProductId, cancellationToken);
				if (markerSupported is true)
				{
					lineReviews.Add(new OfferLineReview(
						index,
						line.ProductId,
						null,
						OfferLineReviewStatus.NoOfferMarkerSupported,
						null,
						null));
					continue;
				}

				var field = markerSupported is null ? $"lines[{index}].productId" : $"lines[{index}].offerId";
				var message = markerSupported is null
					? "Product could not be found for offer review."
					: "No-offer persistence is not supported for this product.";
				AddReviewError(errors, field, message);
				lineReviews.Add(new OfferLineReview(
					index,
					line.ProductId,
					null,
					OfferLineReviewStatus.ReviewRequired,
					field,
					message));
				continue;
			}

			var eligibilityData = await catalog.GetOfferEligibilityDataAsync(line.ProductId, cancellationToken);
			if (eligibilityData is null || eligibilityData.ProductId != line.ProductId)
			{
				const string message = "Product could not be found for offer review.";
				var field = $"lines[{index}].productId";
				AddReviewError(errors, field, message);
				lineReviews.Add(new OfferLineReview(
					index,
					line.ProductId,
					line.OfferId,
					OfferLineReviewStatus.ReviewRequired,
					field,
					message));
				continue;
			}

			var selectedOffer = eligibilityData.Offers.FirstOrDefault(offer => offer.SpecialOfferId == line.OfferId);
			if (selectedOffer is not null && OfferEligibilityEvaluator.IsEligible(
					selectedOffer.StartDate,
					selectedOffer.EndDate,
					evaluationInstant,
					line.Quantity,
					selectedOffer.MinQty,
					selectedOffer.MaxQty,
					isProductLinked: true))
			{
				lineReviews.Add(new OfferLineReview(
					index,
					line.ProductId,
					line.OfferId,
					OfferLineReviewStatus.SelectedOfferCurrent,
					null,
					null));
				continue;
			}

			var offerField = $"lines[{index}].offerId";
			var offerMessage = selectedOffer is null
				? "Selected offer is not linked to this product or unavailable."
				: "Selected offer is not eligible for this product and quantity at review time.";
			AddReviewError(errors, offerField, offerMessage);
			lineReviews.Add(new OfferLineReview(
				index,
				line.ProductId,
				line.OfferId,
				OfferLineReviewStatus.ReviewRequired,
				offerField,
				offerMessage));
		}

		return new OfferReviewResult(
			errors.Count == 0 ? OfferReviewStatus.Current : OfferReviewStatus.ReviewRequired,
			draft,
			evaluationInstant,
			lineReviews,
			errors);
	}

	private static void AddReviewError(Dictionary<string, string[]> errors, string field, string message)
	{
		if (errors.TryGetValue(field, out var existing))
		{
			errors[field] = [.. existing, message];
			return;
		}

		errors.Add(field, [message]);
	}
}