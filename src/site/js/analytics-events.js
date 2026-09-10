/*
 * analytics-events.js
 * -------------------
 * First-party analytics + identity for the Cloud Health Office marketing site.
 * Loaded on every page by build.mjs, right after the Google Analytics tag.
 *
 * Design goals:
 *   - No dependencies, no build step, safe to run before or without GA.
 *   - A durable first-party anonymous_id so we can see a visitor's path and,
 *     on any form submit, alias that id to a work email / company.
 *   - A single helper (window.choTrack) other inline scripts and shared
 *     components (lead-capture.js, assistant.js) call to report high-intent
 *     actions.
 *   - Automatic instrumentation of page views, scroll depth, and the outbound /
 *     CTA links that signal a visitor is evaluating the platform.
 *
 * Privacy: this is a B2B marketing site. We collect work-contact and usage only.
 * We never collect member/patient/PHI. See /legal/privacy-policy.
 *
 * All events surface in GA4 under Reports > Engagement > Events. The server-side
 * lead store (Formspree -> sales@cloudhealthoffice.com) is the source of truth for
 * leads; GA holds the event/path history.
 */
(function () {
  'use strict';

  var ANON_KEY = 'cho_anonymous_id';
  var PAGES_KEY = 'cho_session_pages';
  var ATTRIB_KEY = 'cho_attribution';

  // Companion properties operated by Aurelianware. A referral from one of these
  // is reported as a cross-site referral so regulatory content on the reference
  // site can be attributed to the commercial conversation it produced here.
  var COMPANION_HOSTS = ['cms-0057-f.com', 'www.cms-0057-f.com'];

  /* ---------- storage helpers (never throw) ---------- */
  function lsGet(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }
  function lsSet(key, value) {
    try { window.localStorage.setItem(key, value); } catch (e) { /* private mode */ }
  }
  function ssGet(key) {
    try { return window.sessionStorage.getItem(key); } catch (e) { return null; }
  }
  function ssSet(key, value) {
    try { window.sessionStorage.setItem(key, value); } catch (e) { /* private mode */ }
  }
  function cookieGet(key) {
    try {
      // Cookies are joined with "; " but tolerate any whitespace after ";".
      var m = document.cookie.match(new RegExp('(?:^|;\\s*)' + key + '=([^;]*)'));
      return m ? decodeURIComponent(m[1]) : null;
    } catch (e) { return null; }
  }
  function cookieSet(key, value) {
    try {
      var secure = (location.protocol === 'https:') ? '; Secure' : '';
      // Encode the value so any non-token character round-trips safely.
      document.cookie = key + '=' + encodeURIComponent(value) + '; path=/; max-age=' +
        (60 * 60 * 24 * 365) + '; SameSite=Lax' + secure;
    } catch (e) { /* ignore */ }
  }

  /* ---------- durable anonymous id ---------- */
  function makeId() {
    try {
      if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
    } catch (e) { /* fall through */ }
    // Use the Web Crypto CSPRNG for the fallback; never Math.random() for an
    // identifier. Web Crypto is available in every supported browser.
    try {
      if (window.crypto && window.crypto.getRandomValues) {
        var bytes = window.crypto.getRandomValues(new Uint8Array(16));
        var hex = '';
        for (var i = 0; i < bytes.length; i++) {
          hex += ('0' + bytes[i].toString(16)).slice(-2);
        }
        return 'anon-' + hex;
      }
    } catch (e) { /* fall through */ }
    // Last resort where Web Crypto is entirely unavailable: time-based, still
    // no Math.random(). Uniqueness is best-effort in this rare path.
    return 'anon-' + Date.now().toString(36) + '-' + (
      (typeof performance !== 'undefined' && performance.now)
        ? Math.floor(performance.now()).toString(36)
        : '0'
    );
  }

  function getAnonymousId() {
    // Read from localStorage first, then fall back to the first-party cookie so
    // the id stays durable when localStorage is blocked (strict privacy modes).
    var id = lsGet(ANON_KEY) || cookieGet(ANON_KEY);
    if (!id) {
      id = makeId();
    }
    // Always (re)persist to both stores so a value recovered from one heals the
    // other, and the cookie also exposes the id to server-side tooling.
    lsSet(ANON_KEY, id);
    cookieSet(ANON_KEY, id);
    return id;
  }

  /* ---------- pages viewed this session ---------- */
  function recordPageView(path) {
    var raw = ssGet(PAGES_KEY);
    var list = [];
    if (raw) {
      try { list = JSON.parse(raw) || []; } catch (e) { list = []; }
    }
    if (list[list.length - 1] !== path) {
      list.push(path);
      if (list.length > 50) list = list.slice(-50);
      ssSet(PAGES_KEY, JSON.stringify(list));
    }
    return list;
  }
  function getViewedPages() {
    var raw = ssGet(PAGES_KEY);
    if (!raw) return [];
    try { return JSON.parse(raw) || []; } catch (e) { return []; }
  }

  /* ---------- identity (alias anon -> lead on form submit) ---------- */
  // We deliberately do NOT persist the lead's email/name/company anywhere in the
  // browser (no clear-text storage of PII). The email <-> anonymous_id join is
  // recorded server-side in the lead record (Formspree). Here we only keep a
  // non-persistent, in-memory marker for the current page and send a non-PII
  // signal to GA keyed on the anonymous_id.
  var identifiedThisPage = null;
  function getIdentity() {
    return identifiedThisPage;
  }
  function identify(traits) {
    if (!traits || !traits.email) return identifiedThisPage;
    identifiedThisPage = {
      identified: true,
      role: traits.role || (identifiedThisPage && identifiedThisPage.role) || '',
      has_company: !!(traits.company || (identifiedThisPage && identifiedThisPage.has_company)),
      anonymous_id: getAnonymousId()
    };
    // GA4 must never receive PII (email/company/name). Key GA on the
    // anonymous_id only and send a non-PII "identified" signal.
    choTrack('lead_identified', {
      anonymous_id: identifiedThisPage.anonymous_id,
      has_company: identifiedThisPage.has_company,
      role: identifiedThisPage.role
    });
    try {
      if (typeof window.gtag === 'function') {
        window.gtag('set', { user_id: identifiedThisPage.anonymous_id });
      }
    } catch (e) { /* ignore */ }
    return identifiedThisPage;
  }

  /* ---------- GA4 event helper ---------- */
  function choTrack(name, params) {
    var payload = params || {};
    try {
      payload.anonymous_id = payload.anonymous_id || getAnonymousId();
    } catch (e) { /* ignore */ }
    try {
      if (typeof window.gtag === 'function') {
        window.gtag('event', name, payload);
      }
    } catch (e) {
      /* analytics must never break the page */
    }
  }

  window.choTrack = choTrack;
  window.choIdentity = {
    getAnonymousId: getAnonymousId,
    getViewedPages: getViewedPages,
    getIdentity: getIdentity,
    getAttribution: getAttribution,
    identify: identify
  };

  function pagePath() {
    try { return window.location.pathname || '/'; } catch (e) { return '/'; }
  }

  function textOf(el) {
    return (el.getAttribute('aria-label') || el.textContent || '')
      .replace(/\s+/g, ' ')
      .trim()
      .slice(0, 100);
  }

  /* ---------- cross-site attribution (first touch, session scoped) ----------
   * Records where a visit came from so a lead can be traced back to the
   * content that produced it — in particular a regulatory article on the
   * companion reference site cms-0057-f.com.
   *
   * What is recorded: originating host, originating article path, the CTA that
   * was clicked, campaign parameters, and the page this session landed on.
   * What is never recorded: PHI, member or patient data, anything the visitor
   * typed, and any identifier beyond the site's own anonymous_id. Values are
   * whitelisted character-by-character and length-capped so a crafted inbound
   * URL cannot smuggle arbitrary content into the lead record.
   *
   * First touch wins: once a session has attribution, later navigation inside
   * cloudhealthoffice.com does not overwrite it.
   */
  function cleanToken(value, maxLength) {
    if (!value) return '';
    return String(value).replace(/[^A-Za-z0-9._~:/?#@!$&*+,;=%\- ]/g, '').slice(0, maxLength).trim();
  }

  function cleanHost(value) {
    if (!value) return '';
    return String(value).toLowerCase().replace(/[^a-z0-9.-]/g, '').slice(0, 100);
  }

  function cleanPath(value) {
    if (!value) return '';
    // Strip any query string or fragment: only the article path is useful and
    // a query string is exactly where unexpected content would arrive.
    return String(value).split('?')[0].split('#')[0].replace(/[^A-Za-z0-9._~/\-]/g, '').slice(0, 160);
  }

  function referrerParts() {
    var out = { host: '', path: '' };
    try {
      if (!document.referrer) return out;
      var url = new URL(document.referrer);
      if (url.hostname.toLowerCase() === window.location.hostname.toLowerCase()) return out;
      out.host = cleanHost(url.hostname);
      out.path = cleanPath(url.pathname);
    } catch (e) { /* ignore */ }
    return out;
  }

  function readAttribution() {
    var raw = ssGet(ATTRIB_KEY);
    if (!raw) return null;
    try { return JSON.parse(raw) || null; } catch (e) { return null; }
  }

  function captureAttribution() {
    var existing = readAttribution();
    if (existing) return existing;

    var q;
    try { q = new URLSearchParams(window.location.search); } catch (e) { q = null; }
    var param = function (name) { return q ? cleanToken(q.get(name), 120) : ''; };

    var ref = referrerParts();
    var attribution = {
      // An explicit ?ref= wins over the referrer header, which is often
      // stripped or truncated by browsers and privacy tooling.
      ref_site: cleanHost(param('ref')) || ref.host,
      ref_article: cleanPath(param('ref_article')) || ref.path,
      ref_cta: cleanToken(param('ref_cta'), 60),
      // Same treatment as every other stored field: the path is attacker-
      // influenceable through a crafted link and is persisted and transmitted.
      landing_path: cleanPath(pagePath()),
      utm_source: param('utm_source'),
      utm_medium: param('utm_medium'),
      utm_campaign: param('utm_campaign'),
      utm_term: param('utm_term'),
      utm_content: param('utm_content')
    };

    var hasAny = false;
    for (var k in attribution) {
      if (attribution.hasOwnProperty(k) && k !== 'landing_path' && attribution[k]) hasAny = true;
    }
    if (!hasAny) return null;

    ssSet(ATTRIB_KEY, JSON.stringify(attribution));

    if (COMPANION_HOSTS.indexOf(attribution.ref_site) !== -1) {
      choTrack('cross_site_referral', {
        ref_site: attribution.ref_site,
        ref_article: attribution.ref_article,
        ref_cta: attribution.ref_cta,
        landing_path: attribution.landing_path,
        utm_campaign: attribution.utm_campaign
      });
    }
    return attribution;
  }

  function getAttribution() {
    return readAttribution();
  }

  /* ---------- UTM capture ---------- */
  function utmParams() {
    var out = {};
    try {
      var q = new URLSearchParams(window.location.search);
      ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content'].forEach(function (k) {
        var v = q.get(k);
        if (v) out[k] = v;
      });
    } catch (e) { /* ignore */ }
    return out;
  }

  /* ---------- page-specific view events ----------
   * A handful of commercial pages get their own named view event so the
   * services and deployment funnels can be reported without filtering
   * page_view_cho by path. Keyed on the normalized path (no trailing slash).
   */
  var PAGE_VIEW_EVENTS = {
    '/services': 'services_page_view',
    '/deploy': 'deployment_page_view'
  };

  /* ---------- page_view ---------- */
  function firePageView() {
    var path = pagePath();
    recordPageView(path);
    var params = {
      page_path: path,
      page_title: (document.title || '').slice(0, 120)
    };
    try { params.referrer = document.referrer || ''; } catch (e) { /* ignore */ }
    var utm = utmParams();
    for (var k in utm) { if (utm.hasOwnProperty(k)) params[k] = utm[k]; }
    choTrack('page_view_cho', params);

    var named = PAGE_VIEW_EVENTS[path.replace(/\/$/, '') || '/'];
    if (named) choTrack(named, params);
  }

  /* ---------- scroll depth on key pages ---------- */
  var SCROLL_PAGES = ['/', '/platform', '/cms-0057f-compliance', '/evidence', '/what-is', '/deploy', '/services'];
  function initScrollDepth() {
    var path = pagePath().replace(/\/$/, '') || '/';
    if (SCROLL_PAGES.indexOf(path) === -1) return;
    var marks = [25, 50, 75, 100];
    var fired = {};
    function onScroll() {
      var doc = document.documentElement;
      var scrollable = (doc.scrollHeight - window.innerHeight);
      if (scrollable <= 0) return;
      var pct = Math.min(100, Math.round((window.scrollY / scrollable) * 100));
      for (var i = 0; i < marks.length; i++) {
        var m = marks[i];
        if (pct >= m && !fired[m]) {
          fired[m] = true;
          choTrack('scroll_depth', { page_path: pagePath(), percent: m });
        }
      }
      if (fired[100]) {
        window.removeEventListener('scroll', onScroll);
      }
    }
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ---------- high-intent link instrumentation ---------- */
  var CTA_DESTINATIONS = [
    { match: '/schedule-demo', event: 'demo_cta_click' },
    { match: '/contact', event: 'contact_cta_click' },
    { match: '/start', event: 'start_cta_click' },
    { match: '/deploy', event: 'deploy_cta_click' },
    { match: '/services', event: 'services_cta_click' },
    { match: '/pricing', event: 'pricing_cta_click' },
    { match: '/evidence', event: 'evidence_cta_click' },
    { match: '/docs/quickstart', event: 'quickstart_click' }
  ];

  function handleLinkClick(anchor) {
    var href = anchor.getAttribute('href');
    if (!href) return;

    var explicit = anchor.getAttribute('data-ga-event');
    if (explicit) {
      choTrack(explicit, {
        link_text: textOf(anchor),
        link_url: href,
        page_path: pagePath()
      });
      return;
    }

    var url;
    try { url = new URL(href, window.location.href); } catch (e) { return; }

    var host = url.hostname.toLowerCase();

    if (host === 'calendar.proton.me') {
      choTrack('demo_booking_click', { link_url: url.href, page_path: pagePath() });
      return;
    }

    if (host === 'github.com' || host.endsWith('.github.com')) {
      choTrack('github_repo_click', { link_url: url.href, page_path: pagePath() });
      return;
    }

    var isInternal = host === window.location.hostname;
    if (isInternal) {
      for (var i = 0; i < CTA_DESTINATIONS.length; i++) {
        // Match on a path-segment boundary so "/evidence" does not also match
        // "/docs/million-claim-challenge/evidence" and "/pricing" does not
        // match "/pricing-api".
        var m = CTA_DESTINATIONS[i].match;
        var p = url.pathname.replace(/\/$/, '');
        if (p === m || p.indexOf(m + '/') === 0) {
          if (url.pathname !== pagePath()) {
            choTrack(CTA_DESTINATIONS[i].event, {
              link_text: textOf(anchor),
              link_url: url.pathname,
              page_path: pagePath()
            });
          }
          return;
        }
      }
    }
  }

  function init() {
    getAnonymousId();
    captureAttribution();
    firePageView();
    initScrollDepth();
    document.addEventListener(
      'click',
      function (e) {
        var anchor = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (anchor) handleLinkClick(anchor);
      },
      true
    );
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
