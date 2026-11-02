# In-Memory Order Draft UI Slice

**Status:** AW-040 completed product-and-quantity draft. A separate POC finance-display follow-on is tracked under AW-002; this document's AW-040 acceptance remains unchanged. Not a production order workflow.
**Audience:** Local developers using the opt-in loopback Development identity.

## Recommendation

AW-040 is a volatile, client-side draft containing catalog product identity and a positive integer quantity per line. Keep its completed acceptance product-only: do not associate the existing customer, address, or ship-method selections with the draft yet. Those selections remain separate UI state and must not make the product list look like a validated order header. A later, separate AW-002 POC display follow-on may show API-sourced list prices and aggregate demo tax, but must preserve the draft's not-ready/no-submit boundary and must not be described as AW-040 scope.

The user can add a catalog product, change its quantity, and remove a line. A product appears at most once in the draft; adding it again increases its quantity. AW-040 itself shows only product identity fields. Any separate POC finance-display follow-on is governed by AW-002 and the approved Finance Controller POC rules; it does not change AW-040's acceptance or authorize submission.

## Visible Behavior and States

- Keep a prominent, persistent status beside the draft: **Draft only. Not ready for order submission.** State that customer/address/shipping assignment, pricing and offer decisions, tax/freight, due date, and submission are not included.
- Do not render a submit, place-order, or equivalent action, including a disabled submit button.
- Reuse catalog loading, empty, validation, and service/network error states. An unavailable catalog must not be presented as an empty catalog. The draft itself starts empty.
- Reject zero, negative, fractional, or otherwise invalid quantities with a field-level message; preserve the last valid quantity.
- Provide a clear “Discard draft” action. When lines exist, ask for confirmation; cancel keeps every line unchanged, confirm clears all draft lines. With no lines, the action is disabled or absent.
- Draft state exists only in the current page session. Reloading or leaving the page does not restore it; do not persist it to browser storage or the API.
- Keep the existing Development identity/local-only notice visible. This slice is not production authentication or authorization.

## API and Data Boundary

Use the existing bounded product search and product-detail read contracts. No new endpoint or database interaction is required. The draft is transient client state and sends no order data to the API. The browser continues to use the API for catalog reads and never connects to SQL Server.

AW-034 remains pending. Its approved one-offer/no-stacking/recheck boundary does not settle operational eligibility or a valid no-offer representation. Keep offers out of the draft rather than presenting a linked offer as selectable or treating an empty offer choice as a valid no-offer order line. AW-033's tax/freight authority and calculation work, due-date policy implementation, production identity, and all write gates remain separate and unresolved as applicable.

## Separate POC Financial Display Follow-On (AW-002)

AW-040's completed acceptance remains product identity and quantity only. A separate AW-002 follow-on may use the product-detail API's nullable `ListPrice` in the transient draft without changing AW-040's completion claim. Product search stays price-free; adding a draft line goes through product detail. Display amounts as **POC/demo USD**, without conversion or offer discounts. The Finance Controller's POC rule is 7.5% tax on aggregate merchandise subtotal, excluding freight/offers, rounded once to two decimals midpoint-away-from-zero.

- A verified `ListPrice` of zero displays as `$0.00`; when all prices and quantities are valid and aggregate subtotal is zero, demo tax is `$0.00`.
- A null/unavailable line price displays “Price unavailable”; do not coalesce it to zero and suppress dependent subtotal/tax.
- For an empty draft, do not display a calculated zero subtotal/tax.
- While quantity text is invalid, suppress the affected line amount and aggregate subtotal/tax until all quantities are valid.
- Do not display a grand total, quote, payable amount, freight, offer discount, or submit action. Preserve “Draft only. Not ready for order submission.”
- This demo display does not approve production prices/tax or enable production authentication or writes.

## Acceptance Criteria

1. A local authenticated-Development user can add catalog products and maintain one positive-integer quantity per product.
2. The draft displays product identity and quantity only; no price, offer, tax, freight, due date, total, availability, or submit behavior is exposed.
3. The persistent not-ready message makes clear that this is not a submit-ready order.
4. Confirmed discard clears the draft; canceled discard preserves it. Reload does not restore draft lines.
5. Catalog failures, empty results, and quantity validation are distinguishable and actionable.
6. No customer, address, or ship-method value is attached to draft lines or used to imply validation.

## Assumptions and Handoff

- The requested user is the existing opt-in local Development identity, not a production staff identity.
- Existing catalog search/detail and local auth remain available as recorded by AW-024, AW-029, and AW-037; this recommendation does not independently re-verify the current running app.
- AW-040 is recommended as P0 for the next local UI slice because product/quantity composition is a prerequisite to the broader order workflow. This priority does not mean the slice is production-ready or that submission policy gates are satisfied.
- After this slice, decide whether customer/header association belongs in a later review workflow, once customer/address/ship-method selections can be presented without implying authorization, eligibility revalidation, or submit readiness. Do not expand AW-040 to include it.

**Architect handoff:** Implement the transient product-and-quantity interaction against existing read contracts only. Keep the not-ready state and discard semantics in acceptance tests. AW-034, AW-033, AW-036, AW-014, and the eventual AW-017 write gates are not prerequisites for this read-only draft, and this slice does not satisfy or bypass them.