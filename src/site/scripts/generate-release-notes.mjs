// Builds the release notes section of the site from docs/releases.
//
//   docs/releases/RELEASE_NOTES_v6.0.0.md  ->  release-notes/v6-0-0.html
//   every published release                ->  release-notes.html (the hub)
//                                          ->  release-notes/feed.xml (RSS)
//
// The Markdown in docs/releases is the one copy of each release's notes; the
// site pages are generated from it, so a release shows up here as soon as its
// notes are merged with a real date. Notes whose "Release date" is not a date
// yet (for example "TBD" while a release is being prepared) are skipped.
//
// Releases before MIN_VERSION are not published here. The v3 notes describe
// the retired Azure Logic Apps runtime and internal planning material; the hub
// links to GitHub for those instead.
//
// Run by `npm run build`. The generated files are committed, like the Million
// Claim Challenge pages, so run `node scripts/generate-release-notes.mjs` and
// commit the output after changing release notes or this script.
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, posix, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { applyChrome } from './sync-site-chrome.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const siteRoot = resolve(scriptDir, '..');
const repoRoot = resolve(siteRoot, '../..');
const releasesDir = join(repoRoot, 'docs', 'releases');
const outputDir = join(siteRoot, 'release-notes');

const SITE = 'https://cloudhealthoffice.com';
const REPO = 'https://github.com/aurelianware/cloudhealthoffice';
const HUB = `${SITE}/release-notes`;
const FEED = `${HUB}/feed.xml`;
const MIN_VERSION = [5, 0, 0];

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

const escapeHtml = (value) => String(value)
  .replaceAll('&', '&amp;')
  .replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;')
  .replaceAll('"', '&quot;');

const compareVersions = (a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
const slugOf = (version) => `v${version.join('-')}`;

// GitHub's heading anchors, so links like #not-yet-done-for-cms-0057-f keep working.
const anchorOf = (heading) => heading
  .toLowerCase()
  .replace(/[^\p{L}\p{N}\s_-]/gu, '')
  .trim()
  .replace(/\s/g, '-');

function parseDate(text) {
  const m = text.match(/^([A-Z][a-z]+) (\d{1,2}), (\d{4})/);
  const month = m ? MONTHS.indexOf(m[1]) : -1;
  return month === -1 ? null : new Date(Date.UTC(Number(m[3]), month, Number(m[2])));
}

// ---------------------------------------------------------------- links

let published = new Map(); // "6.0.0" -> slug, filled before rendering

function resolveHref(href) {
  if (/^(https?:|mailto:)/.test(href) || href.startsWith('#')) return href;
  const [path, hash = ''] = href.split('#');
  const file = posix.basename(path);
  const sibling = file.match(/^RELEASE_NOTES_v(\d+\.\d+\.\d+)\.md$/);
  if (sibling && posix.normalize(posix.join('docs/releases', path)) === `docs/releases/${file}` && published.has(sibling[1])) {
    return `/release-notes/${published.get(sibling[1])}${hash ? `#${hash}` : ''}`;
  }
  const target = posix.normalize(posix.join('docs/releases', path));
  return `${REPO}/blob/main/${target}${hash ? `#${hash}` : ''}`;
}

function linkTag(label, href) {
  const resolved = resolveHref(href);
  const external = /^https?:/.test(resolved) && new URL(resolved).hostname !== new URL(SITE).hostname;
  return `<a href="${escapeHtml(resolved)}"${external ? ' rel="noopener"' : ''}>${label}</a>`;
}

function inline(text) {
  // Links and code spans are set aside first, so emphasis can span them and
  // nothing inside a code span is formatted or linked.
  const held = [];
  const hold = (html) => `\u0000${held.push(html) - 1}\u0000`;
  const html = escapeHtml(text
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, href) => hold(linkTag(inline(label), href)))
    .replace(/`([^`]+)`/g, (_, code) => hold(`<code>${escapeHtml(code)}</code>`)))
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*\w])\*([^*\s][^*]*)\*/g, '$1<em>$2</em>')
    .replace(/(^|[\s(,])#(\d{3,5})\b/g, `$1<a href="${REPO}/pull/$2" rel="noopener">#$2</a>`);
  return html.replace(/\u0000(\d+)\u0000/g, (_, i) => held[Number(i)]);
}

