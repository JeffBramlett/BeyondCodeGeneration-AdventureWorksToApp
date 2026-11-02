import { isPositiveSmallintQuantity } from './draftQuantity.js'
import { calculateOrderPreview, formatAddressSummary, previewInputLines, selectedDiscountPct } from './orderSubmission.js'
import { formatPocPercent, formatPocUsd } from './pocMoney.js'

function offerOptionLabel(offer) {
  return typeof offer.discountPct === 'number' ? `${offer.description} · ${formatPocPercent(offer.discountPct)} off` : offer.description
}

function LineOfferPicker({ line, lineOffer, onOfferChange, onRetryOffers }) {
  const selectId = `draft-offer-${line.productId}`
  const statusId = `${selectId}-status`
  const noticeId = `${selectId}-notice`
  const offers = Array.isArray(lineOffer?.offers) ? lineOffer.offers : []
  const offerId = lineOffer?.offerId ?? null
  const status = lineOffer?.status ?? 'loading'
  const selectedMissing = offerId !== null && !offers.some((offer) => offer.specialOfferId === offerId)

  return (
    <div className="draft-offer">
      <label htmlFor={selectId}>Offer</label>
      <select
        id={selectId}
        value={offerId === null ? '' : String(offerId)}
        onChange={(event) => {
          const nextId = event.target.value === '' ? null : Number(event.target.value)
          const offer = offers.find((candidate) => candidate.specialOfferId === nextId) ?? null
          onOfferChange(line.productId, nextId, offer?.description ?? null)
        }}
        disabled={status === 'loading'}
        aria-describedby={[statusId, lineOffer?.notice ? noticeId : null].filter(Boolean).join(' ')}
      >
        <option value="">No discount</option>
        {offers.map((offer) => <option key={offer.specialOfferId} value={String(offer.specialOfferId)}>{offerOptionLabel(offer)}</option>)}
        {selectedMissing && <option value={String(offerId)}>{lineOffer.offerDescription || `Offer ${offerId}`}</option>}
      </select>
      <span className="draft-offer-status" id={statusId}>
        {status === 'loading' && 'Checking eligible offers for this quantity…'}
        {status === 'success' && (offers.length === 0 ? 'No eligible offers at this quantity.' : `${offers.length} eligible ${offers.length === 1 ? 'offer' : 'offers'} at quantity ${lineOffer.quantity}.`)}
        {status === 'error' && `${lineOffer.error?.title || 'Offers unavailable'}. ${lineOffer.error?.message || ''}`}
      </span>
      {status === 'error' && <button className="draft-offer-retry" type="button" onClick={() => onRetryOffers(line.productId)}>Retry offers</button>}
      {lineOffer?.notice && <span className="field-error" id={noticeId} role="alert">{lineOffer.notice}</span>}
    </div>
  )
}

