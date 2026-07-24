/*
 * analytics-events.js
 * -------------------
 * Conversion / engagement event tracking for the Cloud Health Office
 * marketing site. Loaded on every page by build.mjs, right after the
 * Google Analytics tag.
 *
 * Design goals:
 *   - No dependencies, no build step, safe to run before or without GA.
 *   - A single helper (window.choTrack) that other inline scripts can call
 *     to report high-intent actions (e.g. a lead form submitting).
 *   - Automatic instrumentation of the outbound / CTA links that signal a
 *     visitor is evaluating the platform, so we don't have to hand-edit
 *     every page.
 *
 * All events surface in GA4 under Reports > Engagement > Events. Mark any of
 * them as a "Key event" in the GA4 UI (Admin > Events) to treat it as a
 * conversion.
 */
(function () {
  'use strict';

  /**
   * Fire a GA4 event. No-op (but never throws) if gtag isn't present yet,
   * so this is safe to call during local development where the build hasn't
   * injected Google Analytics.
   */
  function choTrack(name, params) {
    try {
      if (typeof window.gtag === 'function') {
        window.gtag('event', name, params || {});
      }
    } catch (e) {
      /* analytics must never break the page */
    }
  }

  // Expose for inline form scripts (contact form, founding-client form).
  window.choTrack = choTrack;

  function pagePath() {
    try {
      return window.location.pathname || '/';
    } catch (e) {
      return '/';
    }
  }

  function textOf(el) {
    return (el.getAttribute('aria-label') || el.textContent || '')
      .replace(/\s+/g, ' ')
      .trim()
      .slice(0, 100);
  }

  // High-intent internal destinations worth counting as CTA clicks.
  // Keyed by a substring test against the link's pathname.
  var CTA_DESTINATIONS = [
    { match: '/schedule-demo', event: 'demo_cta_click' },
    { match: '/contact', event: 'contact_cta_click' },
    { match: '/pricing', event: 'pricing_cta_click' },
    { match: '/docs/quickstart', event: 'quickstart_click' }
  ];

  function handleLinkClick(anchor) {
    var href = anchor.getAttribute('href');
    if (!href) return;

    // Explicit opt-in via data attribute always wins.
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
    try {
      url = new URL(href, window.location.href);
    } catch (e) {
      return;
    }

    var host = url.hostname.toLowerCase();

    // Booking a demo happens off-domain (Proton Calendar bookings), so a
    // click here is the only signal we get that someone booked.
    if (host === 'calendar.proton.me') {
      choTrack('demo_booking_click', {
        link_url: url.href,
        page_path: pagePath()
      });
      return;
    }

    // Cloning / browsing the source-available repo is our strongest
    // "evaluating the platform" signal on the web side.
    if (host === 'github.com' || host.endsWith('.github.com')) {
      choTrack('github_repo_click', {
        link_url: url.href,
        page_path: pagePath()
      });
      return;
    }

    // Internal high-intent CTAs.
    var isInternal = host === window.location.hostname;
    if (isInternal) {
      for (var i = 0; i < CTA_DESTINATIONS.length; i++) {
        if (url.pathname.indexOf(CTA_DESTINATIONS[i].match) === 0 ||
            url.pathname.indexOf(CTA_DESTINATIONS[i].match) > -1) {
          // Don't count a CTA click when the visitor is already on that page.
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
    // Single delegated listener so dynamically added links are covered too.
    document.addEventListener(
      'click',
      function (e) {
        var anchor = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (anchor) {
          handleLinkClick(anchor);
        }
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