// ---------------------------------------------------------------- blocks

const LIST_ITEM = /^(\s*)([-*]|\d+\.)\s+(.*)$/;

function renderList(items) {
  let i = 0;
  const walk = (indent) => {
    const ordered = /\d/.test(items[i].marker);
    const start = ordered ? Number.parseInt(items[i].marker, 10) : 1;
    const out = [];
    while (i < items.length && items[i].indent >= indent) {
      if (items[i].indent > indent) {
        out.push(walk(items[i].indent));
        continue;
      }
      out.push(`<li>${inline(items[i].text)}`);
      i += 1;
      if (i < items.length && items[i].indent > indent) out.push(walk(items[i].indent));
      out.push('</li>');
    }
    const tag = ordered ? 'ol' : 'ul';
    return `<${tag}${ordered && start !== 1 ? ` start="${start}"` : ''}>${out.join('')}</${tag}>`;
  };
  const html = [];
  while (i < items.length) html.push(walk(items[i].indent));
  return html.join('\n');
}

function renderTable(rows) {
  const cells = rows.map((line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim()));
  const hasHeader = cells.length > 1 && cells[1].every((c) => /^:?-{2,}:?$/.test(c));
  const head = hasHeader ? `<thead><tr>${cells[0].map((c) => `<th>${inline(c)}</th>`).join('')}</tr></thead>` : '';
  const body = (hasHeader ? cells.slice(2) : cells).map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join('')}</tr>`).join('');
  return `<div class="rn-table"><table>${head}<tbody>${body}</tbody></table></div>`;
}

function renderMarkdown(lines) {
  const out = [];
  const headings = [];
  let paragraph = [];
  let list = [];
  let i = 0;

  const flushParagraph = () => {
    if (paragraph.length) out.push(`<p>${inline(paragraph.join(' '))}</p>`);
    paragraph = [];
  };
  const flushList = () => {
    if (list.length) out.push(renderList(list));
    list = [];
  };

  while (i < lines.length) {
    const line = lines[i];
    const item = line.match(LIST_ITEM);

    if (line.startsWith('```')) {
      flushParagraph(); flushList();
      const code = [];
      for (i += 1; i < lines.length && !lines[i].startsWith('```'); i += 1) code.push(lines[i]);
      out.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
    } else if (!line.trim()) {
      flushParagraph();
      // A list survives a blank line when the next line carries on with it.
      const next = lines.slice(i + 1).find((l) => l.trim());
      if (!(list.length && next && (LIST_ITEM.test(next) || /^\s+\S/.test(next)))) flushList();
    } else if (/^#{2,4} /.test(line)) {
      flushParagraph(); flushList();
      const level = line.match(/^#+/)[0].length;
      const text = line.slice(level + 1).trim();
      const id = anchorOf(text);
      headings.push({ level, text, id });
      out.push(`<h${level} id="${id}">${inline(text)}</h${level}>`);
    } else if (/^-{3,}\s*$/.test(line)) {
      // Section rules: every h2 already draws its own.
      flushParagraph(); flushList();
    } else if (item) {
      flushParagraph();
      list.push({ indent: item[1].length, marker: item[2], text: item[3] });
    } else if (list.length && /^\s+\S/.test(line)) {
      list[list.length - 1].text += ` ${line.trim()}`;
    } else if (line.trim().startsWith('|')) {
      flushParagraph(); flushList();
      const rows = [];
      for (; i < lines.length && lines[i].trim().startsWith('|'); i += 1) rows.push(lines[i]);
      out.push(renderTable(rows));
      continue;
    } else if (line.startsWith('>')) {
      flushParagraph(); flushList();
      const quote = [];
      for (; i < lines.length && lines[i].startsWith('>'); i += 1) quote.push(lines[i].replace(/^>\s?/, ''));
      out.push(`<blockquote><p>${inline(quote.join(' '))}</p></blockquote>`);
      continue;
    } else {
      flushList();
      paragraph.push(line.trim());
    }
    i += 1;
  }
  flushParagraph();
  flushList();
  return { html: out.join('\n'), headings };
}

