/*
 * CC Switch 3.19.x Usage Query -> Custom Script
 *
 * Usage Base URL: https://ergouzi.life (the account API, without /v1)
 * Put the account token accepted by /api/user/self into CC Switch's Usage
 * Query "API Key" field. This field is separate from the provider API key.
 * The inference endpoint remains /v1.
 *
 * Ergouzi currently identifies itself as New API v1.0.0-rc.25. New API quota
 * values are internal units; 500000 units = 1 USD is the default convention.
 * Change QUOTA_PER_UNIT only after comparing this result with /wallet.
 */
var QUOTA_PER_UNIT = 500000

function firstFinite(values) {
  for (var i = 0; i < values.length; i += 1) {
    if (values[i] === null || values[i] === undefined || values[i] === '') continue
    var n = Number(values[i])
    if (isFinite(n)) return n
  }
  return null
}

function pick(object, names) {
  if (!object || typeof object !== 'object') return null
  for (var i = 0; i < names.length; i += 1) {
    if (Object.prototype.hasOwnProperty.call(object, names[i])) {
      var value = object[names[i]]
      if (value !== null && value !== undefined && value !== '') return value
    }
  }
  return null
}

function unwrap(response) {
  var value = response
  for (var i = 0; i < 3; i += 1) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) break
    if (value.data && typeof value.data === 'object') value = value.data
    else if (value.result && typeof value.result === 'object') value = value.result
    else break
  }
  return value
}

({
  request: {
    url: "https://ergouzi.life/api/user/self",
    method: "GET",
    headers: {
      "Authorization": "Bearer {{apiKey}}",
      "Accept": "application/json"
    }
  },

  extractor: function (response) {
    var data = unwrap(response)
    if (!data || typeof data !== 'object' || Array.isArray(data)) {
      return { isValid: false, invalidMessage: "Ergouzi response format is invalid" }
    }

    var remainingRaw = pick(data, [
      'quota', 'remaining_quota', 'remain_quota', 'balance_quota', 'remaining', 'balance'
    ])
    var usedRaw = pick(data, [
      'used_quota', 'usedQuota', 'consumed_quota', 'used'
    ])
    var totalRaw = pick(data, [
      'total_quota', 'totalQuota', 'quota_total', 'total'
    ])
    var remaining = firstFinite([remainingRaw])
    var used = firstFinite([usedRaw])
    var total = firstFinite([totalRaw])

    var directUnit = pick(data, ['unit', 'currency', 'quota_unit'])
    var directBalance = firstFinite([pick(data, ['balance_usd', 'balanceUSD'])])
    if (remaining === null && directBalance !== null) {
      remaining = directBalance
      directUnit = 'USD'
    }
    if (remaining === null && used === null && total === null) {
      return {
        isValid: false,
        invalidMessage: String(data.message || data.error || 'quota fields were not found')
      }
    }

    if (remaining === null && total !== null && used !== null) remaining = total - used
    if (used === null && total !== null && remaining !== null) used = total - remaining
    if (total === null && remaining !== null && used !== null) total = remaining + used
    if (remaining === null || used === null || total === null ||
        !isFinite(remaining) || !isFinite(used) || !isFinite(total)) {
      return { isValid: false, invalidMessage: 'balance fields are incomplete' }
    }

    var hasQuotaField = pick(data, ['quota', 'remaining_quota', 'remain_quota', 'balance_quota']) !== null
    var isDirectMoney = directUnit !== null && /^(USD|CNY|RMB|EUR|JPY)$/i.test(String(directUnit)) &&
      !hasQuotaField && (directBalance !== null || Object.prototype.hasOwnProperty.call(data, 'remaining') ||
      Object.prototype.hasOwnProperty.call(data, 'balance'))
    var divisor = isDirectMoney ? 1 : QUOTA_PER_UNIT
    var unit = isDirectMoney ? String(directUnit).toUpperCase().replace('RMB', 'CNY') : 'USD'
    var username = pick(data, ['username', 'user_name', 'email', 'id'])
    var group = pick(data, ['group', 'group_name', 'plan', 'planName'])

    return {
      isValid: true,
      planName: group === null ? 'Ergouzi' : String(group),
      remaining: remaining / divisor,
      used: used / divisor,
      total: total / divisor,
      unit: unit,
      extra: username === null ? '' : String(username)
    }
  }
})
