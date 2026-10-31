import { isPositiveSmallintQuantity, MAX_DRAFT_QUANTITY } from './draftQuantity.js'
import { decimalParts } from './pocFinancials.js'

// Owner-approved local demo money rules (2026-10-30). Client values are a PREVIEW only;
// the confirmation always shows the amounts persisted and returned by the API.
export const DEMO_TAX_RATE_LABEL = '7.5%'
export const DEMO_FREIGHT = 25
const DEMO_FREIGHT_CENTS = 2500n

export const emptyOrderSelection = Object.freeze({
  customer: null,
  billToAddress: null,
  shipToAddress: null,
  shipMethod: null,
})

const FIELD_LABELS = {
  submissionToken: 'Submission',
  customerId: 'Customer',
  billToAddressId: 'Bill-to address',
  shipToAddressId: 'Ship-to address',
  shipMethodId: 'Ship method',
}

const LINE_FIELD_LABELS = {
  productId: 'Product',
  quantity: 'Quantity',
  offerId: 'Offer',
}

function isValidQuantity(quantity) {
  return Number.isInteger(quantity) && quantity >= 1 && quantity <= MAX_DRAFT_QUANTITY
}

function isValidDiscount(discountPct) {
  return typeof discountPct === 'number' && Number.isFinite(discountPct) && discountPct >= 0 && discountPct <= 1
}

function unitsToNumber(units, scale) {
  return Number(units) / (10 ** scale)
}

/**
 * Discount fraction for a draft line's chosen offer.
 * Returns 0 for "No discount", the API-provided discountPct for a chosen eligible offer,
 * or null when the discount is not known to the client (preview cannot be calculated).
 */
export function selectedDiscountPct(lineOffer) {
  const offerId = lineOffer?.offerId ?? null
  if (offerId === null) return 0
  const offer = Array.isArray(lineOffer?.offers)
    ? lineOffer.offers.find((candidate) => candidate.specialOfferId === offerId)
    : null
  return isValidDiscount(offer?.discountPct) ? offer.discountPct : null
}

export function selectedOffer(lineOffer) {
  const offerId = lineOffer?.offerId ?? null
  if (offerId === null || !Array.isArray(lineOffer?.offers)) return null
  return lineOffer.offers.find((candidate) => candidate.specialOfferId === offerId) ?? null
}

/**
 * Demo preview: line total = unitPrice * (1 - discount) * qty; subtotal = sum of line totals;
 * demo tax = 7.5% of subtotal rounded to cents half away from zero; freight = flat $25.00.
 * Uses exact decimal arithmetic so binary floating point never changes a cent.
 * @param {{ productId: number, listPrice: number|null, quantity: number, discountPct: number|null }[]} lines
 */
export function calculateOrderPreview(lines) {
  const unavailable = (reason, previewLines = []) => ({
    available: false,
    reason,
    lines: previewLines,
    subTotal: null,
    taxAmt: null,
    freight: DEMO_FREIGHT,
    totalDue: null,
  })

  if (!Array.isArray(lines) || lines.length === 0) return unavailable('empty')

  const previewLines = lines.map((line) => {
    if (!Number.isFinite(line.listPrice) || line.listPrice < 0) return { productId: line.productId, lineTotal: null, reason: 'price' }
    if (!isValidQuantity(line.quantity)) return { productId: line.productId, lineTotal: null, reason: 'quantity' }
    if (!isValidDiscount(line.discountPct)) return { productId: line.productId, lineTotal: null, reason: 'discount' }

    const price = decimalParts(line.listPrice)
    const discount = decimalParts(line.discountPct)
    const factor = (10n ** BigInt(discount.scale)) - discount.units
    const units = price.units * factor * BigInt(line.quantity)
    const scale = price.scale + discount.scale
    return { productId: line.productId, units, scale, lineTotal: unitsToNumber(units, scale), reason: null }
  })

  const firstProblem = previewLines.find((line) => line.reason)
  if (firstProblem) {
    return unavailable(firstProblem.reason, previewLines.map(({ productId, lineTotal, reason }) => ({ productId, lineTotal, reason })))
  }

  const scale = Math.max(2, ...previewLines.map((line) => line.scale))
  const subTotalUnits = previewLines.reduce((sum, line) => sum + line.units * (10n ** BigInt(scale - line.scale)), 0n)
  // 7.5% = 3/40; convert to cents and round half away from zero (subtotal is never negative).
  const taxNumerator = subTotalUnits * 3n * 100n
  const taxDenominator = 40n * (10n ** BigInt(scale))
  const taxCents = (taxNumerator + taxDenominator / 2n) / taxDenominator
  const centsScale = 10n ** BigInt(scale - 2)
  const totalUnits = subTotalUnits + (taxCents + DEMO_FREIGHT_CENTS) * centsScale

  return {
    available: true,
    reason: null,
    lines: previewLines.map(({ productId, lineTotal }) => ({ productId, lineTotal, reason: null })),
    subTotal: unitsToNumber(subTotalUnits, scale),
    taxAmt: Number(taxCents) / 100,
    freight: DEMO_FREIGHT,
    totalDue: unitsToNumber(totalUnits, scale),
  }
}

