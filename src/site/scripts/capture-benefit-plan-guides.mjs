import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const portalUrl = (process.env.GUIDE_PORTAL_URL ?? "http://localhost:8080").replace(/\/$/, "");
const planId = process.env.GUIDE_PLAN_ID;
const providerNpi = process.env.GUIDE_PROVIDER_NPI ?? "1999999992";

if (!planId) {
  throw new Error(
    "GUIDE_PLAN_ID is required. Use a synthetic plan visible to the local demo tenant."
  );
}

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const outputDirectory = path.resolve(
  scriptDirectory,
  "../graphics/user-guides/benefit-plans"
);

await mkdir(outputDirectory, { recursive: true });

const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({
  viewport: { width: 1920, height: 1080 },
  deviceScaleFactor: 1,
  colorScheme: "dark",
  reducedMotion: "reduce"
});
const page = await context.newPage();

try {
  await page.goto(`${portalUrl}/local-demo/sign-in?redirectUri=/benefit-plans`, {
    waitUntil: "domcontentloaded"
  });
  await page.getByText("Benefit Plans", { exact: true }).first().waitFor();

  const search = page.getByPlaceholder("Plan name, ID...");
  await search.fill(planId);
  await search.press("Enter");
  await page.getByText(planId, { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "plan-list.png"),
    fullPage: false
  });

  await page.getByRole("button", { name: "New Plan" }).click();
  await page.getByText("Create New Benefit Plan", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "create-plan.png"),
    fullPage: false
  });
  await page.getByRole("button", { name: "Cancel" }).click();

  const planRow = page.locator("tbody tr").filter({ hasText: planId });
  await planRow.locator("button").first().click();
  await page.getByText("Benefit Plan Details", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "plan-details.png"),
    fullPage: false
  });

  await page.locator(".mud-tab").filter({ hasText: "Benefits" }).click();
  await page.getByText("Covered Services", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "benefits-tab.png"),
    fullPage: false
  });

  await page.getByRole("button", { name: "Add Benefits" }).click();
  await page.getByText("Add Benefit", { exact: true }).first().waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "add-benefit-rule.png"),
    fullPage: false
  });
  await page.getByRole("button", { name: "Cancel" }).click();

  await page.locator(".mud-tab").filter({ hasText: "Networks" }).click();
  await page.getByText("Plan Network Tiers", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "networks-tab.png"),
    fullPage: false
  });

  await page.getByRole("button", { name: "Add Network Tier" }).click();
  await page.getByText("Add Network Tier", { exact: true }).first().waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "add-network-tier.png"),
    fullPage: false
  });
  await page.getByRole("button", { name: "Cancel" }).click();

  await page.getByTestId("load-network-roster").click();
  await page.getByText(/Effective provider roster/).waitFor();
  await page.getByText(/Effective provider roster/).scrollIntoViewIfNeeded();
  await page.screenshot({
    path: path.join(outputDirectory, "network-roster-verification.png"),
    fullPage: false
  });

  await page.getByTestId(`verify-roster-provider-${providerNpi}`).click();
  await page.getByTestId("network-membership-result").waitFor();
  await page.getByTestId("network-membership-result").scrollIntoViewIfNeeded();
  await page.screenshot({
    path: path.join(outputDirectory, "network-membership-proof.png"),
    fullPage: false
  });

  await page.locator(".mud-tab").filter({ hasText: "Exclusions" }).click();
  await page.getByText("Services Not Covered", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "exclusions-tab.png"),
    fullPage: false
  });

  await page.getByRole("button", { name: "Add Exclusion" }).click();
  await page.getByText("Add Plan Exclusion", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "add-plan-exclusion.png"),
    fullPage: false
  });
  await page.getByRole("button", { name: "Cancel" }).click();

  await page.locator(".mud-tab").filter({ hasText: "Validation" }).click();
  await page.getByText("Ready for a claim test", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "validation-checks.png"),
    fullPage: false
  });

  await page.getByTestId("run-synthetic-837").click();
  await page.getByText(/Claim BPV837.*resolved to/).waitFor({ timeout: 90_000 });
  await page.getByText("Network tier", { exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({
    path: path.join(outputDirectory, "synthetic-837-result.png"),
    fullPage: false
  });
} finally {
  await browser.close();
}

console.log(`Captured synthetic benefit-plan screenshots in ${outputDirectory}`);