function OrderDraft({
  lines,
  quantityInputs,
  lineOffers,
  selection,
  readiness,
  recordedReference,
  onQuantityChange,
  onOfferChange,
  onRetryOffers,
  onRemove,
  onDiscard,
  onChooseCustomer,
  onReview,
  statusMessage,
}) {
  const previewLines = lines.map((line) => {
    const input = quantityInputs[line.productId] ?? String(line.quantity)
    return { ...line, quantity: isPositiveSmallintQuantity(input) ? Number(input) : Number.NaN }
  })
  const preview = calculateOrderPreview(previewInputLines(previewLines, lineOffers))
  const previewByProduct = Object.fromEntries(preview.lines.map((line) => [line.productId, line]))
  const hasInvalidQuantity = lines.some((line) => !isPositiveSmallintQuantity(quantityInputs[line.productId] ?? String(line.quantity)))

  return (
    <section className="draft-panel" aria-labelledby="draft-title">
      <div className="draft-heading">
        <div><p className="eyebrow">ORDER DRAFT</p><h2 id="draft-title">Order draft</h2></div>
        <span className="draft-count" aria-label={`${lines.length} products`}>{lines.length}</span>
      </div>
      <div className="draft-boundary" role="note">
        <strong>Local demo: orders are recorded in the AdventureWorks test copy.</strong>
        <p>Demo USD preview: unit price is the product list price less the chosen offer&apos;s discount; demo tax is 7.5% of the discounted merchandise subtotal; freight is a flat $25.00. The confirmation shows the amounts the server recorded. Stock availability, shipping, and fulfillment are not part of this demo.</p>
      </div>
      {recordedReference && (
        <p className="draft-recorded-note" role="status">This draft was already recorded as order {recordedReference}. Submitting it again returns the same order. Use Start new order on the confirmation, or Discard draft, to begin another order.</p>
      )}
      {lines.length === 0 ? (
        <p className="draft-empty">No products in this draft. Open a product in the catalog and add it.</p>
      ) : (
        <>
          <ul className="draft-list">
            {lines.map((line) => {
              const inputValue = quantityInputs[line.productId] ?? String(line.quantity)
              const isValid = isPositiveSmallintQuantity(inputValue)
              const quantityId = `draft-quantity-${line.productId}`
              const errorId = `${quantityId}-error`
              const discountPct = selectedDiscountPct(lineOffers[line.productId])
              const lineTotal = previewByProduct[line.productId]?.lineTotal ?? null

              return (
                <li className="draft-line draft-order-line" key={line.productId}>
                  <div className="draft-product">
                    <strong>{line.name}</strong>
                    <span>Product {line.productId} · {line.productNumber}</span>
                    {(line.categoryName || line.subcategoryName) && <span>{[line.categoryName, line.subcategoryName].filter(Boolean).join(' / ')}</span>}
                    {line.listPrice === null || !Number.isFinite(line.listPrice)
                      ? <span className="draft-price-unavailable">Price unavailable</span>
                      : (
                        <span>
                          Unit price {formatPocUsd(line.listPrice)}
                          {' · '}Discount {discountPct === null ? 'confirmed on submission' : formatPocPercent(discountPct)}
                          {' · '}{!isValid ? 'Line total unavailable until quantity is valid' : lineTotal === null ? 'Line total calculated on submission' : `Line total ${formatPocUsd(lineTotal)} (preview)`}
                        </span>
                      )}
                  </div>
                  <div className="draft-quantity">
                    <label htmlFor={quantityId}>Quantity</label>
                    <input
                      id={quantityId}
                      type="text"
                      inputMode="numeric"
                      value={inputValue}
                      aria-invalid={!isValid}
                      aria-describedby={!isValid ? errorId : undefined}
                      onChange={(event) => onQuantityChange(line.productId, event.target.value)}
                    />
                    {!isValid && <span className="field-error" id={errorId} role="alert">Enter a whole number from 1 to 32767.</span>}
                  </div>
                  <LineOfferPicker line={line} lineOffer={lineOffers[line.productId]} onOfferChange={onOfferChange} onRetryOffers={onRetryOffers} />
                  <button className="draft-remove" type="button" onClick={() => onRemove(line.productId)} aria-label={`Remove ${line.name} from draft`}>Remove</button>
                </li>
              )
            })}
          </ul>
          {preview.available ? (
            <dl className="draft-financials" aria-label="Demo amount preview">
              <div><dt>Merchandise subtotal · preview USD</dt><dd>{formatPocUsd(preview.subTotal)}</dd></div>
              <div><dt>Demo tax · 7.5%</dt><dd>{formatPocUsd(preview.taxAmt)}</dd></div>
              <div><dt>Demo freight · flat</dt><dd>{formatPocUsd(preview.freight)}</dd></div>
              <div className="draft-total"><dt>Total · preview</dt><dd>{formatPocUsd(preview.totalDue)}</dd></div>
            </dl>
          ) : (
            <p className="draft-price-unavailable" role="status">
              {hasInvalidQuantity
                ? 'Fix invalid quantities to preview amounts.'
                : preview.reason === 'price'
                  ? 'List price unavailable for one or more products; amounts will be calculated by the server on submission.'
                  : preview.reason === 'discount'
                    ? 'The discount for a chosen offer was not provided by the API; amounts will be calculated by the server on submission.'
                    : 'Add a product to preview amounts.'}
            </p>
          )}
        </>
      )}

      <div className="order-selection-summary" aria-labelledby="order-selection-title">
        <h3 id="order-selection-title">Customer and shipping</h3>
        {selection.customer ? (
          <dl>
            <div><dt>Customer</dt><dd>{selection.customer.displayName} · #{selection.customer.customerId}</dd></div>
            <div><dt>Bill-to</dt><dd>{selection.billToAddress ? formatAddressSummary(selection.billToAddress) : 'Not chosen'}</dd></div>
            <div><dt>Ship-to</dt><dd>{selection.shipToAddress ? formatAddressSummary(selection.shipToAddress) : 'Not chosen'}</dd></div>
            <div><dt>Ship method</dt><dd>{selection.shipMethod ? selection.shipMethod.name : 'Not chosen'}</dd></div>
          </dl>
        ) : <p className="draft-empty">No customer chosen.</p>}
        <button className="back-button" type="button" onClick={onChooseCustomer}>{selection.customer ? 'Change customer or shipping' : 'Choose customer and shipping'}</button>
      </div>

      <div className="draft-actions">
        <div className="draft-readiness">
          {!readiness.ready && (
            <>
              <p className="draft-readiness-title" id="review-blockers-title">Before review:</p>
              <ul id="review-blockers" aria-labelledby="review-blockers-title">
                {readiness.missing.map((item) => <li key={item.key}>{item.message}</li>)}
              </ul>
            </>
          )}
          <p className="draft-live-message" role="status" aria-live="polite">{statusMessage}</p>
        </div>
        <div className="draft-action-buttons">
          {lines.length > 0 && <button className="draft-discard" type="button" onClick={onDiscard}>Discard draft</button>}
          <button className="search-button review-button" type="button" onClick={onReview} disabled={!readiness.ready} aria-describedby={!readiness.ready ? 'review-blockers' : undefined}>Review order</button>
        </div>
      </div>
    </section>
  )
}

export default OrderDraft
