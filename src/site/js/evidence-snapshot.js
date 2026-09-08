/* Cloud Health Office — Evidence Hub live snapshots
 *
 * Renders the two published evidence documents into the page:
 *
 *   cms0057-public-evidence.json          CMS-0057-F acceptance evidence
 *                                         (PASSABLE / PARTIAL / GAP / N/A)
 *   davinci-interop-public-evidence.json  external Da Vinci interoperability
 *                                         (Passed / Failed / Skipped / NotRun)
 *
 * The two are rendered by separate functions with separate vocabularies and are
 * never summed, cross-referenced or rolled into a single score. That separation
 * is the point of publishing them as two documents, and it is preserved here so
 * a later edit cannot quietly merge them in the UI.
 *
 * Every status a visitor reads comes from the fetched document. Nothing in this
 * file hard-codes a result, so a page cannot go stale against the evidence it
 * cites. When a fetch fails the block degrades to the static prose already in
 * the markup rather than showing a status it cannot substantiate.
 *
 * Usage:
 *   <div data-evidence="cms0057" data-src="…json" data-view="summary">
 *   <div data-evidence="cms0057" data-src="…json" data-view="scenarios"
 *        data-capability="PayerToPayer">
 *   <div data-evidence="interop" data-src="…json">
 *
 * Each block must contain [data-evidence-fallback] (visible prose) and
 * [data-evidence-body] (hidden until the render succeeds).
 */
