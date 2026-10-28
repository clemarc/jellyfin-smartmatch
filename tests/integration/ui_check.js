#!/usr/bin/env node
// Drives the real Jellyfin web client in headless Chromium: logs in as admin, opens the Smart Match
// dashboard page, and checks that the ES-module controller rendered status, tiles and findings.
// Usage: node tests/integration/ui_check.js <baseUrl> <user> <password> <screenshotDir>
const { chromium } = require('playwright');

(async () => {
    const [base, user, password, outDir] = process.argv.slice(2);
    const launchOptions = process.env.PW_CHROMIUM ? { executablePath: process.env.PW_CHROMIUM } : {};
    const browser = await chromium.launch(launchOptions);
    const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
    const errors = [];
    page.on('pageerror', (e) => errors.push(String(e)));
    page.on('console', (m) => { if (m.type() === 'error' && /SmartMatch|configurationpage/i.test(m.text())) errors.push(m.text()); });
    const result = { ok: false, checks: {} };
    try {
        await page.goto(base + '/web/#/login', { waitUntil: 'networkidle' });
        const manual = page.locator('.btnManual');
        if (await manual.isVisible().catch(() => false)) await manual.click();
        await page.fill('#txtManualName', user);
        await page.fill('#txtManualPassword', password);
        await page.locator('.manualLoginForm button[type=submit]').click();
        await page.waitForURL(/#\/(home|dashboard)/, { timeout: 60000 });

        await page.goto(base + '/web/#/configurationpage?name=SmartMatch', { waitUntil: 'networkidle' });
        await page.waitForSelector('#smTiles .sm-tile', { timeout: 60000 });
        await page.waitForSelector('#smFindingsTable tbody tr', { timeout: 60000 });
        result.checks.header = (await page.textContent('#smCompat')).trim();
        result.checks.mode = (await page.textContent('#smMode')).trim();
        result.checks.tiles = await page.locator('#smTiles .sm-tile').count();
        result.checks.rows = await page.locator('#smFindingsTable tbody tr').count();
        result.checks.firstRow = (await page.locator('#smFindingsTable tbody tr').first().innerText()).replace(/\s+/g, ' ');
        await page.screenshot({ path: outDir + '/smartmatch-overview.png', fullPage: true });

        // Row detail with trace and candidates.
        await page.locator('#smFindingsTable tbody tr').first().click();
        await page.waitForSelector('#smDetail h4', { timeout: 30000 });
        result.checks.detail = (await page.locator('#smDetail').innerText()).includes('Normalization trace');
        await page.screenshot({ path: outDir + '/smartmatch-detail.png', fullPage: true });

        // Bulk preview dialog opens and can be cancelled without changes.
        await page.locator('#smApplyAll').click();
        await page.waitForSelector('.sm-dialog', { timeout: 30000 });
        result.checks.preview = (await page.locator('.sm-dialog').innerText()).replace(/\s+/g, ' ').slice(0, 300);
        await page.screenshot({ path: outDir + '/smartmatch-preview.png' });
        await page.keyboard.press('Escape');

        // Settings tab renders the libraries table.
        await page.locator('.sm-tab[data-tab=settings]').click();
        await page.waitForSelector('#smLibraries tbody tr', { timeout: 30000 });
        result.checks.settingsLibraries = await page.locator('#smLibraries tbody tr').count();
        await page.screenshot({ path: outDir + '/smartmatch-settings.png', fullPage: true });

        // Mobile width stays usable (no horizontal page scroll).
        await page.setViewportSize({ width: 390, height: 844 });
        await page.locator('.sm-tab[data-tab=findings]').click();
        result.checks.mobileOverflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        await page.screenshot({ path: outDir + '/smartmatch-mobile.png', fullPage: true });

        result.errors = errors;
        result.ok = errors.length === 0 && result.checks.tiles === 6 && result.checks.rows > 0 && result.checks.detail && result.checks.settingsLibraries > 0;
    } catch (e) {
        result.errors = errors.concat([String(e)]);
        await page.screenshot({ path: outDir + '/smartmatch-failure.png', fullPage: true }).catch(() => {});
    }
    await browser.close();
    console.log(JSON.stringify(result));
    process.exit(result.ok ? 0 : 1);
})();
