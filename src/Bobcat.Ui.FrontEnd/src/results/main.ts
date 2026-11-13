import { createApp } from 'vue'

import ResultsPage from './ResultsPage.vue'
import { RESULTS_ELEMENT_ID, focusOf, readPageData } from './fromResults'

import '../styles/variables.css'

// The HTML results page (bobcat#541): one file, no server. `bobcat results-html` inlines the run's
// results files into the element readPageData reads, and this bundle into the same file.
const { data, error } = readPageData(document.getElementById(RESULTS_ELEMENT_ID)?.textContent)

// `?spec=<identity>` (or `#spec=`) shows one specification, for a documentation page to embed in an
// iframe (bobcat#553); the link back is this page without its query and fragment.
const focus = focusOf(location.search, location.hash)
const fullPage = location.href.split(/[?#]/)[0] ?? 'results.html'

createApp(ResultsPage, { data, error, focus, fullPage }).mount('#app')
