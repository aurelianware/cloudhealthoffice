/* Pure arithmetic is shared by the browser and node:test. No input leaves the page. */
(function (root) {
  "use strict";
  var costKeys = [
    "Pmpm",
    "Fixed",
    "Infrastructure",
    "Support",
    "Staffing",
    "Integration",
  ];
  function number(value, name) {
    if (
      value === "" ||
      value === null ||
      value === undefined ||
      typeof value === "boolean" ||
      (typeof value === "string" && !value.trim())
    )
      throw new Error(
        name + ": enter a value, including an explicit zero where appropriate.",
      );
    var n = Number(value);
    if (!Number.isFinite(n) || n < 0)
      throw new Error(name + ": use a finite, non-negative number.");
    return n;
  }
  function calculate(input) {
    var v = number(input.memberMonths, "Annual member months");
    var h = number(input.horizon, "Analysis horizon");
    if (!Number.isInteger(v) || v <= 0)
      throw new Error("Annual member months must be a positive whole number.");
    if (!Number.isInteger(h) || h < 1 || h > 50)
      throw new Error("Analysis horizon must be a whole number from 1 to 50.");
    if (input.licenseKnown !== true || input.supportKnown !== true)
      return { unknown: true };
    var x = {};
    ["current", "alternative"].forEach(function (side) {
      costKeys.forEach(function (key) {
        x[side + key] = number(input[side + key], side + " " + key);
      });
    });
    ["transition", "exit", "parallel"].forEach(function (key) {
      x[key] = number(input[key], key);
    });
    function annual(side) {
      return (
        v * x[side + "Pmpm"] +
        costKeys.slice(1).reduce(function (sum, key) {
          return sum + x[side + key];
        }, 0)
      );
    }
    var a = annual("current"),
      b = annual("alternative"),
      upfront = x.transition + x.exit + x.parallel;
    var out = {
      unknown: false,
      currentAnnual: a,
      alternativeAnnual: b,
      currentHorizon: h * a,
      alternativeHorizon: h * b + upfront,
      difference: h * (a - b) - upfront,
      effectivePmpm: (h * b + upfront) / (h * v),
      payback: a > b ? upfront / (a - b) : null,
    };
    Object.keys(out).forEach(function (key) {
      if (typeof out[key] === "number" && !Number.isFinite(out[key]))
        throw new Error("Scenario exceeds the supported numeric range.");
    });
    return out;
  }
  if (typeof module === "object" && module.exports)
    module.exports = { calculate: calculate };
  if (!root || !root.document) return;
  var form = document.getElementById("economics-form");
  if (!form) return;
  var results = document.getElementById("calculator-results"),
    error = document.getElementById("calculator-error");
  var usd = new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 2,
  });
  function el(id) {
    return document.getElementById(id);
  }
  function syncVolume() {
    el("memberMonths").readOnly = el("derive").checked;
    el("members").disabled = !el("derive").checked;
    if (el("derive").checked)
      el("memberMonths").value =
        el("members").value === "" ? "" : Number(el("members").value) * 12;
  }
  var engaged = false;
  form.addEventListener("input", function () {
    syncVolume();
    results.replaceChildren(
      Object.assign(document.createElement("p"), {
        textContent: "Assumptions changed. Calculate again to refresh results.",
      }),
    );
    if (!engaged) {
      engaged = true;
      if (root.choTrack)
        root.choTrack("calculator_engagement", {
          page_path: "/economics-of-vendor-lock-in",
        });
    }
  });
  form.addEventListener("submit", function (event) {
    event.preventDefault();
    error.hidden = true;
    syncVolume();
    try {
      if (!el("licenseKnown").checked || !el("supportKnown").checked) {
        results.textContent =
          "Alternative licensing or support is unknown. Complete alternative totals, modeled difference and payback are unavailable until both assumptions are supplied and confirmed.";
        return;
      }
      if (!form.reportValidity()) return;
      if (
        el("derive").checked &&
        (!Number.isInteger(Number(el("members").value)) ||
          Number(el("members").value) <= 0)
      )
        throw new Error(
          "Average covered members must be a positive whole number.",
        );
      var data = {};
      form.querySelectorAll("input[name]").forEach(function (field) {
        data[field.name] = field.value;
      });
      data.licenseKnown = el("licenseKnown").checked;
      data.supportKnown = el("supportKnown").checked;
      // Missing alternative commercial assumptions are never interpreted as free licensing/support.
      var out = calculate(data);
      results.replaceChildren();
      function paragraph(text) {
        var p = document.createElement("p");
        p.textContent = text;
        results.appendChild(p);
      }
      if (out.unknown) {
        paragraph(
          "Alternative licensing or support is unknown. Complete alternative totals, modeled difference and payback are unavailable until both assumptions are supplied and confirmed.",
        );
        return;
      }
      var dl = document.createElement("dl");
      function row(label, value, negative) {
        var dt = document.createElement("dt"),
          dd = document.createElement("dd");
        dt.textContent = label;
        dd.textContent = value;
        if (negative) dd.className = "negative";
        dl.append(dt, dd);
      }
      row("Current annual cost", usd.format(out.currentAnnual));
      row("Alternative annual cost", usd.format(out.alternativeAnnual));
      row("Current horizon cost", usd.format(out.currentHorizon));
      row("Alternative horizon cost", usd.format(out.alternativeHorizon));
      row(
        "Modeled difference (current − alternative)",
        usd.format(out.difference),
        out.difference < 0,
      );
      row("Effective alternative PMPM", usd.format(out.effectivePmpm));
      row(
        "Simple payback",
        out.payback === null
          ? "Not applicable: no positive recurring cost difference"
          : out.payback.toFixed(2) +
              " years" +
              (out.payback > Number(data.horizon)
                ? " (beyond analysis horizon)"
                : ""),
      );
      results.appendChild(dl);
      paragraph(
        out.difference < 0
          ? "The alternative scenario costs more across this horizon."
          : out.difference > 0
            ? "The alternative scenario has a lower modeled total cost. This is not a savings guarantee."
            : "The scenarios have equal modeled total costs.",
      );
      var heading = document.createElement("h3");
      heading.textContent = "Scenario assumptions";
      results.appendChild(heading);
      paragraph(
        "Annual member months: " +
          Number(data.memberMonths).toLocaleString("en-US") +
          (el("derive").checked
            ? " (average members × 12)"
            : " (entered directly)") +
          ". Horizon: " +
          data.horizon +
          " years. Licensing and support confirmed included once. All values in USD; constant volume and costs; no discounting.",
      );
      var assumptions = document.createElement("dl");
      results.appendChild(assumptions);
      Object.keys(data)
        .filter(function (key) {
          return ![
            "licenseKnown",
            "supportKnown",
            "horizon",
            "memberMonths",
            "members",
          ].includes(key);
        })
        .forEach(function (key) {
          var dt = document.createElement("dt"),
            dd = document.createElement("dd");
          dt.textContent = key.replace(/([A-Z])/g, " $1");
          dd.textContent = usd.format(Number(data[key]));
          assumptions.append(dt, dd);
        });
      if (root.choTrack)
        root.choTrack("calculator_calculated", {
          page_path: "/economics-of-vendor-lock-in",
        });
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
      results.textContent = "Correct the inputs and calculate again.";
    }
  });
  el("example").addEventListener("click", function () {
    el("derive").checked = true;
    el("members").value = 100000;
    el("horizon").value = 3;
    ["current", "alternative"].forEach(function (side) {
      costKeys.forEach(function (key) {
        el(side + key).value =
          key === "Pmpm"
            ? side === "current"
              ? 2
              : 1.5
            : key === "Fixed"
              ? 100000
              : 0;
      });
    });
    el("licenseKnown").checked = true;
    el("supportKnown").checked = true;
    el("transition").value = 1800000;
    el("exit").value = 200000;
    el("parallel").value = 100000;
    syncVolume();
    form.requestSubmit();
  });
  el("print-results").addEventListener("click", function () {
    root.print();
  });
  syncVolume();
})(typeof window !== "undefined" ? window : null);
