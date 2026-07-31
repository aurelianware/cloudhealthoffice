import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const portalUrl = (process.env.GUIDE_PORTAL_URL ?? "http://localhost:8080").replace(/\/$/, "");
const claimId = process.env.GUIDE_CLAIM_ID;

if (!claimId) {
  throw new Error(
    "GUIDE_CLAIM_ID is required. Run scripts/smoke/834-to-837-e2e-smoke.sh and use the returned claim ID."
  );
}

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const outputDirectory = path.resolve(
  scriptDirectory,
  "../graphics/user-guides/submit-837"
);

await mkdir(outputDirectory, { recursive: true });

const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({
  viewport: { width: 1440, height: 1000 },
  deviceScaleFactor: 1,
  colorScheme: "dark",
  reducedMotion: "reduce"
});
const page = await context.newPage();

try {
  await page.goto(`${portalUrl}/local-demo/sign-in?redirectUri=/`, {
    waitUntil: "domcontentloaded"
  });
  await page.goto(`${portalUrl}/claims/${claimId}`, { waitUntil: "domcontentloaded" });

  await page.locator(".mud-tabs").waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "claim-overview.png"),
    fullPage: false
  });

  await page.getByText("Adjudication Pipeline", { exact: true }).click();
  await page.waitForTimeout(300);
  await page.screenshot({
    path: path.join(outputDirectory, "adjudication-pipeline.png"),
    fullPage: false
  });
} finally {
  await browser.close();
}

console.log(`Captured synthetic guide screenshots in ${outputDirectory}`);
