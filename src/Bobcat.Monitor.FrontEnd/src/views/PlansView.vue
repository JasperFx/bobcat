<script setup lang="ts">
import { onMounted } from 'vue'
import { usePlansStore } from '@/stores/plans-store'

const plans = usePlansStore()

onMounted(() => {
  void plans.fetchPlans()
})

async function rescan(): Promise<void> {
  try {
    await fetch('/api/plans/rescan', { method: 'POST' })
  } catch {
    // Best-effort — the refetch below shows whatever the server has.
  }
  await plans.fetchPlans()
}
</script>

<template>
  <div>
    <div class="bm-plans-header">
      <h2>Plans</h2>
      <el-button size="small" @click="rescan">Rescan plans directory</el-button>
    </div>

    <el-empty
      v-if="plans.allPlans.length === 0"
      description="No plans registered — drop a plan document in the plans directory or PUT /api/plans/{slug}"
    />

    <el-card v-for="plan in plans.allPlans" :key="plan.slug" class="bm-plan-card">
      <router-link
        v-if="plan.valid"
        :to="{ name: 'plan', params: { slug: plan.slug } }"
        class="bm-plan-title"
      >
        {{ plan.title }}
      </router-link>
      <span v-else class="bm-plan-title bm-plan-broken">{{ plan.title }}</span>

      <div class="bm-plan-meta">
        {{ plan.slug }} · {{ plan.source }}<span v-if="plan.sourcePath"> · {{ plan.sourcePath }}</span>
        <span v-if="plan.valid"> · {{ plan.nodes }} nodes</span>
      </div>

      <!-- A broken plan file renders WITH its errors — it must not vanish from the board. -->
      <el-alert
        v-if="!plan.valid"
        type="error"
        :closable="false"
        title="This plan document has errors"
      >
        <ul class="bm-plan-errors">
          <li v-for="error in plan.errors" :key="error">{{ error }}</li>
        </ul>
      </el-alert>
    </el-card>
  </div>
</template>

<style scoped>
.bm-plans-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.bm-plan-card {
  margin-bottom: 12px;
}

.bm-plan-title {
  font-weight: 600;
  color: var(--bm-primary);
  text-decoration: none;
}

.bm-plan-broken {
  color: var(--bm-state-failed);
}

.bm-plan-meta {
  font-size: 12px;
  color: var(--bm-menu-text);
  margin: 4px 0 8px;
}

.bm-plan-errors {
  margin: 0;
  padding-left: 18px;
}
</style>
