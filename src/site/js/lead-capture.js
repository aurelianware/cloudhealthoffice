/*
 * lead-capture.js
 * ---------------
 * One reusable behavior for every lead form on the marketing site. Loaded on
 * every page by build.mjs. Progressive enhancement: any <form data-lead-form>
 * is upgraded; without JS the form still POSTs to its Formspree action.
 *
 * A form opts in with:
 *   <form data-lead-form data-lead-interest="evaluator-pack"
 *         action="https://formspree.io/f/xxxx" method="POST">
 *     ... inputs (name=firstName,lastName,email,company,role,coreSystem,interest,message) ...
 *     <div data-lead-error role="alert" hidden></div>
 *     <button type="submit">Send</button>
 *   </form>
 *   <div data-lead-thankyou hidden> ...thank-you + asset link... </div>
 *
 * Behavior:
 *   - Attaches identity fields (anonymous_id, pages viewed this session).
 *   - Prefers work email; flags consumer inboxes but never blocks them.
 *   - Never collects PHI / member data — this is a B2B marketing site.
 *   - Fires form_start / form_submit / form_error / asset_download analytics.
 *   - Aliases anonymous_id -> email/company on success.
 */
(function () {
  'use strict';

  var CONSUMER_DOMAINS = [
    'gmail.com', 'googlemail.com', 'yahoo.com', 'ymail.com', 'hotmail.com',
    'outlook.com', 'live.com', 'aol.com', 'icloud.com', 'me.com', 'mac.com',
    'proton.me', 'protonmail.com', 'gmx.com', 'mail.com', 'msn.com'
  ];

  function track(name, params) {
    if (typeof window.choTrack === 'function') window.choTrack(name, params || {});
  }

  function isEmail(value) {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
  }

  function isConsumerInbox(email) {
    var at = email.lastIndexOf('@');
    if (at === -1) return false;
    return CONSUMER_DOMAINS.indexOf(email.slice(at + 1).toLowerCase()) !== -1;
  }

  function isFormspreeConfigured(form) {
    return /^https:\/\/formspree\.io\/f\/[a-z0-9]+$/i.test(form.getAttribute('action') || '');
  }

  function setHidden(form, name, value) {
    var input = form.querySelector('input[name="' + name + '"][type="hidden"]');
    if (!input) {
      input = document.createElement('input');
      input.type = 'hidden';
      input.name = name;
      form.appendChild(input);
    }
    input.value = value;
  }

  function showError(form, message) {
    var el = form.querySelector('[data-lead-error]');
    if (el) {
      el.textContent = message;
      el.hidden = false;
    }
  }
  function clearError(form) {
    var el = form.querySelector('[data-lead-error]');
    if (el) {
      el.textContent = '';
      el.hidden = true;
    }
  }

  function enhance(form) {
    var interest = form.getAttribute('data-lead-interest') || 'general';
    var startedFired = false;

    form.addEventListener('focusin', function () {
      if (!startedFired) {
        startedFired = true;
        track('form_start', { form_interest: interest, page_path: location.pathname });
      }
    });

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      clearError(form);

      var emailField = form.querySelector('input[type="email"], input[name="email"]');
      var email = emailField ? emailField.value.trim() : '';

      // Required-field check driven by the markup (required attributes).
      var missing = false;
      form.querySelectorAll('[required]').forEach(function (field) {
        if (!String(field.value || '').trim()) missing = true;
      });
      if (missing) {
        showError(form, 'Please fill in the required fields before sending.');
        return;
      }
      if (email && !isEmail(email)) {
        showError(form, 'Please enter a valid email address.');
        return;
      }

      // Attach identity + context. Never collect PHI.
      var anonId = (window.choIdentity && window.choIdentity.getAnonymousId)
        ? window.choIdentity.getAnonymousId() : '';
      var pages = (window.choIdentity && window.choIdentity.getViewedPages)
        ? window.choIdentity.getViewedPages() : [];
      setHidden(form, 'anonymous_id', anonId);
      setHidden(form, 'pages_viewed', pages.join(' > '));
      setHidden(form, 'interest', form.getAttribute('data-lead-interest') || (
        (form.querySelector('[name="interest"]') || {}).value || 'general'));
      if (email) {
        setHidden(form, 'consumer_inbox', isConsumerInbox(email) ? 'yes' : 'no');
        setHidden(form, '_replyto', email);
      }

      if (!isFormspreeConfigured(form)) {
        showError(form, 'This form is not configured yet. Please email sales@cloudhealthoffice.com directly.');
        return;
      }

      var submitBtn = form.querySelector('[type="submit"]');
      var originalLabel = submitBtn ? submitBtn.textContent : '';
      if (submitBtn) { submitBtn.disabled = true; submitBtn.textContent = 'Sending…'; }

      var company = (form.querySelector('[name="company"]') || {}).value || '';
      var role = (form.querySelector('[name="role"]') || {}).value || '';
      var firstName = (form.querySelector('[name="firstName"]') || {}).value || '';
      var lastName = (form.querySelector('[name="lastName"]') || {}).value || '';

      fetch(form.getAttribute('action'), {
        method: 'POST',
        headers: { 'Accept': 'application/json' },
        body: new FormData(form)
      }).then(function (res) {
        if (!res.ok) {
          return res.json().catch(function () { return null; }).then(function (data) {
            var msg = data && data.errors && data.errors.length
              ? data.errors.map(function (x) { return x.message; }).join(' ')
              : 'Request failed';
            throw new Error(msg);
          });
        }
        // Success: alias, reveal thank-you, count asset download if present.
        if (email && window.choIdentity && window.choIdentity.identify) {
          window.choIdentity.identify({
            email: email, company: company, role: role,
            name: (firstName + ' ' + lastName).trim()
          });
        }
        track('form_submit', {
          form_interest: interest,
          consumer_inbox: email ? isConsumerInbox(email) : false,
          page_path: location.pathname
        });
        // Prefer the explicit data-for mapping so a container with more than
        // one lead form always reveals the matching thank-you block; fall back
        // to the nearest one under the same parent.
        var thankyou = (form.id && document.querySelector('[data-lead-thankyou][data-for="' + form.id + '"]')) ||
          form.parentNode.querySelector('[data-lead-thankyou]');
        form.hidden = true;
        if (thankyou) {
          thankyou.hidden = false;
          var asset = thankyou.querySelector('[data-asset-download]');
          if (asset) {
            track('asset_download', {
              asset: asset.getAttribute('data-asset-download') || interest,
              page_path: location.pathname
            });
          }
          try { thankyou.scrollIntoView({ behavior: 'smooth', block: 'center' }); } catch (e) {}
        }
      }).catch(function (err) {
        track('form_error', { form_interest: interest, message: (err && err.message) || 'error' });
        showError(form, (err && err.message) || 'Something went wrong. Please email sales@cloudhealthoffice.com.');
        if (submitBtn) { submitBtn.disabled = false; submitBtn.textContent = originalLabel; }
      });
    });
  }

  function init() {
    var forms = document.querySelectorAll('form[data-lead-form]');
    for (var i = 0; i < forms.length; i++) enhance(forms[i]);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
