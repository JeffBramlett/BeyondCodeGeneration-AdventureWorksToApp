import assert from 'node:assert/strict'
import test from 'node:test'
import { fetchOrderDetail, fetchOrderPage, orderLinePageBounds, orderPageBounds, validateOrderSearch } from './orders.js'

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  }
}

const referenceCriteria = { mode: 'reference', orderReference: ' SO123 ', fromDate: '', toDate: '' }
const dateCriteria = { mode: 'dateRange', orderReference: '', fromDate: '2024-02-01', toDate: '2024-03-02' }
const orderPage = { items: [{ orderReference: 'SO123', orderDate: '2024-01-02T03:04:05' }], page: 1, pageSize: 25, hasMore: false }
const orderDetail = { orderReference: 'SO123', orderDate: '2024-01-02T03:04:05', lines: [{ productId: 7, orderQty: 3 }], linePage: 1, linePageSize: 50, hasMore: false }

test('validates exact reference or complete inclusive date ranges up to 31 calendar days', () => {
  assert.equal(validateOrderSearch(referenceCriteria), '')
  assert.equal(validateOrderSearch({ ...referenceCriteria, orderReference: ' ' }), 'Enter an exact order reference.')
  assert.equal(validateOrderSearch(dateCriteria), '')
  assert.equal(validateOrderSearch({ ...dateCriteria, toDate: '2024-03-03' }), 'Choose an inclusive date range of 31 calendar days or fewer.')
  assert.equal(validateOrderSearch({ ...dateCriteria, toDate: '2024-01-31' }), 'The start date must be on or before the end date.')
  assert.equal(validateOrderSearch({ ...dateCriteria, fromDate: '2023-02-29' }), 'Enter a complete, valid start and end date.')
})

test('uses approved page-size defaults and maxima', () => {
  assert.deepEqual(orderPageBounds, { defaultPageSize: 25, maxPageSize: 100 })
  assert.deepEqual(orderLinePageBounds, { defaultPageSize: 50, maxPageSize: 100 })
})

test('requests exact reference with bounded default pagination', async () => {
  const requests = []
  const result = await fetchOrderPage(referenceCriteria, { fetchImpl: async (url, options) => { requests.push({ url, options }); return response(200, orderPage) } })
  assert.equal(requests[0].url, '/api/orders?page=1&pageSize=25&orderReference=SO123')
  assert.equal(requests[0].options.method, undefined)
  assert.deepEqual(result.page, orderPage)
})

test('requests a complete date range and does not send order reference mode parameters', async () => {
  const requests = []
  await fetchOrderPage(dateCriteria, { page: 2, pageSize: 100, fetchImpl: async (url) => { requests.push(url); return response(200, { ...orderPage, page: 2, pageSize: 100 }) } })
  assert.equal(requests[0], '/api/orders?page=2&pageSize=100&fromDate=2024-02-01&toDate=2024-03-02')
})

test('requests detail lines with bounded default pagination and encoded reference', async () => {
  const requests = []
  const result = await fetchOrderDetail('SO / 123', { fetchImpl: async (url) => { requests.push(url); return response(200, orderDetail) } })
  assert.equal(requests[0], '/api/orders/SO%20%2F%20123?linePage=1&linePageSize=50')
  assert.deepEqual(result.detail, orderDetail)
})

test('accepts serialized API search and detail DTOs using the client wire-contract names', async () => {
  const serializedSearchResponse = '{"items":[{"orderReference":"SO123","orderDate":"2024-01-02T03:04:05"}],"page":1,"pageSize":25,"hasMore":false}'
  const serializedDetailResponse = '{"orderReference":"SO123","orderDate":"2024-01-02T03:04:05","lines":[{"productId":7,"orderQty":3}],"linePage":1,"linePageSize":50,"hasMore":false}'

  const page = await fetchOrderPage(referenceCriteria, {
    fetchImpl: async () => response(200, JSON.parse(serializedSearchResponse)),
  })
  const detail = await fetchOrderDetail('SO123', {
    fetchImpl: async () => response(200, JSON.parse(serializedDetailResponse)),
  })

  assert.deepEqual(page.page, orderPage)
  assert.deepEqual(detail.detail, orderDetail)
})

test('rejects invalid criteria and page sizes without issuing a request', async () => {
  let count = 0
  const fetchImpl = async () => { count += 1; return response(200, orderPage) }
  assert.equal((await fetchOrderPage(dateCriteria, { pageSize: 101, fetchImpl })).error.kind, 'validation')
  assert.equal((await fetchOrderPage(referenceCriteria, { page: 10_001, fetchImpl })).error.kind, 'validation')
  assert.equal((await fetchOrderPage({ ...referenceCriteria, orderReference: '' }, { fetchImpl })).error.kind, 'validation')
  assert.equal((await fetchOrderDetail('SO123', { linePageSize: 101, fetchImpl })).error.kind, 'validation')
  assert.equal((await fetchOrderDetail('SO123', { linePage: 10_001, fetchImpl })).error.kind, 'validation')
  assert.equal(count, 0)
})

test('maps unauthorized, forbidden and unavailable responses to explicit states', async (context) => {
  for (const [status, kind] of [[401, 'unauthorized'], [403, 'forbidden'], [503, 'unavailable']]) {
    await context.test(String(status), async () => {
      const result = await fetchOrderPage(referenceCriteria, { fetchImpl: async () => response(status, { title: 'server detail not displayed' }) })
      assert.equal(result.error.kind, kind)
      assert.equal(result.error.message.includes('server detail'), false)
    })
  }
})

test('rejects malformed payloads and strips every response field outside the approved display allowlist', async () => {
  const badPage = await fetchOrderPage(referenceCriteria, { fetchImpl: async () => response(200, { ...orderPage, items: [{ orderReference: 'SO123' }] }) })
  assert.equal(badPage.error.kind, 'service')
  const badDetail = await fetchOrderDetail('SO123', { fetchImpl: async () => response(200, { ...orderDetail, lines: [{ productId: 7, orderQty: 'many' }] }) })
  assert.equal(badDetail.error.kind, 'service')

  const pageWithExtraFields = await fetchOrderPage(referenceCriteria, {
    fetchImpl: async () => response(200, { ...orderPage, items: [{ ...orderPage.items[0], customerName: 'Private', totalDue: 999 }] }),
  })
  assert.deepEqual(pageWithExtraFields.page.items, [{ orderReference: 'SO123', orderDate: '2024-01-02T03:04:05' }])

  const detailWithExtraFields = await fetchOrderDetail('SO123', {
    fetchImpl: async () => response(200, { ...orderDetail, customerName: 'Private', orderDate: '2024-01-02T03:04:05', lines: [{ productId: 7, orderQty: 3, unitPrice: 99 }] }),
  })
  assert.deepEqual(detailWithExtraFields.detail, orderDetail)
})

test('propagates cancellation and maps network failure', async () => {
  const controller = new AbortController()
  const error = new Error('aborted')
  error.name = 'AbortError'
  await assert.rejects(fetchOrderPage(referenceCriteria, { signal: controller.signal, fetchImpl: async () => { throw error } }), { name: 'AbortError' })
  const offline = await fetchOrderDetail('SO123', { fetchImpl: async () => { throw new Error('offline') } })
  assert.equal(offline.error.kind, 'network')
})