/** Build preview input lines from draft lines and per-line offer state. */
export function previewInputLines(lines, lineOffers = {}) {
  return lines.map((line) => ({
    productId: line.productId,
    listPrice: line.listPrice,
    quantity: line.quantity,
    discountPct: selectedDiscountPct(lineOffers[line.productId]),
  }))
}

/**
 * Determines whether the draft can move to Review and lists what is missing.
 * Server validation remains authoritative.
 */
export function getOrderReadiness({ lines = [], quantityInputs = {}, lineOffers = {}, selection = emptyOrderSelection } = {}) {
  const missing = []

  if (lines.length === 0) {
    missing.push({ key: 'lines', target: 'catalog', message: 'Add at least one product to the draft.' })
  }

  for (const line of lines) {
    const input = quantityInputs[line.productId] ?? String(line.quantity)
    if (!isPositiveSmallintQuantity(input)) {
      missing.push({ key: `quantity-${line.productId}`, target: 'catalog', message: `Enter a valid quantity for ${line.name}.` })
    } else if (lineOffers[line.productId]?.offerId != null
      && (lineOffers[line.productId].status === 'loading' || lineOffers[line.productId].quantity !== line.quantity)) {
      missing.push({ key: `offers-${line.productId}`, target: 'catalog', message: `Wait for the chosen offer to be rechecked for ${line.name}.` })
    }
  }

  if (!selection?.customer) missing.push({ key: 'customerId', target: 'customers', message: 'Choose a customer.' })
  if (!selection?.billToAddress) missing.push({ key: 'billToAddressId', target: 'customers', message: 'Choose a bill-to address.' })
  if (!selection?.shipToAddress) missing.push({ key: 'shipToAddressId', target: 'customers', message: 'Choose a ship-to address.' })
  if (!selection?.shipMethod) missing.push({ key: 'shipMethodId', target: 'customers', message: 'Choose a ship method.' })

  return { ready: missing.length === 0, missing }
}

/** One-line address text for summaries, e.g. "#985 · 1 Main St · Seattle · 98101". */
export function formatAddressSummary(address) {
  if (!address) return ''
  return [
    `#${address.addressId}`,
    address.addressLine1,
    address.addressLine2,
    address.city,
    address.postalCode,
  ].filter(Boolean).join(' · ')
}

export function createSubmissionToken(cryptoImpl = globalThis.crypto) {
  if (typeof cryptoImpl?.randomUUID !== 'function') throw new Error('crypto.randomUUID is not available in this browser.')
  return cryptoImpl.randomUUID()
}

/** Exact POST /api/orders request body. */
export function buildOrderPayload({ submissionToken, selection, lines, lineOffers = {} }) {
  return {
    submissionToken,
    customerId: selection.customer.customerId,
    billToAddressId: selection.billToAddress.addressId,
    shipToAddressId: selection.shipToAddress.addressId,
    shipMethodId: selection.shipMethod.shipMethodId,
    lines: lines.map((line) => ({
      productId: line.productId,
      quantity: line.quantity,
      offerId: lineOffers[line.productId]?.offerId ?? null,
    })),
  }
}

