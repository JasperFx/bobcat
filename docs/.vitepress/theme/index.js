import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import './style.css'

// The banner art carries the identity, so it belongs ABOVE the hero rather than trailing the
// feature cards as ordinary page content. `home-hero-before` is the only slot that renders
// ahead of the hero block on the home layout.
const banner = () =>
  h('div', { class: 'bc-banner-wrap' }, [
    h('img', {
      class: 'bc-banner bc-banner--light',
      src: '/bobcat-docs-header-light-1280x320.png',
      alt: 'Bobcat — integration testing toolset for .NET'
    }),
    h('img', {
      class: 'bc-banner bc-banner--dark',
      src: '/bobcat-docs-header-dark-1280x320.png',
      alt: 'Bobcat — integration testing toolset for .NET'
    })
  ])

export default {
  extends: DefaultTheme,
  Layout: () => h(DefaultTheme.Layout, null, { 'home-hero-before': banner })
}
