/* CC Switch Usage Query backed by the local Ergouzi account agent. */
({
  request: {
    url: 'http://127.0.0.1:17891/api/balance',
    method: 'GET',
    headers: {
      Authorization: 'Bearer {{apiKey}}',
      Accept: 'application/json'
    }
  },
  extractor: function (response) {
    var data = response
    for (var i = 0; i < 3; i += 1) {
      if (!data || typeof data !== 'object' || Array.isArray(data)) break
      if (data.data && typeof data.data === 'object') data = data.data
      else if (data.result && typeof data.result === 'object') data = data.result
      else break
    }
    if (!data || data.ok !== true) {
      return { isValid: false, invalidMessage: String(data && (data.error || data.message) || 'Local Ergouzi account agent is unavailable') }
    }
    var remaining = Number(data.totalBalance)
    if (!isFinite(remaining)) {
      return { isValid: false, invalidMessage: 'Local agent totalBalance was not found' }
    }
    var used = Number(data.todayUsage)
    if (!isFinite(used) || used < 0) used = 0
    var unit = String(data.currency || 'USD').toUpperCase()
    return {
      isValid: true,
      planName: 'Ergouzi',
      remaining: remaining,
      used: used,
      total: remaining + used,
      unit: unit,
      extra: 'Local account agent'
    }
  }
})
