export const POC_TAX_RATE = 0.075

export function decimalParts(value) {
  const [coefficient, exponentText] = value.toString().toLowerCase().split('e')
  const exponent = exponentText ? Number(exponentText) : 0
  const [whole, fraction = ''] = coefficient.split('.')
  const digits = `${whole}${fraction}`
  const scale = fraction.length - exponent

  return scale >= 0
    ? { units: BigInt(digits), scale }
    : { units: BigInt(digits) * (10n ** BigInt(-scale)), scale: 0 }
}

export function calculatePocFinancials(lines) {
  if (lines.length === 0 || lines.some((line) => (
    !Number.isFinite(line.listPrice)
    || !Number.isInteger(line.quantity)
    || line.quantity < 1
    || line.quantity > 32767
  ))) {
    return { available: false, merchandiseSubtotal: null, demoTax: null }
  }

  const lineParts = lines.map((line) => ({
    ...decimalParts(line.listPrice),
    quantity: BigInt(line.quantity),
  }))
  const scale = Math.max(...lineParts.map((line) => line.scale))
  const subtotalUnits = lineParts.reduce((subtotal, line) => (
    subtotal + line.units * line.quantity * (10n ** BigInt(scale - line.scale))
  ), 0n)
  const taxNumerator = subtotalUnits * 3n * 100n
  const taxDenominator = 40n * (10n ** BigInt(scale))
  const taxInCents = (taxNumerator + taxDenominator / 2n) / taxDenominator
  const merchandiseSubtotal = Number(subtotalUnits) / (10 ** scale)

  return {
    available: true,
    merchandiseSubtotal,
    demoTax: Number(taxInCents) / 100,
  }
}
