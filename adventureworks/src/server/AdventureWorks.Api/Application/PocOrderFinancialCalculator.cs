namespace AdventureWorks.Api.Application;

public sealed record PocOrderFinancialInputLine(int ProductId, int Quantity);

public sealed record PocOrderFinancialLine(
	int ProductId,
	int Quantity,
	decimal UnitPrice,
	decimal LineExtension);

public sealed record PocOrderFinancialCalculation(
	string ScopeLabel,
	string CurrencyLabel,
	IReadOnlyList<PocOrderFinancialLine> Lines,
	decimal MerchandiseSubtotal,
	decimal DemonstrationTax,
	decimal Freight);

public sealed record PocOrderFinancialCalculationResult(
	PocOrderFinancialCalculation? Calculation,
	IReadOnlyDictionary<string, string[]> Errors)
{
	public bool IsValid => Calculation is not null;
}

public sealed class PocOrderFinancialCalculator
{
	public const decimal SqlMoneyMaximum = 922337203685477.5807m;
	public const string ScopeLabel = "POC/demo only; not a production payable amount";
	public const string CurrencyLabel = "USD (demo convention)";
	public const decimal DemonstrationTaxRate = 0.075m;
	public const decimal ExplicitFreight = 0.00m;

	public PocOrderFinancialCalculationResult Calculate(
		IReadOnlyList<PocOrderFinancialInputLine>? inputLines,
		IReadOnlyDictionary<int, decimal?>? serverReadListPrices)
	{
		var errors = new Dictionary<string, List<string>>();
		if (inputLines is null || inputLines.Count == 0)
		{
			AddError(errors, "lines", "At least one line is required.");
			return Invalid(errors);
		}

		if (inputLines.Count > OrderDraftRequestValidator.MaximumLineCount)
		{
			AddError(errors, "lines", $"No more than {OrderDraftRequestValidator.MaximumLineCount} lines are allowed.");
			return Invalid(errors);
		}

		var seenProductIds = new HashSet<int>();
		var lines = new List<PocOrderFinancialLine>(inputLines.Count);
		var subtotal = 0m;
		for (var index = 0; index < inputLines.Count; index++)
		{
			var line = inputLines[index];
			var field = $"lines[{index}]";
			if (line.ProductId <= 0)
			{
				AddError(errors, $"{field}.productId", "Product ID must be positive.");
				continue;
			}

			if (!seenProductIds.Add(line.ProductId))
			{
				AddError(errors, $"{field}.productId", "A product may appear only once in the request.");
				continue;
			}

			if (line.Quantity is < 1 or > short.MaxValue)
			{
				AddError(errors, $"{field}.quantity", "Quantity must be between 1 and 32767.");
				continue;
			}

			if (serverReadListPrices is null
				|| !serverReadListPrices.TryGetValue(line.ProductId, out var listPrice)
				|| listPrice is null)
			{
				AddError(errors, $"{field}.listPrice", "A server-read product list price is required.");
				continue;
			}

			if (listPrice < 0m)
			{
				AddError(errors, $"{field}.listPrice", "Product list price cannot be negative.");
				continue;
			}
			if (listPrice > SqlMoneyMaximum || listPrice.Value != decimal.Round(listPrice.Value, 4, MidpointRounding.ToEven))
			{
				AddError(errors, $"{field}.listPrice", "Product list price is outside the verified SQL money(19,4) representation.");
				continue;
			}

			try
			{
				var lineExtension = listPrice.Value * line.Quantity;
				if (lineExtension > SqlMoneyMaximum)
				{
					AddError(errors, $"{field}.quantity", "Line extension exceeds the verified SQL money(19,4) subtotal range.");
					continue;
				}

				if (subtotal > SqlMoneyMaximum - lineExtension)
				{
					AddError(errors, "lines", "Aggregate merchandise subtotal exceeds the verified SQL money(19,4) range.");
					continue;
				}

				subtotal += lineExtension;
				lines.Add(new PocOrderFinancialLine(line.ProductId, line.Quantity, listPrice.Value, lineExtension));
			}
			catch (OverflowException)
			{
				AddError(errors, $"{field}.listPrice", "Price and quantity exceed the supported calculation range.");
			}
		}

		if (errors.Count > 0)
		{
			return Invalid(errors);
		}

		var tax = decimal.Round(subtotal * DemonstrationTaxRate, 2, MidpointRounding.AwayFromZero);
		var calculation = new PocOrderFinancialCalculation(
			ScopeLabel,
			CurrencyLabel,
			lines,
			subtotal,
			tax,
			ExplicitFreight);
		return new PocOrderFinancialCalculationResult(calculation, new Dictionary<string, string[]>());
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

	private static PocOrderFinancialCalculationResult Invalid(Dictionary<string, List<string>> errors) =>
		new(null, errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));
}