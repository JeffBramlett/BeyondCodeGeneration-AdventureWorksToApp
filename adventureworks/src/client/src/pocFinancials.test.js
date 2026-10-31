import assert from 'node:assert/strict'
import test from 'node:test'
import { calculatePocFinancials } from './pocFinancials.js'
import { formatPocUsd } from './pocMoney.js'

test('calculates demo tax once from aggregate merchandise subtotal', () => {
  const result = calculatePocFinancials([
    { listPrice: 0.1, quantity: 1 },
    { listPrice: 0.1, quantity: 1 },
    { listPrice: 0.1, quantity: 1 },
  ])

  assert.deepEqual(result, { available: true, merchandiseSubtotal: 0.3, demoTax: 0.02 })
})

test('rounds a half-cent tax amount away from zero after aggregation', () => {
  const result = calculatePocFinancials([{ listPrice: 0.2, quantity: 1 }])

  assert.equal(result.demoTax, 0.02)
})

test('preserves verified zero prices and shows zero tax for zero subtotal', () => {
  const result = calculatePocFinancials([{ listPrice: 0, quantity: 3 }])

  assert.deepEqual(result, { available: true, merchandiseSubtotal: 0, demoTax: 0 })
})

test('suppresses subtotal and tax when a required line price is unavailable', () => {
  const result = calculatePocFinancials([
    { listPrice: 12.5, quantity: 1 },
    { listPrice: null, quantity: 2 },
  ])

  assert.deepEqual(result, { available: false, merchandiseSubtotal: null, demoTax: null })
})

test('suppresses subtotal and tax for invalid quantities', () => {
  const result = calculatePocFinancials([{ listPrice: 12.5, quantity: 0 }])

  assert.deepEqual(result, { available: false, merchandiseSubtotal: null, demoTax: null })
})

test('does not present an empty draft as a calculated zero order', () => {
  assert.deepEqual(calculatePocFinancials([]), { available: false, merchandiseSubtotal: null, demoTax: null })
})

test('formats verified zero and demo tax as USD', () => {
  assert.equal(formatPocUsd(0), '$0.00')
  assert.equal(formatPocUsd(0.02), '$0.02')
})
