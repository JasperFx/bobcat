<script setup lang="ts">
import { onMounted } from 'vue'
import { useSignalR } from '@/composables/useSignalR'
import { useConnectionStore } from '@/stores/connection-store'

const connectionStore = useConnectionStore()
const { connect } = useSignalR()

onMounted(() => {
  void connect()
})
</script>

<template>
  <el-container class="bm-app">
    <!-- Issue #179 — the title bar: the product on the left, the company on the right. The
         sidebar's plain text brand moved here rather than being duplicated, which is also what
         gives the JasperFx mark a right-hand edge to sit against (a 220px rail has no "right"). -->
    <el-header class="bm-titlebar" height="52px">
      <div class="bm-titlebar-product">
        <img src="/bobcat-mark-128.png" alt="" class="bm-titlebar-mark" />
        <span class="bm-titlebar-name">Bobcat Console</span>
      </div>
      <a
        class="bm-titlebar-company"
        href="https://jasperfx.net"
        target="_blank"
        rel="noopener noreferrer"
        title="JasperFx"
      >
        <img src="/jasperfx-logo-128.png" alt="JasperFx" class="bm-titlebar-logo" />
      </a>
    </el-header>
    <el-container class="bm-body">
      <el-aside width="220px" class="bm-sidebar">
        <el-menu router :default-active="'/'">
          <el-menu-item index="/">Dashboard</el-menu-item>
          <el-menu-item index="/event-model">Event Model</el-menu-item>
        </el-menu>
        <div class="bm-connection" :data-status="connectionStore.status">
          {{ connectionStore.status }}
        </div>
      </el-aside>
      <el-main>
        <router-view />
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
/* Exactly the viewport, never taller: el-main (overflow: auto from Element Plus)
   is the one scroll container, so the wheel always lands on a real scroller and
   the sidebar stays pinned. min-height let the page itself grow instead, leaving
   el-main with nothing to scroll (#166). The title bar is a fixed-height row above
   that arrangement, so the inner container needs min-height: 0 for the same reason —
   without it the flex child refuses to shrink and el-main is pushed off the bottom. */
.bm-app {
  height: 100vh;
}

.bm-body {
  flex: 1;
  min-height: 0;
}

.bm-titlebar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-shrink: 0;
  padding: 0 16px;
  background-color: var(--bm-menu-bg);
  border-bottom: 1px solid var(--bm-border);
}

.bm-titlebar-product {
  display: flex;
  align-items: center;
  gap: 10px;
}

.bm-titlebar-mark {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  object-fit: contain;
}

.bm-titlebar-name {
  font-weight: 700;
  font-size: 16px;
  color: var(--bm-primary);
}

.bm-titlebar-company {
  display: flex;
  align-items: center;
}

.bm-titlebar-logo {
  width: 26px;
  height: 26px;
  object-fit: contain;
  opacity: 0.85;
}

.bm-titlebar-company:hover .bm-titlebar-logo {
  opacity: 1;
}

.bm-connection {
  padding: 12px 20px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-connection[data-status='connected'] {
  color: var(--bm-state-passed);
}

.bm-connection[data-status='disconnected'],
.bm-connection[data-status='error'] {
  color: var(--bm-state-failed);
}
</style>
