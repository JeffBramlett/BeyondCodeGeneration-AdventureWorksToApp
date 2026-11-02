import assert from 'node:assert/strict'
import test from 'node:test'
import {
  buildOrderPayload,
  calculateOrderPreview,
  createSubmissionToken,
  getOrderReadiness,
  mapOrderValidationErrors,
  normalizeOrderConfirmation,
  orderContentKey,
  previewInputLines,
  selectedDiscountPct,
  submitOrder,
} from './orderSubmission.js'

const selection = {
  customer: { customerId: 29825, displayName: 'Example Store', customerKind: 'Store' },
  billToAddress: { addressId: 985, addressLine1: '1 Main', city: 'Seattle' },
  shipToAddress: { addressId: 986, addressLine1: '2 Main', city: 'Seattle' },
  shipMethod: { shipMethodId: 5, name: 'CARGO TRANSPORT 5' },
}

const draftLines = [
  { productId: 776, name: 'Mountain-100 Black, 42', listPrice: 3374.99, quantity: 2 },
  { productId: 707, name: 'Sport-100 Helmet, Red', listPrice: 34.99, quantity: 10 },
]

function confirmationBody(overrides = {}) {
  return {
    orderReference: 'SO75124',
    status: 'Recorded',
    orderDate: '2026-10-30T00:00:00',
    dueDate: '2026-11-11T00:00:00',
    customerId: 29825,
    billToAddressId: 985,
    shipToAddressId: 986,
    shipMethodId: 5,
    lines: [
      { productId: 776, productName: 'Mountain-100 Black, 42', quantity: 2, offerId: null, offerDescription: null, unitPrice: 3374.99, unitPriceDiscount: 0, lineTotal: 6749.98 },
    ],
    subTotal: 6749.98,
    taxAmt: 506.25,
    freight: 25,
    totalDue: 7281.23,
    ...overrides,
  }
}

function response(status, body) {
  return { ok: status >= 200 && status < 300, status, json: async () => body }
}

test('preview applies line discount, demo tax on discounted subtotal and flat freight', () => {
  const preview = calculateOrderPreview([
    { productId: 1, listPrice: 100, quantity: 3, discountPct: 0.1 },
    { productId: 2, listPrice: 0.1, quantity: 3, discountPct: 0 },
  ])

  assert.equal(preview.available, true)
  assert.deepEqual(preview.lines.map((line) => line.lineTotal), [270, 0.3])
  assert.equal(preview.subTotal, 270.3)
  // 7.5% of 270.30 = 20.2725 -> 20.27
  assert.equal(preview.taxAmt, 20.27)
  assert.equal(preview.freight, 25)
  assert.equal(preview.totalDue, 315.57)
})

test('preview rounds half-cent demo tax away from zero', () => {
  // 7.5% of 0.20 = 0.015 -> 0.02
  const preview = calculateOrderPreview([{ productId: 1, listPrice: 0.2, quantity: 1, discountPct: 0 }])
  assert.equal(preview.taxAmt, 0.02)
  assert.equal(preview.totalDue, 25.22)
})

test('preview keeps exact cents for discounted fractional prices', () => {
  const preview = calculateOrderPreview([{ productId: 1, listPrice: 3374.99, quantity: 2, discountPct: 0.02 }])
  // 3374.99 * 0.98 * 2 = 6614.9804
  assert.equal(preview.subTotal, 6614.9804)
  // 7.5% = 496.12353 -> 496.12
  assert.equal(preview.taxAmt, 496.12)
  assert.equal(preview.totalDue, 7136.1004)
})

test('preview is unavailable when a discount is unknown, price missing or draft empty', () => {
  assert.equal(calculateOrderPreview([]).available, false)
  const unknownDiscount = calculateOrderPreview([{ productId: 1, listPrice: 10, quantity: 1, discountPct: null }])
  assert.equal(unknownDiscount.available, false)
  assert.equal(unknownDiscount.reason, 'discount')
  assert.equal(unknownDiscount.totalDue, null)
  assert.equal(calculateOrderPreview([{ productId: 1, listPrice: null, quantity: 1, discountPct: 0 }]).reason, 'price')
  assert.equal(calculateOrderPreview([{ productId: 1, listPrice: 1, quantity: 0, discountPct: 0 }]).reason, 'quantity')
})

