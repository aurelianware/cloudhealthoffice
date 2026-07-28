import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const siteRoot = resolve(scriptDir, '..');
const repoRoot = resolve(siteRoot, '../..');
const outputDir = join(siteRoot, 'insights', 'million-claim-challenge');
const githubBase = 'https://github.com/aurelianware/cloudhealthoffice/blob/main/docs/million-claim-challenge/podcast';

const publications = [
  {
    episode: '005',
    part: 'Part 5',
    slug: 'part-5-repeatably-faster',
    summary: 'How the local Kubernetes sweep harness turned isolated fast runs into repeatable performance evidence.'
  },
  {
    episode: '006',
    part: 'Part 6',
    slug: 'part-6-honest-edge-case-scoring',
    summary: 'Why paid is not the only correct outcome, and why unsupported scenarios must remain visible.'
  },
  {
    episode: '007',
    part: 'Part 7',
    slug: 'part-7-operator-console',
    summary: 'Moving benchmark proof from terminal logs into an operator-facing Mass Adjudication console.'
  },
  {
    episode: '008',
    part: 'Part 8',
    slug: 'part-8-clean-100k',
    summary: 'How stronger correctness gates reached a clean 100,000-claim run and exposed the next local scaling bottleneck.'
  },
  {
    episode: '009',
    part: 'Part 9',
    slug: 'part-9-from-unsupported-to-scored',
    summary: 'How prior-auth wrong-provider and behavioral-health scenarios moved from unsupported to deliberately scored platform behavior.'
  },
  {
    episode: '010',
    part: 'Part 10',
    slug: 'part-10-the-migration-cost-that-wasnt',
    summary: 'Testing Part 9\'s unconfirmed migration-cost theory at 250,000 claims, finding the real causes through profiling, and confirming a 3.5x throughput gain.'
  },
  {
    episode: '011',
    part: 'Part 11',
    slug: 'part-11-the-check-that-only-ran-in-the-benchmark',
    summary: 'Re-investigating a bug Part 10 disclosed but didn\'t fix, and finding federal provider-exclusion screening had never been wired into the real adjudication pipeline at all.'
  },
  {
    episode: '012',
    part: 'Part 12',
    slug: 'part-12-the-database-nobody-profiled',
    summary: 'Two benchmark fixture bugs, a Submit-chain bottleneck traced to an under-provisioned shared database, and this series\' first clean 500,000-claim confirmation.'
  },
  {
    episode: '013',
    part: 'Part 13',
    slug: 'part-13-the-gap-was-the-laptop',
    summary: 'Closing the wall-clock gap disclosed in Part 10 and Part 12 by proving, with matching host sleep-log evidence, that it was macOS suspending the local Kubernetes cluster mid-run.'
  },
  {
    episode: '014',
    part: 'Part 14',
    slug: 'part-14-zero-unsupported-parallelism',
    summary: 'Closing the scoring gap carried since Part 9 with a first-ever zero-unsupported run, then finding and fixing why parallelism 56 had quietly underperformed lower concurrency this whole series.'
  },
  {
    episode: '015',
    part: 'Part 15',
    slug: 'part-15-one-million-claims',
    summary: 'Running this series\' first full 1,000,000-claim confirmation, finding a Redis memory ceiling that only that scale could expose, and fixing it live.'
  },
  {
    episode: '016',
    part: 'Part 16',
    slug: 'part-16-the-million-went-through-the-bus',
    summary: 'Moving the full million-claim corpus onto asynchronous Service Bus adjudication, then proving the separate raw X12 837 onramp at 100,000 claims.'
  }
];

const escapeHtml = (value) => value
  .replaceAll('&', '&amp;')
  .replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;')
  .replaceAll('"', '&quot;');

