import { isPositiveSmallintQuantity } from './draftQuantity.js'

function problemMessage(problem) {
  if (problem?.errors && typeof problem.errors === 'object') {
    return Object.values(problem.errors).flat().filter(Boolean).join(' ')
  }

  return problem?.detail || problem?.title || 'The catalog request could not be completed.'
}

async function readProblem(response) {
  try {
    return await response.json()
  } catch {
    return null
  }
}

function requestError(response, problem) {
  if (response.status === 400) return { kind: 'validation', title: 'Offer quantity could not be validated', message: problemMessage(problem) }
  if (response.status === 404) return { kind: 'not-found', title: 'Product not found', message: 'No product was returned for this eligibility check.' }
  if (response.status === 503) return { kind: 'unavailable', title: 'Catalog service unavailable', message: problemMessage(problem) }
  if (response.status === 502) return { kind: 'network', title: 'Connection problem', message: 'The catalog service could not be reached. Check the connection and try again.' }
  return { kind: 'service', title: 'Catalog request failed', message: problemMessage(problem) }
}

function isEligibleOfferDto(offer) {
  return Number.isInteger(offer?.specialOfferId)
    && offer.specialOfferId > 0
    && typeof offer.description === 'string'
    && (offer.discountPct === undefined
      || offer.discountPct === null
      || (typeof offer.discountPct === 'number' && Number.isFinite(offer.discountPct) && offer.discountPct >= 0 && offer.discountPct <= 1))
}

export async function fetchEligibleOffers(productId, quantity, { signal, fetchImpl = fetch } = {}) {
  if (!Number.isInteger(productId) || productId <= 0 || !isPositiveSmallintQuantity(String(quantity))) {
    return { error: { kind: 'validation', title: 'Offer quantity could not be validated', message: 'Enter a whole number from 1 to 32767.' } }
  }

  let response
  try {
    response = await fetchImpl(`/api/products/${productId}/eligible-offers?quantity=${quantity}`, { signal })
  } catch (error) {
    if (signal?.aborted || error?.name === 'AbortError') throw error
    return { error: { kind: 'network', title: 'Connection problem', message: 'The catalog service could not be reached. Check the connection and try again.' } }
  }

  if (!response.ok) {
    const problem = await readProblem(response)
    return { error: requestError(response, problem) }
  }

  try {
    const offers = await response.json()
    if (!Array.isArray(offers) || !offers.every(isEligibleOfferDto)) throw new Error('Invalid eligible-offers response')
    return { offers: offers.filter((offer) => offer.specialOfferId !== 1) }
  } catch (error) {
    if (signal?.aborted || error?.name === 'AbortError') throw error
    return {
      error: {
        kind: 'service',
        title: 'Offer response unreadable',
        message: 'The catalog returned an unreadable eligible-offers response. Try again.',
      },
    }
  }
}