test('selected discount is zero for no offer and comes from the API offer otherwise', () => {
  assert.equal(selectedDiscountPct(undefined), 0)
  assert.equal(selectedDiscountPct({ offerId: null, offers: [] }), 0)
  assert.equal(selectedDiscountPct({ offerId: 7, offers: [{ specialOfferId: 7, description: 'x', discountPct: 0.15 }] }), 0.15)
  assert.equal(selectedDiscountPct({ offerId: 7, offers: [{ specialOfferId: 7, description: 'x' }] }), null)
  assert.equal(selectedDiscountPct({ offerId: 7, offers: [] }), null)
  assert.deepEqual(
    previewInputLines([draftLines[0]], { 776: { offerId: null } }),
    [{ productId: 776, listPrice: 3374.99, quantity: 2, discountPct: 0 }],
  )
})

test('readiness lists every missing item and becomes ready when complete', () => {
  const empty = getOrderReadiness({})
  assert.equal(empty.ready, false)
  assert.deepEqual(empty.missing.map((item) => item.key), ['lines', 'customerId', 'billToAddressId', 'shipToAddressId', 'shipMethodId'])

  const invalid = getOrderReadiness({
    lines: draftLines,
    quantityInputs: { 776: '0', 707: '10' },
    lineOffers: { 707: { status: 'loading', offerId: 3, quantity: 10 } },
    selection,
  })
  assert.deepEqual(invalid.missing.map((item) => item.key), ['quantity-776', 'offers-707'])

  const staleQuantity = getOrderReadiness({
    lines: draftLines,
    lineOffers: { 707: { status: 'success', offerId: 3, quantity: 5 }, 776: { status: 'loading', offerId: null, quantity: 1 } },
    selection,
  })
  assert.deepEqual(staleQuantity.missing.map((item) => item.key), ['offers-707'])

  const ready = getOrderReadiness({ lines: draftLines, quantityInputs: { 776: '2', 707: '10' }, lineOffers: {}, selection })
  assert.deepEqual(ready, { ready: true, missing: [] })
})

test('builds the exact POST /api/orders payload with null offer for no discount', () => {
  const payload = buildOrderPayload({
    submissionToken: 'token-1',
    selection,
    lines: draftLines,
    lineOffers: { 776: { offerId: 7 }, 707: { offerId: null } },
  })

  assert.deepEqual(payload, {
    submissionToken: 'token-1',
    customerId: 29825,
    billToAddressId: 985,
    shipToAddressId: 986,
    shipMethodId: 5,
    lines: [
      { productId: 776, quantity: 2, offerId: 7 },
      { productId: 707, quantity: 10, offerId: null },
    ],
  })
})

test('content key ignores the token but changes with order content', () => {
  const a = buildOrderPayload({ submissionToken: 'a', selection, lines: draftLines })
  const b = buildOrderPayload({ submissionToken: 'b', selection, lines: draftLines })
  const c = buildOrderPayload({ submissionToken: 'a', selection, lines: [{ ...draftLines[0], quantity: 3 }] })
  assert.equal(orderContentKey(a), orderContentKey(b))
  assert.notEqual(orderContentKey(a), orderContentKey(c))
})

test('creates submission tokens with crypto.randomUUID', () => {
  assert.equal(createSubmissionToken({ randomUUID: () => 'uuid-1' }), 'uuid-1')
  assert.throws(() => createSubmissionToken({}))
})

test('normalizes a valid confirmation and rejects unreadable ones', () => {
  const order = normalizeOrderConfirmation(confirmationBody())
  assert.equal(order.orderReference, 'SO75124')
  assert.equal(order.lines[0].offerDescription, null)
  assert.equal(order.totalDue, 7281.23)

  assert.equal(normalizeOrderConfirmation(null), null)
  assert.equal(normalizeOrderConfirmation(confirmationBody({ orderReference: '' })), null)
  assert.equal(normalizeOrderConfirmation(confirmationBody({ totalDue: '7281.23' })), null)
  assert.equal(normalizeOrderConfirmation(confirmationBody({ lines: [] })), null)
  assert.equal(normalizeOrderConfirmation(confirmationBody({ lines: [{ productId: 1 }] })), null)
})

