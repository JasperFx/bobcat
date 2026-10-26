<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { useSignalR } from '@/composables/useSignalR'
import StatusStrip from '@/components/StatusStrip.vue'

const route = useRoute()
const { connect } = useSignalR()

onMounted(() => {
  void connect()
})

const onEventModel = computed(() => route.path.startsWith('/event-model'))
const onRuns = computed(() => !onEventModel.value)
</script>

<template>
  <div class="bm-app">
    <header class="bm-topbar">
      <div class="bm-brand">
        <img src="/bobcat-mark.svg" alt="" class="bm-brand-mark" />
        <span class="bm-brand-word">Bobcat</span>
      </div>
      <nav class="bm-tabs">
        <router-link to="/runs" class="bm-tab" :class="{ 'is-active': onRuns }">Runs</router-link>
        <router-link to="/event-model" class="bm-tab" :class="{ 'is-active': onEventModel }">Event Model</router-link>
      </nav>
      <a
        class="bm-company"
        href="https://jasperfx.net"
        target="_blank"
        rel="noopener noreferrer"
        title="JasperFx"
      >
        <img src="/jasperfx-logo-128.png" alt="JasperFx" class="bm-company-logo" />
      </a>
    </header>
    <!-- bobcat#786: one status line on every page — the runner, a broken build, the live run,
         bobcat watch and the connection — in place of the header's "connected" text. -->
    <StatusStrip />
    <main class="bm-main">
      <router-view />
    </main>
  </div>
</template>

<style scoped>
.bm-app {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

.bm-topbar {
  display: flex;
  align-items: stretch;
  gap: 24px;
  height: 52px;
  padding: 0 20px;
  border-bottom: 1px solid var(--bm-divider);
}

.bm-brand {
  display: flex;
  align-items: center;
  gap: 10px;
}

.bm-brand-mark {
  width: 26px;
  height: 26px;
}

.bm-brand-word {
  font-size: 16px;
  font-weight: 600;
  letter-spacing: -0.01em;
  color: var(--bc-coat);
}

.bm-tabs {
  display: flex;
  align-items: stretch;
}

.bm-tab {
  display: inline-flex;
  align-items: center;
  padding: 0 14px;
  font-size: 13px;
  color: var(--bm-menu-text);
  text-decoration: none;
}

.bm-tab:hover {
  color: var(--bc-coat);
  background: var(--bm-menu-hover);
}

/* Active is accent text over an accent underline. */
.bm-tab.is-active {
  color: var(--bm-menu-active-text);
  box-shadow: inset 0 -2px 0 var(--bc-accent);
}

.bm-company {
  margin-left: auto;
  display: flex;
  align-items: center;
}

.bm-company-logo {
  width: 24px;
  height: 24px;
  object-fit: contain;
  opacity: 0.8;
}

.bm-company:hover .bm-company-logo {
  opacity: 1;
}

.bm-main {
  flex: 1;
  padding: 20px;
}
</style>
