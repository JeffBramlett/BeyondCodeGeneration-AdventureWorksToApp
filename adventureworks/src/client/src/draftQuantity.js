export const MAX_DRAFT_QUANTITY = 32767

export function isPositiveSmallintQuantity(value) {
  return /^\d+$/.test(value)
    && Number(value) >= 1
    && Number(value) <= MAX_DRAFT_QUANTITY
}

export function updateDraftQuantity(lines, productId, rawValue) {
  if (!isPositiveSmallintQuantity(rawValue)) {
    return { lines, isValid: false }
  }

  const quantity = Number(rawValue)
  return {
    lines: lines.map((line) => line.productId === productId ? { ...line, quantity } : line),
    isValid: true,
  }
}
