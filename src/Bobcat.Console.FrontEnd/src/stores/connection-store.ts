import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ConnectionStatus = 'disconnected' | 'connected' | 'reconnecting' | 'error'

/**
 * SignalR connection state, lifted from CritterWatch's connection store. The
 * staleness/epoch machinery (their GH-722 zombie-socket watchdog) comes over
 * when this host grows a periodic full-state broadcast; until then a plain
 * status + last-synced timestamp is the honest amount of state.
 */
export const useConnectionStore = defineStore('connection', () => {
  const status = ref<ConnectionStatus>('disconnected')
  const errorMessage = ref<string | null>(null)
  // Timestamp of the last successful inbound message — the "how stale is what
  // I'm looking at" input for the disconnect banner.
  const lastSyncedAt = ref<Date | null>(null)

  function setConnected() {
    status.value = 'connected'
    errorMessage.value = null
  }

  function setReconnecting() {
    status.value = 'reconnecting'
  }

  function setDisconnected() {
    status.value = 'disconnected'
  }

  function setError(message: string) {
    status.value = 'error'
    errorMessage.value = message
  }

  function markSynced(at: Date = new Date()) {
    lastSyncedAt.value = at
  }

  return {
    status,
    errorMessage,
    lastSyncedAt,
    setConnected,
    setReconnecting,
    setDisconnected,
    setError,
    markSynced,
  }
})