const inlineMarkdown = (value) => escapeHtml(value)
  .replace(/`([^`]+)`/g, '<code>$1</code>')
  .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
  .replace(/\*([^*]+)\*/g, '<em>$1</em>')
  .replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
  .replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener noreferrer">$1</a>');

function renderArticle(source) {
  const lines = source.replace(/\r/g, '').split('\n');
  const title = lines.find((line) => line.startsWith('# '))?.slice(2).trim() ?? 'Million Claim Challenge';
  const body = [];
  let paragraph = [];
  let list = [];
  let table = [];

  const flushParagraph = () => {
    if (paragraph.length) body.push(`<p>${inlineMarkdown(paragraph.join(' '))}</p>`);
    paragraph = [];
  };
  const flushList = () => {
    if (list.length) body.push(`<ul>${list.map((item) => `<li>${inlineMarkdown(item)}</li>`).join('')}</ul>`);
    list = [];
  };
  const splitTableRow = (line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim());
  const isTableSeparatorRow = (cells) => cells.length > 0 && cells.every((cell) => cell === '' || /^:?-{2,}:?$/.test(cell));
  const flushTable = () => {
    if (!table.length) return;
    const rows = table.map(splitTableRow);
    const hasHeader = rows.length > 1 && isTableSeparatorRow(rows[1]);
    const headerRow = hasHeader ? rows[0] : null;
    const bodyRows = hasHeader ? rows.slice(2) : rows;
    const thead = headerRow
      ? `<thead><tr>${headerRow.map((cell) => `<th>${inlineMarkdown(cell)}</th>`).join('')}</tr></thead>`
      : '';
    const tbody = `<tbody>${bodyRows.map((row) => `<tr>${row.map((cell) => `<td>${inlineMarkdown(cell)}</td>`).join('')}</tr>`).join('')}</tbody>`;
    body.push(`<div class="table-wrap"><table>${thead}${tbody}</table></div>`);
    table = [];
  };

  for (const line of lines.slice(lines.indexOf(`# ${title}`) + 1)) {
    if (!line.trim()) {
      flushParagraph();
      flushList();
      flushTable();
    } else if (line.startsWith('## ')) {
      flushParagraph();
      flushList();
      flushTable();
      const heading = line.slice(3).trim();
      const id = heading.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
      body.push(`<h2 id="${id}">${inlineMarkdown(heading)}</h2>`);
    } else if (line.startsWith('- ')) {
      flushParagraph();
      flushTable();
      list.push(line.slice(2));
    } else if (line.trim().startsWith('|')) {
      flushParagraph();
      flushList();
      table.push(line);
    } else if (line.startsWith('![')) {
      flushParagraph();
      flushList();
      flushTable();
      const match = line.match(/^!\[([^\]]*)\]\(([^)\s]+)(?:\s+"([^"]*)")?\)$/);
      if (match) {
        const [, alt, src, caption] = match;
        const figcaption = caption ? `<figcaption>${inlineMarkdown(caption)}</figcaption>` : '';
        body.push(`<figure class="article-figure"><img src="${escapeHtml(src)}" alt="${escapeHtml(alt)}" loading="lazy">${figcaption}</figure>`);
      }
    } else if (line.startsWith('Published article: ')) {
      flushParagraph();
      flushTable();
      body.push(`<p class="source-note">Originally published externally: ${inlineMarkdown(line.slice(19))}</p>`);
    } else {
      flushList();
      flushTable();
      paragraph.push(line.trim());
    }
  }
  flushParagraph();
  flushList();
  flushTable();
  return { title, body: body.join('\n') };
}