test('maps validation keys to fields, lines by request index, and general errors', () => {
  const mapped = mapOrderValidationErrors({
    errors: {
      CustomerId: ['Customer was not found.'],
      'lines[1].offerId': ['Offer is not eligible.'],
      '$.Lines[0].Quantity': ['Quantity too large.'],
      lines: ['At least one line is required.'],
      somethingElse: ['Unexpected.'],
    },
  }, [{ productId: 776, name: 'Bike' }, { productId: 707, name: 'Helmet' }])

  assert.deepEqual(mapped.fields, { customerId: ['Customer was not found.'] })
  assert.deepEqual(mapped.lines, { 707: ['Offer: Offer is not eligible.'], 776: ['Quantity: Quantity too large.'] })
  assert.deepEqual(mapped.general, ['At least one line is required.', 'Unexpected.'])
  assert.equal(mapped.summary.length, 5)
  assert.deepEqual(mapped.summary[1], { label: 'Helmet', message: 'Offer: Offer is not eligible.', productId: 707 })
})

test('maps a problem without errors to a general message', () => {
  const mapped = mapOrderValidationErrors({ title: 'Bad request' })
  assert.deepEqual(mapped.general, ['Bad request'])
  assert.deepEqual(mapped.summary, [{ label: 'Order', message: 'Bad request' }])
})

test('submitOrder posts JSON and returns created and replayed confirmations', async () => {
  const requests = []
  const payload = buildOrderPayload({ submissionToken: 't', selection, lines: [draftLines[0]] })
  const created = await submitOrder(payload, {
    fetchImpl: async (url, options) => {
      requests.push({ url, options })
      return response(201, confirmationBody())
    },
  })
  assert.equal(requests[0].url, '/api/orders')
  assert.equal(requests[0].options.method, 'POST')
  assert.deepEqual(JSON.parse(requests[0].options.body), payload)
  assert.equal(created.order.orderReference, 'SO75124')
  assert.equal(created.replayed, false)

  const replayed = await submitOrder(payload, { fetchImpl: async () => response(200, confirmationBody()) })
  assert.equal(replayed.replayed, true)
})

test('submitOrder maps error statuses to retry semantics', async () => {
  const payload = buildOrderPayload({ submissionToken: 't', selection, lines: draftLines })
  const run = (status, body = {}) => submitOrder(payload, { fetchImpl: async () => response(status, body), lineNames: { 707: 'Helmet' } })

  const validation = await run(422, { errors: { 'lines[1].offerId': ['Not eligible.'] } })
  assert.equal(validation.error.kind, 'validation')
  assert.equal(validation.error.retryable, false)
  assert.deepEqual(validation.error.validation.lines, { 707: ['Offer: Not eligible.'] })
  assert.equal((await run(400, { errors: { shipMethodId: ['Required.'] } })).error.validation.fields.shipMethodId[0], 'Required.')

  assert.equal((await run(409)).error.kind, 'in-progress')
  assert.equal((await run(409)).error.retryable, true)
  assert.equal((await run(401)).error.kind, 'unauthorized')
  assert.equal((await run(403)).error.kind, 'forbidden')
  assert.equal((await run(503)).error.kind, 'unavailable')
  assert.equal((await run(503)).error.retryable, true)
  assert.equal((await run(500)).error.kind, 'unavailable')
  assert.equal((await run(201, { orderReference: 'x' })).error.kind, 'unreadable')
})

test('submitOrder treats network failures as unavailable and rethrows aborts', async () => {
  const payload = buildOrderPayload({ submissionToken: 't', selection, lines: draftLines })
  const network = await submitOrder(payload, { fetchImpl: async () => { throw new TypeError('Failed to fetch') } })
  assert.equal(network.error.kind, 'unavailable')
  assert.match(network.error.message, /Nothing was recorded unless you see a confirmation/)

  const abort = Object.assign(new Error('aborted'), { name: 'AbortError' })
  await assert.rejects(submitOrder(payload, { fetchImpl: async () => { throw abort } }), { name: 'AbortError' })
})
