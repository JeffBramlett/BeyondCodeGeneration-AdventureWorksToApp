import { useEffect } from 'react'
import { fetchEligibleOffers } from './eligibleOffers.js'

/**
 * Renders nothing. Loads eligible offers for one draft line at its current quantity and reports
 * the outcome to App-level state, so offers stay current while the user is on any view.
 * A quantity change or retry aborts the previous request.
 */
function LineOfferLoader({ productId, quantity, retryToken, onLoading, onResult }) {
  useEffect(() => {
    const controller = new AbortController()

    async function loadOffers() {
      onLoading(productId, quantity)
      try {
        const result = await fetchEligibleOffers(productId, quantity, { signal: controller.signal })
        if (!controller.signal.aborted) onResult(productId, quantity, result)
      } catch (error) {
        if (!controller.signal.aborted && error?.name !== 'AbortError') {
          onResult(productId, quantity, { error: { kind: 'service', title: 'Offers unavailable', message: 'Eligible offers could not be loaded. Try again.' } })
        }
      }
    }

    loadOffers()
    return () => controller.abort()
  }, [productId, quantity, retryToken, onLoading, onResult])

  return null
}

export default LineOfferLoader
