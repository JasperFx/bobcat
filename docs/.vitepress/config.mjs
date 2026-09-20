import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Bobcat',
  description: 'Author, supervise, and run integration tests in .NET',
  base: '/',
  cleanUrls: true,
  head: [
    ['link', { rel: 'icon', type: 'image/png', sizes: '64x64', href: '/bobcat-favicon-64.png' }],
    ['link', { rel: 'icon', type: 'image/png', sizes: '48x48', href: '/bobcat-favicon-48.png' }],
    ['link', { rel: 'icon', type: 'image/png', sizes: '32x32', href: '/bobcat-favicon-32.png' }],
    ['link', { rel: 'icon', type: 'image/png', sizes: '16x16', href: '/bobcat-favicon-16.png' }],
    ['link', { rel: 'apple-touch-icon', sizes: '180x180', href: '/bobcat-apple-touch-icon.png' }],
    ['meta', { property: 'og:image', content: 'https://bobcat.jasperfx.net/bobcat-social-dark-1280x640.png' }],
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }]
  ],
  themeConfig: {
    logo: { light: '/bobcat-avatar-light-512.png', dark: '/bobcat-avatar-dark-512.png', alt: 'Bobcat' },
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
          { text: 'The Command Line', link: '/command-line' },
          { text: 'Running Specs with dotnet test', link: '/dotnet-test' },
          { text: 'Sample Wiring Playbook', link: '/sample-wiring' },
          { text: 'Composing Grammar Modules', link: '/composing-grammars' },
          { text: 'Code-First Specifications', link: '/code-first-specs' },
          { text: 'Specs From Existing Tests', link: '/marker-steps' },
          { text: 'Checking Spec Identities', link: '/spec-identities' },
          { text: 'Editor Integration', link: '/editor-integration' }
        ]
      },
      {
        text: 'Supervising',
        collapsed: false,
        items: [
          { text: 'Parallel-Ready Suites', link: '/parallel-ready-suites' },
          { text: 'What a Run Publishes', link: '/monitor-design' }
        ]
      },
      {
        text: 'Reference',
        collapsed: false,
        items: [
          { text: 'Version Matrix', link: '/versions' },
          { text: 'Test Ledger Design', link: '/ledger-design' },
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
