# Website

The landing page and the docs at https://pithyx.org (D340): Astro with Starlight, React for the landing page sections, built to static files and published with GitHub Pages (`.github/workflows/pages.yml`). Everything a reader sees is content in Markdown or YAML; components only arrange it. The root `CLAUDE.md` stays binding.

## Commands

- `pnpm --filter @pithyx/website dev`: dev server on http://localhost:4321 (search works only after a build).
- `pnpm --filter @pithyx/website build:site`: build to `website/dist/`, then `scripts/check-build.ts` (every docs page has a Markdown twin, the llms files exist, no MDX component left in a twin, every link in `llms.txt` is built). It prints how many pages are still placeholders.
- `pnpm --filter @pithyx/website check`: `astro check` (types of the content schemas and components).
- CI: the `website` job in `ci.yml` runs both on every change under `website/`, `i18n/`, the tokens or the lockfile.

## Where content lives

- Docs pages: `src/content/docs/docs/<area>/<page>.md` (English, the reference) and the same path under `src/content/docs/de/docs/` (German). The URL is the path: `/docs/admin/storage/`, `/de/docs/admin/storage/`.
- Areas: `src/lib/sections.ts` (id, folder, order) with label and summary in `src/content/i18n/<locale>.json` (`section.<id>.label`, `section.<id>.summary`). The sidebar, the docs overview and the LLM bundles read that list; a new area is added there once and gets its folder.
- FAQ: one file per question in `src/content/faq/<locale>/<slug>.md`, the same slug in both languages. Frontmatter: `question`, `category` (one of `src/lib/faqCategories.ts`), `tags` (search words), `order` (within the category), `status`. The body is the answer, short, plain Markdown.
- Landing page: every text, link and picture in `src/content/landing/en.yaml` and `de.yaml` (same keys, schema in `src/content.config.ts`, a missing field fails the build). Feature icons are the names in `src/lib/landingIcons.ts`.
- Site strings (buttons, banners, FAQ labels, the llms texts): `src/content/i18n/en.json` and `de.json`. German must have every English key (the type check fails otherwise).
- Pictures: `src/assets/screenshots/`, real screenshots only: WebP, 1920 x 1200 (taken at 1440 x 900 with device scale factor 2), `<name>.webp` for English and `<name>.de.webp` for German. Docs pages use plain Markdown (`![alt](relative path)` with an italic caption line below). Take them from a throwaway VM (`image/test-install.sh`, `image/test-boot.sh`) or the dev box, with invented sample data and nothing of the owner, the dev box or the home network in view.
- Generated pages: `reference/errors` comes from `i18n/<locale>/errors.json`, the docs overview from `sections.ts`, the FAQ page from the FAQ files (frontmatter `generated`). Do not write their lists by hand.

## Writing a docs page

- Frontmatter: `title`, `description` (one sentence, required; it is the line in `llms.txt`), `status`, optional `audience` (`user`, `admin`, `developer`), `since` (the Pithyx version) and `sidebar.order`.
- `status`: `placeholder` (only the outline), `draft` (written, not checked against the code), `ready` (checked against the code as it is). Pages that are not `ready` show a banner and say so in their Markdown twin. Set `ready` only after reading the code the page describes.
- A placeholder page has a `:::note[Placeholder]` aside that says what belongs on it and where the facts are (`TODO.md` backlog notes "Website user docs for ...", decisions, source folders). Replace the aside when the page is written; keep the planned headings unless the content asks for others.
- Write plain Markdown (`.md`). Use `.mdx` only where a page needs a component; a component's line disappears from the Markdown twin unless `toPlainMarkdown` in `src/lib/docs.ts` converts it (asides, `LinkCard`, `Card`, `Tabs`, `Steps` are converted). Asides: `:::note`, `:::tip`, `:::caution`, `:::danger`, with an optional `[title]`.
- Link other pages with site paths (`/docs/admin/storage/`); the twins turn them into absolute links.
- Describe behavior from the code as it is, not from plans. Name settings and buttons as the UI shows them (the texts in `i18n/<locale>/*.json`). Error codes link to `/docs/reference/errors/#<code>`.
- Every English page has its German page with the same path, translated, informal "du". English is the reference: change it first, then German in the same commit.
- Rules of the root `CLAUDE.md` apply: no em dashes, no other NAS vendors or their products, open-source projects may be named, nothing that phones home (no analytics, no external fonts, no CDN).

## For language models

Every docs page has a Markdown twin (`/docs/admin/storage.md`, `<link rel="alternate" type="text/markdown">` in its head, buttons under the title). `/llms.txt` is the index in the llmstxt.org format, `/llms/<area>.txt` bundles one area (`getting-started`, `user`, `admin`, `install`, `developers` and `developers-daemon|desktop|apps`, `sdk`, `reference`, `project`, `faq`), `/llms-full.txt` holds everything; each under `/de/` in German. The endpoints are in `src/pages/`, the logic in `src/lib/docs.ts`. Keep a page's first paragraph self-contained: models often read only the start.

## Keeping the docs current

Whoever changes Pithyx checks whether the docs describe it (root `CLAUDE.md`, "Before you commit"): find the page with the site search or `grep -r` under `src/content/docs/`, update it in both languages in the same commit, or add a page or section in the matching area when a user, admin or developer would otherwise guess. A question users will likely ask goes into the FAQ.
