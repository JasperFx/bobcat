/**
 * What the console keeps (bobcat#786): the latest `runs` finished runs of each suite, and the
 * archive for `days` days — as that console is configured, read from `GET /api/retention`. The
 * board states it, so a person looking at a suite's five cards knows where the sixth went. Zero
 * means that bound is off.
 */
export interface RetentionPolicy {
  runs: number
  days: number
}

export async function loadRetention(fetchImpl: typeof fetch = fetch): Promise<RetentionPolicy | null> {
  try {
    const response = await fetchImpl('/api/retention')
    if (!response.ok) return null
    const body = (await response.json()) as Partial<RetentionPolicy> | null
    if (!body || typeof body.runs !== 'number' || typeof body.days !== 'number') return null
    return { runs: body.runs, days: body.days }
  } catch {
    return null
  }
}
