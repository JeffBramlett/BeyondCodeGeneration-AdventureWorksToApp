import { useEffect, useRef } from 'react'
import { formatAddressSummary } from './orderSubmission.js'
import { formatPocPercent, formatPocUsd } from './pocMoney.js'

function addressText(address, addressId) {
  return address?.addressId === addressId ? formatAddressSummary(address) : `Address #${addressId}`
}

function OrderConfirmation({ confirmation, onViewInLookup, onStartNew }) {
  const headingRef = useRef(null)
  const { order, replayed, selection, previewTotal } = confirmation

  useEffect(() => {
    headingRef.current?.focus()
  }, [order.orderReference])

  const customerText = selection.customer?.customerId === order.customerId
    ? `${selection.customer.displayName} · #${order.customerId}`
    : `Customer #${order.customerId}`
  const shipMethodText = selection.shipMethod?.shipMethodId === order.shipMethodId
    ? `${selection.shipMethod.name} · #${order.shipMethodId}`
    : `Ship method #${order.shipMethodId}`
  const differsFromPreview = typeof previewTotal === 'number' && Math.abs(previewTotal - order.totalDue) >= 0.005

  return (
    <div className="order-confirmation">
      <section className="page-heading confirmation-heading">
        <div>
          <p className="eyebrow">LOCAL DEMO <span>·</span> ORDER CONFIRMATION</p>
          <h1 ref={headingRef} tabIndex="-1">Order {order.orderReference} recorded</h1>
          <p className="heading-copy">
            Recorded in the AdventureWorks test copy. This confirms the order record only; it has not been shipped or fulfilled.
          </p>
        </div>
      </section>

      {replayed && (
        <p className="local-selection-note" role="status">This submission had already been recorded, so the existing order is shown. No duplicate order was created.</p>
      )}

      <section className="detail-section review-section" aria-labelledby="confirmation-summary-title">
        <h2 id="confirmation-summary-title">Recorded order</h2>
        <dl className="review-summary confirmation-summary">
          <div><dt>Order reference</dt><dd className="confirmation-reference">{order.orderReference}</dd></div>
          <div><dt>Status</dt><dd><span className="status-pill">{order.status}</span></dd></div>
          <div><dt>Order date</dt><dd><time>{order.orderDate}</time></dd></div>
          <div><dt>Due date</dt><dd>{order.dueDate ? <time>{order.dueDate}</time> : 'Not provided'}</dd></div>
          <div><dt>Customer</dt><dd>{customerText}</dd></div>
          <div><dt>Bill-to address</dt><dd>{addressText(selection.billToAddress, order.billToAddressId)}</dd></div>
          <div><dt>Ship-to address</dt><dd>{addressText(selection.shipToAddress, order.shipToAddressId)}</dd></div>
          <div><dt>Ship method</dt><dd>{shipMethodText}</dd></div>
        </dl>
      </section>

      <section className="results-section review-section" aria-labelledby="confirmation-lines-title">
        <div className="results-heading"><div><p className="eyebrow">RECORDED LINES</p><h2 id="confirmation-lines-title">Lines and amounts</h2></div></div>
        <div className="table-scroll" role="region" aria-label={`Recorded lines for order ${order.orderReference}`} tabIndex="0">
          <table className="review-table">
            <thead>
              <tr>
                <th scope="col">Product</th>
                <th scope="col">Offer</th>
                <th scope="col" className="numeric">Quantity</th>
                <th scope="col" className="numeric">Unit price</th>
                <th scope="col" className="numeric">Discount</th>
                <th scope="col" className="numeric">Line total</th>
              </tr>
            </thead>
            <tbody>
              {order.lines.map((line, index) => (
                <tr key={`${line.productId}-${index}`}>
                  <td data-label="Product"><strong>{line.productName}</strong><br /><small>#{line.productId}</small></td>
                  <td data-label="Offer">{line.offerDescription || (line.offerId === null ? 'No discount' : `Offer ${line.offerId}`)}</td>
                  <td data-label="Quantity" className="numeric">{line.quantity}</td>
                  <td data-label="Unit price" className="numeric">{formatPocUsd(line.unitPrice)}</td>
                  <td data-label="Discount" className="numeric">{formatPocPercent(line.unitPriceDiscount)}</td>
                  <td data-label="Line total" className="numeric">{formatPocUsd(line.lineTotal)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <dl className="draft-financials review-financials" aria-label="Recorded amounts">
          <div><dt>Merchandise subtotal · USD</dt><dd>{formatPocUsd(order.subTotal)}</dd></div>
          <div><dt>Demo tax</dt><dd>{formatPocUsd(order.taxAmt)}</dd></div>
          <div><dt>Demo freight</dt><dd>{formatPocUsd(order.freight)}</dd></div>
          <div className="draft-total"><dt>Total recorded</dt><dd>{formatPocUsd(order.totalDue)}</dd></div>
        </dl>
        <p className="review-preview-note">
          Amounts above are the values recorded by the server (local demo rules).
          {differsFromPreview ? ' They differ from the browser preview; the recorded values are authoritative.' : ''}
        </p>
      </section>

      <div className="review-actions">
        <button className="back-button" type="button" onClick={() => onViewInLookup(order.orderReference)}>View in Order Lookup</button>
        <button className="search-button" type="button" onClick={onStartNew}>Start new order</button>
      </div>
    </div>
  )
}

export default OrderConfirmation
