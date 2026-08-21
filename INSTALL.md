# Dropping this into ~/code/bobcat

Unzip at the repo root. Everything lands in place; your existing `docs/*.md` become the site's pages.

```bash
cd ~/code/bobcat
git checkout -b docs/vitepress-site
# unzip here, then:
npm install
npm run docs        # dev server
npm run docs:build  # production build
```

## What's included

| Path | Note |
|---|---|
| `package.json` | root-level, `vitepress dev docs` — merge if you already have one |
| `docs/.vitepress/config.mjs` | nav + sidebar pointing at your flat `docs/*.md` paths |
| `docs/.vitepress/theme/` | the "Ember on Ink" theme |
| `docs/index.md` | home page, light/dark banner pair |
| `docs/getting-started.md` | new page |
| `docs/public/` | banners, avatars, favicons, marks (binaries: see the note below) |
| `docs/.gitignore` | ignores `.vitepress/dist` + `cache` |
| `README.md` | rewritten with the banner `<picture>` block |

Nothing in your existing `docs/*.md` needs editing — the relative links
(`versions.md`, `sample-wiring.md`) resolve correctly under VitePress.

## Binary assets

The PNGs are in the zip under `docs/public/`. If your unzip flattens them, they are also
available individually from the project's `brand/` folder.
