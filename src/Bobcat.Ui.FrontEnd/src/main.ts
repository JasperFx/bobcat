import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'

import App from './App.vue'
import router from './router'

import 'element-plus/dist/index.css'
// The dark sheet declares Element Plus's html.dark variables; variables.css
// (loaded after) overrides them with the Ember on Ink tokens. index.html sets the
// class — the UI is dark-only.
import 'element-plus/theme-chalk/dark/css-vars.css'
import './styles/variables.css'

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(ElementPlus)

app.mount('#app')
