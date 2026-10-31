import { useEffect, useState } from 'react'

const initialRequest = {
  page: 1,
  pageSize: 25,
  search: '',
  sortBy: 'displayName',
  sortDirection: 'asc',
}

function problemMessage(problem) {
  if (problem?.errors && typeof problem.errors === 'object') {
    return Object.values(problem.errors).flat().filter(Boolean).join(' ')
  }

  return problem?.detail || problem?.title || 'The customer request could not be completed.'
}

const emptySelection = { customer: null, billToAddress: null, shipToAddress: null, shipMethod: null }

// Local contained demo (owner decision 2026-10-30): the UI is served by the local API from wwwroot,
// so the lookup is available in built bundles too. Pass `selection` + `onSelectionChange` to use it
// as the order customer/address/ship-method step; without them it works as a standalone lookup.
function CustomerSelector({ selection, onSelectionChange, onContinue }) {
  const [localSelection, setLocalSelection] = useState(emptySelection)
  const controlled = selection !== undefined && typeof onSelectionChange === 'function'

  return (
    <CustomerLookup
      selection={controlled ? selection : localSelection}
      onSelectionChange={controlled ? onSelectionChange : setLocalSelection}
      onContinue={controlled ? onContinue : undefined}
    />
  )
}

function CustomerLookup({ selection, onSelectionChange, onContinue }) {
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
  const selectedCustomer = selection.customer
  const selectedCustomerId = selectedCustomer?.customerId ?? null
  const [orderOptions, setOrderOptions] = useState(null)
  const [optionsStatus, setOptionsStatus] = useState(selectedCustomer ? 'loading' : 'idle')
  const [optionsError, setOptionsError] = useState(null)
  const [optionsRetryToken, setOptionsRetryToken] = useState(0)
  const billingAddressId = selection.billToAddress?.addressId ?? null
  const shippingAddressId = selection.shipToAddress?.addressId ?? null
  const shipMethodId = selection.shipMethod?.shipMethodId ?? null

  useEffect(() => {
    const controller = new AbortController()
    const params = new URLSearchParams({
      page: String(request.page),
      pageSize: String(request.pageSize),
      search: request.search,
      sortBy: request.sortBy,
      sortDirection: request.sortDirection,
    })

    async function loadCustomers() {
      setStatus('loading')
      setError(null)
      setPageData(null)

      let response
      try {
        response = await fetch(`/api/customers?${params}`, { signal: controller.signal })
      } catch (fetchError) {
        if (fetchError.name !== 'AbortError') {
          setError({ kind: 'network', message: 'The customer service could not be reached. Check your connection and try again.' })
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
          message: problemMessage(problem) || 'The customer service is temporarily unavailable.',
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
          kind: response.status === 400 ? 'validation' : response.status === 502 ? 'network' : 'service',
          message: response.status === 502
            ? 'The customer service could not be reached. Check the connection and try again.'
            : problemMessage(problem),
        })
        setStatus('error')
        return
      }

      try {
        const result = await response.json()
        const validItems = Array.isArray(result.items) && result.items.every((customer) => (
          Number.isInteger(customer.customerId)
          && typeof customer.displayName === 'string'
          && typeof customer.customerKind === 'string'
        ))
        if (!validItems) throw new Error('Invalid customer response')
        setPageData(result)
        setStatus('success')
      } catch {
        setError({ kind: 'service', message: 'The customer service returned an unreadable response. Try again.' })
        setStatus('error')
      }
    }

    loadCustomers()
    return () => controller.abort()
  }, [request, retryToken])

  useEffect(() => {
    if (selectedCustomerId === null) return undefined

    const controller = new AbortController()

    async function loadOrderOptions() {
      setOptionsStatus('loading')
      setOptionsError(null)
      setOrderOptions(null)

      let response
      try {
        response = await fetch(`/api/customers/${selectedCustomerId}/order-options`, { signal: controller.signal })
      } catch (fetchError) {
        if (fetchError.name !== 'AbortError' && !controller.signal.aborted) {
          setOptionsError({ kind: 'network', title: 'Connection problem', message: 'Customer order options could not be reached. Check the connection and try again.' })
          setOptionsStatus('error')
        }
        return
      }

      if (!response.ok) {
        let problem
        try {
          problem = await response.json()
        } catch {
          problem = null
        }
        if (controller.signal.aborted) return

        const detail = problemMessage(problem)
        const error = response.status === 503
          ? { kind: 'unavailable', title: 'Customer order options unavailable', message: detail }
          : response.status === 404
            ? { kind: 'not-found', title: 'Customer not found', message: 'The API did not find this customer.' }
            : response.status === 502
              ? { kind: 'network', title: 'Connection problem', message: 'Customer order options could not be reached. Check the connection and try again.' }
              : { kind: 'service', title: 'Order-options request failed', message: detail }
        setOptionsError(error)
        setOptionsStatus('error')
        return
      }

      try {
        const result = await response.json()
        const validAddresses = Array.isArray(result.addresses) && result.addresses.every((address) => (
          Number.isInteger(address.addressId)
          && typeof address.addressLine1 === 'string'
          && (address.addressLine2 === undefined || address.addressLine2 === null || typeof address.addressLine2 === 'string')
          && typeof address.city === 'string'
          && (address.postalCode === undefined || address.postalCode === null || typeof address.postalCode === 'string')
          && Array.isArray(address.addressTypes)
          && address.addressTypes.every((type) => typeof type === 'string')
        ))
        const validShipMethods = Array.isArray(result.shipMethods) && result.shipMethods.every((method) => (
          Number.isInteger(method.shipMethodId) && typeof method.name === 'string'
        ))
        if (!validAddresses || !validShipMethods) throw new Error('Invalid customer order-options response')
        if (controller.signal.aborted) return
        setOrderOptions({
          addresses: result.addresses.map(({ addressId, addressLine1, addressLine2, city, postalCode, addressTypes }) => ({
            addressId,
            addressLine1,
            addressLine2: addressLine2 ?? null,
            city,
            postalCode: postalCode ?? null,
            addressTypes,
          })),
          shipMethods: result.shipMethods.map(({ shipMethodId, name }) => ({ shipMethodId, name })),
        })
        setOptionsStatus('success')
      } catch {
        if (!controller.signal.aborted) {
          setOptionsError({ kind: 'service', title: 'Order-options response unreadable', message: 'The API returned an unreadable customer order-options response. Try again.' })
          setOptionsStatus('error')
        }
      }
    }

    loadOrderOptions()
    return () => controller.abort()
  }, [selectedCustomerId, optionsRetryToken])

  function submitSearch(event) {
    event.preventDefault()
    const search = searchInput.trim()
    if (searchInput.length > 100) {
      setValidationMessage('Search must be 100 characters or fewer.')
      return
    }
    if (searchInput.length > 0 && search.length === 0) {
      setValidationMessage('Search must contain a non-whitespace character.')
      return
    }

    setValidationMessage('')
    setRequest({ page: 1, pageSize, search, sortBy, sortDirection })
  }

  function changePage(nextPage) {
    if (nextPage < 1 || nextPage > 10_000) return
    setRequest((current) => ({ ...current, page: nextPage }))
  }

  function chooseCustomer(customer) {
    if (selectedCustomerId === customer.customerId) return
    setOrderOptions(null)
    setOptionsError(null)
    setOptionsStatus('loading')
    onSelectionChange({
      customer: { customerId: customer.customerId, displayName: customer.displayName, customerKind: customer.customerKind },
      billToAddress: null,
      shipToAddress: null,
      shipMethod: null,
    })
  }

  function chooseOption(property, value) {
    onSelectionChange({ ...selection, [property]: value })
  }

  const selectionComplete = Boolean(selectedCustomer && billingAddressId !== null && shippingAddressId !== null && shipMethodId !== null)

  function formatAddress(address) {
    return [
      `#${address.addressId}`,
      address.addressLine1,
      address.addressLine2,
      address.city,
      address.postalCode,
      address.addressTypes.join(', '),
    ].filter(Boolean).join(' · ')
  }

  const errorTitle = error?.kind === 'unavailable'
    ? 'Customer service unavailable'
    : error?.kind === 'network'
      ? 'Connection problem'
      : error?.kind === 'validation'
        ? 'Search could not be applied'
        : 'Customer request failed'

  return (
    <>
      <section className="page-heading">
        <div><p className="eyebrow">ADVENTUREWORKS 2022 <span>·</span> CUSTOMERS</p><h1>Customer lookup</h1><p className="heading-copy">Find a customer by ID or store name, then choose bill-to, ship-to, and ship method for the order.</p></div>
      </section>

      <aside className="local-only-banner" aria-label="Local demo notice">
        <strong>Local demo</strong>
        <span>Customer data comes from the AdventureWorks test copy through the local API. Not a production sign-in.</span>
      </aside>

      <section className="catalog-panel" aria-labelledby="customer-controls-title">
        <div className="section-heading">
          <div><p className="eyebrow">CUSTOMER SELECTION</p><h2 id="customer-controls-title">Search and refine</h2></div>
        </div>
        <form className="catalog-form" onSubmit={submitSearch} noValidate>
          <div className="search-field">
            <label htmlFor="customer-search">Customer ID or store name</label>
            <input
              id="customer-search"
              type="search"
              maxLength={101}
              value={searchInput}
              onChange={(event) => {
                setSearchInput(event.target.value)
                if (validationMessage) setValidationMessage('')
              }}
              aria-invalid={Boolean(validationMessage)}
              aria-describedby={validationMessage ? 'customer-search-validation' : undefined}
            />
            {validationMessage && <span id="customer-search-validation" className="field-error" role="alert">{validationMessage}</span>}
          </div>
          <div className="select-field">
            <label htmlFor="customer-sort">Sort by</label>
            <select id="customer-sort" value={sortBy} onChange={(event) => setSortBy(event.target.value)}>
              <option value="displayName">Display name</option>
              <option value="customerId">Customer ID</option>
              <option value="customerKind">Customer kind</option>
            </select>
          </div>
          <div className="select-field">
            <label htmlFor="customer-direction">Direction</label>
            <select id="customer-direction" value={sortDirection} onChange={(event) => setSortDirection(event.target.value)}>
              <option value="asc">Ascending</option>
              <option value="desc">Descending</option>
            </select>
          </div>
          <div className="select-field">
            <label htmlFor="customer-page-size">Rows</label>
            <select id="customer-page-size" value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))}>
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
              <option value={100}>100</option>
            </select>
          </div>
          <button className="search-button" type="submit" disabled={status === 'loading'}>Search</button>
        </form>
      </section>

      <section className="results-section" aria-labelledby="customer-results-title">
        <div className="results-heading">
          <div><p className="eyebrow">RESULTS</p><h2 id="customer-results-title">Customers</h2></div>
          {status === 'success' && pageData && <span className="result-page">Page {pageData.page} · {pageData.pageSize} per page</span>}
        </div>
        {status === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading customers…</p>}
        {status === 'error' && error && (
          <div className={`state-message error-message ${error.kind}`} role="alert">
            <strong>{errorTitle}</strong>
            <span>{error.message}</span>
            <button className="retry-button" type="button" onClick={() => setRetryToken((current) => current + 1)}>Try again</button>
          </div>
        )}
        {status === 'success' && pageData?.items.length === 0 && <p className="state-message">No customers match this search.</p>}
        {status === 'success' && pageData?.items.length > 0 && (
          <>
            <div className="table-scroll customer-table" role="region" aria-label="Customer results" tabIndex="0">
              <table>
                <thead><tr><th scope="col"><span className="visually-hidden">Customer selection</span></th><th scope="col">Customer ID</th><th scope="col">Display name</th><th scope="col">Customer kind</th></tr></thead>
                <tbody>
                  {pageData.items.map((customer) => (
                      <tr key={customer.customerId} className={selectedCustomer?.customerId === customer.customerId ? 'selected-customer-row' : undefined}>
                        <td><button className="customer-select-button" type="button" aria-pressed={selectedCustomer?.customerId === customer.customerId} onClick={() => chooseCustomer(customer)}>{selectedCustomer?.customerId === customer.customerId ? 'Selected' : 'Select'}</button></td>
                      <td>{customer.customerId}</td>
                      <td className="product-name">{customer.displayName}</td>
                      <td>{customer.customerKind}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <nav className="pagination" aria-label="Customer pages">
              <button type="button" onClick={() => changePage(pageData.page - 1)} disabled={pageData.page <= 1 || status === 'loading'}>Previous</button>
              <span aria-live="polite">Page {pageData.page}</span>
              <button type="button" onClick={() => changePage(pageData.page + 1)} disabled={!pageData.hasMore || pageData.page >= 10_000 || status === 'loading'}>Next</button>
            </nav>
          </>
        )}
      </section>

      {selectedCustomer && (
        <section className="order-options-section" aria-labelledby="order-options-title" aria-busy={optionsStatus === 'loading'}>
          <div className="results-heading">
            <div><p className="eyebrow">ORDER OPTIONS</p><h2 id="order-options-title">{selectedCustomer.displayName} · #{selectedCustomer.customerId}</h2></div>
          </div>
          {optionsStatus === 'loading' && <p className="state-message" role="status" aria-live="polite">Loading verified addresses and ship methods…</p>}
          {optionsStatus === 'error' && optionsError && (
            <div className={`state-message error-message ${optionsError.kind}`} role="alert">
              <strong>{optionsError.title}</strong>
              <span>{optionsError.message}</span>
              {optionsError.kind !== 'not-found' && <button className="retry-button" type="button" onClick={() => setOptionsRetryToken((current) => current + 1)}>Try again</button>}
            </div>
          )}
          {optionsStatus === 'success' && orderOptions?.addresses.length === 0 && (
            <p className="state-message empty-addresses" role="status">No verified customer-linked addresses were returned. Address selection and order review cannot continue.</p>
          )}
          {optionsStatus === 'success' && orderOptions && orderOptions.addresses.length > 0 && (
            <div className="order-options-content">
              <div className="address-choice-grid">
                <fieldset className="option-fieldset">
                  <legend>Bill-to address</legend>
                  {orderOptions.addresses.map((address) => (
                    <label className="address-option" key={`billing-${address.addressId}`}>
                      <input type="radio" name="billing-address" value={address.addressId} checked={billingAddressId === address.addressId} onChange={() => chooseOption('billToAddress', address)} />
                      <span>{formatAddress(address)}</span>
                    </label>
                  ))}
                </fieldset>
                <fieldset className="option-fieldset">
                  <legend>Ship-to address</legend>
                  {orderOptions.addresses.map((address) => (
                    <label className="address-option" key={`shipping-${address.addressId}`}>
                      <input type="radio" name="shipping-address" value={address.addressId} checked={shippingAddressId === address.addressId} onChange={() => chooseOption('shipToAddress', address)} />
                      <span>{formatAddress(address)}</span>
                    </label>
                  ))}
                </fieldset>
              </div>
              <fieldset className="option-fieldset ship-method-fieldset">
                <legend>Ship method</legend>
                {orderOptions.shipMethods.length === 0
                  ? <p className="state-message">No ship methods were returned.</p>
                  : orderOptions.shipMethods.map((method) => (
                    <label className="ship-method-option" key={method.shipMethodId}>
                      <input type="radio" name="ship-method" value={method.shipMethodId} checked={shipMethodId === method.shipMethodId} onChange={() => chooseOption('shipMethod', method)} />
                      <span><strong>{method.name}</strong><small>Ship method ID {method.shipMethodId}</small></span>
                    </label>
                  ))}
              </fieldset>
              <p className="local-selection-note" role="status">
                {selectionComplete
                  ? 'Customer, bill-to, ship-to, and ship method are kept with the current order draft.'
                  : 'Choose a bill-to address, ship-to address, and ship method for the order.'}
              </p>
              {onContinue && (
                <div className="selection-continue">
                  <button className="search-button" type="button" onClick={onContinue}>{selectionComplete ? 'Continue to order draft' : 'Back to order draft'}</button>
                </div>
              )}
            </div>
          )}
        </section>
      )}
    </>
  )
}

export default CustomerSelector