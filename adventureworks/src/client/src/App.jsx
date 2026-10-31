import { useCallback, useEffect, useRef, useState } from 'react'
import CustomerSelector from './CustomerSelector.jsx'
import LineOfferLoader from './LineOfferLoader.jsx'
import OrderConfirmation from './OrderConfirmation.jsx'
import OrderDraft from './OrderDraft.jsx'
import OrderLookup from './OrderLookup.jsx'
import OrderReview from './OrderReview.jsx'
import ProductDetail from './ProductDetail.jsx'
import { updateDraftQuantity } from './draftQuantity.js'
import {
  buildOrderPayload,
  calculateOrderPreview,
  createSubmissionToken,
  emptyOrderSelection,
  getOrderReadiness,
  orderContentKey,
  previewInputLines,
  submitOrder,
} from './orderSubmission.js'
import './App.css'

const initialRequest = {
  page: 1,
  pageSize: 25,
  search: '',
  sortBy: 'name',
  sortDirection: 'asc',
}

const MAX_DRAFT_QUANTITY = 32767

function problemMessage(problem) {
  if (problem?.errors && typeof problem.errors === 'object') {
    return Object.values(problem.errors).flat().filter(Boolean).join(' ')
  }

  return problem?.detail || problem?.title || 'The catalog request could not be completed.'
}

