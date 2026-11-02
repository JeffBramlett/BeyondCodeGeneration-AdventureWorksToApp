using AdventureWorks.Api.Application;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class PocOrderFinancialCalculatorTests
{
	[Fact]
	public void Calculate_UsesServerListPricesAndAggregatesLines()
	{
		var lines = Lines((7, 3), (8, 2));

		var result = new PocOrderFinancialCalculator().Calculate(lines,
			new Dictionary<int, decimal?> { [7] = 12.3456m, [8] = 4.25m });

		var calculation = AssertValid(result);
		Assert.Equal(12.3456m, calculation.Lines[0].UnitPrice);
		Assert.Equal(37.0368m, calculation.Lines[0].LineExtension);
		Assert.Equal(8.50m, calculation.Lines[1].LineExtension);
		Assert.Equal(45.5368m, calculation.MerchandiseSubtotal);
		Assert.Equal(3.42m, calculation.DemonstrationTax);
		Assert.Equal(0.00m, calculation.Freight);
		Assert.Contains("POC/demo only", calculation.ScopeLabel, StringComparison.Ordinal);
		Assert.Contains("USD", calculation.CurrencyLabel, StringComparison.Ordinal);
		Assert.DoesNotContain("Total", typeof(PocOrderFinancialCalculation).GetProperties().Select(property => property.Name));
	}

	[Theory]
	[InlineData(0.1999, 0.01)]
	[InlineData(0.20, 0.02)]
	public void Calculate_RoundsAggregateTaxOnceAwayFromZero(decimal subtotal, decimal expectedTax)
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 1)),
			new Dictionary<int, decimal?> { [7] = subtotal });

		Assert.Equal(expectedTax, AssertValid(result).DemonstrationTax);
	}

	[Fact]
	public void Calculate_PreservesKnownZeroPriceAndZeroAggregates()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 5)),
			new Dictionary<int, decimal?> { [7] = 0m });

		var calculation = AssertValid(result);
		Assert.Equal(0m, calculation.Lines[0].UnitPrice);
		Assert.Equal(0m, calculation.Lines[0].LineExtension);
		Assert.Equal(0m, calculation.MerchandiseSubtotal);
		Assert.Equal(0m, calculation.DemonstrationTax);
		Assert.Equal(0.00m, calculation.Freight);
	}

	[Fact]
	public void Calculate_MissingPriceBlocksAllFinancialOutputsAtItsLine()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 1), (8, 2)),
			new Dictionary<int, decimal?> { [7] = 10m });

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines[1].listPrice", result.Errors.Keys);
	}

	[Fact]
	public void Calculate_NullPriceBlocksAllFinancialOutputsAtItsLine()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 1)),
			new Dictionary<int, decimal?> { [7] = null });

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines[0].listPrice", result.Errors.Keys);
	}

	[Fact]
	public void Calculate_InputContractDoesNotAcceptOfferTerms()
	{
		Assert.Equal(
			new[] { "ProductId", "Quantity" },
			typeof(PocOrderFinancialInputLine).GetProperties().Select(property => property.Name).Order().ToArray());
	}

	[Theory]
	[InlineData(0, 1, "lines[0].productId")]
	[InlineData(7, 0, "lines[0].quantity")]
	[InlineData(7, 32768, "lines[0].quantity")]
	public void Calculate_RejectsInvalidProductAndQuantityBounds(int productId, int quantity, string expectedField)
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((productId, quantity)),
			new Dictionary<int, decimal?> { [productId] = 1m });

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains(expectedField, result.Errors.Keys);
	}

	[Fact]
	public void Calculate_RejectsMoreThanMaximumLineCount()
	{
		var lines = Enumerable.Range(1, OrderDraftRequestValidator.MaximumLineCount + 1)
			.Select(productId => new PocOrderFinancialInputLine(productId, 1))
			.ToArray();

		var result = new PocOrderFinancialCalculator().Calculate(
			lines,
			lines.ToDictionary(line => line.ProductId, _ => (decimal?)1m));

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines", result.Errors.Keys);
	}

	[Fact]
	public void Calculate_RejectsLineExtensionOutsideSqlMoneyRange()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 2)),
			new Dictionary<int, decimal?> { [7] = PocOrderFinancialCalculator.SqlMoneyMaximum });

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines[0].quantity", result.Errors.Keys);
	}

	[Fact]
	public void Calculate_RejectsAggregateSubtotalOutsideSqlMoneyRange()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 1), (8, 1)),
			new Dictionary<int, decimal?>
			{
				[7] = 461168601842738.7904m,
				[8] = 461168601842738.7904m
			});

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines", result.Errors.Keys);
	}

	[Fact]
	public void Calculate_RejectsPriceWithPrecisionBeyondSqlMoneyScale()
	{
		var result = new PocOrderFinancialCalculator().Calculate(
			Lines((7, 1)),
			new Dictionary<int, decimal?> { [7] = 1.00001m });

		Assert.False(result.IsValid);
		Assert.Null(result.Calculation);
		Assert.Contains("lines[0].listPrice", result.Errors.Keys);
	}

	private static PocOrderFinancialInputLine[] Lines(params (int ProductId, int Quantity)[] lines) =>
		lines.Select(line => new PocOrderFinancialInputLine(line.ProductId, line.Quantity)).ToArray();

	private static PocOrderFinancialCalculation AssertValid(PocOrderFinancialCalculationResult result)
	{
		Assert.True(result.IsValid);
		Assert.Empty(result.Errors);
		return Assert.IsType<PocOrderFinancialCalculation>(result.Calculation);
	}
}