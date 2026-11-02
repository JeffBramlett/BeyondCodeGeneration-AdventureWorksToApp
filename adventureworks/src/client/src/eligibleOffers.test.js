import assert from 'node:assert/strict'
import test from 'node:test'
import { fetchEligibleOffers } from './eligibleOffers.js'

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  }
}

test('requests eligibility only for the chosen positive smallint quantity', async () => {
  const requests = []
  const fetchImpl = async (url, options) => {
    requests.push({ url, options })
    return response(200, [{ specialOfferId: 7, description: 'Seasonal offer' }])
  }

  const result = await fetchEligibleOffers(42, '32767', { fetchImpl })

  assert.equal(requests[0].url, '/api/products/42/eligible-offers?quantity=32767')
  assert.deepEqual(result.offers, [{ specialOfferId: 7, description: 'Seasonal offer' }])
})

test('does not request for an invalid quantity', async () => {
  let requestCount = 0
  const result = await fetchEligibleOffers(42, '32768', { fetchImpl: async () => { requestCount += 1 } })

  assert.equal(requestCount, 0)
  assert.equal(result.error.kind, 'validation')
})

test('accepts an empty eligible result without inferring no-offer support', async () => {
  const result = await fetchEligibleOffers(42, '1', { fetchImpl: async () => response(200, []) })

  assert.deepEqual(result.offers, [])
})

test('never exposes Offer 1 as an eligible selection', async () => {
  const result = await fetchEligibleOffers(42, '1', {
    fetchImpl: async () => response(200, [
      { specialOfferId: 1, description: 'No Discount' },
      { specialOfferId: 7, description: 'Seasonal offer' },
    ]),
  })

  assert.deepEqual(result.offers, [{ specialOfferId: 7, description: 'Seasonal offer' }])
})

test('maps validation, not-found, unavailable, and gateway responses', async (context) => {
  const cases = [
    [400, 'validation'],
    [404, 'not-found'],
    [503, 'unavailable'],
    [502, 'network'],
  ]

  for (const [status, kind] of cases) {
    await context.test(String(status), async () => {
      const result = await fetchEligibleOffers(42, '1', { fetchImpl: async () => response(status, { title: 'Request failed' }) })
      assert.equal(result.error.kind, kind)
    })
  }
})

test('maps network failures and malformed offer responses', async (context) => {
  await context.test('network failure', async () => {
    const result = await fetchEligibleOffers(42, '1', { fetchImpl: async () => { throw new Error('offline') } })
    assert.equal(result.error.kind, 'network')
  })

  await context.test('malformed DTO', async () => {
    const result = await fetchEligibleOffers(42, '1', { fetchImpl: async () => response(200, [{ specialOfferId: '7', description: 'Offer' }]) })
    assert.equal(result.error.kind, 'service')
  })

  await context.test('malformed JSON', async () => {
    const result = await fetchEligibleOffers(42, '1', { fetchImpl: async () => ({ ok: true, json: async () => { throw new SyntaxError('invalid JSON') } }) })
    assert.equal(result.error.kind, 'service')
  })
})

test('propagates cancellation and passes the signal to fetch', async () => {
  const controller = new AbortController()
  let receivedSignal
  const fetchImpl = async (_url, options) => {
    receivedSignal = options.signal
    controller.abort()
    const error = new Error('aborted')
    error.name = 'AbortError'
    throw error
  }

  await assert.rejects(fetchEligibleOffers(42, '1', { signal: controller.signal, fetchImpl }), { name: 'AbortError' })
  assert.equal(receivedSignal, controller.signal)
})