function App() {
  const [activeView, setActiveView] = useState('catalog')
  const [selectedProductId, setSelectedProductId] = useState(null)
  const [request, setRequest] = useState(initialRequest)
  const [searchInput, setSearchInput] = useState('')
  const [sortBy, setSortBy] = useState(initialRequest.sortBy)
  const [sortDirection, setSortDirection] = useState(initialRequest.sortDirection)
  const [pageSize, setPageSize] = useState(initialRequest.pageSize)
  const [pageData, setPageData] = useState(null)
  const [status, setStatus] = useState('loading')
  const [error, setError] = useState(null)
  const [validationMessage, setValidationMessage] = useState('')
  const [retryToken, setRetryToken] = useState(0)
  const [draftLines, setDraftLines] = useState([])
  const [quantityInputs, setQuantityInputs] = useState({})
  const [draftStatusMessage, setDraftStatusMessage] = useState('')
  const [lineOffers, setLineOffers] = useState({})
  const [offerRetryTokens, setOfferRetryTokens] = useState({})
  const [selection, setSelection] = useState(emptyOrderSelection)
  const [review, setReview] = useState(null)
  const [submission, setSubmission] = useState({ status: 'idle', error: null })
  const [confirmation, setConfirmation] = useState(null)
  const [lookupReference, setLookupReference] = useState(null)
  const submittingRef = useRef(false)

  const readiness = getOrderReadiness({ lines: draftLines, quantityInputs, lineOffers, selection })

  const handleOffersLoading = useCallback((productId, quantity) => {
    setLineOffers((current) => ({
      ...current,
      [productId]: { offerId: null, offerDescription: null, notice: '', offers: null, ...current[productId], status: 'loading', quantity, error: null },
    }))
  }, [])

  const handleOffersResult = useCallback((productId, quantity, result) => {
    setLineOffers((current) => {
      const previous = { offerId: null, offerDescription: null, notice: '', ...current[productId] }
      if (result.error) {
        return { ...current, [productId]: { ...previous, status: 'error', quantity, offers: null, error: result.error } }
      }
      const stillEligible = previous.offerId === null || result.offers.some((offer) => offer.specialOfferId === previous.offerId)
      return {
        ...current,
        [productId]: {
          ...previous,
          status: 'success',
          quantity,
          offers: result.offers,
          error: null,
          offerId: stillEligible ? previous.offerId : null,
          offerDescription: stillEligible ? previous.offerDescription : null,
          notice: stillEligible
            ? previous.notice
            : `"${previous.offerDescription || `Offer ${previous.offerId}`}" is not eligible at quantity ${quantity}. This line was reset to No discount.`,
        },
      }
    })
  }, [])

  function changeLineOffer(productId, offerId, offerDescription) {
    setLineOffers((current) => ({
      ...current,
      [productId]: { status: 'idle', offers: null, quantity: null, error: null, ...current[productId], offerId, offerDescription, notice: '' },
    }))
  }

  function retryLineOffers(productId) {
    setOfferRetryTokens((current) => ({ ...current, [productId]: (current[productId] ?? 0) + 1 }))
  }

  function draftContentKey() {
    if (!readiness.ready) return null
    return orderContentKey(buildOrderPayload({ submissionToken: '', selection, lines: draftLines, lineOffers }))
  }

  function openReview() {
    const contentKey = draftContentKey()
    if (!contentKey) return
    // Reuse the token only while the reviewed content is unchanged, so retries and re-reviews of
    // the same order are replayed by the server instead of recording a duplicate.
    if (!review || review.contentKey !== contentKey) {
      setReview({ token: createSubmissionToken(), contentKey })
    }
    setSubmission({ status: 'idle', error: null })
    setActiveView('review')
  }

  async function submitReviewedOrder() {
    if (submittingRef.current || !review) return
    submittingRef.current = true
    setSubmission({ status: 'submitting', error: null })
    const payload = buildOrderPayload({ submissionToken: review.token, selection, lines: draftLines, lineOffers })
    const preview = calculateOrderPreview(previewInputLines(draftLines, lineOffers))
    const lineNames = Object.fromEntries(draftLines.map((line) => [line.productId, line.name]))

    try {
      const result = await submitOrder(payload, { lineNames })
      if (result.error) {
        setSubmission({ status: 'error', error: result.error })
        return
      }
      setConfirmation({
        order: result.order,
        replayed: result.replayed,
        selection,
        previewTotal: preview.available ? preview.totalDue : null,
        contentKey: review.contentKey,
      })
      setSubmission({ status: 'idle', error: null })
      setActiveView('confirmation')
    } catch {
      setSubmission({ status: 'error', error: { kind: 'unavailable', title: 'Order service unavailable', message: 'Nothing was recorded unless you see a confirmation. Retry uses the same submission, so the order will not be recorded twice.', retryable: true } })
    } finally {
      submittingRef.current = false
    }
  }

  function startNewOrder() {
    setDraftLines([])
    setQuantityInputs({})
    setLineOffers({})
    setOfferRetryTokens({})
    setSelection(emptyOrderSelection)
    setReview(null)
    setSubmission({ status: 'idle', error: null })
    setConfirmation(null)
    setSelectedProductId(null)
    setDraftStatusMessage('New order started.')
    setActiveView('catalog')
  }

  function viewInOrderLookup(orderReference) {
    setLookupReference(orderReference)
    setActiveView('orders')
  }

  function navigate(view) {
    if (view === 'orders') setLookupReference(null)
    setActiveView(view)
  }

  const currentContentKey = draftContentKey()
  const recordedReference = confirmation && currentContentKey === confirmation.contentKey ? confirmation.order.orderReference : null
  const navView = activeView === 'review' || activeView === 'confirmation' ? 'catalog' : activeView
  const viewLabel = {
    catalog: selectedProductId ? 'Product detail' : 'Product catalog',
    customers: 'Customer lookup',
    orders: 'Order lookup',
    review: 'Order review',
    confirmation: 'Order confirmation',
  }[activeView]

  useEffect(() => {
    const controller = new AbortController()
    const params = new URLSearchParams({
      page: String(request.page),
      pageSize: String(request.pageSize),
      sortBy: request.sortBy,
      sortDirection: request.sortDirection,
    })
    if (request.search) params.set('search', request.search)

    async function loadProducts() {
      setStatus('loading')
      setError(null)
      setPageData(null)

      let response
      try {
        response = await fetch(`/api/products?${params}`, { signal: controller.signal })
      } catch (fetchError) {
        if (fetchError.name !== 'AbortError') {
          setError({ kind: 'network', message: 'The catalog could not be reached. Check your connection and try again.' })
          setStatus('error')
        }
        return
      }

      if (response.status === 503) {
        let problem
        try {
          problem = await response.json()
        } catch {
          problem = null
        }
        setError({
          kind: 'unavailable',
          message: problemMessage(problem) || 'The catalog service is temporarily unavailable.',
        })
        setStatus('error')
        return
      }

      if (!response.ok) {
        let problem
        try {
          problem = await response.json()
        } catch {
          problem = null
        }
        setError({
          kind: response.status === 502 ? 'network' : response.status === 400 ? 'validation' : 'service',
          message: response.status === 502
            ? 'The catalog could not be reached. Check the connection and try again.'
            : problemMessage(problem),
        })
        setStatus('error')
        return
      }

      try {
        const result = await response.json()
        if (!Array.isArray(result.items)) throw new Error('Invalid catalog response')
        setPageData(result)
        setStatus('success')
      } catch {
        setError({ kind: 'service', message: 'The catalog returned an unreadable response. Try again.' })
        setStatus('error')
      }
    }

    loadProducts()

    return () => controller.abort()
  }, [request, retryToken])

  function submitSearch(event) {
    event.preventDefault()
    const search = searchInput.trim()
    if (search.length > 100) {
      setValidationMessage('Search must be 100 characters or fewer.')
      return
    }

    setValidationMessage('')
    setRequest({ page: 1, pageSize, search, sortBy, sortDirection })
  }

  function changePage(nextPage) {
    if (nextPage < 1 || nextPage > 10_000) return
    setRequest((current) => ({ ...current, page: nextPage }))
  }

  function addToDraft(product) {
    const existing = draftLines.find((line) => line.productId === product.productId)
    if (existing) {
      if (existing.quantity >= MAX_DRAFT_QUANTITY) {
        setDraftStatusMessage(`Quantity cannot exceed ${MAX_DRAFT_QUANTITY}, the SQL smallint schema storage limit.`)
        return
      }
      const quantity = existing.quantity + 1
      setDraftLines((current) => current.map((line) => line.productId === product.productId ? { ...line, quantity } : line))
      setQuantityInputs((current) => ({ ...current, [product.productId]: String(quantity) }))
      setDraftStatusMessage(`${product.name} quantity increased to ${quantity}.`)
      return
    }

    const identity = {
      productId: product.productId,
      name: product.name,
      productNumber: product.productNumber,
      categoryName: product.categoryName,
      subcategoryName: product.subcategoryName,
      listPrice: product.listPrice,
      quantity: 1,
    }
    setDraftLines((current) => [...current, identity])
    setQuantityInputs((current) => ({ ...current, [product.productId]: '1' }))
    setDraftStatusMessage(`${product.name} added to draft.`)
  }

  function changeDraftQuantity(productId, rawValue) {
    setQuantityInputs((current) => ({ ...current, [productId]: rawValue }))
    setDraftLines((current) => updateDraftQuantity(current, productId, rawValue).lines)
  }

  function removeFromDraft(productId) {
    const removed = draftLines.find((line) => line.productId === productId)
    setDraftLines((current) => current.filter((line) => line.productId !== productId))
    setQuantityInputs((current) => {
      const next = { ...current }
      delete next[productId]
      return next
    })
    setLineOffers((current) => {
      const next = { ...current }
      delete next[productId]
      return next
    })
    if (removed) setDraftStatusMessage(`${removed.name} removed from draft.`)
  }

  function discardDraft() {
    if (!window.confirm('Discard all products in this draft?')) return
    setDraftLines([])
    setQuantityInputs({})
    setLineOffers({})
    setOfferRetryTokens({})
    setReview(null)
    setDraftStatusMessage('Draft discarded.')
  }

  return (
    <div className="app-shell">
      <aside className="sidebar" aria-label="Main navigation">
        <a className="brand" href="#catalog" aria-label="AdventureWorks catalog" onClick={() => navigate('catalog')}>
          <span className="brand-mark" aria-hidden="true">A</span>
          <span className="brand-copy"><strong>AdventureWorks</strong><small>SALES OPERATIONS</small></span>
        </a>
        <div className="nav-label">WORKSPACE</div>
        <nav className="nav-list" aria-label="Workspace sections">
          <button className={`nav-item nav-button${navView === 'catalog' ? ' active' : ''}`} type="button" aria-current={navView === 'catalog' ? 'page' : undefined} onClick={() => navigate('catalog')}><span aria-hidden="true">▦</span>Catalog</button>
          <button className={`nav-item nav-button${navView === 'customers' ? ' active' : ''}`} type="button" aria-current={navView === 'customers' ? 'page' : undefined} onClick={() => navigate('customers')}><span aria-hidden="true">◉</span>Customers</button>
          <button className={`nav-item nav-button${navView === 'orders' ? ' active' : ''}`} type="button" aria-current={navView === 'orders' ? 'page' : undefined} onClick={() => navigate('orders')}><span aria-hidden="true">≡</span>Orders</button>
        </nav>
        <div className="sidebar-bottom"><span className="environment-dot" aria-hidden="true" /><div><strong>Local demo</strong><small>AdventureWorks test copy · not production</small></div></div>
      </aside>

      <main id={navView} className="main-content">
        <header className="topbar">
          <div className="breadcrumb">Workspace <span>/</span> {viewLabel}</div>
          <div className="api-indicator"><span className="status-dot" aria-hidden="true" />{viewLabel}</div>
        </header>

        {activeView === 'catalog' ? (
          <>
            <section className="page-heading">
              <div><p className="eyebrow">ADVENTUREWORKS 2022 <span>·</span> CATALOG</p><h1>Product catalog</h1><p className="heading-copy">Find products by name or product number and add them to the order draft.</p></div>
            </section>

            <OrderDraft
              lines={draftLines}
              quantityInputs={quantityInputs}
              lineOffers={lineOffers}
              selection={selection}
              readiness={readiness}
              recordedReference={recordedReference}
              onQuantityChange={changeDraftQuantity}
              onOfferChange={changeLineOffer}
              onRetryOffers={retryLineOffers}
              onRemove={removeFromDraft}
              onDiscard={discardDraft}
              onChooseCustomer={() => navigate('customers')}
              onReview={openReview}
              statusMessage={draftStatusMessage}
            />

            <section className="catalog-panel" aria-labelledby="catalog-controls-title">
          <div className="section-heading">
            <div><p className="eyebrow">PRODUCT LOOKUP</p><h2 id="catalog-controls-title">Search and refine</h2></div>
          </div>
          <form className="catalog-form" onSubmit={submitSearch} noValidate>
            <div className="search-field">
              <label htmlFor="catalog-search">Name or product number</label>
              <input
                id="catalog-search"
                type="search"
                value={searchInput}
                onChange={(event) => {
                  setSearchInput(event.target.value)
                  if (validationMessage) setValidationMessage('')
                }}
                aria-invalid={Boolean(validationMessage)}
                aria-describedby={validationMessage ? 'search-validation' : undefined}
              />
              {validationMessage && <span id="search-validation" className="field-error" role="alert">{validationMessage}</span>}
            </div>
            <div className="select-field">
              <label htmlFor="catalog-sort">Sort by</label>
              <select id="catalog-sort" value={sortBy} onChange={(event) => setSortBy(event.target.value)}>
                <option value="name">Name</option>
                <option value="productNumber">Product number</option>
                <option value="productId">Product ID</option>
              </select>
            </div>
            <div className="select-field">
              <label htmlFor="catalog-direction">Direction</label>
              <select id="catalog-direction" value={sortDirection} onChange={(event) => setSortDirection(event.target.value)}>
                <option value="asc">Ascending</option>
                <option value="desc">Descending</option>
              </select>
            </div>
            <div className="select-field">
              <label htmlFor="catalog-page-size">Rows</label>
              <select id="catalog-page-size" value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))}>
                <option value={10}>10</option>
                <option value={25}>25</option>
                <option value={50}>50</option>
                <option value={100}>100</option>
              </select>
            </div>
            <button className="search-button" type="submit" disabled={status === 'loading'}>Search</button>
          </form>
          </section>

          {selectedProductId !== null ? (
            <ProductDetail productId={selectedProductId} onBack={() => setSelectedProductId(null)} onAddToDraft={addToDraft} />
          ) : (
          <section className="results-section" aria-labelledby="results-title">
          <div className="results-heading">
            <div><p className="eyebrow">RESULTS</p><h2 id="results-title">Products</h2></div>
            {status === 'success' && pageData && <span className="result-page">Page {pageData.page} · {pageData.pageSize} per page</span>}
          </div>
          {status === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading products…</p>}
          {status === 'error' && error && (
            <div className={`state-message error-message ${error.kind}`} role="alert">
              <strong>{error.kind === 'unavailable' ? 'Catalog service unavailable' : error.kind === 'network' ? 'Connection problem' : error.kind === 'validation' ? 'Search could not be applied' : 'Catalog request failed'}</strong>
              <span>{error.message}</span>
              <button className="retry-button" type="button" onClick={() => setRetryToken((current) => current + 1)}>Try again</button>
            </div>
          )}
          {status === 'success' && pageData?.items.length === 0 && <p className="state-message">No products match this search.</p>}
          {status === 'success' && pageData?.items.length > 0 && (
            <>
              <div className="table-scroll" role="region" aria-label="Product results" tabIndex="0">
                <table>
                  <thead><tr><th scope="col">Product ID</th><th scope="col">Name</th><th scope="col">Product number</th><th scope="col">Category</th><th scope="col">Subcategory</th><th scope="col">Detail</th></tr></thead>
                  <tbody>
                    {pageData.items.map((product) => (
                      <tr key={product.productId}>
                        <td data-label="Product ID">{product.productId}</td>
                        <td data-label="Name" className="product-name"><button className="product-detail-link" type="button" onClick={() => setSelectedProductId(product.productId)} aria-label={`View details for ${product.name}`}>{product.name}</button></td>
                        <td data-label="Product number">{product.productNumber}</td>
                        <td data-label="Category">{product.categoryName || '—'}</td>
                        <td data-label="Subcategory">{product.subcategoryName || '—'}</td>
                        <td data-label="Detail"><button className="customer-select-button" type="button" onClick={() => setSelectedProductId(product.productId)} aria-label={`View ${product.name} and its POC price`}>View</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <nav className="pagination" aria-label="Catalog pages">
                <button type="button" onClick={() => changePage(pageData.page - 1)} disabled={pageData.page <= 1 || status === 'loading'}>Previous</button>
                <span aria-live="polite">Page {pageData.page}</span>
                <button type="button" onClick={() => changePage(pageData.page + 1)} disabled={!pageData.hasMore || pageData.page >= 10_000 || status === 'loading'}>Next</button>
              </nav>
            </>
          )}
            </section>
          )}
          </>
        ) : activeView === 'customers' ? (
          <CustomerSelector selection={selection} onSelectionChange={setSelection} onContinue={() => navigate('catalog')} />
        ) : activeView === 'review' && review && readiness.ready ? (
          <OrderReview
            lines={draftLines}
            lineOffers={lineOffers}
            selection={selection}
            submission={submission}
            onBack={() => navigate('catalog')}
            onSubmit={submitReviewedOrder}
          />
        ) : activeView === 'confirmation' && confirmation ? (
          <OrderConfirmation confirmation={confirmation} onViewInLookup={viewInOrderLookup} onStartNew={startNewOrder} />
        ) : activeView === 'orders' ? (
          <OrderLookup key={lookupReference ?? 'search'} initialReference={lookupReference} />
        ) : (
          <section className="page-heading">
            <div>
              <h1>Nothing to review</h1>
              <p className="heading-copy">The order draft is incomplete or has changed.</p>
              <button className="search-button" type="button" onClick={() => navigate('catalog')}>Back to order draft</button>
            </div>
          </section>
        )}

        {draftLines.map((line) => (
          <LineOfferLoader
            key={line.productId}
            productId={line.productId}
            quantity={line.quantity}
            retryToken={offerRetryTokens[line.productId] ?? 0}
            onLoading={handleOffersLoading}
            onResult={handleOffersResult}
          />
        ))}

        <footer className="page-footer"><span>PROJECT / ADVENTUREWORKS</span><span>{navView === 'catalog' ? 'CATALOG' : navView === 'customers' ? 'CUSTOMERS' : 'ORDERS'} <i /> REACT <i /> API</span></footer>
      </main>
    </div>
  )
}

export default App