// ---------------------------------------------------------------- releases

function readRelease(file) {
  const version = file.match(/^RELEASE_NOTES_v(\d+)\.(\d+)\.(\d+)\.md$/).slice(1).map(Number);
  const lines = readFileSync(join(releasesDir, file), 'utf8').replace(/\r/g, '').split('\n');
  const titleAt = lines.findIndex((l) => l.startsWith('# '));
  const title = lines[titleAt].slice(2).trim();
  const codename = title.split(/\s+[—–-]\s+/)[1] ?? '';

  // "**Release date:** October 28, 2026" lines directly under the title.
  const meta = {};
  let at = titleAt + 1;
  while (at < lines.length && !lines[at].trim()) at += 1;
  for (; at < lines.length && /^\*\*[^*]+:\*\*/.test(lines[at]); at += 1) {
    const [, key, value] = lines[at].match(/^\*\*([^*]+):\*\*\s*(.*)$/);
    meta[key.trim().toLowerCase()] = value.trim();
  }
  const date = parseDate(meta['release date'] ?? '');
  const body = lines.slice(at);

  // The Highlights subsections are the release in a sentence: they become the
  // page description, the hub card and the feed item.
  const highlights = [];
  let inHighlights = false;
  for (const line of body) {
    if (line.startsWith('## ')) inHighlights = /^## highlights/i.test(line);
    else if (inHighlights && line.startsWith('### ')) {
      highlights.push(line.slice(4).replace(/\s*\((?:[GD]-\d+(?:,\s*)?)+\)/g, '').replace(/[`*]/g, '').trim());
    }
  }

  return { file, version, versionText: version.join('.'), slug: slugOf(version), title, codename, meta, date, body, highlights };
}

function describe(release) {
  const lead = `Cloud Health Office v${release.versionText}${release.codename ? ` (${release.codename})` : ''}: `;
  let text = lead;
  for (const [n, h] of release.highlights.entries()) {
    const next = `${n ? '; ' : ''}${n ? h.charAt(0).toLowerCase() + h.slice(1) : h}`;
    if ((text + next).length > 160 && n) break;
    text += next;
  }
  return `${text}.`;
}

const formatDate = (date) => `${MONTHS[date.getUTCMonth()]} ${date.getUTCDate()}, ${date.getUTCFullYear()}`;
const isoDate = (date) => date.toISOString().slice(0, 10);

// ---------------------------------------------------------------- pages

const jsonLd = (graph) => JSON.stringify({ '@context': 'https://schema.org', '@graph': graph }, null, 2).replace(/</g, '\\u003c');
const PUBLISHER = { '@type': 'Organization', name: 'Cloud Health Office', url: `${SITE}/`, logo: { '@type': 'ImageObject', url: `${SITE}/graphics/logo-cloudhealthoffice-sentinel-primary.svg` } };
const breadcrumb = (crumbs) => ({
  '@type': 'BreadcrumbList',
  itemListElement: [['Home', `${SITE}/`], ...crumbs].map(([name, item], n) => ({ '@type': 'ListItem', position: n + 1, name, item }))
});

const STYLE = `
    .rn { max-width: 860px; margin: 0 auto; padding: 48px 20px 96px; }
    .rn-crumbs { font-size: .9rem; color: var(--cho-muted); margin-bottom: 28px; }
    .rn-crumbs a { color: var(--cho-accent); }
    .rn-eyebrow { color: var(--cho-accent); font-size: .8rem; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; }
    .rn h1 { font-size: clamp(2rem, 4.5vw, 3rem); line-height: 1.1; margin: 10px 0 18px; }
    .rn h2 { margin-top: 56px; padding-top: 8px; border-top: 1px solid var(--cho-line); font-size: 1.7rem; }
    .rn h3 { margin-top: 36px; font-size: 1.25rem; }
    .rn h4 { margin-top: 28px; font-size: 1.05rem; }
    .rn p, .rn li { font-size: 1.04rem; line-height: 1.75; color: var(--cho-body); }
    .rn li { margin: 6px 0; }
    .rn a { color: var(--cho-accent); }
    .rn a:hover { color: var(--cho-accent-hover); }
    .rn code { font-family: var(--cho-mono); font-size: .88em; background: var(--cho-surface-2); padding: .1rem .35rem; border-radius: 4px; overflow-wrap: anywhere; }
    .rn pre { background: var(--cho-navy); color: var(--cho-navy-ink); padding: 18px 20px; border-radius: 8px; overflow-x: auto; }
    .rn pre code { background: none; padding: 0; color: inherit; }
    .rn hr { border: 0; border-top: 1px solid var(--cho-line); margin: 40px 0; }
    .rn blockquote { margin: 24px 0; padding: 4px 20px; border-left: 4px solid var(--cho-accent); background: var(--cho-surface); }
    .rn-meta { display: flex; flex-wrap: wrap; gap: 8px 28px; margin: 0 0 28px; padding: 16px 20px; background: var(--cho-surface); border: 1px solid var(--cho-line); border-radius: 8px; font-size: .95rem; }
    .rn-meta dt { font-weight: 600; color: var(--cho-ink); }
    .rn-meta dd { margin: 0; }
    .rn-meta div { display: flex; gap: 6px; }
    .rn-toc { margin: 0 0 32px; padding: 16px 20px; border: 1px solid var(--cho-line); border-radius: 8px; }
    .rn-toc p { margin: 0 0 6px; font-weight: 600; color: var(--cho-ink); }
    .rn-toc ul { margin: 0; padding-left: 20px; }
    .rn-toc li { font-size: .97rem; margin: 2px 0; }
    .rn-table { margin: 24px 0; overflow-x: auto; border: 1px solid var(--cho-line); border-radius: 8px; }
    .rn-table table { margin: 0; width: 100%; border-collapse: collapse; font-size: .95rem; }
    .rn-table th, .rn-table td { padding: 10px 14px; text-align: left; border-bottom: 1px solid var(--cho-line); vertical-align: top; }
    .rn-table th { background: var(--cho-surface); color: var(--cho-ink); }
    .rn-actions { display: flex; flex-wrap: wrap; gap: 12px; margin: 28px 0; }
    .rn-actions a { border: 1px solid var(--cho-line-strong); border-radius: 6px; padding: 9px 14px; text-decoration: none; font-weight: 500; }
    .rn-pager { display: flex; justify-content: space-between; gap: 16px; margin-top: 64px; padding-top: 24px; border-top: 1px solid var(--cho-line); }
    .rn-card { margin: 28px 0; padding: 24px; border: 1px solid var(--cho-line); border-radius: 10px; }
    .rn-card h2 { margin: 6px 0 4px; padding: 0; border: 0; font-size: 1.5rem; }
    .rn-card h2 a { color: var(--cho-ink); text-decoration: none; }
    .rn-card time { color: var(--cho-muted); font-size: .95rem; }
    .rn-card ul { margin: 14px 0 0; }
    .rn-latest { border-color: var(--cho-accent); box-shadow: 0 0 0 1px var(--cho-accent); }`;

function page({ route, title, description, ogType, ld, body, published: datePublished }) {
  const canonical = `${SITE}${route}`;
  const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${escapeHtml(title)}</title>
  <meta name="description" content="${escapeHtml(description)}">
  <link rel="canonical" href="${canonical}">
  <link rel="alternate" type="application/rss+xml" title="Cloud Health Office release notes" href="/release-notes/feed.xml">
  <meta property="og:type" content="${ogType}">
  <meta property="og:site_name" content="Cloud Health Office">
  <meta property="og:url" content="${canonical}">
  <meta property="og:title" content="${escapeHtml(title)}">
  <meta property="og:description" content="${escapeHtml(description)}">
  <meta property="og:image" content="${SITE}/graphics/logo-cloudhealthoffice-sentinel-primary.svg">${datePublished ? `\n  <meta property="article:published_time" content="${datePublished}">` : ''}
  <meta name="twitter:card" content="summary">
  <script type="application/ld+json">
${ld}
  </script>
  <link rel="stylesheet" href="/css/sentinel.css">
  <style>${STYLE}
  </style>
</head>
<body>
<nav class="site-nav"><ul id="mainNav"></ul></nav>
<main id="main-content" class="rn">
${body.trim()}
</main>
<footer></footer>
</body>
</html>
`;
  return applyChrome(html, route);
}

function releasePage(release, newer, older) {
  const route = `/release-notes/${release.slug}`;
  const description = describe(release);
  const { html, headings } = renderMarkdown(release.body);
  const toc = headings.filter((h) => h.level === 2);
  const metaRows = [['Released', formatDate(release.date)]];
  if (older) metaRows.push(['Previous release', `<a href="/release-notes/${older.slug}">v${older.versionText}</a>`]);
  else if (release.meta['previous release']) metaRows.push(['Previous release', inline(release.meta['previous release'])]);
  for (const [key, value] of Object.entries(release.meta)) {
    if (key !== 'release date' && key !== 'previous release') {
      metaRows.push([key.charAt(0).toUpperCase() + key.slice(1), inline(value)]);
    }
  }
  const heading = release.codename ? `v${release.versionText}: ${release.codename}` : `v${release.versionText}`;
  const ld = jsonLd([
    {
      '@type': 'TechArticle',
      headline: `Cloud Health Office ${heading} release notes`,
      description,
      url: `${SITE}${route}`,
      mainEntityOfPage: `${SITE}${route}`,
      datePublished: isoDate(release.date),
      author: { '@type': 'Organization', name: 'Aurelianware, Inc.' },
      publisher: PUBLISHER,
      about: { '@type': 'SoftwareApplication', name: 'Cloud Health Office', softwareVersion: release.versionText, applicationCategory: 'BusinessApplication' }
    },
    breadcrumb([['Release notes', HUB], [`v${release.versionText}`, `${SITE}${route}`]])
  ]);
  const body = `
<div class="rn-crumbs"><a href="/">Home</a> / <a href="/release-notes">Release notes</a> / v${release.versionText}</div>
<div class="rn-eyebrow">Release notes</div>
<h1>${escapeHtml(heading)}</h1>
<dl class="rn-meta">${metaRows.map(([k, v]) => `<div><dt>${escapeHtml(k)}</dt><dd>${v}</dd></div>`).join('')}</dl>
${toc.length > 2 ? `<aside class="rn-toc" aria-label="On this page"><p>On this page</p><ul>${toc.map((h) => `<li><a href="#${h.id}">${escapeHtml(h.text.replace(/[`*]/g, ''))}</a></li>`).join('')}</ul></aside>` : ''}
${html}
<div class="rn-actions">
<a href="${REPO}/blob/main/docs/releases/${release.file}" rel="noopener">Source of these notes</a>
<a href="${REPO}/blob/main/CHANGELOG.md" rel="noopener">Full changelog</a>
<a href="/release-notes/feed.xml">Subscribe (RSS)</a>
</div>
<div class="rn-pager">
<span>${older ? `<a href="/release-notes/${older.slug}">&larr; v${older.versionText}${older.codename ? ` ${escapeHtml(older.codename)}` : ''}</a>` : ''}</span>
<span>${newer ? `<a href="/release-notes/${newer.slug}">v${newer.versionText}${newer.codename ? ` ${escapeHtml(newer.codename)}` : ''} &rarr;</a>` : '<a href="/release-notes">All releases</a>'}</span>
</div>`;
  return page({
    route,
    title: `${heading} Release Notes | Cloud Health Office`,
    description,
    ogType: 'article',
    ld,
    body,
    published: isoDate(release.date)
  });
}

function hubPage(releases) {
  const latest = releases[0];
  const description = `What changed in each Cloud Health Office release: claims, pricing, CMS-0057-F APIs and platform, with breaking changes and upgrade notes. Latest: v${latest.versionText}${latest.codename ? ` ${latest.codename}` : ''}.`;
  const ld = jsonLd([
    {
      '@type': 'CollectionPage',
      name: 'Cloud Health Office release notes',
      description,
      url: HUB,
      publisher: PUBLISHER,
      mainEntity: {
        '@type': 'ItemList',
        itemListElement: releases.map((r, n) => ({ '@type': 'ListItem', position: n + 1, url: `${SITE}/release-notes/${r.slug}`, name: `v${r.versionText}${r.codename ? ` ${r.codename}` : ''}` }))
      }
    },
    breadcrumb([['Release notes', HUB]])
  ]);
  const cards = releases.map((r, n) => `
<article class="rn-card${n === 0 ? ' rn-latest' : ''}">
<div class="rn-eyebrow">${n === 0 ? 'Latest release' : 'Release'}</div>
<h2 id="${r.slug}"><a href="/release-notes/${r.slug}">v${r.versionText}${r.codename ? `: ${escapeHtml(r.codename)}` : ''}</a></h2>
<time datetime="${isoDate(r.date)}">${formatDate(r.date)}</time>
${r.highlights.length ? `<ul>${r.highlights.map((h) => `<li>${escapeHtml(h)}</li>`).join('')}</ul>` : ''}
<div class="rn-actions"><a href="/release-notes/${r.slug}">Read the v${r.versionText} notes</a></div>
</article>`).join('');
  const body = `
<div class="rn-crumbs"><a href="/">Home</a> / Release notes</div>
<div class="rn-eyebrow">Version history</div>
<h1>Cloud Health Office Release Notes</h1>
<p>What changed in each release of Cloud Health Office, the core administration platform for health plans. Each release lists its highlights, breaking changes, upgrade steps, and what is validated and what is not yet done.</p>
<div class="rn-actions"><a href="/release-notes/feed.xml">Subscribe (RSS)</a><a href="${REPO}/blob/main/CHANGELOG.md" rel="noopener">Full changelog</a><a href="/cms-0057f-compliance">CMS-0057-F compliance status</a></div>
${cards}
<h2 id="earlier-releases">Earlier releases</h2>
<p>Notes for v4.0.0 and earlier are on <a href="${REPO}/releases" rel="noopener">GitHub releases</a>, and every change is itemized in the <a href="${REPO}/blob/main/CHANGELOG.md" rel="noopener">changelog</a>.</p>`;
  return page({ route: '/release-notes', title: 'Cloud Health Office Release Notes | Payer Platform', description, ogType: 'website', ld, body });
}

function feed(releases) {
  const items = releases.map((r) => `    <item>
      <title>${escapeHtml(`Cloud Health Office v${r.versionText}${r.codename ? `: ${r.codename}` : ''}`)}</title>
      <link>${SITE}/release-notes/${r.slug}</link>
      <guid isPermaLink="true">${SITE}/release-notes/${r.slug}</guid>
      <pubDate>${r.date.toUTCString()}</pubDate>
      <description>${escapeHtml(describe(r))}</description>
    </item>`).join('\n');
  return `<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
  <channel>
    <title>Cloud Health Office release notes</title>
    <link>${HUB}</link>
    <atom:link href="${FEED}" rel="self" type="application/rss+xml"/>
    <description>New releases of Cloud Health Office, the core administration platform for health plans.</description>
    <language>en-us</language>
    <lastBuildDate>${releases[0].date.toUTCString()}</lastBuildDate>
${items}
  </channel>
</rss>
`;
}

// ---------------------------------------------------------------- main

const releases = readdirSync(releasesDir)
  .filter((f) => /^RELEASE_NOTES_v\d+\.\d+\.\d+\.md$/.test(f))
  .map(readRelease)
  .filter((r) => compareVersions(r.version, MIN_VERSION) >= 0)
  .filter((r) => {
    if (!r.date) console.log(`Skipping ${r.file}: release date "${r.meta['release date'] ?? ''}" is not a date yet.`);
    return r.date;
  })
  .sort((a, b) => compareVersions(b.version, a.version));

if (!releases.length) throw new Error(`No dated release notes at or above v${MIN_VERSION.join('.')} in ${releasesDir}`);
published = new Map(releases.map((r) => [r.versionText, r.slug]));

mkdirSync(outputDir, { recursive: true });
for (const [n, release] of releases.entries()) {
  writeFileSync(join(outputDir, `${release.slug}.html`), releasePage(release, releases[n - 1], releases[n + 1]));
}
writeFileSync(join(siteRoot, 'release-notes.html'), hubPage(releases));
writeFileSync(join(outputDir, 'feed.xml'), feed(releases));

console.log(`Generated ${releases.length} release pages (${releases.map((r) => `v${r.versionText}`).join(', ')}), the release notes hub and its RSS feed.`);
