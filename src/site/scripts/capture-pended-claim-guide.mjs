import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const portalUrl = (process.env.GUIDE_PORTAL_URL ?? "http://localhost:8080").replace(/\/$/, "");
const claimId = process.env.GUIDE_CLAIM_ID;
const resolvedClaimId = process.env.GUIDE_RESOLVED_CLAIM_ID;

if (!claimId) {
  throw new Error(
    "GUIDE_CLAIM_ID is required. Use a synthetic claim whose status is Pended."
  );
}

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const outputDirectory = path.resolve(
  scriptDirectory,
  "../graphics/user-guides/pended-claim"
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
  await page.goto(`${portalUrl}/local-demo/sign-in?redirectUri=/`, {
    waitUntil: "domcontentloaded"
  });

  await page.goto(`${portalUrl}/work-queues`, { waitUntil: "domcontentloaded" });
  await page.getByText("Claims Work Queues", { exact: true }).waitFor();
  await page.getByText(claimId, { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "work-queue.png"),
    fullPage: false
  });

  await page.goto(`${portalUrl}/claims/${claimId}`, { waitUntil: "domcontentloaded" });
  await page.getByText("Manual Review Required", { exact: true }).waitFor();
  await page.getByText("AI Claims Examiner Advisory", { exact: true }).waitFor();
  await page.screenshot({
    path: path.join(outputDirectory, "ai-advisory.png"),
    fullPage: false
  });

  if (resolvedClaimId) {
    await page.goto(`${portalUrl}/claims/${resolvedClaimId}`, { waitUntil: "domcontentloaded" });
    await page.getByText("Examiner Disposition Record", { exact: true }).waitFor();
    await page.screenshot({
      path: path.join(outputDirectory, "examiner-disposition.png"),
      fullPage: false
    });
  }
} finally {
  await browser.close();
}

console.log(`Captured synthetic pended-claim screenshots in ${outputDirectory}`);
