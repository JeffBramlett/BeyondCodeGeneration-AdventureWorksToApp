import { useEffect, useRef, useState } from 'react'
import { isPositiveSmallintQuantity } from './draftQuantity.js'
import { fetchEligibleOffers } from './eligibleOffers.js'
import { formatPocUsd } from './pocMoney.js'

function problemMessage(problem) {
  if (problem?.errors && typeof problem.errors === 'object') {
    return Object.values(problem.errors).flat().filter(Boolean).join(' ')
  }

  return problem?.detail || problem?.title || 'The catalog request could not be completed.'
}

async function readProblem(response) {
  try {
    return await response.json()
  } catch {
    return null
  }
}

function requestError(response, problem) {
  if (response.status === 400) return { kind: 'validation', title: 'Product ID could not be validated', message: problemMessage(problem) }
  if (response.status === 404) return { kind: 'not-found', title: 'Product not found', message: 'No product was returned for this catalog result.' }
  if (response.status === 503) return { kind: 'unavailable', title: 'Catalog service unavailable', message: problemMessage(problem) }
  if (response.status === 502) return { kind: 'network', title: 'Connection problem', message: 'The catalog service could not be reached. Check the connection and try again.' }
  return { kind: 'service', title: 'Catalog request failed', message: problemMessage(problem) }
}

function isProductDto(product) {
  return Number.isInteger(product?.productId)
    && typeof product.name === 'string'
    && typeof product.productNumber === 'string'
    && (product.categoryName === null || typeof product.categoryName === 'string')
    && (product.subcategoryName === null || typeof product.subcategoryName === 'string')
    && (product.listPrice === null || (typeof product.listPrice === 'number' && Number.isFinite(product.listPrice)))
}

