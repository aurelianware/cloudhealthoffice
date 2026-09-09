/**
 * Site tests — software + professional-services positioning
 * ---------------------------------------------------------
 * Guards the parts of the /services and /deploy positioning that can be checked
 * mechanically: routing, navigation, metadata, structured data, the sitemap,
 * the deployment status labels, the contact form's interest taxonomy, the
 * analytics events, the PHI warning, and basic accessibility affordances.
 *
 * The positioning itself is defined in src/site/MESSAGE_SHEET.md and
 * docs/adr/012-product-led-professional-services.md. These tests exist so that
 * a copy edit cannot quietly turn "under evaluation" into "available", or drop
 * the sensitive-data warning from a public form.
 */

import * as fs from 'fs';
import * as path from 'path';

const SITE = path.join(__dirname, '..', '..', 'src', 'site');

const read = (relative: string): string =>
  fs.readFileSync(path.join(SITE, relative), 'utf8');

const services = read('services.html');
const deploy = read('deploy.html');
const contact = read('contact.html');
const index = read('index.html');
const sitemap = read('sitemap.xml');
const redirects = read('_redirects');
const swaConfig = JSON.parse(read('staticwebapp.config.json'));
const analytics = read('js/analytics-events.js');
const messageSheet = read('MESSAGE_SHEET.md');
const knowledge = read('assistant/knowledge.md');
const servicesCss = read('css/services.css');

/** Every HTML page that carries the shared primary navigation. */
function htmlPagesWithNav(): string[] {
  const found: string[] = [];
  const walk = (dir: string) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === 'node_modules' || entry.name === 'dist') continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (entry.isFile() && entry.name.endsWith('.html')) {
        if (fs.readFileSync(full, 'utf8').includes('id="mainNav"')) {
          found.push(path.relative(SITE, full));
        }
      }
    }
  };
  walk(SITE);
  return found;
}

const mainNavOf = (html: string): string => {
  const match = html.match(/<ul id="mainNav">[\s\S]*?<\/ul>/);
  return match ? match[0] : '';
};

const jsonLdBlocks = (html: string): unknown[] =>
  [...html.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)]
    .map((m) => JSON.parse(m[1]));

