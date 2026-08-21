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
    <el-aside width="220px" class="bm-sidebar">
      <div class="bm-brand">Bobcat Console</div>
      <el-menu router :default-active="'/'">
        <el-menu-item index="/">Dashboard</el-menu-item>
      </el-menu>
      <div class="bm-connection" :data-status="connectionStore.status">
        {{ connectionStore.status }}
      </div>
    </el-aside>
    <el-main>
      <router-view />
    </el-main>
  </el-container>
</template>

<style scoped>
.bm-app {
  min-height: 100vh;
}

.bm-brand {
  padding: 18px 20px;
  font-weight: 700;
  color: var(--bm-primary);
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
