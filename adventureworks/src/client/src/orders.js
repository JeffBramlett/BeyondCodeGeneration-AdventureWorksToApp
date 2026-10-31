const DEFAULT_ORDER_PAGE_SIZE = 25
const MAX_ORDER_PAGE_SIZE = 100
const DEFAULT_LINE_PAGE_SIZE = 50
const MAX_LINE_PAGE_SIZE = 100
const MAX_DATE_RANGE_DAYS = 31
const MAX_PAGE_NUMBER = 10_000

function isValidDateOnly(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return false

  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  if (year < 1 || month < 1 || month > 12) return false

  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0)
  const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
  return day >= 1 && day <= daysInMonth[month - 1]
}

function dateOrdinal(value) {
  const [year, month, day] = value.split('-').map(Number)
  const date = new Date(0)
  date.setUTCHours(0, 0, 0, 0)
  date.setUTCFullYear(year, month - 1, day)
  return date.getTime() / 86_400_000
}

export function validateOrderSearch({ mode, orderReference, fromDate, toDate }) {
  if (mode === 'reference') {
    const reference = orderReference.trim()
    if (!reference) return 'Enter an exact order reference.'
    if (reference.length > 25) return 'Order reference must be 25 characters or fewer.'
    return ''
  }

  if (!isValidDateOnly(fromDate) || !isValidDateOnly(toDate)) {
    return 'Enter a complete, valid start and end date.'
  }

  const dayCount = dateOrdinal(toDate) - dateOrdinal(fromDate) + 1
  if (dayCount < 1) return 'The start date must be on or before the end date.'
  if (dayCount > MAX_DATE_RANGE_DAYS) return 'Choose an inclusive date range of 31 calendar days or fewer.'
  return ''
}

async function readJson(response) {
  try {
    return await response.json()
  } catch {
    return null
  }
}

function requestError(status) {
  if (status === 401) return { kind: 'unauthorized', message: 'Sign in with the local Development test identity, then retry.' }
  if (status === 403) return { kind: 'forbidden', message: 'This Development identity is not authorized to view local order records.' }
  if (status === 400) return { kind: 'validation', message: 'The API could not validate this order lookup. Check the search criteria and try again.' }
  if (status === 404) return { kind: 'not-found', message: 'No matching order was found.' }
  if (status === 503) return { kind: 'unavailable', message: 'Order lookup is unavailable. The local test-clone read permission may not be configured.' }
  if (status === 502) return { kind: 'network', message: 'The order service could not be reached. Check the local API connection and try again.' }
  return { kind: 'service', message: 'The order lookup could not be completed. Try again.' }
}

function isOrderPage(value) {
  return value !== null
    && typeof value === 'object'
    && Array.isArray(value.items)
    && Number.isInteger(value.page)
    && Number.isInteger(value.pageSize)
    && typeof value.hasMore === 'boolean'
    && value.items.every((item) => typeof item?.orderReference === 'string' && typeof item?.orderDate === 'string')
}

function isOrderDetail(value) {
  return value !== null
    && typeof value === 'object'
    && typeof value.orderReference === 'string'
    && typeof value.orderDate === 'string'
    && Array.isArray(value.lines)
    && Number.isInteger(value.linePage)
    && Number.isInteger(value.linePageSize)
    && typeof value.hasMore === 'boolean'
    && value.lines.every((line) => Number.isInteger(line?.productId) && Number.isInteger(line?.orderQty))
}

async function getOrders(url, { signal, fetchImpl }) {
  let response
  try {
    response = await fetchImpl(url, { signal })
  } catch (error) {
    if (signal?.aborted || error?.name === 'AbortError') throw error
    return { error: { kind: 'network', message: 'The local API could not be reached. Check your connection and try again.' } }
  }

  if (!response.ok) {
    await readJson(response)
    return { error: requestError(response.status) }
  }

  try {
    const data = await response.json()
    return data
  } catch {
    return { error: { kind: 'service', message: 'The order service returned an unreadable response. Try again.' } }
  }
}

export async function fetchOrderPage(criteria, { page = 1, pageSize = DEFAULT_ORDER_PAGE_SIZE, signal, fetchImpl = fetch } = {}) {
  if (!Number.isInteger(page) || page < 1 || page > MAX_PAGE_NUMBER || !Number.isInteger(pageSize) || pageSize < 1 || pageSize > MAX_ORDER_PAGE_SIZE) {
    return { error: { kind: 'validation', message: 'Order page settings are outside the allowed bounds.' } }
  }

  const validation = validateOrderSearch(criteria)
  if (validation) return { error: { kind: 'validation', message: validation } }

  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (criteria.mode === 'reference') {
    params.set('orderReference', criteria.orderReference.trim())
  } else {
    params.set('fromDate', criteria.fromDate)
    params.set('toDate', criteria.toDate)
  }

  const result = await getOrders(`/api/orders?${params}`, { signal, fetchImpl })
  if (result.error) return result
  if (!isOrderPage(result)) return { error: { kind: 'service', message: 'The order service returned an unreadable result page. Try again.' } }
  return {
    page: {
      items: result.items.map(({ orderReference, orderDate }) => ({ orderReference, orderDate })),
      page: result.page,
      pageSize: result.pageSize,
      hasMore: result.hasMore,
    },
  }
}

export async function fetchOrderDetail(orderReference, { linePage = 1, linePageSize = DEFAULT_LINE_PAGE_SIZE, signal, fetchImpl = fetch } = {}) {
  if (typeof orderReference !== 'string' || !orderReference.trim() || orderReference.length > 25
    || !Number.isInteger(linePage) || linePage < 1 || linePage > MAX_PAGE_NUMBER
    || !Number.isInteger(linePageSize) || linePageSize < 1 || linePageSize > MAX_LINE_PAGE_SIZE) {
    return { error: { kind: 'validation', message: 'Order detail page settings are outside the allowed bounds.' } }
  }

  const params = new URLSearchParams({ linePage: String(linePage), linePageSize: String(linePageSize) })
  const result = await getOrders(`/api/orders/${encodeURIComponent(orderReference)}?${params}`, { signal, fetchImpl })
  if (result.error) return result
  if (!isOrderDetail(result)) return { error: { kind: 'service', message: 'The order service returned unreadable order details. Try again.' } }
  return {
    detail: {
      orderReference: result.orderReference,
      orderDate: result.orderDate,
      lines: result.lines.map(({ productId, orderQty }) => ({ productId, orderQty })),
      linePage: result.linePage,
      linePageSize: result.linePageSize,
      hasMore: result.hasMore,
    },
  }
}

export const orderPageBounds = {
  defaultPageSize: DEFAULT_ORDER_PAGE_SIZE,
  maxPageSize: MAX_ORDER_PAGE_SIZE,
}

export const orderLinePageBounds = {
  defaultPageSize: DEFAULT_LINE_PAGE_SIZE,
  maxPageSize: MAX_LINE_PAGE_SIZE,
}
