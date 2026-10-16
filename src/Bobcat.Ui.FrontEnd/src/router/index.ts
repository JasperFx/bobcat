import { createRouter, createWebHistory } from 'vue-router'

/**
 * The Bobcat UI (bobcat#524): the run board and the Event Model canvas. These came back from
 * Stoat's merged SPA, whose routes they keep — `/runs`, `/runs/:runId`, the spec page and
 * `/event-model` — so a link into a run reads the same either way. Home is the run board.
 */
const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      redirect: '/runs',
    },
    {
      path: '/runs',
      name: 'runs',
      component: () => import('@/views/DashboardView.vue'),
    },
    {
      path: '/runs/:runId',
      name: 'run',
      component: () => import('@/views/RunView.vue'),
      props: true,
    },
    {
      // One specification from one run (stoat#61). The spec identity `{Feature}/{Scenario}`
      // carries a slash, so the param takes the rest of the path whole.
      path: '/runs/:runId/specs/:uid(.*)',
      name: 'spec',
      component: () => import('@/views/SpecView.vue'),
      props: true,
    },
    {
      // One attached runner and what it can run (bobcat#644): a suite's specifications and their
      // steps before anything has run, with a run button beside each.
      path: '/runners/:runnerId',
      name: 'runner',
      component: () => import('@/views/RunnerView.vue'),
      props: true,
    },
    {
      path: '/event-model',
      name: 'event-model',
      // Lazy-loaded: pulls in the shared @jasperfx/event-model-vue renderer.
      component: () => import('@/views/EventModelPage.vue'),
    },
  ],
})

export default router