const pageShell = ({ title, description, canonical, content, type = 'article' }) => `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${escapeHtml(title)} | Cloud Health Office</title>
  <meta name="description" content="${escapeHtml(description)}">
  <link rel="canonical" href="${canonical}">
  <meta property="og:type" content="${type}">
  <meta property="og:site_name" content="Cloud Health Office">
  <meta property="og:title" content="${escapeHtml(title)}">
  <meta property="og:description" content="${escapeHtml(description)}">
  <meta property="og:url" content="${canonical}">
  <meta property="og:image" content="https://cloudhealthoffice.com/graphics/og-million-claim-challenge.png">
  <meta name="twitter:card" content="summary_large_image">
  <link rel="stylesheet" href="/css/sentinel.css">
  <script src="/js/mobile-nav.js"></script>
  <style>
    .publication { max-width: 860px; margin: 0 auto; padding: 64px 24px 96px; }
    .publication-nav { margin-bottom: 40px; color: #8b949e; }
    .publication-nav a { color: #00ffff; }
    .eyebrow { color: #00ff88; font-size: .8rem; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; }
    .publication h1 { font-size: clamp(2rem, 5vw, 3.4rem); line-height: 1.08; margin: 12px 0 20px; }
    .publication h2 { margin-top: 52px; color: #00ffff; }
    .publication p, .publication li { color: #c9d1d9; font-size: 1.05rem; line-height: 1.8; }
    .publication code { color: #00ff88; background: #111820; padding: .12rem .35rem; border-radius: 4px; }
    .disclosure { border-left: 4px solid #f0b429; background: rgba(240,180,41,.08); padding: 18px 22px; margin: 30px 0; }
    .source-note { font-size: .9rem !important; color: #8b949e !important; }
    .article-links { display: flex; flex-wrap: wrap; gap: 12px; margin: 40px 0; }
    .article-links a { border: 1px solid rgba(0,255,255,.35); border-radius: 6px; padding: 10px 14px; color: #00ffff; text-decoration: none; }
    .evidence-block { margin: 28px 0; border: 1px solid rgba(0,255,255,.22); border-radius: 8px; overflow: hidden; }
    .evidence-block summary { cursor: pointer; padding: 18px 20px; color: #00ffff; font-weight: 700; background: #0d1117; }
    .evidence-block pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; padding: 22px; background: #080c10; color: #c9d1d9; font-size: .84rem; line-height: 1.55; }
    .article-figure { margin: 40px 0; }
    .article-figure img { width: 100%; height: auto; display: block; border-radius: 10px; border: 1px solid rgba(0,255,255,.18); background: #000; }
    .article-figure figcaption { margin-top: 12px; font-size: .85rem; color: #8b949e; text-align: center; line-height: 1.5; }
    .table-wrap { margin: 28px 0; overflow-x: auto; border: 1px solid rgba(0,255,255,.22); border-radius: 8px; }
    .table-wrap table { width: 100%; border-collapse: collapse; font-size: .95rem; font-variant-numeric: tabular-nums; }
    .table-wrap th, .table-wrap td { padding: 10px 16px; text-align: left; border-bottom: 1px solid rgba(139,148,158,.2); white-space: nowrap; }
    .table-wrap th { color: #00ffff; font-weight: 700; background: #0d1117; }
    .table-wrap td { color: #c9d1d9; }
    .table-wrap tr:last-child td { border-bottom: none; }
  </style>
</head>
<body>
  <main class="publication">
    <nav class="publication-nav"><a href="/">Home</a> / <a href="/insights">Insights</a> / <a href="/docs/million-claim-challenge">Million Claim Challenge</a></nav>
    ${content.trim()}
  </main>
</body>
</html>`;

mkdirSync(outputDir, { recursive: true });

for (const publication of publications) {
  const episodeDir = join(repoRoot, 'docs', 'million-claim-challenge', 'podcast', `episode-${publication.episode}`);
  const rendered = renderArticle(readFileSync(join(episodeDir, 'article.txt'), 'utf8'));
  const canonical = `https://cloudhealthoffice.com/insights/million-claim-challenge/${publication.slug}`;
  const content = `
    <div class="eyebrow">Million Claim Challenge · ${publication.part}</div>
    <h1>${escapeHtml(rendered.title)}</h1>
    <div class="disclosure"><strong>Evidence scope:</strong> This engineering field note describes local Kubernetes development and validation. It is not a production-cloud capacity claim. Exact results and limitations are preserved in the linked evidence artifact.</div>
    <div class="article-links">
      <a href="/docs/million-claim-challenge/evidence#episode-${publication.episode}">View evidence</a>
      <a href="${githubBase}/episode-${publication.episode}/article.txt" target="_blank" rel="noopener noreferrer">View article source</a>
    </div>
    ${rendered.body}
    <div class="article-links"><a href="/insights/million-claim-challenge">All MCC articles</a><a href="/docs/million-claim-challenge/evidence">Evidence archive</a></div>`;
  writeFileSync(join(outputDir, `${publication.slug}.html`), pageShell({ title: rendered.title, description: publication.summary, canonical, content }));
}