/**
 * Identity of the order content, excluding the token. The same token is reused only while the
 * reviewed content is unchanged, so a retry or re-review can be replayed by the server instead
 * of recording a duplicate order.
 */
export function orderContentKey(payload) {
  const content = { ...payload }
  delete content.submissionToken
  return JSON.stringify(content)
}

function isNonEmptyString(value) {
  return typeof value === 'string' && value.trim().length > 0
}

function isFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value)
}

function isOptionalString(value) {
  return value === null || value === undefined || typeof value === 'string'
}

/** Validates and normalizes the 200/201 POST /api/orders response. Returns null when unreadable. */
export function normalizeOrderConfirmation(data) {
  if (data === null || typeof data !== 'object') return null
  const validHeader = isNonEmptyString(data.orderReference)
    && isNonEmptyString(data.status)
    && isNonEmptyString(data.orderDate)
    && isOptionalString(data.dueDate)
    && Number.isInteger(data.customerId)
    && Number.isInteger(data.billToAddressId)
    && Number.isInteger(data.shipToAddressId)
    && Number.isInteger(data.shipMethodId)
    && isFiniteNumber(data.subTotal)
    && isFiniteNumber(data.taxAmt)
    && isFiniteNumber(data.freight)
    && isFiniteNumber(data.totalDue)
  if (!validHeader || !Array.isArray(data.lines) || data.lines.length === 0) return null

  const validLines = data.lines.every((line) => (
    Number.isInteger(line?.productId)
    && typeof line.productName === 'string'
    && Number.isInteger(line.quantity)
    && (line.offerId === null || line.offerId === undefined || Number.isInteger(line.offerId))
    && isOptionalString(line.offerDescription)
    && isFiniteNumber(line.unitPrice)
    && isFiniteNumber(line.unitPriceDiscount)
    && isFiniteNumber(line.lineTotal)
  ))
  if (!validLines) return null

  return {
    orderReference: data.orderReference,
    status: data.status,
    orderDate: data.orderDate,
    dueDate: data.dueDate ?? null,
    customerId: data.customerId,
    billToAddressId: data.billToAddressId,
    shipToAddressId: data.shipToAddressId,
    shipMethodId: data.shipMethodId,
    lines: data.lines.map((line) => ({
      productId: line.productId,
      productName: line.productName,
      quantity: line.quantity,
      offerId: line.offerId ?? null,
      offerDescription: line.offerDescription ?? null,
      unitPrice: line.unitPrice,
      unitPriceDiscount: line.unitPriceDiscount,
      lineTotal: line.lineTotal,
    })),
    subTotal: data.subTotal,
    taxAmt: data.taxAmt,
    freight: data.freight,
    totalDue: data.totalDue,
  }
}

function canonicalField(name) {
  const lower = name.toLowerCase()
  return Object.keys(FIELD_LABELS).find((field) => field.toLowerCase() === lower) ?? null
}

function canonicalLineField(name) {
  const lower = name.toLowerCase()
  return Object.keys(LINE_FIELD_LABELS).find((field) => field.toLowerCase() === lower) ?? null
}

/**
 * Maps ValidationProblemDetails.errors keys such as "customerId", "Lines[1].OfferId" or
 * "$.lines[0].quantity" to order fields and draft lines (by request line index).
 * @param {object|null} problem
 * @param {{ productId: number, name?: string }[]} lines the lines in request order
 */
