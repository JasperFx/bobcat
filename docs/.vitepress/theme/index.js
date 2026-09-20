import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import './style.css'

// The Critter Stack .NET server invite — the same one Marten's and Wolverine's nav bars use.
// Deliberately an INVITE rather than a deep link to the #bobcat channel: a
// discord.com/channels/<server>/<channel> URL only resolves for someone who has already joined
// the server, so it dead-ends for exactly the reader this link exists for.
const DISCORD = 'https://discord.gg/WMxrvegf8H'

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

// Same placement Marten uses for its community box — beside the outline, on every doc page.
const community = () =>
  h('div', { class: 'info-box' }, [
    h('p', [
      'Questions? The ',
      h('a', {
        class: 'vp-external-link-icon',
        href: DISCORD,
        target: '_blank',
        rel: 'noreferrer'
      }, '#bobcat channel'),
      ' on the Critter Stack .NET Discord is the fastest way to reach the team.'
    ])
  ])

export default {
  extends: DefaultTheme,
  Layout: () =>
    h(DefaultTheme.Layout, null, {
      'home-hero-before': banner,
      'aside-ads-before': community
    })
}
