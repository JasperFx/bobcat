import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from '@/views/DashboardView.vue'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'dashboard',
      component: DashboardView,
    },
    {
      path: '/runs/:runId',
      name: 'run',
      // Lazy-loaded: the drill-in view will grow the step-streaming UI.
      component: () => import('@/views/RunView.vue'),
      props: true,
    },
  ],
})

export default router
