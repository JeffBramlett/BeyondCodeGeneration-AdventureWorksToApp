import { useEffect, useState } from 'react'
import { fetchOrderDetail, fetchOrderPage, orderLinePageBounds, orderPageBounds, validateOrderSearch } from './orders.js'

const initialCriteria = { mode: 'reference', orderReference: '', fromDate: '', toDate: '' }
const MAX_PAGE_NUMBER = 10_000

function OrderLookup({ initialReference = null }) {
  const startReference = typeof initialReference === 'string' && initialReference.trim() ? initialReference.trim() : null
  const [criteria, setCriteria] = useState(() => startReference ? { ...initialCriteria, orderReference: startReference } : null)
  const [draftCriteria, setDraftCriteria] = useState(() => startReference ? { ...initialCriteria, orderReference: startReference } : initialCriteria)
  const [validationMessage, setValidationMessage] = useState('')
  const [pageSize, setPageSize] = useState(orderPageBounds.defaultPageSize)
  const [page, setPage] = useState(1)
  const [results, setResults] = useState(null)
  const [listStatus, setListStatus] = useState('idle')
  const [listError, setListError] = useState(null)
  const [detailReference, setDetailReference] = useState(startReference)
  const [linePage, setLinePage] = useState(1)
  const [linePageSize, setLinePageSize] = useState(orderLinePageBounds.defaultPageSize)
  const [detail, setDetail] = useState(null)
  const [detailStatus, setDetailStatus] = useState('idle')
  const [detailError, setDetailError] = useState(null)
  const [retryList, setRetryList] = useState(0)
  const [retryDetail, setRetryDetail] = useState(0)

  useEffect(() => {
    if (!criteria) return undefined

    const controller = new AbortController()
    async function loadOrderPage() {
      setListStatus('loading')
      setListError(null)
      setResults(null)
      try {
        const response = await fetchOrderPage(criteria, { page, pageSize, signal: controller.signal })
        if (controller.signal.aborted) return
        if (response.error) {
          setListError(response.error)
          setListStatus('error')
          return
        }
        setResults(response.page)
        setListStatus('success')
      } catch (error) {
        if (!controller.signal.aborted && error.name !== 'AbortError') {
          setListError({ kind: 'service', message: 'The order lookup could not be completed. Try again.' })
          setListStatus('error')
        }
      }
    }
    loadOrderPage()

    return () => controller.abort()
  }, [criteria, page, pageSize, retryList])

  useEffect(() => {
    if (!detailReference) return undefined

    const controller = new AbortController()
    async function loadOrderDetail() {
      setDetailStatus('loading')
      setDetailError(null)
      setDetail(null)
      try {
        const response = await fetchOrderDetail(detailReference, { linePage, linePageSize, signal: controller.signal })
        if (controller.signal.aborted) return
        if (response.error) {
          setDetailError(response.error)
          setDetailStatus('error')
          return
        }
        setDetail(response.detail)
        setDetailStatus('success')
      } catch (error) {
        if (!controller.signal.aborted && error.name !== 'AbortError') {
          setDetailError({ kind: 'service', message: 'Order details could not be loaded. Try again.' })
          setDetailStatus('error')
        }
      }
    }
    loadOrderDetail()

    return () => controller.abort()
  }, [detailReference, linePage, linePageSize, retryDetail])

  function updateCriteria(property, value) {
    setDraftCriteria((current) => ({ ...current, [property]: value }))
    setValidationMessage('')
  }

  function submitSearch(event) {
    event.preventDefault()
    const message = validateOrderSearch(draftCriteria)
    if (message) {
      setValidationMessage(message)
      return
    }

    setValidationMessage('')
    setCriteria({ ...draftCriteria, orderReference: draftCriteria.orderReference.trim() })
    setPage(1)
    setDetailReference(null)
    setDetail(null)
  }

  function openDetail(orderReference) {
    setDetailReference(orderReference)
    setLinePage(1)
  }

  function errorTitle(error) {
    if (error.kind === 'unauthorized') return 'Local Development sign-in required'
    if (error.kind === 'forbidden') return 'Order lookup is not authorized'
    if (error.kind === 'unavailable') return 'Order lookup unavailable'
    if (error.kind === 'network') return 'Connection problem'
    return 'Order lookup failed'
  }

  return (
    <div className="order-lookup">
      <section className="page-heading">
        <div>
          <p className="eyebrow">LOCAL DEMO <span>·</span> READ ONLY</p>
          <h1>Order lookup</h1>
          <p className="heading-copy">Find a stored order by exact reference or a short date range. Customer and financial information are not shown here.</p>
        </div>
      </section>

      <aside className="local-only-banner" aria-label="Local order lookup boundary">
        <strong>Local demo lookup</strong>
        <span>Reads from the AdventureWorks test copy through the local API. This page does not change orders.</span>
      </aside>

      <section className="catalog-panel order-search-panel" aria-labelledby="order-search-title">
        <div className="section-heading">
          <div><p className="eyebrow">SEARCH ORDERS</p><h2 id="order-search-title">Choose one lookup method</h2></div>
        </div>
        <form className="order-form" onSubmit={submitSearch} noValidate>
          <fieldset className="order-mode">
            <legend>Search by</legend>
            <label>
              <input type="radio" name="order-search-mode" value="reference" checked={draftCriteria.mode === 'reference'} onChange={() => updateCriteria('mode', 'reference')} />
              Exact order reference
            </label>
            <label>
              <input type="radio" name="order-search-mode" value="dateRange" checked={draftCriteria.mode === 'dateRange'} onChange={() => updateCriteria('mode', 'dateRange')} />
              Inclusive date range
            </label>
          </fieldset>
          {draftCriteria.mode === 'reference' ? (
            <div className="search-field order-reference-field">
              <label htmlFor="order-reference">Order reference</label>
              <input id="order-reference" type="text" maxLength="25" autoComplete="off" value={draftCriteria.orderReference} onChange={(event) => updateCriteria('orderReference', event.target.value)} aria-invalid={Boolean(validationMessage)} aria-describedby={validationMessage ? 'order-search-error' : 'order-reference-help'} />
              <span className="field-hint" id="order-reference-help">Enter the complete reference; partial matches are not used.</span>
            </div>
          ) : (
            <>
              <div className="search-field">
                <label htmlFor="order-from-date">From date</label>
                <input id="order-from-date" type="date" value={draftCriteria.fromDate} onChange={(event) => updateCriteria('fromDate', event.target.value)} aria-invalid={Boolean(validationMessage)} aria-describedby={validationMessage ? 'order-search-error' : undefined} />
              </div>
              <div className="search-field">
                <label htmlFor="order-to-date">To date</label>
                <input id="order-to-date" type="date" value={draftCriteria.toDate} onChange={(event) => updateCriteria('toDate', event.target.value)} aria-invalid={Boolean(validationMessage)} aria-describedby={validationMessage ? 'order-search-error' : undefined} />
              </div>
            </>
          )}
          {validationMessage && <p className="field-error order-validation" id="order-search-error" role="alert">{validationMessage}</p>}
          <div className="select-field order-page-size">
            <label htmlFor="order-page-size">Results per page</label>
            <select id="order-page-size" value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))}>
              <option value={25}>25</option><option value={50}>50</option><option value={100}>100</option>
            </select>
          </div>
          <button className="search-button order-submit" type="submit" disabled={listStatus === 'loading'}>Search orders</button>
        </form>
      </section>

      <section className="results-section" aria-labelledby="orders-results-title">
        <div className="results-heading">
          <div><p className="eyebrow">RESULTS</p><h2 id="orders-results-title">Orders</h2></div>
          {listStatus === 'success' && results && <span className="result-page">Page {results.page} · {results.pageSize} per page</span>}
        </div>
        {listStatus === 'idle' && <p className="state-message">Enter an exact reference or a complete date range to search.</p>}
        {listStatus === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading orders…</p>}
        {listStatus === 'error' && listError && (
          <div className={`state-message error-message ${listError.kind}`} role="alert">
            <strong>{errorTitle(listError)}</strong><span>{listError.message}</span>
            <button className="retry-button" type="button" onClick={() => setRetryList((value) => value + 1)}>Try again</button>
          </div>
        )}
        {listStatus === 'success' && results?.items.length === 0 && <p className="state-message">No orders match this search.</p>}
        {listStatus === 'success' && results?.items.length > 0 && (
          <>
            <div className="table-scroll" role="region" aria-label="Order search results" tabIndex="0">
              <table>
                <thead><tr><th scope="col">Order reference</th><th scope="col">Stored OrderDate</th><th scope="col">Details</th></tr></thead>
                <tbody>
                  {results.items.map((order) => (
                    <tr key={order.orderReference}>
                      <td data-label="Order reference">{order.orderReference}</td>
                      <td data-label="Stored OrderDate"><time>{order.orderDate}</time></td>
                      <td data-label="Details"><button className="customer-select-button" type="button" onClick={() => openDetail(order.orderReference)} aria-label={`View product and quantity lines for order ${order.orderReference}`}>View lines</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <nav className="pagination" aria-label="Order result pages">
              <button type="button" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={results.page <= 1 || listStatus === 'loading'}>Previous</button>
              <span aria-live="polite">Page {results.page}</span>
              <button type="button" onClick={() => setPage((value) => Math.min(MAX_PAGE_NUMBER, value + 1))} disabled={!results.hasMore || results.page >= MAX_PAGE_NUMBER || listStatus === 'loading'}>Next</button>
            </nav>
          </>
        )}
      </section>

      {detailReference && (
        <section className="results-section order-detail-section" aria-labelledby="order-detail-title">
          <div className="results-heading order-detail-heading">
            <div><p className="eyebrow">ORDER DETAIL</p><h2 id="order-detail-title">Order {detail?.orderReference || detailReference}</h2></div>
            <button className="back-button" type="button" onClick={() => { setDetailReference(null); setDetail(null) }}>Close details</button>
          </div>
          {detailStatus === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading order lines…</p>}
          {detailStatus === 'error' && detailError && (
            <div className={`state-message error-message ${detailError.kind}`} role="alert">
              <strong>{errorTitle(detailError)}</strong><span>{detailError.message}</span>
              <button className="retry-button" type="button" onClick={() => setRetryDetail((value) => value + 1)}>Try again</button>
            </div>
          )}
          {detailStatus === 'success' && detail && (
            <>
              <dl className="order-summary">
                <div><dt>Order reference</dt><dd>{detail.orderReference}</dd></div>
                <div><dt>Stored OrderDate · server value, no timezone conversion</dt><dd><time>{detail.orderDate}</time></dd></div>
              </dl>
              <div className="results-heading order-lines-heading">
                <h3>Product and quantity lines</h3>
                <div className="order-line-controls">
                  <label htmlFor="order-line-page-size">Lines per page</label>
                  <select id="order-line-page-size" value={linePageSize} onChange={(event) => { setLinePageSize(Number(event.target.value)); setLinePage(1) }}>
                    <option value={50}>50</option><option value={100}>100</option>
                  </select>
                  <span className="result-page">Page {detail.linePage}</span>
                </div>
              </div>
              {detail.lines.length === 0 ? <p className="state-message">No order lines were returned for this page.</p> : (
                <div className="table-scroll" role="region" aria-label={`Product and quantity lines for order ${detail.orderReference}`} tabIndex="0">
                  <table>
                    <thead><tr><th scope="col">Product ID</th><th scope="col">Quantity</th></tr></thead>
                    <tbody>{detail.lines.map((line, index) => <tr key={`${line.productId}-${index}`}><td data-label="Product ID">{line.productId}</td><td data-label="Quantity">{line.orderQty}</td></tr>)}</tbody>
                  </table>
                </div>
              )}
              <nav className="pagination" aria-label="Order line pages">
                <button type="button" onClick={() => setLinePage((value) => Math.max(1, value - 1))} disabled={detail.linePage <= 1 || detailStatus === 'loading'}>Previous</button>
                <span aria-live="polite">Page {detail.linePage}</span>
                <button type="button" onClick={() => setLinePage((value) => Math.min(MAX_PAGE_NUMBER, value + 1))} disabled={!detail.hasMore || detail.linePage >= MAX_PAGE_NUMBER || detailStatus === 'loading'}>Next</button>
              </nav>
              <p className="field-hint order-datetime-note">OrderDate is displayed exactly as returned by the server; no timezone conversion is applied.</p>
            </>
          )}
        </section>
      )}
    </div>
  )
}

export default OrderLookup
