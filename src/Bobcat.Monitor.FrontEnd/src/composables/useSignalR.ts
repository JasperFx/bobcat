import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { relayToStore } from '@/messages/relayToStore'
import { useConnectionStore } from '@/stores/connection-store'

let connection: HubConnection | null = null

// Frame-level message batching, lifted from CritterWatch: accumulate incoming
// messages and flush them in one pass per animation frame, so Vue's reactivity
// batches all the resulting store updates into a single render cycle. This is
// the client half of burst-handling; the server sends per-message today.
const pendingMessages: unknown[] = []
let flushScheduled = false

function flushPendingMessages() {
  flushScheduled = false
  if (pendingMessages.length === 0) return

  const batch = pendingMessages.splice(0, pendingMessages.length)

  try {
    useConnectionStore().markSynced()
  } catch {
    // Pinia not ready yet — skip
  }

  for (const message of batch) {
    try {
      relayToStore(message)
    } catch (err) {
      console.error('Error in relayToStore:', err)
    }
  }
}

function scheduleFlush() {
  if (flushScheduled) return
  flushScheduled = true
  const raf =
    typeof window !== 'undefined' && typeof window.requestAnimationFrame === 'function'
      ? window.requestAnimationFrame
      : (cb: FrameRequestCallback) => setTimeout(() => cb(performance.now()), 0)
  raf(flushPendingMessages)
}

function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl('/api/messages')
      // CritterWatch GH-722: the default reconnect policy gives up after four
      // attempts (~42s), shorter than a routine host restart. Retry forever,
      // exponential backoff capped at 30s.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (ctx) =>
          Math.min(30_000, 1_000 * Math.pow(2, ctx.previousRetryCount)),
      })
      .configureLogging(LogLevel.Information)
      .build()

    connection.on('ReceiveMessage', (message: unknown) => {
      pendingMessages.push(message)
      scheduleFlush()
    })

    connection.onreconnecting(() => {
      useConnectionStore().setReconnecting()
    })

    connection.onreconnected(() => {
      const store = useConnectionStore()
      store.setConnected()
      store.markSynced()
    })

    connection.onclose(() => {
      useConnectionStore().setDisconnected()
    })
  }

  return connection
}

export function useSignalR() {
  async function connect() {
    const conn = getConnection()
    if (conn.state !== HubConnectionState.Disconnected) return

    const store = useConnectionStore()
    try {
      await conn.start()
      store.setConnected()
      store.markSynced()
    } catch (err) {
      store.setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function disconnect() {
    if (!connection) return
    await connection.stop()
    useConnectionStore().setDisconnected()
  }

  return { connect, disconnect }
}
