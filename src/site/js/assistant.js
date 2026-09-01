/*
 * assistant.js
 * ------------
 * "Ask about Cloud Health Office" — a CONSTRAINED on-site assistant, not a
 * general chatbot. Loaded on every page by build.mjs.
 *
 * It answers ONLY from the allowed knowledge pack below (mirrored from
 * assistant/knowledge.md + MESSAGE_SHEET.md). Topic buttons drive the happy
 * path; a text box only keyword-matches the allowed topics — anything else is
 * refused and routed to the lead form / calendar. It never invents customers,
 * never quotes a PMPM, never says "open source".
 *
 * After 2 assistant turns it offers: send to work email, or book time. Every
 * conversation stores anonymous_id + pages viewed + transcript; if the visitor
 * gives an email it attaches to the lead (Formspree -> sales@).
 */
(function () {
  'use strict';

  // Wired by build.mjs via window.CHO_LEADS_ENDPOINT (falls back to the
  // configured default Formspree form; empty in raw source before a build).
  var LEAD_ENDPOINT = (typeof window !== 'undefined' && window.CHO_LEADS_ENDPOINT) || '';

  // ---- Allowed knowledge (source of truth: assistant/knowledge.md) ----
  var CMS_DEF = 'CMS-0057-F is the federal rule that requires Medicare Advantage, ' +
    'Medicaid, CHIP, and some exchange plans to offer FHIR APIs for patient access, ' +
    'provider access, payer-to-payer exchange, and prior authorization by January 1, 2027.';

  var TOPICS = [
    {
      id: 'what',
      label: 'What is Cloud Health Office?',
      keywords: ['what is', 'what', 'about', 'product', 'overview', 'clearinghouse'],
      answer: 'Cloud Health Office is the claims platform you can put beside QNXT, Facets, ' +
        'or HealthEdge — so you hit the 2027 FHIR mandate without a core replacement. ' +
        'It is a payer administration platform (claims, benefits, eligibility, prior auth, ' +
        'payments, FHIR) you deploy in your own cloud. It is not a clearinghouse and not a ' +
        'black-box SaaS.',
      link: { href: '/what-is', text: 'Read: What is Cloud Health Office?' }
    },
    {
      id: 'cms',
      label: 'CMS-0057-F',
      keywords: ['cms', '0057', 'mandate', 'deadline', 'fhir', 'rule', 'prior auth', 'compliance'],
      answer: CMS_DEF + ' Cloud Health Office ships the FHIR compliance surface you deploy ' +
        'beside your core to meet it.',
      link: { href: '/cms-0057f-compliance', text: 'Read the CMS-0057-F summary' }
    },
    {
      id: 'qnxt',
      label: 'How it sits on QNXT / Facets',
      keywords: ['qnxt', 'facets', 'healthedge', 'core', 'sit', 'beside', 'replace', 'alongside', 'amisys'],
      answer: 'It deploys alongside your core admin system. Layer 1 serves the CMS-0057-F ' +
        'APIs now; then you replace one domain at a time when you are ready. Your system of ' +
        'record stays authoritative until you choose to cut over — no overnight rip-and-replace.',
      link: { href: '/platform', text: 'See the platform staircase' }
    },
    {
      id: 'evidence',
      label: 'Evidence',
      keywords: ['evidence', 'proof', 'benchmark', 'million', 'claims', 'performance', 'speed'],
      answer: 'Million Claim Challenge: 1,000,000 deterministic synthetic claims on local ' +
        'Docker Desktop Kubernetes at 155.89 claims/sec, zero dead letters, with published ' +
        'artifacts. This is local engineering evidence, not a production-cloud capacity claim.',
      link: { href: '/evidence', text: 'See the evidence' }
    },
    {
      id: 'deploy',
      label: 'Deploy in your cloud',
      keywords: ['deploy', 'cloud', 'azure', 'aws', 'gcp', 'install', 'run', 'host', 'phi', 'data'],
      answer: 'You deploy Cloud Health Office in your own Azure, AWS, or GCP. PHI stays inside ' +
        'your boundary. You can also evaluate and run it locally for free.',
      link: { href: '/deploy', text: 'How deployment works' }
    },
    {
      id: 'license',
      label: 'License & pricing',
      keywords: ['license', 'bsl', 'price', 'pricing', 'cost', 'free', 'commercial'],
      answer: 'Source-available under BSL 1.1. Evaluate and run locally for free. Production ' +
        'use requires a license. Deploy in your cloud or as a managed tenant. Published ' +
        'pricing is on the pricing page.',
      link: { href: '/pricing', text: 'View pricing' }
    },
    {
      id: 'stage',
      label: 'Is this beta? Who uses it?',
      keywords: ['beta', 'who uses', 'customers', 'live', 'production', 'tenant', 'reference'],
      answer: 'Cloud Health Office is ready to deploy in your cloud as a compliance layer ' +
        'beside QNXT, Facets, or HealthEdge. The first production tenant is under discussion; ' +
        'the evidence and source are already public.',
      link: { href: '/deploy', text: 'First production deployment terms' }
    }
  ];

  // Off-policy intents. Deliberately NOT including bare "phi" or "patient":
  // "does PHI leave our boundary?" and "patient access API" are on-topic
  // (deploy / CMS-0057-F). Medical-advice intent is caught by diagnos/symptom/
  // treat; PHI *submission* is caught by "member id".
  var OFF_POLICY = ['diagnos', 'symptom', 'treat', 'member id', 'hack',
    'exploit', 'bypass', 'pmpm', 'quote me', 'discount'];

  function track(name, params) {
    if (typeof window.choTrack === 'function') window.choTrack(name, params || {});
  }

  var transcript = [];
  var assistantTurns = 0;
  var handoffOffered = false;

  function logTurn(role, text) {
    transcript.push(role + ': ' + text);
    try { sessionStorage.setItem('cho_assistant_transcript', JSON.stringify(transcript)); } catch (e) {}
  }

  /* ---------- DOM ---------- */
  var panel, log, textInput;

  function el(tag, attrs, html) {
    var node = document.createElement(tag);
    if (attrs) for (var k in attrs) { if (attrs.hasOwnProperty(k)) node.setAttribute(k, attrs[k]); }
    if (html != null) node.innerHTML = html;
    return node;
  }

  // Bot bubbles render trusted, static markup (topic answers + fixed links).
  function addBubble(role, html) {
    var b = el('div', { class: 'cho-asst__msg cho-asst__msg--' + role });
    b.innerHTML = html;
    log.appendChild(b);
    log.scrollTop = log.scrollHeight;
  }

  // User (and topic-button) bubbles use textContent so visitor-typed text can
  // never reach an innerHTML sink — no HTML is ever parsed from user input.
  function addUserBubble(text) {
    var b = el('div', { class: 'cho-asst__msg cho-asst__msg--user' });
    b.textContent = String(text);
    log.appendChild(b);
    log.scrollTop = log.scrollHeight;
  }

  function botSay(html, plainForLog) {
    addBubble('bot', html);
    logTurn('assistant', plainForLog || html.replace(/<[^>]+>/g, ''));
    assistantTurns++;
    if (assistantTurns >= 2 && !handoffOffered) offerHandoff();
  }

  function answerTopic(topic) {
    var linkHtml = topic.link
      ? ' <a href="' + topic.link.href + '" class="cho-asst__link">' + topic.link.text + ' &rarr;</a>'
      : '';
    botSay(escapeText(topic.answer) + linkHtml, topic.answer);
  }

  function escapeText(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function matchTopic(text) {
    var t = text.toLowerCase();
    for (var i = 0; i < OFF_POLICY.length; i++) {
      if (t.indexOf(OFF_POLICY[i]) > -1) return 'off';
    }
    var best = null, bestScore = 0;
    for (var j = 0; j < TOPICS.length; j++) {
      var score = 0;
      for (var k = 0; k < TOPICS[j].keywords.length; k++) {
        if (t.indexOf(TOPICS[j].keywords[k]) > -1) score++;
      }
      if (score > bestScore) { bestScore = score; best = TOPICS[j]; }
    }
    return bestScore > 0 ? best : null;
  }

  function handleUserText(text) {
    if (!text) return;
    addUserBubble(text);
    logTurn('visitor', text);
    track('assistant_message', { page_path: location.pathname });

    var match = matchTopic(text);
    if (match === 'off') {
      botSay('I can only answer questions about the Cloud Health Office product — not ' +
        'clinical, PHI, security-bypass, or custom-quote questions. The team can help with ' +
        'that directly. Want to send your question to your work email or book time?',
        'off-policy refusal');
      return;
    }
    if (!match) {
      botSay('I can help with CMS-0057-F, how it sits on QNXT/Facets, the evidence, ' +
        'deployment, and licensing. For anything else, the team can help directly — want ' +
        'to leave your work email or book time?', 'no-match');
      return;
    }
    answerTopic(match);
  }

  function offerHandoff() {
    handoffOffered = true;
    var wrap = el('div', { class: 'cho-asst__handoff' });
    wrap.innerHTML =
      '<p>Want this sent to your work email, or should we book time?</p>' +
      '<form class="cho-asst__email" novalidate>' +
      '  <input type="email" name="email" placeholder="you@healthplan.org" aria-label="Work email" required />' +
      '  <button type="submit">Email me</button>' +
      '</form>' +
      '<a href="/contact" class="cho-asst__book" data-ga-event="assistant_book_click">Book 30 minutes &rarr;</a>' +
      '<p class="cho-asst__note" hidden></p>';
    log.appendChild(wrap);
    log.scrollTop = log.scrollHeight;

    var form = wrap.querySelector('form');
    var note = wrap.querySelector('.cho-asst__note');
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var email = form.email.value.trim();
      if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
        note.hidden = false; note.textContent = 'Please enter a valid work email.';
        return;
      }
      sendTranscript(email, note, form);
    });
  }

  function sendTranscript(email, note, form) {
    track('assistant_handoff', { page_path: location.pathname });
    var anonId = (window.choIdentity && window.choIdentity.getAnonymousId)
      ? window.choIdentity.getAnonymousId() : '';
    var pages = (window.choIdentity && window.choIdentity.getViewedPages)
      ? window.choIdentity.getViewedPages().join(' > ') : '';
    if (window.choIdentity && window.choIdentity.identify) {
      window.choIdentity.identify({ email: email });
    }

    if (!/^https:\/\/formspree\.io\/f\/[a-z0-9]+$/i.test(LEAD_ENDPOINT)) {
      note.hidden = false;
      note.textContent = 'Thanks — email sales@cloudhealthoffice.com and we will follow up.';
      return;
    }

    var fd = new FormData();
    fd.set('email', email);
    fd.set('_replyto', email);
    fd.set('_subject', 'Assistant conversation — Cloud Health Office');
    fd.set('source', 'on-site assistant');
    fd.set('anonymous_id', anonId);
    fd.set('pages_viewed', pages);
    fd.set('transcript', transcript.join('\n'));

    var btn = form.querySelector('button');
    if (btn) { btn.disabled = true; btn.textContent = 'Sending…'; }

    fetch(LEAD_ENDPOINT, { method: 'POST', headers: { 'Accept': 'application/json' }, body: fd })
      .then(function (res) {
        note.hidden = false;
        if (res.ok) {
          form.hidden = true;
          note.textContent = 'Sent. We reply within one business day.';
        } else {
          note.textContent = 'Could not send just now — email sales@cloudhealthoffice.com.';
          if (btn) { btn.disabled = false; btn.textContent = 'Email me'; }
        }
      })
      .catch(function () {
        note.hidden = false;
        note.textContent = 'Could not send just now — email sales@cloudhealthoffice.com.';
        if (btn) { btn.disabled = false; btn.textContent = 'Email me'; }
      });
  }

  var lastFocused = null;
  function openPanel() {
    lastFocused = document.activeElement;
    panel.hidden = false;
    document.getElementById('cho-asst-toggle').setAttribute('aria-expanded', 'true');
    track('assistant_open', { page_path: location.pathname });
    if (!log.dataset.greeted) {
      log.dataset.greeted = '1';
      botSay('Hi — I can answer product questions about Cloud Health Office. Pick a topic, ' +
        'or type a question.', 'greeting');
      assistantTurns = 0; // greeting doesn't count toward the handoff
    }
    // Move keyboard focus into the dialog.
    try { (textInput || panel).focus(); } catch (e) { /* ignore */ }
  }
  function closePanel() {
    panel.hidden = true;
    document.getElementById('cho-asst-toggle').setAttribute('aria-expanded', 'false');
    // Restore focus to whatever opened the dialog (usually the toggle).
    try {
      (lastFocused && lastFocused.focus ? lastFocused
        : document.getElementById('cho-asst-toggle')).focus();
    } catch (e) { /* ignore */ }
  }

  function build() {
    var style = el('style', null, CSS);
    document.head.appendChild(style);

    var toggle = el('button', {
      id: 'cho-asst-toggle', class: 'cho-asst__toggle',
      'aria-expanded': 'false', 'aria-controls': 'cho-asst-panel',
      type: 'button'
    }, '<span aria-hidden="true">&#128172;</span> Ask about Cloud Health Office');
    toggle.addEventListener('click', function () {
      if (panel.hidden) openPanel(); else closePanel();
    });

    panel = el('section', {
      id: 'cho-asst-panel', class: 'cho-asst__panel', hidden: 'hidden',
      role: 'dialog', 'aria-modal': 'true', tabindex: '-1',
      'aria-label': 'Ask about Cloud Health Office'
    });
    // Escape closes the dialog from anywhere inside it.
    panel.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' || e.keyCode === 27) {
        e.stopPropagation();
        closePanel();
      }
    });

    var header = el('header', { class: 'cho-asst__header' },
      '<strong>Ask about Cloud Health Office</strong>' +
      '<button type="button" class="cho-asst__close" aria-label="Close">&times;</button>');
    header.querySelector('.cho-asst__close').addEventListener('click', closePanel);

    log = el('div', { class: 'cho-asst__log', 'aria-live': 'polite' });

    var topics = el('div', { class: 'cho-asst__topics' });
    TOPICS.forEach(function (topic) {
      var b = el('button', { type: 'button', class: 'cho-asst__chip' }, escapeText(topic.label));
      b.addEventListener('click', function () {
        addUserBubble(topic.label);
        logTurn('visitor', topic.label);
        track('assistant_message', { topic: topic.id, page_path: location.pathname });
        answerTopic(topic);
      });
      topics.appendChild(b);
    });

    var form = el('form', { class: 'cho-asst__input' },
      '<input type="text" name="q" autocomplete="off" placeholder="Type a question…" aria-label="Ask a question" />' +
      '<button type="submit" aria-label="Send">&rarr;</button>');
    textInput = form.querySelector('input');
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var v = textInput.value.trim();
      textInput.value = '';
      handleUserText(v);
    });

    var foot = el('p', { class: 'cho-asst__foot' },
      'Product answers only. No PHI. <a href="/contact" class="cho-asst__link">Talk to the team &rarr;</a>');

    panel.appendChild(header);
    panel.appendChild(log);
    panel.appendChild(topics);
    panel.appendChild(form);
    panel.appendChild(foot);

    document.body.appendChild(toggle);
    document.body.appendChild(panel);
  }

  var CSS = [
    '.cho-asst__toggle{position:fixed;right:16px;bottom:16px;z-index:1300;display:inline-flex;',
    'align-items:center;gap:8px;padding:12px 18px;border-radius:999px;border:1px solid rgba(0,255,255,.35);',
    'background:linear-gradient(90deg,#04121f,#04202b);color:#dffcff;font:600 .9rem/1 system-ui,-apple-system,sans-serif;',
    'cursor:pointer;box-shadow:0 10px 30px rgba(0,0,0,.5);}',
    '.cho-asst__toggle:hover{border-color:rgba(0,255,255,.7);}',
    '.cho-asst__panel{position:fixed;right:16px;bottom:76px;z-index:1300;width:min(360px,calc(100vw - 24px));',
    'max-height:min(560px,calc(100vh - 100px));display:flex;flex-direction:column;background:rgba(4,6,12,.98);',
    'border:1px solid rgba(0,255,255,.22);border-radius:16px;box-shadow:0 24px 60px rgba(0,0,0,.6);overflow:hidden;',
    'font:400 .9rem/1.5 system-ui,-apple-system,sans-serif;color:#e6f7ff;}',
    // The panel and email form set an explicit display, which overrides the
    // user-agent [hidden]{display:none} rule — so toggling el.hidden alone
    // never hides them (this is why the close/✕ button did nothing on mobile).
    // These [hidden] rules restore the hidden attribute as the source of truth.
    '.cho-asst__panel[hidden]{display:none;}',
    '.cho-asst__email[hidden]{display:none;}',
    '.cho-asst__header{display:flex;align-items:center;justify-content:space-between;padding:14px 16px;',
    'border-bottom:1px solid rgba(0,255,255,.15);color:#7fffd4;}',
    '.cho-asst__close{background:none;border:none;color:rgba(230,247,255,.6);font-size:1.4rem;line-height:1;cursor:pointer;}',
    '.cho-asst__log{flex:1;overflow-y:auto;padding:14px 16px;display:flex;flex-direction:column;gap:10px;}',
    '.cho-asst__msg{padding:10px 12px;border-radius:12px;max-width:90%;}',
    '.cho-asst__msg--bot{background:rgba(0,255,255,.07);border:1px solid rgba(0,255,255,.15);align-self:flex-start;}',
    '.cho-asst__msg--user{background:rgba(0,255,136,.10);border:1px solid rgba(0,255,136,.2);align-self:flex-end;color:#eaffe9;}',
    '.cho-asst__link{color:#00ffff;text-decoration:underline;}',
    '.cho-asst__topics{display:flex;flex-wrap:wrap;gap:6px;padding:0 16px 10px;}',
    '.cho-asst__chip{padding:7px 11px;border-radius:999px;border:1px solid rgba(0,255,255,.25);background:rgba(255,255,255,.03);',
    'color:#cdefff;font-size:.78rem;cursor:pointer;}',
    '.cho-asst__chip:hover{border-color:rgba(0,255,255,.6);}',
    '.cho-asst__input{display:flex;gap:8px;padding:10px 16px;border-top:1px solid rgba(0,255,255,.12);}',
    '.cho-asst__input input{flex:1;padding:10px 12px;border-radius:10px;border:1px solid rgba(0,255,255,.2);',
    'background:rgba(255,255,255,.04);color:#fff;font-size:.9rem;}',
    '.cho-asst__input button{padding:0 14px;border-radius:10px;border:none;background:#00ffff;color:#001018;font-weight:700;cursor:pointer;}',
    '.cho-asst__foot{margin:0;padding:0 16px 12px;font-size:.72rem;color:rgba(230,247,255,.5);}',
    '.cho-asst__handoff{border-top:1px dashed rgba(0,255,255,.2);padding-top:10px;margin-top:4px;}',
    '.cho-asst__email{display:flex;gap:8px;margin:8px 0;}',
    '.cho-asst__email input{flex:1;padding:9px 11px;border-radius:10px;border:1px solid rgba(0,255,255,.2);background:rgba(255,255,255,.04);color:#fff;}',
    '.cho-asst__email button{padding:0 14px;border-radius:10px;border:none;background:#00ff88;color:#001b0e;font-weight:700;cursor:pointer;}',
    '.cho-asst__book{display:inline-block;color:#00ffff;font-size:.82rem;}',
    '.cho-asst__note{font-size:.8rem;color:#7fffd4;margin:6px 0 0;}',
    '@media (max-width:400px){.cho-asst__toggle{font-size:.8rem;padding:10px 14px;}}'
  ].join('');

  function init() { build(); }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
