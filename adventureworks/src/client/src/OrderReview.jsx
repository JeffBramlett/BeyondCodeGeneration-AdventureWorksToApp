import { calculateOrderPreview, formatAddressSummary, previewInputLines, selectedDiscountPct, selectedOffer } from './orderSubmission.js'
import { formatPocPercent, formatPocUsd } from './pocMoney.js'

function FieldErrors({ id, messages }) {
  if (!messages || messages.length === 0) return null
  return (
    <ul className="review-field-errors" id={id}>
      {messages.map((message, index) => <li className="field-error" key={index}>{message}</li>)}
    </ul>
  )
}

function OrderReview({ lines, lineOffers, selection, submission, onBack, onSubmit }) {
  const preview = calculateOrderPreview(previewInputLines(lines, lineOffers))
  const previewByProduct = Object.fromEntries(preview.lines.map((line) => [line.productId, line]))
  const submitting = submission.status === 'submitting'
  const error = submission.status === 'error' ? submission.error : null
  const validation = error?.kind === 'validation' ? error.validation : null
  const fieldErrors = validation?.fields ?? {}
  const lineErrors = validation?.lines ?? {}

  return (
    <div className="order-review">
      <section className="page-heading">
        <div>
          <p className="eyebrow">LOCAL DEMO <span>·</span> ORDER REVIEW</p>
          <h1>Review order</h1>
          <p className="heading-copy">Check the customer, shipping, lines, and preview amounts, then submit to record the order in the AdventureWorks test copy.</p>
        </div>
      </section>

      {error && (
        <div className={`state-message error-message review-error ${error.kind}`} role="alert">
          <strong>{error.title}</strong>
          <span>{error.message}</span>
          {validation && validation.summary.length > 0 && (
            <ul className="review-error-summary">
              {validation.summary.map((item, index) => <li key={index}><b>{item.label}:</b> {item.message}</li>)}
            </ul>
          )}
          {error.retryable && (
            <button className="retry-button" type="button" onClick={onSubmit} disabled={submitting}>Retry submission</button>
          )}
          {error.retryable && <span className="review-retry-note">Retry reuses this review&apos;s submission, so the order is recorded at most once.</span>}
        </div>
      )}

      <section className="detail-section review-section" aria-labelledby="review-customer-title">
        <h2 id="review-customer-title">Customer and shipping</h2>
        <dl className="review-summary">
          <div>
            <dt>Customer</dt>
            <dd>{selection.customer.displayName} · #{selection.customer.customerId} · {selection.customer.customerKind}</dd>
            <FieldErrors id="review-customer-errors" messages={fieldErrors.customerId} />
          </div>
          <div>
            <dt>Bill-to address</dt>
            <dd>{formatAddressSummary(selection.billToAddress)}</dd>
            <FieldErrors id="review-bill-to-errors" messages={fieldErrors.billToAddressId} />
          </div>
          <div>
            <dt>Ship-to address</dt>
            <dd>{formatAddressSummary(selection.shipToAddress)}</dd>
            <FieldErrors id="review-ship-to-errors" messages={fieldErrors.shipToAddressId} />
          </div>
          <div>
            <dt>Ship method</dt>
            <dd>{selection.shipMethod.name} · #{selection.shipMethod.shipMethodId}</dd>
            <FieldErrors id="review-ship-method-errors" messages={fieldErrors.shipMethodId} />
          </div>
        </dl>
      </section>

      <section className="results-section review-section" aria-labelledby="review-lines-title">
        <div className="results-heading"><div><p className="eyebrow">LINES</p><h2 id="review-lines-title">Products and offers</h2></div></div>
        <div className="table-scroll" role="region" aria-label="Order lines to submit" tabIndex="0">
          <table className="review-table">
            <thead>
              <tr>
                <th scope="col">Product</th>
                <th scope="col">Offer</th>
                <th scope="col" className="numeric">Quantity</th>
                <th scope="col" className="numeric">Unit price</th>
                <th scope="col" className="numeric">Discount</th>
                <th scope="col" className="numeric">Line total · preview</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => {
                const lineOffer = lineOffers[line.productId]
                const offer = selectedOffer(lineOffer)
                const discountPct = selectedDiscountPct(lineOffer)
                const lineTotal = previewByProduct[line.productId]?.lineTotal ?? null
                const errors = lineErrors[line.productId]
                return (
                  <tr key={line.productId} className={errors ? 'review-line-invalid' : undefined}>
                    <td data-label="Product">
                      <strong>{line.name}</strong><br /><small>#{line.productId} · {line.productNumber}</small>
                      <FieldErrors id={`review-line-${line.productId}-errors`} messages={errors} />
                    </td>
                    <td data-label="Offer">{lineOffer?.offerId == null ? 'No discount' : offer?.description || lineOffer.offerDescription || `Offer ${lineOffer.offerId}`}</td>
                    <td data-label="Quantity" className="numeric">{line.quantity}</td>
                    <td data-label="Unit price" className="numeric">{Number.isFinite(line.listPrice) ? formatPocUsd(line.listPrice) : 'Set by server'}</td>
                    <td data-label="Discount" className="numeric">{discountPct === null ? 'Set by server' : formatPocPercent(discountPct)}</td>
                    <td data-label="Line total · preview" className="numeric">{lineTotal === null ? 'Set by server' : formatPocUsd(lineTotal)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        <FieldErrors id="review-general-errors" messages={validation?.general} />

        {preview.available ? (
          <dl className="draft-financials review-financials" aria-label="Demo amount preview">
            <div><dt>Merchandise subtotal · preview USD</dt><dd>{formatPocUsd(preview.subTotal)}</dd></div>
            <div><dt>Demo tax · 7.5% of subtotal</dt><dd>{formatPocUsd(preview.taxAmt)}</dd></div>
            <div><dt>Demo freight · flat</dt><dd>{formatPocUsd(preview.freight)}</dd></div>
            <div className="draft-total"><dt>Total · preview</dt><dd>{formatPocUsd(preview.totalDue)}</dd></div>
          </dl>
        ) : (
          <p className="draft-price-unavailable" role="status">Some amounts cannot be previewed in the browser. The server calculates and records the amounts; they appear on the confirmation.</p>
        )}
        <p className="review-preview-note">Preview only. The recorded amounts are calculated by the server and shown on the confirmation.</p>
      </section>

      <div className="review-actions">
        <button className="back-button" type="button" onClick={onBack} disabled={submitting}>Back to edit</button>
        <p className="draft-live-message review-status" role="status" aria-live="polite">{submitting ? 'Submitting order…' : ''}</p>
        <button className="search-button submit-order-button" type="button" onClick={onSubmit} disabled={submitting} aria-busy={submitting}>
          {submitting ? 'Submitting…' : 'Submit order'}
        </button>
      </div>
    </div>
  )
}

export default OrderReview
