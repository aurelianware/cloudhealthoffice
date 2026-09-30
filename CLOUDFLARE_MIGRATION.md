# Cloudflare Pages Migration — Cloud Health Office marketing site

Moves the marketing site (`src/site/`) from **GitHub Pages** (the live host
today) to **Cloudflare Pages**. DNS for `cloudhealthoffice.com` is already on
Cloudflare (proxied to GitHub Pages), so the cutover is a DNS/custom-domain
change and is reversible.

> **Site location:** `src/site/`. The deployable artifact is the **build
> output** `src/site/dist/`, not `src/site/` itself: `npm run build` generates
> the MCC articles, injects analytics and the Formspree endpoint, and writes
> `dist/sitemap.xml` with per-page `lastmod` from git.

---

## What's in the repo

| File | Purpose |
|---|---|
| `.github/workflows/deploy-cloudflare-pages.yml` | Builds exactly like `deploy-pages.yml` (full history, `SITEMAP_STRICT=1`, test-metrics injection) and runs `wrangler pages deploy dist`. `main` → production branch; pull requests → `pr-<n>` preview aliases. Skips the upload with a warning until the secrets below exist. |
| `src/site/_redirects` | One rule (`/docs/ → /docs`). Everything else is Cloudflare Pages' default routing — see below. |
| `src/site/_headers` | Security headers (incl. HSTS) and cache policy. |
| `src/site/404.html` | Served automatically by Pages for unknown paths. |

### URL policy (and why `_redirects` is nearly empty)

Cloudflare Pages already does this by default, in one hop:

| File | Served at | Also redirected (308) |
|---|---|---|
| `founder.html` | `/founder` | `/founder.html`, `/founder/` |
| `schedule-demo/index.html` | `/schedule-demo/` | `/schedule-demo`, `/schedule-demo/index.html` |

That is the same URL for every page that GitHub Pages serves today, and it
matches the canonical tags and the sitemap. The previous 258-rule
`_redirects` (translated from the retired Azure `staticwebapp.config.json`)
restated this with `200` rewrites plus a `/*/` catch-all. On top of the
defaults, that **looped**: `/founder`, `/login`, `/schedule-demo`,
`/portal/` and every directory page redirected 6+ times under
`wrangler pages dev`. Pages also ignores everything after 100 dynamic rules
and rejects absolute URLs, so the `www` rule never ran. Tests in
`scripts/tests/site-services-positioning.test.ts` now fail on any rule that
restates the defaults.

### Cache headers

Pages **joins** header values from every matching `_headers` rule; the
more specific rule does not win. Each asset override therefore starts with
`! Cache-Control` to drop the `/*` default. CSS, JS and images have stable,
un-hashed filenames, so they get short caches (1 h with
stale-while-revalidate; images 7 days), never `immutable`.

### Verify locally

```bash
cd src/site && npm ci && npm run build
npx wrangler@4 pages dev dist --port 8788
# Restart the server after changing _redirects/_headers; hot reload is unreliable.
curl -sIL http://localhost:8788/founder.html        # one 308 -> /founder
curl -sIL http://localhost:8788/schedule-demo       # one 308 -> /schedule-demo/
curl -sI  http://localhost:8788/css/sentinel.css    # a single Cache-Control
```

---

## Manual steps

### 1. Add GitHub secrets (the only setup step)
Repository → **Settings → Secrets and variables → Actions**:
- `CLOUDFLARE_API_TOKEN`: create it at Cloudflare dashboard → **My Profile →
  API Tokens → Create Token → Custom token**, with permission
  **Account → Cloudflare Pages → Edit**, scoped to your account.
- `CLOUDFLARE_ACCOUNT_ID`: shown on the right side of the Cloudflare dashboard
  **Overview** page (or under **Workers & Pages**).

There is no need to create the project or upload anything by hand. On its first
run, the workflow creates the Pages project (`cloudhealthoffice`, production
branch `main`; set the repository variable `CLOUDFLARE_PAGES_PROJECT` to use
another name), then builds and uploads `src/site/dist`. Do **not** upload a
zip of the repo or of `src/site`: that is the unbuilt source, with no
sitemap, analytics, or generated articles.

### 2. Run the deploy
**Actions → Deploy marketing site to Cloudflare Pages → Run workflow** on
`main`, or push any change under `src/site/`.

### 3. Check the preview
On `https://cloudhealthoffice.pages.dev` (or the project's `*.pages.dev`
address), run the checklist at the bottom. `cloudhealthoffice.com` is still
served by GitHub Pages at this point.

### 4. Protect the portal (optional)
`/portal/*` is public on GitHub Pages today (it is `noindex` only). To gate it,
use **Cloudflare Access (Zero Trust)**: Access → Applications → Self-hosted,
domain `cloudhealthoffice.com`, path `/portal`. Entra ID can be reused as the
identity provider.

### 5. Cut over
1. Pages project → **Custom domains** → add `cloudhealthoffice.com`.
   Cloudflare replaces the GitHub Pages DNS record and provisions TLS.
   **Leave MX, SPF, DKIM and DMARC records untouched.**
2. Also add `www.cloudhealthoffice.com`, then **Rules → Redirect Rules** →
   create: *When* `http.host eq "www.cloudhealthoffice.com"`, *Then* Dynamic
   301 to `concat("https://cloudhealthoffice.com", http.request.uri.path)`,
   preserve query string. (GitHub Pages did this redirect before; `_redirects`
   cannot.)
3. Verify on the real domain (checklist below).
4. Rollback, if needed: remove the custom domain from the Pages project and
   restore the previous DNS records pointing at GitHub Pages.

### 6. After a week of clean Search Console data
- Disable GitHub Pages (repo **Settings → Pages**) and delete
  `.github/workflows/deploy-pages.yml`.
- Update `src/site/README.md` "Deployment" to name Cloudflare Pages as live.
- Resubmit `https://cloudhealthoffice.com/sitemap.xml` in Search Console.

---

## Verification checklist

- [ ] Homepage loads; `/nope` shows the custom 404 with status 404
- [ ] `/founder.html` and `/founder/` → one redirect → `/founder`
- [ ] `/schedule-demo` → one redirect → `/schedule-demo/`
- [ ] `/docs/` → `/docs`
- [ ] `/sitemap.xml` lists 83 URLs, and `lastmod` dates vary (not all the build day)
- [ ] Every response has `Strict-Transport-Security`, `X-Frame-Options`, `X-Content-Type-Options`
- [ ] `/css/sentinel.css` has a single `Cache-Control` value, without `immutable`
- [ ] The Formspree and Google Calendar demo forms still submit
- [ ] `www.cloudhealthoffice.com/pricing` → one 301 → `cloudhealthoffice.com/pricing`
- [ ] Google Analytics receives page views
