import { createApp } from 'vue'

import ResultsPage from './ResultsPage.vue'
import { RESULTS_ELEMENT_ID, readPageData } from './fromResults'

import '../styles/variables.css'

// The HTML results page (bobcat#541): one file, no server. `bobcat results-html` inlines the run's
// results files into the element readPageData reads, and this bundle into the same file.
const { data, error } = readPageData(document.getElementById(RESULTS_ELEMENT_ID)?.textContent)

createApp(ResultsPage, { data, error }).mount('#app')