(function () {
  'use strict';

  var CMS_STATUS = {
    PASSABLE: { cls: 'pass', label: 'Passable' },
    PARTIAL: { cls: 'part', label: 'Partial' },
    GAP: { cls: 'gap', label: 'Gap' },
    'N/A': { cls: 'na', label: 'N/A' }
  };

  var INTEROP_STATUS = {
    Passed: { cls: 'pass', label: 'Passed' },
    Failed: { cls: 'gap', label: 'Failed' },
    Skipped: { cls: 'part', label: 'Skipped' },
    NotRun: { cls: 'na', label: 'Not run' },
    // The harness executes this scenario in CI, but no run has been published
    // to the site yet. Deliberately not "Passed" and not "Failed".
    NotPublished: { cls: 'idle', label: 'Awaiting published run' },
    NotImplemented: { cls: 'na', label: 'Defined, not executed' }
  };

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== null && text !== undefined) node.textContent = String(text);
    return node;
  }

  function link(href, text) {
    var a = el('a', null, text);
    a.href = href;
    a.rel = 'noopener';
    return a;
  }

  function pill(map, status) {
    var meta = map[status] || { cls: 'na', label: status || 'Unknown' };
    return el('span', 'ev-pill ev-pill--' + meta.cls, meta.label);
  }

  function pillCell(map, status) {
    var td = document.createElement('td');
    td.appendChild(pill(map, status));
    return td;
  }

  function metaItem(parent, label, valueNode) {
    var span = el('span');
    span.appendChild(el('strong', null, label + ' '));
    span.appendChild(valueNode);
    parent.appendChild(span);
  }

  function table(headings, rows) {
    var scroll = el('div', 'ev-scroll');
    var tbl = el('table', 'ev-table');
    var thead = document.createElement('thead');
    var tr = document.createElement('tr');
    headings.forEach(function (h) { tr.appendChild(el('th', null, h)); });
    thead.appendChild(tr);
    tbl.appendChild(thead);
    var tbody = document.createElement('tbody');
    rows.forEach(function (row) { tbody.appendChild(row); });
    tbl.appendChild(tbody);
    scroll.appendChild(tbl);
    return scroll;
  }

  function tallies(counts, labels) {
    var wrap = el('div', 'ev-tallies');
    labels.forEach(function (pair) {
      var t = el('span', 'ev-tally');
      t.appendChild(el('b', null, counts[pair[0]] || 0));
      t.appendChild(document.createTextNode(' ' + pair[1]));
      wrap.appendChild(t);
    });
    return wrap;
  }

  function shaNode(short, url) {
    return url ? link(url, short) : el('span', null, short);
  }

  /* ── CMS-0057-F acceptance evidence ───────────────────────────────────── */

  function renderCms0057(root, body, data) {
    var view = root.getAttribute('data-view') || 'summary';
    var capability = root.getAttribute('data-capability');
    var integrationKeys = Object.keys(data.integrations || {}).sort();

    var head = el('div', 'ev-snapshot__head');
    head.appendChild(el('h3', null, 'Latest published acceptance evidence'));
    head.appendChild(
      el(
        'span',
        'ev-badge ev-badge--green',
        (data.evidenceStatus === 'validated' ? 'Validated' : data.evidenceStatus || 'Published') +
          (data.generatedAtUtc ? ' · ' + data.generatedAtUtc.slice(0, 10) : '')
      )
    );
    body.appendChild(head);

    var meta = el('div', 'ev-meta');
    if (data.commitShaShort) {
      metaItem(meta, 'Source revision:', shaNode(data.commitShaShort, data.sourceCommitUrl));
    }
    metaItem(meta, 'Scenarios:', el('span', null, data.scenarioCount));
    var ts = data.testSummary || {};
    metaItem(
      meta,
      'Tests:',
      el('span', null, (ts.passed || 0) + ' passed / ' + (ts.failed || 0) + ' failed / ' + (ts.skipped || 0) + ' skipped')
    );
    metaItem(meta, 'Data:', el('span', null, data.testDataClassification || 'synthetic'));
    metaItem(meta, 'FHIR:', el('span', null, data.fhirVersion || 'R4'));
    body.appendChild(meta);

    var cmsLabels = [['passable', 'Passable'], ['partial', 'Partial'], ['gap', 'Gap'], ['na', 'N/A']];

    if (view === 'summary') {
      var grid = el('div', 'ev-grid');
      var product = el('div', 'ev-card');
      product.appendChild(el('h3', null, 'Cloud Health Office Replace'));
      product.appendChild(el('p', null, 'Product capability — Cloud Health Office is the authoritative backend.'));
      product.appendChild(tallies(data.replaceSummary || {}, cmsLabels));
      grid.appendChild(product);
      integrationKeys.forEach(function (key) {
        var card = el('div', 'ev-card');
        card.appendChild(el('h3', null, key.toUpperCase() + ' Augment'));
        card.appendChild(el('p', null, 'Integration capability — proven against that external core, reported separately.'));
        card.appendChild(tallies(data.integrations[key] || {}, cmsLabels));
        grid.appendChild(card);
      });
      body.appendChild(grid);
    }

    if (view === 'scenarios') {
      // data-capability takes one capability or a comma-separated list, so a
      // page can show exactly the scenarios it discusses and no others.
      var wanted = capability
        ? capability.split(',').map(function (c) { return c.trim(); }).filter(Boolean)
        : null;
      var scenarios = (data.scenarios || []).filter(function (s) {
        return !wanted || wanted.indexOf(s.capability) !== -1;
      });
      var headings = ['ID', 'Scenario', 'CHO Replace'];
      integrationKeys.forEach(function (k) { headings.push(k.toUpperCase() + ' Augment'); });

      var rows = scenarios.map(function (s) {
        var tr = document.createElement('tr');
        tr.appendChild(el('td', null, s.id));
        var nameTd = document.createElement('td');
        nameTd.appendChild(el('span', 'ev-what', s.name));
        tr.appendChild(nameTd);
        tr.appendChild(pillCell(CMS_STATUS, s.replace));
        integrationKeys.forEach(function (k) {
          tr.appendChild(pillCell(CMS_STATUS, (s.integrations || {})[k] || 'N/A'));
        });
        return tr;
      });

      if (!rows.length) return false;
      body.appendChild(table(headings, rows));

      var legend = el('div', 'ev-legend');
      [
        ['pass', 'Passable', 'supported by the tested implementation'],
        ['part', 'Partial', 'core present; part is engagement work'],
        ['gap', 'Gap', 'not built; tracked so it cannot be silently claimed'],
        ['na', 'N/A', 'no dependency on this dimension']
      ].forEach(function (row) {
        var span = el('span');
        span.appendChild(el('span', 'ev-pill ev-pill--' + row[0], row[1]));
        span.appendChild(document.createTextNode(row[2]));
        legend.appendChild(span);
      });
      body.appendChild(legend);
    }

    var disclaimers = data.disclaimers || [];
    if (disclaimers.length) {
      body.appendChild(el('p', 'ev-fallback', disclaimers.join(' ')));
    }
    return true;
  }

  /* ── External Da Vinci interoperability evidence ──────────────────────── */

  function renderInterop(root, body, data) {
    var run = data.latestRun;

    var head = el('div', 'ev-snapshot__head');
    head.appendChild(el('h3', null, 'External interoperability run'));
    head.appendChild(
      run
        ? el('span', 'ev-badge ev-badge--green', 'Published · ' + String(run.generatedAtUtc || '').slice(0, 10))
        : el('span', 'ev-badge ev-badge--amber', 'No run published yet')
    );
    body.appendChild(head);

    var meta = el('div', 'ev-meta');
    if (run && run.choCommit) {
      metaItem(meta, 'Tested revision:', shaNode(run.choCommit.slice(0, 12), run.choCommitUrl));
    } else if (data.sourceCommitShort) {
      metaItem(meta, 'Inventory revision:', shaNode(data.sourceCommitShort, data.sourceCommitUrl));
    }
    if (run) {
      var s = run.summary || {};
      metaItem(
        meta,
        'Scenarios:',
        el('span', null, (s.passed || 0) + ' passed / ' + (s.failed || 0) + ' failed / ' + (s.skipped || 0) + ' skipped / ' + (s.notRun || 0) + ' not run')
      );
      metaItem(meta, 'Environment:', el('span', null, run.environment || 'CI'));
    }
    metaItem(meta, 'Data:', el('span', null, data.dataClassification || 'synthetic'));
    body.appendChild(meta);

    if (!run) {
      body.appendChild(
        el(
          'p',
          'ev-fallback',
          'The scenario inventory and the pinned external implementations below come from the repository at the ' +
            'revision named above. Execution status is published here after the harness next runs on the main ' +
            'branch — until then no scenario is shown as passed.'
        )
      );
    }

    // Scenario rows: inventory is always shown, status comes from the run.
    var scenarioRows = (data.scenarios || []).map(function (sc) {
      var tr = document.createElement('tr');
      tr.appendChild(el('td', null, sc.id));
      var what = document.createElement('td');
      what.appendChild(el('span', 'ev-what', sc.title));
      var role = 'CHO as ' + String(sc.choRole || '').toLowerCase() + ' · ' + sc.externalTarget;
      if (sc.linkedFromScenario) role += ' · chained from ' + sc.linkedFromScenario;
      what.appendChild(el('p', null, role));
      tr.appendChild(what);
      tr.appendChild(el('td', null, sc.protocol));
      tr.appendChild(pillCell(INTEROP_STATUS, sc.status));
      return tr;
    });
    if (scenarioRows.length) {
      body.appendChild(table(['ID', 'Scenario', 'Protocol', 'Status'], scenarioRows));
    }

    // Pinned external implementations — the reproducibility half of the claim.
    var targetRows = (data.targets || []).map(function (t) {
      var tr = document.createElement('tr');
      var nameTd = document.createElement('td');
      nameTd.appendChild(t.upstreamRepository ? link(t.upstreamRepository, t.name) : el('span', null, t.name));
      tr.appendChild(nameTd);
      tr.appendChild(el('td', null, (t.protocols || []).join(', ')));
      var pinTd = document.createElement('td');
      pinTd.appendChild(el('code', null, (t.pin || {}).reference || '—'));
      tr.appendChild(pinTd);
      var igs = t.implementationGuides || {};
      tr.appendChild(
        el(
          'td',
          null,
          Object.keys(igs)
            .map(function (k) { return k + ' ' + igs[k]; })
            .join(' · ') || '—'
        )
      );
      tr.appendChild(pillCell({ true: { cls: 'pass', label: 'Exercised' }, false: { cls: 'na', label: 'Pinned only' } }, String(t.exercised)));
      return tr;
    });
    if (targetRows.length) {
      body.appendChild(el('h4', null, 'Pinned external implementations'));
      body.appendChild(table(['Implementation', 'Protocols', 'Pin', 'IG versions', 'Exercised'], targetRows));
    }

    // Findings are observations, not verdicts: a Warning does not fail a run.
    if (run && run.findings && run.findings.length) {
      var findingRows = run.findings.map(function (f) {
        var tr = document.createElement('tr');
        var codeTd = document.createElement('td');
        codeTd.appendChild(el('code', null, f.code));
        tr.appendChild(codeTd);
        tr.appendChild(pillCell({ Warning: { cls: 'part', label: 'Warning' }, Info: { cls: 'info', label: 'Info' }, Error: { cls: 'gap', label: 'Error' } }, f.severity));
        var summaryTd = document.createElement('td');
        summaryTd.appendChild(el('span', 'ev-what', f.summary));
        if (f.choObserved || f.externalObserved) {
          summaryTd.appendChild(
            el('p', null, 'CHO: ' + (f.choObserved || '—') + ' · External: ' + (f.externalObserved || '—'))
          );
        }
        tr.appendChild(summaryTd);
        return tr;
      });
      body.appendChild(el('h4', null, 'Findings recorded this run'));
      body.appendChild(table(['Code', 'Severity', 'Observation'], findingRows));
    }

    if (data.relationshipToCmsAcceptance) {
      body.appendChild(el('p', 'ev-fallback', data.relationshipToCmsAcceptance));
    }
    return true;
  }

  /* ── Wiring ───────────────────────────────────────────────────────────── */

  var RENDERERS = { cms0057: renderCms0057, interop: renderInterop };

  function hydrate(root) {
    var kind = root.getAttribute('data-evidence');
    var render = RENDERERS[kind];
    var src = root.getAttribute('data-src');
    var fallback = root.querySelector('[data-evidence-fallback]');
    var body = root.querySelector('[data-evidence-body]');
    if (!render || !src || !body) return;

    fetch(src, { cache: 'no-cache' })
      .then(function (response) {
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response.json();
      })
      .then(function (data) {
        while (body.firstChild) body.removeChild(body.firstChild);
        if (render(root, body, data) === false) return;
        if (fallback) fallback.hidden = true;
        body.hidden = false;
      })
      .catch(function () {
        // Leave the static prose in place. A snapshot that cannot be loaded
        // must not be replaced by a status the page invented.
        if (fallback) {
          fallback.textContent =
            'The latest published evidence snapshot could not be loaded here. It is generated in CI from the ' +
            'test suite and committed to this repository against the tested source revision.';
        }
      });
  }

  function init() {
    Array.prototype.forEach.call(document.querySelectorAll('[data-evidence]'), hydrate);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
