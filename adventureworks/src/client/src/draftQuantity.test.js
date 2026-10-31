import assert from 'node:assert/strict'
import test from 'node:test'
import { isPositiveSmallintQuantity, updateDraftQuantity } from './draftQuantity.js'

const initialLines = [{ productId: 7, name: 'Test product', quantity: 3 }]

test('accepts the positive SQL smallint endpoints', () => {
  assert.equal(isPositiveSmallintQuantity('1'), true)
  assert.equal(isPositiveSmallintQuantity('32767'), true)
})

test('rejects values outside the positive SQL smallint range', () => {
  for (const value of ['0', '-1', '1.5', '32768', 'abc', '9007199254740992']) {
    assert.equal(isPositiveSmallintQuantity(value), false, `Expected ${value} to be rejected`)
  }
})

test('invalid edits preserve the last valid draft quantity', () => {
  const result = updateDraftQuantity(initialLines, 7, '32768')
  assert.equal(result.isValid, false)
  assert.equal(result.lines, initialLines)
  assert.equal(result.lines[0].quantity, 3)
})

test('valid edits update only the selected product quantity', () => {
  const lines = [...initialLines, { productId: 8, name: 'Other product', quantity: 2 }]
  const result = updateDraftQuantity(lines, 7, '32767')
  assert.equal(result.isValid, true)
  assert.deepEqual(result.lines.map((line) => line.quantity), [32767, 2])
})
