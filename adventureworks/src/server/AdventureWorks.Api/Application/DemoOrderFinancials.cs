namespace AdventureWorks.Api.Application;

/// <summary>
/// Local-demo order financial policy approved by the Project Owner (2026-10-30).
/// Single source for demo freight, tax rate, and line/subtotal/tax arithmetic used by POST /api/orders.
/// Not a production payable-amount policy.
/// </summary>
public static class DemoOrderFinancials
{
	/// <summary>Flat demo freight per order.</summary>
	public const decimal FreightPerOrder = 25.00m;

	/// <summary>Demo tax rate applied to the discounted merchandise subtotal.</summary>
	public const decimal TaxRate = PocOrderFinancialCalculator.DemonstrationTaxRate;

	/// <summary>SpecialOfferID persisted when the user selects no offer ("No Discount").</summary>
	public const int NoOfferSpecialOfferId = 1;

	/// <summary>Matches SQL computed Sales.SalesOrderDetail.LineTotal: UnitPrice * (1 - UnitPriceDiscount) * OrderQty.</summary>
	public static decimal LineTotal(decimal unitPrice, decimal unitPriceDiscount, int quantity) =>
		unitPrice * (1m - unitPriceDiscount) * quantity;

	/// <summary>Merchandise subtotal rounded to SQL money scale (4 dp).</summary>
	public static decimal Subtotal(IEnumerable<decimal> lineTotals) =>
		decimal.Round(lineTotals.Sum(), 4, MidpointRounding.AwayFromZero);

	public static decimal Tax(decimal subtotal) =>
		decimal.Round(subtotal * TaxRate, 2, MidpointRounding.AwayFromZero);

	public static decimal TotalDue(decimal subtotal, decimal tax, decimal freight) => subtotal + tax + freight;
}