function ProductDetail({ productId, onBack, onAddToDraft }) {
  const [retryToken, setRetryToken] = useState(0)
  const [product, setProduct] = useState(null)
  const [productStatus, setProductStatus] = useState('loading')
  const [productError, setProductError] = useState(null)
  const [offerQuantity, setOfferQuantity] = useState('1')
  const [requestedQuantity, setRequestedQuantity] = useState(null)
  const [offerRequestToken, setOfferRequestToken] = useState(0)
  const [eligibleOffers, setEligibleOffers] = useState(null)
  const [offerStatus, setOfferStatus] = useState('idle')
  const [offerError, setOfferError] = useState(null)
  const activeOfferRequest = useRef(null)

  useEffect(() => {
    const controller = new AbortController()

    async function loadProduct() {
      setProduct(null)
      setProductStatus('loading')
      setProductError(null)

      let response
      try {
        response = await fetch(`/api/products/${productId}`, { signal: controller.signal })
      } catch (fetchError) {
        if (fetchError.name !== 'AbortError') {
          setProductError({ kind: 'network', title: 'Connection problem', message: 'The catalog service could not be reached. Check the connection and try again.' })
          setProductStatus('error')
        }
        return
      }

      if (!response.ok) {
        const problem = await readProblem(response)
        if (!controller.signal.aborted) {
          setProductError(requestError(response, problem))
          setProductStatus('error')
        }
        return
      }

      let result
      try {
        result = await response.json()
        if (!isProductDto(result)) throw new Error('Invalid product response')
      } catch {
        if (!controller.signal.aborted) {
          setProductError({ kind: 'service', title: 'Product response unreadable', message: 'The catalog returned an unreadable product response. Try again.' })
          setProductStatus('error')
        }
        return
      }

      if (controller.signal.aborted) return
      setProduct(result)
      setProductStatus('success')
    }

    loadProduct()
    return () => controller.abort()
  }, [productId, retryToken])

  useEffect(() => {
    if (requestedQuantity === null) return undefined

    const controller = new AbortController()
    activeOfferRequest.current = controller

    async function loadEligibleOffers() {
      const result = await fetchEligibleOffers(productId, requestedQuantity, { signal: controller.signal })
      if (controller.signal.aborted) return
      if (result.error) {
        setOfferError(result.error)
        setOfferStatus('error')
        return
      }
      setEligibleOffers(result.offers)
      setOfferStatus('success')
    }

    loadEligibleOffers().catch((error) => {
      if (error.name !== 'AbortError' && !controller.signal.aborted) {
        setOfferError({ kind: 'network', title: 'Connection problem', message: 'The catalog service could not be reached. Check the connection and try again.' })
        setOfferStatus('error')
      }
    })

    return () => {
      controller.abort()
      if (activeOfferRequest.current === controller) activeOfferRequest.current = null
    }
  }, [productId, requestedQuantity, offerRequestToken])

  function retryProduct() {
    setRetryToken((current) => current + 1)
  }

  function changeOfferQuantity(value) {
    activeOfferRequest.current?.abort()
    activeOfferRequest.current = null
    setOfferQuantity(value)
    setRequestedQuantity(null)
    setEligibleOffers(null)
    setOfferError(null)
    setOfferStatus('idle')
  }

  function checkEligibleOffers(event) {
    event.preventDefault()
    if (!isPositiveSmallintQuantity(offerQuantity)) return
    activeOfferRequest.current?.abort()
    setEligibleOffers(null)
    setOfferError(null)
    setOfferStatus('loading')
    setRequestedQuantity(offerQuantity)
    setOfferRequestToken((current) => current + 1)
  }

  function retryEligibleOffers() {
    setEligibleOffers(null)
    setOfferError(null)
    setOfferStatus('loading')
    setOfferRequestToken((current) => current + 1)
  }

  return (
    <section className="product-detail" aria-labelledby="product-detail-title">
      <div className="product-detail-heading">
        <div>
          <p className="eyebrow">PRODUCT CATALOG <span>·</span> DETAIL</p>
          <h1 id="product-detail-title">{product?.name || 'Product detail'}</h1>
          <p className="heading-copy">Catalog product identity.</p>
        </div>
        <button className="detail-back-button" type="button" onClick={onBack}>Back to results</button>
      </div>

      {productStatus === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading product details…</p>}
      {productStatus === 'error' && productError && (
        <div className={`state-message error-message ${productError.kind}`} role="alert">
          <strong>{productError.title}</strong>
          <span>{productError.message}</span>
          {productError.kind !== 'not-found' && <button className="retry-button" type="button" onClick={retryProduct}>Try again</button>}
        </div>
      )}

      {productStatus === 'success' && product && (
        <>
          <section className="detail-section" aria-labelledby="product-identity-title">
            <div className="section-heading"><div><p className="eyebrow">CATALOG RECORD</p><h2 id="product-identity-title">Product identity</h2></div></div>
            <dl className="product-attributes">
              <div><dt>Product ID</dt><dd>{product.productId}</dd></div>
              <div><dt>Product number</dt><dd>{product.productNumber}</dd></div>
              <div><dt>Category</dt><dd>{product.categoryName || '—'}</dd></div>
              <div><dt>Subcategory</dt><dd>{product.subcategoryName || '—'}</dd></div>
              <div><dt>POC/demo USD · List price</dt><dd>{product.listPrice === null ? 'Price unavailable' : formatPocUsd(product.listPrice)}</dd></div>
            </dl>
            <button className="search-button detail-add-button" type="button" onClick={() => onAddToDraft(product)}>Add priced line to draft</button>
          </section>
          <section className="detail-section eligible-offers-section" aria-labelledby="eligible-offers-title">
            <div className="section-heading"><div><p className="eyebrow">CURRENT ELIGIBILITY</p><h2 id="eligible-offers-title">Eligible offers</h2></div></div>
            <p className="offer-eligibility-copy">Eligibility is checked for this product and quantity at the current time. Results do not establish price, discount amount, order validity, or an applied offer.</p>
            <form className="offer-quantity-form" onSubmit={checkEligibleOffers} noValidate>
              <div className="offer-quantity-field">
                <label htmlFor="eligible-offer-quantity">Line quantity</label>
                <input
                  id="eligible-offer-quantity"
                  type="text"
                  inputMode="numeric"
                  value={offerQuantity}
                  aria-invalid={!isPositiveSmallintQuantity(offerQuantity)}
                  aria-describedby={!isPositiveSmallintQuantity(offerQuantity) ? 'eligible-offer-quantity-error' : undefined}
                  onChange={(event) => changeOfferQuantity(event.target.value)}
                />
                {!isPositiveSmallintQuantity(offerQuantity) && <span className="field-error" id="eligible-offer-quantity-error" role="alert">Enter a whole number from 1 to 32767.</span>}
              </div>
              <button className="search-button" type="submit" disabled={!isPositiveSmallintQuantity(offerQuantity) || offerStatus === 'loading'}>Check offers</button>
            </form>
            {offerStatus === 'idle' && <p className="offer-state" role="status">Choose a valid quantity and check for eligible offers.</p>}
            {offerStatus === 'loading' && <p className="offer-state" role="status" aria-live="polite">Checking eligibility for quantity {requestedQuantity}…</p>}
            {offerStatus === 'error' && offerError && (
              <div className={`offer-state error-message ${offerError.kind}`} role="alert">
                <strong>{offerError.title}</strong>
                <span>{offerError.message}</span>
                {offerError.kind !== 'not-found' && <button className="retry-button" type="button" onClick={retryEligibleOffers}>Try again</button>}
              </div>
            )}
            {offerStatus === 'success' && eligibleOffers?.length === 0 && (
              <p className="offer-state" role="status">No offers are eligible for this product and quantity at this time. This does not indicate whether a line without a promotion can be submitted.</p>
            )}
            {offerStatus === 'success' && eligibleOffers?.length > 0 && (
              <ul className="eligible-offer-list" aria-label={`Offers eligible for quantity ${requestedQuantity}`}>
                {eligibleOffers.map((offer) => (
                  <li key={offer.specialOfferId}><strong>{offer.description}</strong></li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </section>
  )
}

export default ProductDetail