const cards = publications.map((publication) => {
  const article = renderArticle(readFileSync(join(repoRoot, 'docs', 'million-claim-challenge', 'podcast', `episode-${publication.episode}`, 'article.txt'), 'utf8'));
  return `<article class="evidence-block" style="padding:22px"><div class="eyebrow">${publication.part}</div><h2 style="margin-top:8px">${escapeHtml(article.title)}</h2><p>${escapeHtml(publication.summary)}</p><div class="article-links"><a href="/insights/million-claim-challenge/${publication.slug}">Read article</a><a href="/docs/million-claim-challenge/evidence#episode-${publication.episode}">Inspect evidence</a></div></article>`;
}).join('\n');

writeFileSync(join(outputDir, 'index.html'), pageShell({
  title: 'Million Claim Challenge Engineering Series',
  description: 'Engineering field notes documenting how the Million Claim Challenge became a repeatable, inspectable claims-adjudication benchmark.',
  canonical: 'https://cloudhealthoffice.com/insights/million-claim-challenge',
  type: 'website',
  content: `<div class="eyebrow">Engineering series</div><h1>Million Claim Challenge Field Notes</h1><p>How the benchmark moved from local Kubernetes runs to repeatable measurement, honest workflow scoring, and operator-facing evidence.</p><div class="disclosure"><strong>Current verified scope:</strong> the latest asynchronous local Kubernetes run reached the full 1,000,000-claim corpus at 155.89 claims/sec, with all claims eventually terminal. A separate 100,000-claim raw X12 837 run reached 199.42 claims/sec end-to-end. These are local validation results, not production-cloud capacity claims.</div>${cards}`
}));

const evidenceSections = publications.map((publication) => {
  const episodeDir = join(repoRoot, 'docs', 'million-claim-challenge', 'podcast', `episode-${publication.episode}`);
  const evidence = escapeHtml(readFileSync(join(episodeDir, 'benchmark-results.txt'), 'utf8'));
  const rawLink = publication.episode === '006'
    ? `<a href="${githubBase}/episode-006/raw-validator-output-50k.txt" target="_blank" rel="noopener noreferrer">Raw 50K validator output</a>`
    : '';
  return `<section id="episode-${publication.episode}"><div class="eyebrow">${publication.part} · recorded artifact</div><h2>${escapeHtml(publication.summary)}</h2><div class="article-links"><a href="/insights/million-claim-challenge/${publication.slug}">Read article</a><a href="${githubBase}/episode-${publication.episode}/benchmark-results.txt" target="_blank" rel="noopener noreferrer">Source artifact</a>${rawLink}</div><details class="evidence-block" open><summary>Benchmark results recorded with Episode ${publication.episode}</summary><pre>${evidence}</pre></details></section>`;
}).join('\n');

const evidenceOutputDir = join(siteRoot, 'docs', 'million-claim-challenge');
mkdirSync(evidenceOutputDir, { recursive: true });
writeFileSync(join(evidenceOutputDir, 'evidence.html'), pageShell({
  title: 'Million Claim Challenge Evidence Archive',
  description: 'Dated benchmark summaries, environment details, limitations, and raw artifacts for published Million Claim Challenge engineering results.',
  canonical: 'https://cloudhealthoffice.com/docs/million-claim-challenge/evidence',
  type: 'website',
  content: `<div class="eyebrow">Reproducibility archive</div><h1>Million Claim Challenge Evidence</h1><p>This archive keeps the evidence behind the engineering series visible and reviewable. Results are historical records, not generalized production capacity claims.</p><div class="disclosure"><strong>Interpretation rules:</strong> distinguish platform failures from business dispositions; do not count unsupported scenarios as passes; preserve allocated compute, corpus size, latency, and validator limitations with every result.</div>${evidenceSections}`
}));

console.log(`Generated ${publications.length} articles, an article index, and an evidence archive.`);
