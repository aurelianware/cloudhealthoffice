import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const portalUrl = (process.env.GUIDE_PORTAL_URL ?? "http://localhost:8080").replace(/\/$/, "");
const planId = process.env.GUIDE_PLAN_ID;

if (!planId) {
  throw new Error("GUIDE_PLAN_ID is required. Use a published synthetic plan visible to the local demo tenant.");
}

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const outputDirectory = path.resolve(scriptDirectory, "../graphics/user-guides/first-claim");
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

  const planRow = page.locator("tbody tr").filter({ hasText: planId });
  await planRow.waitFor();
  await planRow.locator("button").first().click();
  await page.getByText("Benefit Plan Details", { exact: true }).waitFor();
  await page.locator(".mud-tab").filter({ hasText: "Validation" }).click();
  await page.getByText("Ready for a claim test", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "plan-ready-for-claim.png"),
    fullPage: false
  });

  await page.getByTestId("run-synthetic-837").click();
  await page.getByTestId("open-synthetic-member").waitFor({ timeout: 90_000 });
  await page.getByText("Network tier", { exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({
    path: path.join(outputDirectory, "claim-plan-resolution.png"),
    fullPage: false
  });

  await page.getByTestId("open-synthetic-member").click();
  await page.getByText("Member Details", { exact: true }).waitFor();
  await page.locator(".mud-tab").filter({ hasText: "Coverage" }).click();
  await page.getByText("BPVALIDATE", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "member-active-coverage.png"),
    fullPage: false
  });
} finally {
  await browser.close();
}

console.log(`Captured first-claim evaluator screenshots in ${outputDirectory}`);
