const pocUsdFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

export function formatPocUsd(amount) {
  return pocUsdFormatter.format(amount)
}

const pocPercentFormatter = new Intl.NumberFormat('en-US', {
  style: 'percent',
  minimumFractionDigits: 0,
  maximumFractionDigits: 2,
})

/** Formats a discount fraction (0.15) as a percentage ("15%"). */
export function formatPocPercent(fraction) {
  return pocPercentFormatter.format(fraction)
}
