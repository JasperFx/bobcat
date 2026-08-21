import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Bobcat',
  description: 'Author, supervise, and run integration tests in .NET',
  base: '/bobcat/',
  cleanUrls: true,
  head: [
    ['link', { rel: 'icon', type: 'image/png', sizes: '64x64', href: '/bobcat/bobcat-favicon-64.png' }],
    ['link', { rel: 'icon', type: 'image/png', sizes: '32x32', href: '/bobcat/bobcat-favicon-32.png' }],
    ['meta', { property: 'og:image', content: 'https://jasperfx.github.io/bobcat/bobcat-social-dark-1280x640.png' }],
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }]
  ],
  themeConfig: {
    logo: { light: '/bobcat-mark-light.svg', dark: '/bobcat-mark.svg', alt: 'Bobcat' },
    siteTitle: 'Bobcat',
    nav: [
      { text: 'Guide', link: '/getting-started' },
      { text: 'Supervising', link: '/parallel-ready-suites' },
      { text: 'Reference', link: '/versions' }
    ],
    sidebar: [
      {
        text: 'Guide',
        collapsed: false,
        items: [
          { text: 'Getting Started', link: '/getting-started' },
          { text: 'Sample Wiring Playbook', link: '/sample-wiring' }
        ]
      },
      {
        text: 'Supervising',
        collapsed: false,
        items: [
          { text: 'Parallel-Ready Suites', link: '/parallel-ready-suites' },
          { text: 'Test-Run Viewer', link: '/monitor-design' }
        ]
      },
      {
        text: 'Reference',
        collapsed: false,
        items: [
          { text: 'Version Matrix', link: '/versions' },
          { text: 'Wolverine CI Rollout', link: '/wolverine-ci-rollout' }
        ]
      }
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/JasperFx/bobcat' }],
    search: { provider: 'local' },
    editLink: {
      pattern: 'https://github.com/JasperFx/bobcat/edit/main/docs/:path',
      text: 'Suggest an edit to this page'
    },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © JasperFx'
    },
    outline: [2, 3]
  },
  ignoreDeadLinks: [/^https?:\/\/localhost/, /Directory\.Packages\.props$/],
  markdown: {
    theme: { light: 'github-light', dark: 'github-dark' },
    lineNumbers: false
  }
})
