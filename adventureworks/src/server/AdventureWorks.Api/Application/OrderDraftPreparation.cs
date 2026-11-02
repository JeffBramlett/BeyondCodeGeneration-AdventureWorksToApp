using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Application;

public sealed record OrderDraftValidationResult(
	ValidatedOrderDraftShape? Draft,
	IReadOnlyDictionary<string, string[]> Errors)
{
	public bool IsValid => Draft is not null;
}

public static class OrderDraftRequestValidator
{
	public const int MaximumLineCount = 100;

	public static OrderDraftValidationResult Validate(OrderDraftRequest? request)
	{
		var errors = new Dictionary<string, List<string>>();
		if (request is null)
		{
			AddError(errors, "request", "A request is required.");
			return Invalid(errors);
		}

		RequirePositive(errors, "customerId", request.CustomerId);
		RequirePositive(errors, "billToAddressId", request.BillToAddressId);
		RequirePositive(errors, "shipToAddressId", request.ShipToAddressId);
		RequirePositive(errors, "shipMethodId", request.ShipMethodId);

		if (request.Lines is null || request.Lines.Count == 0)
		{
			AddError(errors, "lines", "At least one line is required.");
		}
		else if (request.Lines.Count > MaximumLineCount)
		{
			AddError(errors, "lines", $"No more than {MaximumLineCount} lines are allowed.");
		}
		else
		{
			var seenProductIds = new HashSet<int>();
			for (var index = 0; index < request.Lines.Count; index++)
			{
				var line = request.Lines[index];
				if (line is null)
				{
					AddError(errors, $"lines[{index}]", "A line is required.");
					continue;
				}

				RequirePositive(errors, $"lines[{index}].productId", line.ProductId);
				if (line.ProductId > 0 && !seenProductIds.Add(line.ProductId))
				{
					AddError(errors, $"lines[{index}].productId", "A product may appear only once in the request.");
				}

				if (line.Quantity is < 1 or > short.MaxValue)
				{
					AddError(errors, $"lines[{index}].quantity", "Quantity must be between 1 and 32767.");
				}

				if (line.OfferId is <= 0)
				{
					AddError(errors, $"lines[{index}].offerId", "Offer ID must be positive when supplied.");
				}
			}
		}

		if (errors.Count > 0)
		{
			return Invalid(errors);
		}

		var draft = new ValidatedOrderDraftShape(
			request.CustomerId,
			request.BillToAddressId,
			request.ShipToAddressId,
			request.ShipMethodId,
			request.Lines!
				.Select(line => new ValidatedOrderDraftLineShape(line!.ProductId, line.Quantity, line.OfferId))
				.ToArray());
		return new OrderDraftValidationResult(draft, new Dictionary<string, string[]>());
	}

	private static void RequirePositive(Dictionary<string, List<string>> errors, string field, int value)
	{
		if (value <= 0)
		{
			AddError(errors, field, "Value must be positive.");
		}
	}

	private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
	{
		if (!errors.TryGetValue(field, out var messages))
		{
			messages = [];
			errors.Add(field, messages);
		}

		messages.Add(message);
	}

	private static OrderDraftValidationResult Invalid(Dictionary<string, List<string>> errors) =>
		new(null, errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));
}

public sealed class OrderDraftPreparation
{
	public OrderDraftValidationResult Prepare(OrderDraftRequest? request) =>
		OrderDraftRequestValidator.Validate(request);
}