export function mapOrderValidationErrors(problem, lines = []) {
  const result = { fields: {}, lines: {}, general: [], summary: [] }
  const errors = problem?.errors && typeof problem.errors === 'object' ? problem.errors : null

  if (!errors) {
    const message = problem?.detail || problem?.title || 'The order could not be validated.'
    result.general.push(message)
    result.summary.push({ label: 'Order', message })
    return result
  }

  for (const [rawKey, rawMessages] of Object.entries(errors)) {
    const messages = (Array.isArray(rawMessages) ? rawMessages : [rawMessages]).filter((message) => typeof message === 'string' && message)
    if (messages.length === 0) continue
    const key = rawKey.replace(/^\$\.?/, '')

    const field = canonicalField(key)
    if (field) {
      result.fields[field] = [...(result.fields[field] ?? []), ...messages]
      messages.forEach((message) => result.summary.push({ label: FIELD_LABELS[field], message, field }))
      continue
    }

    const lineMatch = /^lines\[(\d+)\](?:\.(\w+))?$/i.exec(key)
    const line = lineMatch ? lines[Number(lineMatch[1])] : undefined
    if (line) {
      const lineField = lineMatch[2] ? canonicalLineField(lineMatch[2]) : null
      const prefix = lineField ? `${LINE_FIELD_LABELS[lineField]}: ` : ''
      const entries = messages.map((message) => `${prefix}${message}`)
      result.lines[line.productId] = [...(result.lines[line.productId] ?? []), ...entries]
      const label = line.name ? `${line.name}` : `Line ${Number(lineMatch[1]) + 1}`
      entries.forEach((message) => result.summary.push({ label, message, productId: line.productId }))
      continue
    }

    result.general.push(...messages)
    messages.forEach((message) => result.summary.push({ label: /^lines/i.test(key) ? 'Order lines' : 'Order', message }))
  }

  if (result.summary.length === 0) {
    const message = problem?.detail || problem?.title || 'The order could not be validated.'
    result.general.push(message)
    result.summary.push({ label: 'Order', message })
  }

  return result
}

async function readJson(response) {
  try {
    return await response.json()
  } catch {
    return null
  }
}

const UNAVAILABLE_ERROR = {
  kind: 'unavailable',
  title: 'Order service unavailable',
  message: 'Nothing was recorded unless you see a confirmation. Retry uses the same submission, so the order will not be recorded twice.',
  retryable: true,
}

/**
 * POST /api/orders. Resolves to { order, replayed } or { error }.
 * Abort errors are rethrown so callers can ignore them.
 */
export async function submitOrder(payload, { signal, fetchImpl = fetch, lineNames = {} } = {}) {
  let response
  try {
    response = await fetchImpl('/api/orders', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(payload),
      signal,
    })
  } catch (error) {
    if (signal?.aborted || error?.name === 'AbortError') throw error
    return { error: { ...UNAVAILABLE_ERROR, title: 'Connection problem' } }
  }

  if (response.status === 200 || response.status === 201) {
    const order = normalizeOrderConfirmation(await readJson(response))
    if (!order) {
      return {
        error: {
          kind: 'unreadable',
          title: 'Confirmation could not be read',
          message: 'The order service responded, but its confirmation was unreadable. The order may have been recorded. Retry to fetch the confirmation for this same submission; it will not be recorded twice.',
          retryable: true,
        },
      }
    }
    return { order, replayed: response.status === 200 }
  }

  const problem = await readJson(response)

  if (response.status === 400 || response.status === 422) {
    const requestLines = payload.lines.map((line) => ({ productId: line.productId, name: lineNames[line.productId] }))
    return {
      error: {
        kind: 'validation',
        title: 'The order needs changes',
        message: 'Nothing was recorded. Review the items below, go back to edit if needed, and submit again.',
        retryable: false,
        validation: mapOrderValidationErrors(problem, requestLines),
      },
    }
  }

  if (response.status === 409) {
    return {
      error: {
        kind: 'in-progress',
        title: 'Submission still processing',
        message: 'This submission is still being processed. Wait a moment, then retry; the same submission is reused, so the order will not be recorded twice.',
        retryable: true,
      },
    }
  }

  if (response.status === 401) {
    return { error: { kind: 'unauthorized', title: 'Sign-in required', message: 'The order service requires the local Development identity. Sign in, then retry this submission.', retryable: true } }
  }

  if (response.status === 403) {
    return { error: { kind: 'forbidden', title: 'Not permitted to record orders', message: 'This identity is not allowed to record orders. Nothing was recorded.', retryable: false } }
  }

  return { error: UNAVAILABLE_ERROR }
}
