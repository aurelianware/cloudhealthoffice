# Cloudflare Pages Migration — Cloud Health Office marketing site

This document covers migrating the marketing website (`src/site/`) from
**Azure Static Web Apps (SWA)** to **Cloudflare Pages**, and the steps you must
perform **manually in the Cloudflare dashboard** (they can't be done from the
repo).

> **Site location:** the site lives at **`src/site/`** (there is no top-level
> `site/` folder). Use `src/site` wherever an output/root directory is requested.

---

## What's already in the repo (done for you)

These file-based config files were translated from `staticwebapp.config.json`
and live next to the site so Cloudflare Pages picks them up automatically:

| File | Purpose |
|---|---|
| `src/site/_redirects` | `*.html` → clean-URL 301 redirects + clean-URL → `.html` 200 rewrites |
| `src/site/_headers` | Global security headers + per-path `Cache-Control` |
| `src/site/404.html` | Custom 404 page (Cloudflare serves it automatically) |

What was **dropped** in translation and why:

- **`mimeTypes`** — unnecessary on Cloudflare; Content-Type is inferred from the
  file extension.
- **`navigationFallback` / `404` rewrite to `/index.html`** — this is a
  multi-page static site, so a SPA catch-all (`/* /index.html 200`) was
  deliberately **not** added (it would swallow real pages). A `404.html` is the
  correct equivalent.
- **Azure AD auth** (`auth` block, `allowedRoles: authenticated` on `/portal/*`
  and `/api/*`, `401 → /.auth/login/aad`) — **cannot** be expressed in
  file-based config. See **"Protect the portal"** below.

---

## Deploy method — recommendation: Cloudflare Git integration

**Recommended: Git integration (no workflow file).**

The site ships as **pre-built static HTML** (the Markdown→HTML output is already
committed, and the old Azure deploy used `skip_app_build: true`). So there is no
build to run, which makes Git integration the simplest, lowest-maintenance
option.

| | Git integration (recommended) | `wrangler pages deploy` GitHub Action |
|---|---|---|
| Setup | Connect repo once in dashboard; no YAML | Add workflow + `CLOUDFLARE_API_TOKEN`/`CLOUDFLARE_ACCOUNT_ID` secrets |
| Maintenance | None in-repo | Maintain a workflow file |
| Preview deploys | Automatic per PR | Manual to wire up |
| Control over build | Limited (dashboard settings) | Full (can run `npm run build:site`, metrics injection) |
| Secrets in GitHub | None needed | API token stored in repo secrets |

Because the deploy is "upload static files," the extra control of a wrangler
Action buys little here. **No GitHub workflow was added** — that's intentional.
If you later need the optional `npm run build:site` (regenerates
`assessment.html`) or `scripts/inject-test-metrics.js` to run on every deploy,
switch to a wrangler Action or set a Cloudflare build command (see note below).

### Build settings to enter in the dashboard

| Setting | Value |
|---|---|
| **Framework preset** | None |
| **Build command** | *(leave empty)* |
| **Build output directory** | `src/site` |
| **Root directory** | `/` (repo root) |

> Optional: if you want the Markdown build to run on Cloudflare, set the build
> command to `npm install && npm run build:site` — but note the `build:site`
> script currently points at `site/js/...` and must be fixed to
> `src/site/js/markdown-converter.js` first. Not required, since the HTML is
> already committed.

---

## Manual steps in the Cloudflare dashboard

### 1. Create the Pages project
1. Cloudflare Dashboard → **Workers & Pages** → **Create** → **Pages** →
   **Connect to Git**.
2. Authorize and select the `aurelianware/cloudhealthoffice` repository.
3. Set the **production branch** to `main`.
4. Enter the build settings from the table above (build command empty, output
   directory `src/site`).
5. **Save and Deploy.**

### 2. Set build environment variables
Project → **Settings** → **Environment variables / Build** (only needed if you
enable a build command):
- `NODE_VERSION = 20`

### 3. Add the custom domain (`cloudhealthoffice.com`)
DNS is already on Cloudflare, so this is a few clicks:
1. Project → **Custom domains** → **Set up a custom domain**.
2. Add `cloudhealthoffice.com` (and `www.cloudhealthoffice.com` if you want the
   `www` host too).
3. Cloudflare auto-creates the CNAME and provisions the TLS certificate. No
   ALIAS/ANAME juggling (that was an Azure constraint) — Cloudflare flattens the
   apex CNAME automatically.
4. If you add `www`, optionally add a redirect rule (`www` → apex, or vice
   versa) under **Rules → Redirect Rules**.

### 4. Protect the portal (replaces Azure AD auth) — **important**
The old config gated `/portal/*` (and `/api/*`) behind Azure AD login. Pages
file config can't do this. Use **Cloudflare Access (Zero Trust)**:
1. Zero Trust dashboard → **Access** → **Applications** → **Add an application**
   → **Self-hosted**.
2. Application domain: `cloudhealthoffice.com`, path `/portal` (add another for
   `/portal/*`).
3. Add an **identity provider** (Azure AD / Entra ID is supported, so you can
   reuse the existing app registration) under **Settings → Authentication**.
4. Create an **Access policy** (e.g. allow your org's email domain, or specific
   users/groups).
5. Repeat for any `/api/*` paths that must stay authenticated.

> If the portal isn't launched yet / doesn't need gating at go-live, you can
> skip this and add it later — but the pages will be **publicly reachable** in
> the meantime.

### 5. Verify, then clean up
1. Confirm the `*.pages.dev` preview and the custom domain serve the site and
   that clean URLs, redirects, headers, and the 404 page behave as expected.
2. Disable/disconnect the Azure Static Web App so it no longer serves or holds
   the custom domain (do this in the Azure portal).
3. In the repo, remove the Azure artifacts (handled as **Phase 4** — see below)
   once you've confirmed Cloudflare is correct.

---

## Phase 4 — repo cleanup (pending your confirmation)

After you confirm the Cloudflare setup works, these will be removed/updated:
- **Delete** `.github/workflows/deploy-static-site.yml`
- **Delete** `src/site/staticwebapp.config.json`
- **Update** docs that reference Azure SWA: `src/site/README.md`,
  `src/site/DEPLOYMENT.md`, `src/site/IMPLEMENTATION-SUMMARY.md` (and relevant
  `docs/**` files).

`Dockerfile` / `nginx.conf` in `src/site/` are unrelated to SWA (they're for the
container/AKS path) and will be left alone unless you say otherwise.

---

## Quick verification checklist

- [ ] `https://<project>.pages.dev/` loads the homepage
- [ ] `/pricing` serves `pricing.html`; `/pricing.html` 301s to `/pricing`
- [ ] `/docs` serves the docs index
- [ ] A bogus URL (e.g. `/nope`) shows the custom **404.html**
- [ ] Response headers include `X-Frame-Options`, `X-Content-Type-Options`, etc.
- [ ] `/css/*`, `/js/*`, `/graphics/*` return `Cache-Control: ...immutable`
- [ ] `cloudhealthoffice.com` resolves to Pages with valid TLS
- [ ] `/portal` is gated by Cloudflare Access (if required at launch)