describe('Services & deployment positioning', () => {
  describe('route availability', () => {
    it('ships a /services page and keeps /deploy', () => {
      expect(fs.existsSync(path.join(SITE, 'services.html'))).toBe(true);
      expect(fs.existsSync(path.join(SITE, 'deploy.html'))).toBe(true);
    });

    it('does not create a duplicate /platform/deployment page (ADR 012)', () => {
      expect(fs.existsSync(path.join(SITE, 'platform', 'deployment.html'))).toBe(false);
    });

    it('routes the clean /services URL in _redirects', () => {
      expect(redirects).toMatch(/^\/services\s+\/services\.html\s+200$/m);
      expect(redirects).toMatch(/^\/services\.html\s+\/services\s+301$/m);
      expect(redirects).toMatch(/^\/services\/\s+\/services\s+301$/m);
    });

    it('routes the clean /services URL in staticwebapp.config.json', () => {
      const routes = swaConfig.routes as Array<Record<string, unknown>>;
      expect(routes).toContainEqual({ route: '/services', rewrite: '/services.html' });
      expect(routes).toContainEqual({
        route: '/services.html',
        redirect: '/services',
        statusCode: 301
      });
    });

    it('lists /services in the sitemap', () => {
      expect(sitemap).toContain('<loc>https://cloudhealthoffice.com/services</loc>');
      expect(sitemap).toContain('<loc>https://cloudhealthoffice.com/deploy</loc>');
    });
  });

  describe('navigation', () => {
    const pages = htmlPagesWithNav();

    it('finds the shared navigation on the marketing pages', () => {
      expect(pages.length).toBeGreaterThan(30);
    });

    it('exposes Services in the primary nav on every page that has one', () => {
      const missing = pages.filter((page) => !mainNavOf(read(page)).includes('href="/services"'));
      expect(missing).toEqual([]);
    });

    it('keeps the product-first nav order — Services sits before Contact', () => {
      const nav = mainNavOf(services);
      expect(nav.indexOf('href="/platform"')).toBeLessThan(nav.indexOf('href="/services"'));
      expect(nav.indexOf('href="/services"')).toBeLessThan(nav.indexOf('href="/contact"'));
    });

    it('marks the current page in the nav on /services', () => {
      expect(mainNavOf(services)).toContain('aria-current="page"');
    });

    it('keeps the mobile menu toggle wired on the new pages', () => {
      for (const html of [services, deploy]) {
        expect(html).toContain('id="mobileMenuToggle"');
        expect(html).toContain('aria-expanded="false"');
        expect(html).toContain('js/mobile-nav.js');
      }
    });

    it('links Professional Services from the footer of the primary marketing pages', () => {
      for (const page of ['index.html', 'platform.html', 'services.html', 'deploy.html', 'contact.html']) {
        const footer = read(page).match(/<footer[\s\S]*?<\/footer>/);
        expect(footer).not.toBeNull();
        expect(footer![0]).toContain('href="/services"');
      }
    });
  });

  describe('metadata', () => {
    it('gives /services a canonical, description, and social cards', () => {
      expect(services).toContain('<link rel="canonical" href="https://cloudhealthoffice.com/services" />');
      expect(services).toMatch(/<meta name="description" content="[^"]{80,}"/);
      expect(services).toContain('property="og:url" content="https://cloudhealthoffice.com/services"');
      expect(services).toContain('name="twitter:card" content="summary_large_image"');
    });

    it('gives /deploy a canonical, description, and social cards', () => {
      expect(deploy).toContain('<link rel="canonical" href="https://cloudhealthoffice.com/deploy" />');
      expect(deploy).toMatch(/<meta name="description" content="[^"]{80,}"/);
      expect(deploy).toContain('property="og:url" content="https://cloudhealthoffice.com/deploy"');
    });

    it('titles the services page for services intent, not product intent', () => {
      const title = services.match(/<title>([^<]+)<\/title>/)?.[1] ?? '';
      expect(title).toMatch(/Professional Services/i);
      expect(title).toMatch(/Cloud Health Office/);
    });
  });

  describe('structured data', () => {
    it('parses every JSON-LD block on the new pages', () => {
      expect(() => jsonLdBlocks(services)).not.toThrow();
      expect(() => jsonLdBlocks(deploy)).not.toThrow();
    });

    it('describes /services as a ProfessionalService with a breadcrumb and FAQ', () => {
      const graph = (jsonLdBlocks(services)[0] as { '@graph': Array<{ '@type': string }> })['@graph'];
      const types = graph.map((node) => node['@type']);
      expect(types).toContain('ProfessionalService');
      expect(types).toContain('BreadcrumbList');
      expect(types).toContain('FAQPage');
    });

    it('only publishes FAQ structured data for questions answered on the page', () => {
      const graph = (jsonLdBlocks(services)[0] as {
        '@graph': Array<{ '@type': string; mainEntity?: Array<{ name: string }> }>;
      })['@graph'];
      const faq = graph.find((node) => node['@type'] === 'FAQPage');
      expect(faq?.mainEntity?.length).toBeGreaterThan(0);
      for (const question of faq!.mainEntity!) {
        // The rendered <summary> carries the same question text.
        expect(services).toContain(question.name.replace(/'/g, '&#39;').replace(/&/g, '&amp;'));
      }
    });

    it('gives /deploy a breadcrumb rooted under the platform', () => {
      const graph = (jsonLdBlocks(deploy)[0] as {
        '@graph': Array<{ '@type': string; itemListElement?: Array<{ name: string }> }>;
      })['@graph'];
      const crumbs = graph.find((node) => node['@type'] === 'BreadcrumbList');
      expect(crumbs?.itemListElement?.map((c) => c.name)).toEqual(['Home', 'Platform', 'Deployment']);
    });

    it('makes no Offer, Review, or AggregateRating claims', () => {
      for (const html of [services, deploy]) {
        for (const block of jsonLdBlocks(html)) {
          const raw = JSON.stringify(block);
          expect(raw).not.toContain('"AggregateRating"');
          expect(raw).not.toContain('"Review"');
          expect(raw).not.toContain('"Offer"');
        }
      }
    });
  });

  describe('deployment and operating-model content', () => {
    it('documents all four operating models with anchors', () => {
      for (const id of ['model-payer-cloud', 'model-managed', 'model-hybrid', 'model-saas']) {
        expect(deploy).toContain(`id="${id}"`);
      }
    });

    it('labels payer-controlled cloud deployment as available', () => {
      expect(deploy).toMatch(/svc-status--available">Available</);
      expect(services).toMatch(/svc-status--available">Available</);
    });

    it('never describes the hosted SaaS model as available', () => {
      for (const html of [services, deploy, index]) {
        expect(html).not.toMatch(/SaaS[^<.]{0,80}\bis available\b/i);
        expect(html).not.toMatch(/\bSaaS (?:now )?(?:generally )?available\b/i);
      }
      expect(deploy).toContain('svc-status--evaluation">Under evaluation');
      expect(deploy).toContain('not offered today');
    });

    it('carries a deployment decision framework covering the ownership dimensions', () => {
      for (const dimension of [
        'Infrastructure ownership',
        'Operational responsibility',
        'Cloud governance',
        'Integration control',
        'Release management',
        'Security review',
        'Observability',
        'Support model',
        'Procurement complexity',
        'Internal engineering required',
        'Data &amp; network boundary'
      ]) {
        expect(deploy).toContain(dimension);
      }
    });

    it('does not declare one operating model universally superior', () => {
      expect(deploy).toContain('The right operating model depends on');
      expect(deploy).toMatch(/not automatically/i);
    });

    it('keeps the deployment comparison table horizontally scrollable on small screens', () => {
      expect(deploy).toMatch(/<div class="ev-scroll">[\s\S]*?<table class="ev-table">/);
    });
  });

  describe('professional-services content', () => {
    it('covers all six service categories with stable anchors', () => {
      for (const id of [
        'offer-assessment',
        'offer-implementation',
        'offer-core-admin',
        'offer-sow-review',
        'offer-interop',
        'offer-fractional'
      ]) {
        expect(services).toContain(`id="${id}"`);
      }
    });

    it('states that services do not require buying the product', () => {
      expect(services).toMatch(/does not need to purchase or deploy Cloud Health Office/i);
    });

    it('keeps the software-first framing in the hero', () => {
      const hero = services.match(/<header class="ev-hero">[\s\S]*?<\/header>/)?.[0] ?? '';
      expect(hero).toMatch(/Payer software/i);
      expect(hero).toContain('href="/platform"');
    });

    it('makes no compliance, savings, timeline, certification, or partnership promises', () => {
      // Affirmative constructions only — the page deliberately *raises* the
      // certification question in order to answer "no", so a bare
      // "certified partner" substring is expected and correct.
      for (const html of [services, deploy]) {
        expect(html).not.toMatch(/guarantee[sd]? compliance/i);
        expect(html).not.toMatch(/(?<!\bno )guaranteed (?:savings|cost|timeline|outcome)/i);
        expect(html).not.toMatch(/\bwe are (?:an? )?(?:certified|authorized|official)/i);
        expect(html).not.toMatch(/\bofficial(?:ly)? (?:certified|endorsed|authorized)/i);
        expect(html).not.toMatch(/certifi(?:ed|cation) (?:by|from) (?:Cognizant|TriZetto|HealthEdge|Availity)/i);
      }
      expect(services).toContain('not legal advice');
      // ...and it says so explicitly.
      expect(services).toMatch(/no claim of certification, affiliation, endorsement, or implementation partnership/i);
    });

    it('uses coexistence language about named vendors, never displacement', () => {
      expect(services).toMatch(/designed to work alongside the systems health plans already depend on/i);
      expect(services).not.toMatch(/rip (?:and replace|out) (?:QNXT|Facets|HealthEdge)/i);
      expect(services).not.toMatch(/(?:QNXT|Facets|HealthEdge|Cognizant|TriZetto|Availity) is (?:inferior|obsolete|legacy junk)/i);
    });

    it('keeps the homepage hero product-led and puts services further down', () => {
      const heroStart = index.indexOf('<!-- ===== 1. HERO ===== -->');
      const servicesBand = index.indexOf('id="software-services"');
      const advisoryBand = index.indexOf('id="advisory"');
      expect(heroStart).toBeGreaterThan(-1);
      expect(servicesBand).toBeGreaterThan(heroStart);
      expect(advisoryBand).toBeGreaterThan(servicesBand);
      // The hero itself must not have become a consulting hero.
      const hero = index.slice(heroStart, index.indexOf('<!-- ===== 2.'));
      expect(hero).not.toMatch(/consult/i);
    });
  });

  describe('contact form and lead capture', () => {
    const TOPICS = [
      'platform',
      'saas',
      'payer-cloud',
      'managed',
      'hybrid',
      'cms0057-assessment',
      'implementation',
      'core-admin',
      'sow-review',
      'interop',
      'fractional-architect',
      'managed-ops',
      'support',
      'other'
    ];

    it('offers every interest category as a topic option', () => {
      for (const topic of TOPICS) {
        expect(contact).toContain(`<option value="${topic}">`);
      }
    });

    it('captures role, core platform, deployment preference, and evaluation stance', () => {
      expect(contact).toContain('name="role"');
      expect(contact).toContain('name="coreSystem"');
      expect(contact).toContain('name="deploymentModel"');
      expect(contact).toContain('name="evaluating"');
      for (const value of ['software', 'services', 'both']) {
        expect(contact).toContain(`name="evaluating" value="${value}"`);
      }
    });

    it('offers the core administration platforms a payer is likely to run', () => {
      for (const core of ['QNXT', 'Facets', 'HealthEdge']) {
        expect(contact).toContain(`<option value="${core}">`);
      }
    });

    it('labels every input and marks the required ones', () => {
      for (const id of ['cs-name', 'cs-org', 'cs-email', 'cs-topic', 'cs-role', 'cs-core', 'cs-deployment', 'cs-message']) {
        expect(contact).toContain(`id="${id}"`);
        expect(contact).toContain(`for="${id}"`);
      }
      expect(contact).toMatch(/id="cs-email"[^>]*required/);
      expect(contact).toMatch(/name="evaluating" value="software" required/);
      expect(contact).toContain('<legend class="contact-form-legend">');
    });

    it('validates required fields, including the evaluation stance, before submitting', () => {
      expect(contact).toContain('if (!name || !org || !email || !topic || !message || !evaluating)');
      expect(contact).toContain('Please fill in all required fields before submitting.');
    });

    it('preselects the topic from /contact?interest=', () => {
      expect(contact).toContain("new URLSearchParams(window.location.search).get('interest')");
      expect(contact).toContain('applyInterestFromQuery');
    });

    it('routes live technical support away from the public marketing form', () => {
      expect(contact).toContain('id="cs-support-notice"');
      expect(contact).toContain('enterprise@cloudhealthoffice.com');
    });

    it('deep-links from /services and /deploy carry a known interest key', () => {
      const links = [...services.matchAll(/\/contact\?interest=([a-z0-9-]+)/g), ...deploy.matchAll(/\/contact\?interest=([a-z0-9-]+)/g)];
      expect(links.length).toBeGreaterThan(0);
      for (const [, key] of links) {
        expect(TOPICS).toContain(key);
      }
    });
  });

  describe('PHI and sensitive-data warnings', () => {
    it('shows a prominent warning above the contact form', () => {
      expect(contact).toContain('class="contact-phi-warning"');
      const warning = contact.match(/<div class="contact-phi-warning"[\s\S]*?<\/div>/)?.[0] ?? '';
      for (const term of ['member data', 'patient data', 'claim data', 'production credentials', 'security secrets']) {
        expect(warning).toContain(term);
      }
      expect(contact.indexOf('contact-phi-warning')).toBeLessThan(contact.indexOf('id="salesContactForm"'));
    });

    it('repeats the warning on the services and deployment CTAs', () => {
      for (const html of [services, deploy]) {
        expect(html).toMatch(/do not send PHI, member data, claim data, production credentials/i);
      }
    });

    it('keeps the assistant knowledge pack refusing PHI and invented availability', () => {
      expect(knowledge).toContain('PHI, member IDs, real claim files');
      expect(knowledge).toMatch(/Availability, certifications, partnerships/i);
      expect(knowledge).toMatch(/under evaluation/i);
      expect(knowledge).toContain('Never describe it as available');
    });
  });

  describe('analytics', () => {
    it('registers named page-view events for the services and deployment funnels', () => {
      expect(analytics).toContain("'/services': 'services_page_view'");
      expect(analytics).toContain("'/deploy': 'deployment_page_view'");
    });

    it('tracks /services as a CTA destination', () => {
      expect(analytics).toContain("{ match: '/services', event: 'services_cta_click' }");
    });

    it('fires the services and deployment CTA events from the pages', () => {
      expect(index).toContain('data-ga-event="platform_services_cta_click"');
      expect(index).toContain('data-ga-event="deployment_discussion_started"');
      expect(services).toContain('data-ga-event="services_cta_click"');
      expect(services).toContain('data-ga-event="advisory_contact_started"');
      expect(deploy).toContain('data-ga-event="deployment_discussion_started"');
    });

    it('emits the interest events from the contact form', () => {
      for (const event of [
        'product_interest_selected',
        'service_interest_selected',
        'deployment_interest_selected',
        'deployment_model_selected',
        'advisory_contact_submitted',
        'cms0057_assessment_interest',
        'implementation_services_interest',
        'core_admin_advisory_interest',
        'sow_review_interest',
        'saas_interest',
        'payer_cloud_interest',
        'managed_operations_interest'
      ]) {
        expect(contact).toContain(event);
      }
    });

    it('uses the existing choTrack helper rather than a second analytics platform', () => {
      expect(contact).toContain('window.choTrack');
      expect(contact).not.toMatch(/segment\.com|mixpanel|hotjar|posthog/i);
    });

    it('sends no health information in analytics payloads', () => {
      const trackCalls = [...contact.matchAll(/track\('[a-z0-9_]+',\s*\{([^}]*)\}/g)].map((m) => m[1]);
      expect(trackCalls.length).toBeGreaterThan(0);
      for (const params of trackCalls) {
        expect(params).not.toMatch(/member|patient|claimNumber|diagnosis|ssn/i);
      }
    });
  });

  describe('accessibility and responsive rendering', () => {
    it('keeps the skip link, a single h1, and a main landmark on the new pages', () => {
      for (const html of [services, deploy]) {
        expect(html).toContain('class="skip-to-main"');
        expect(html).toContain('<main id="main-content">');
        expect((html.match(/<h1[^>]*>/g) || []).length).toBe(1);
      }
    });

    it('labels the in-page navigation landmarks', () => {
      for (const html of [services, deploy]) {
        expect(html).toContain('aria-label="Breadcrumb"');
        expect(html).toContain('aria-label="On this page"');
      }
    });

    it('marks decorative diagram rails as hidden from assistive tech', () => {
      const rails = [...services.matchAll(/<div class="svc-(?:rail|connector)"[^>]*>/g)];
      expect(rails.length).toBeGreaterThan(0);
      for (const [tag] of rails) {
        expect(tag).toContain('aria-hidden="true"');
      }
    });

    it('builds the diagrams from real markup rather than raster images', () => {
      const figures = [...services.matchAll(/<figure class="svc-(?:pillars|stack)">[\s\S]*?<\/figure>/g)];
      expect(figures.length).toBe(2);
      for (const [figure] of figures) {
        expect(figure).not.toContain('<img');
        expect(figure).toContain('<figcaption');
      }
    });

    it('stacks the diagram branches and model cards on narrow viewports', () => {
      expect(servicesCss).toMatch(/@media \(max-width: 780px\)[\s\S]*?\.svc-branches\s*\{\s*grid-template-columns: 1fr;/);
      expect(servicesCss).toMatch(/@media \(max-width: 720px\)[\s\S]*?\.svc-model dl\s*\{\s*grid-template-columns: 1fr;/);
      expect(servicesCss).toContain('minmax(');
    });

    it('respects prefers-reduced-motion', () => {
      expect(servicesCss).toContain('@media (prefers-reduced-motion: reduce)');
    });
  });

  describe('message sheet consistency', () => {
    it('locks the operating-model status labels', () => {
      expect(messageSheet).toContain('Deployment & operating models (locked status labels)');
      expect(messageSheet).toContain('**Under evaluation**');
      expect(messageSheet).toContain('**By engagement**');
    });

    it('adds Services to the documented information architecture', () => {
      expect(messageSheet).toMatch(/Primary nav:.*Services.*Contact/);
      expect(messageSheet).toContain('/services');
    });

    it('bans the unsupported services and deployment claims', () => {
      const banned = messageSheet.slice(messageSheet.indexOf('## BANNED phrases'));
      expect(banned).toMatch(/guaranteed compliance/i);
      expect(banned).toMatch(/certified by, partnered with, or endorsed by/i);
      expect(banned).toMatch(/SaaS is "available"/i);
    });
  });
});
