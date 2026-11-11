'use strict';

/**
 * Drives the installed server's web UI through first setup and one real playback.
 *
 * Reads a JSON config path from argv[2] (written by smoke-installed-app.py) and writes
 * browser-journey.json beside it. The receipt is written on every exit path, including a
 * browser that fails to close, so the native harness never has to guess what happened.
 * The item under test is identified by the server's own record of its path, and playback
 * counts only when the server reports the item as now playing in a session.
 */

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('@playwright/test');

const API_TIMEOUT_MS = 30000;
const ITEM_POLL_MS = 120000;
const SESSION_POLL_MS = 60000;

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

/** Minimal authenticated JSON client for the server the browser is talking to. */
function createApi(baseUrl) {
    const deviceId = crypto.randomUUID();
    let token = null;
    async function call(method, pathname, body) {
        const authorization = `MediaBrowser Client="SlopTank Smoke", Device="GitHub Runner", DeviceId="${deviceId}", Version="0.1.0"`
            + (token ? `, Token="${token}"` : '');
        const response = await fetch(`${baseUrl}${pathname}`, {
            method,
            headers: { Authorization: authorization, 'Content-Type': 'application/json', Accept: 'application/json' },
            body: body === undefined ? undefined : JSON.stringify(body),
            signal: AbortSignal.timeout(API_TIMEOUT_MS)
        });
        const text = await response.text();
        if (!response.ok) {
            throw new Error(`${method} ${pathname} returned ${response.status}: ${text.slice(0, 500)}`);
        }
        return text ? JSON.parse(text) : null;
    }
    return {
        async authenticate(username, password) {
            const session = await call('POST', '/Users/AuthenticateByName', { Username: username, Pw: password });
            assert.ok(session && session.AccessToken, 'the server returned no access token for the UI-created admin');
            token = session.AccessToken;
            return session;
        },
        call
    };
}

/** The server must record the fixture at its exact path; the UI shows display names, not files. */
async function waitForLibraryItem(api, libraryId, mediaPath) {
    const end = Date.now() + ITEM_POLL_MS;
    let seen = [];
    while (Date.now() < end) {
        const result = await api.call('GET',
            `/Items?ParentId=${encodeURIComponent(libraryId)}&Recursive=true&IncludeItemTypes=Movie&Fields=Path`);
        seen = (result && result.Items) || [];
        const match = seen.find(item => item.Path === mediaPath);
        if (match) {
            return match;
        }
        await sleep(2000);
    }
    throw new Error(`the server never listed ${mediaPath} under library ${libraryId}; last items: ${JSON.stringify(seen)}`);
}

/** Playback is proven by the server's session report, not by the browser alone. */
async function waitForServerPlayback(api, itemId) {
    const end = Date.now() + SESSION_POLL_MS;
    let sessions = [];
    while (Date.now() < end) {
        sessions = (await api.call('GET', '/Sessions')) || [];
        const playing = sessions.find(session => session.NowPlayingItem && session.NowPlayingItem.Id === itemId);
        if (playing) {
            return {
                nowPlayingItemId: playing.NowPlayingItem.Id,
                sessionId: playing.Id,
                client: playing.Client,
                playMethod: playing.PlayState && playing.PlayState.PlayMethod,
                positionTicks: playing.PlayState && playing.PlayState.PositionTicks
            };
        }
        await sleep(1000);
    }
    const summary = sessions.map(session => ({
        client: session.Client,
        nowPlaying: session.NowPlayingItem ? session.NowPlayingItem.Id : null
    }));
    throw new Error(`no server session reported item ${itemId} as now playing; sessions: ${JSON.stringify(summary)}`);
}

async function completeWizard(page, config, receipt) {
    await page.goto(config.baseUrl, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.locator('#txtUsername').waitFor({ state: 'visible', timeout: 60000 });
    await page.locator('#txtUsername').fill(config.adminName);
    await page.locator('#txtManualPassword').fill(config.adminPassword);
    await page.locator('#txtPasswordConfirm').fill(config.adminPassword);
    await page.waitForFunction(
        () => !document.querySelector('#wizardStartPage .button-submit')?.disabled,
        null,
        { timeout: 60000 }
    );
    await page.locator('#wizardStartPage .button-submit').click();
    await page.locator('#wizardLibraryPage').waitFor({ state: 'visible', timeout: 60000 });

    await page.locator('#addLibrary').click();
    const creator = page.locator('.dlg-librarycreator');
    await creator.waitFor({ state: 'visible' });
    await creator.locator('#selectCollectionType').selectOption('movies');
    // Selecting a type fills the display name with the type's default; the card is found by this name.
    await creator.locator('#txtValue').fill(config.libraryName);
    await creator.locator('.btnAddFolder').click();
    const picker = page.locator('.directoryPicker');
    await picker.waitFor({ state: 'visible' });
    await picker.locator('#txtDirectoryPickerPath').fill(config.mediaDirectory);
    await picker.locator('form').evaluate(form => form.requestSubmit());
    await picker.waitFor({ state: 'hidden', timeout: 60000 });
    await creator.locator('.folderList').getByText(config.mediaDirectory, { exact: true }).waitFor({ state: 'visible' });
    await creator.locator('.addLibraryForm .btnSubmit').click();
    await creator.waitFor({ state: 'hidden', timeout: 60000 });

    const libraryCard = page.locator('#divVirtualFolders .card').filter({ hasText: config.libraryName }).first();
    await libraryCard.waitFor({ state: 'visible', timeout: 60000 });
    const libraryId = await libraryCard.getAttribute('data-id');
    assert.ok(libraryId, 'the wizard library card did not expose its server item ID');
    receipt.libraryId = libraryId;
    const wizardScreenshot = path.join(config.workDir, 'wizard-library.png');
    await page.locator('#wizardLibraryPage').screenshot({ path: wizardScreenshot });
    receipt.librarySetupScreenshot = wizardScreenshot;

    // This fork's wizard has two steps; library Next completes it and lands on login or home.
    await page.locator('#wizardLibraryPage .button-submit').click();
    // The login page stays in the DOM hidden once cached, so existence alone proves nothing.
    const landing = page.locator('#loginPage, #homeTab').filter({ visible: true }).first();
    await landing.waitFor({ state: 'visible', timeout: 60000 });
    const loginPage = page.locator('#loginPage');
    if (await loginPage.isVisible()) {
        const manual = loginPage.locator('.btnManual');
        if (await manual.isVisible().catch(() => false)) {
            await manual.click();
        }
        await loginPage.locator('#txtManualName').fill(config.adminName);
        await loginPage.locator('#txtManualPassword').fill(config.adminPassword);
        await loginPage.locator('.manualLoginForm .button-submit').click();
    }
    await page.locator('#homeTab').waitFor({ state: 'visible', timeout: 90000 });
    return libraryId;
}

async function playItem(page, config, receipt, item) {
    await page.goto(`${config.baseUrl}/web/index.html#!/details?id=${encodeURIComponent(item.Id)}`, {
        waitUntil: 'domcontentloaded', timeout: 60000
    });
    await page.locator('#itemDetailPage').waitFor({ state: 'visible', timeout: 60000 });
    const playButton = page.locator('#itemDetailPage .btnPlay:not(.hide), #itemDetailPage .btnReplay:not(.hide)').first();
    await playButton.waitFor({ state: 'visible', timeout: 60000 });
    await playButton.click();

    const video = page.locator('video').last();
    await video.waitFor({ state: 'visible', timeout: 60000 });
    const videoHandle = await video.elementHandle();
    await page.waitForFunction(element => !element.paused && element.currentTime >= 0.5
        && element.videoWidth > 0 && element.videoHeight > 0 && element.error === null,
    videoHandle, { timeout: 60000 });
    const firstPlayback = await video.evaluate(element => ({
        currentTime: element.currentTime,
        paused: element.paused,
        readyState: element.readyState,
        width: element.videoWidth,
        height: element.videoHeight,
        error: element.error ? { code: element.error.code, message: element.error.message } : null
    }));
    // Wait for a full second of decoded progress instead of sampling after a fixed sleep.
    await page.waitForFunction(({ element, first }) => element.error === null && !element.ended
        && !element.paused && element.currentTime >= first + 1.0,
    { element: videoHandle, first: firstPlayback.currentTime }, { timeout: 30000 });
    const laterPlayback = await video.evaluate(element => ({
        currentTime: element.currentTime, paused: element.paused, ended: element.ended,
        error: element.error ? { code: element.error.code, message: element.error.message } : null
    }));
    assert.equal(firstPlayback.paused, false);
    assert.equal(firstPlayback.error, null);
    assert.equal(laterPlayback.error, null);
    assert.ok(firstPlayback.width > 0 && firstPlayback.height > 0);
    receipt.playback = { ...firstPlayback, laterTime: laterPlayback.currentTime, advanced: true };
    receipt.finalUrl = page.url();
    await page.screenshot({ path: path.join(config.workDir, 'decoded-playback.png'), fullPage: false });
}

async function main() {
    const config = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
    const packageDirectory = path.dirname(require.resolve('@playwright/test'));
    const playwrightPackage = JSON.parse(fs.readFileSync(path.join(packageDirectory, 'package.json'), 'utf8'));
    assert.equal(playwrightPackage.version, '1.61.1', 'the installed browser driver must match the web-source pin');
    const receiptPath = path.join(config.workDir, 'browser-journey.json');
    const errors = [];
    let browser;
    let context;
    let traceStopped = false;
    const receipt = {
        schemaVersion: 2,
        browserChannel: 'chrome',
        playwrightVersion: playwrightPackage.version,
        baseUrl: config.baseUrl,
        libraryName: config.libraryName,
        mediaName: config.mediaName,
        mediaPath: config.mediaPath,
        status: 'running',
        pageErrors: errors
    };

    try {
        browser = await chromium.launch({ channel: 'chrome', headless: true, timeout: 30000 });
        receipt.browserVersion = browser.version();
        context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
        context.setDefaultTimeout(30000);
        await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
        const page = await context.newPage();
        page.on('pageerror', error => errors.push(String(error)));

        const libraryId = await completeWizard(page, config, receipt);

        const api = createApi(config.baseUrl);
        const session = await api.authenticate(config.adminName, config.adminPassword);
        receipt.adminUserId = session.User && session.User.Id;
        const item = await waitForLibraryItem(api, libraryId, config.mediaPath);
        receipt.itemId = item.Id;
        receipt.itemName = item.Name;

        await playItem(page, config, receipt, item);
        receipt.serverPlaybackReport = await waitForServerPlayback(api, item.Id);

        assert.deepEqual(errors, [], `browser page errors: ${errors.join('\n')}`);
        receipt.status = 'passed';
        await context.tracing.stop({ path: path.join(config.workDir, 'playwright-trace.zip') });
        traceStopped = true;
        await context.close();
    } catch (error) {
        receipt.status = 'failed';
        receipt.error = String(error && error.stack || error);
        try {
            if (browser) {
                const pages = browser.contexts().flatMap(openContext => openContext.pages());
                if (pages[0]) {
                    await pages[0].screenshot({ path: path.join(config.workDir, 'browser-failure.png'), fullPage: true });
                }
            }
        } catch (screenshotError) {
            receipt.screenshotError = String(screenshotError);
        }
    } finally {
        if (context && !traceStopped) {
            try {
                await context.tracing.stop({ path: path.join(config.workDir, 'playwright-failure-trace.zip') });
            } catch (traceError) {
                receipt.traceError = String(traceError);
            }
        }
        if (browser) {
            try {
                await browser.close();
            } catch (closeError) {
                // A browser that will not close is a failed journey, but the receipt still records why.
                receipt.closeError = String(closeError && closeError.stack || closeError);
                receipt.status = 'failed';
            }
        }
        fs.writeFileSync(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`);
        if (receipt.status !== 'passed') {
            process.stderr.write(`browser journey ${receipt.status}: ${receipt.error || receipt.closeError}\n`);
            process.exitCode = 1;
        }
    }
}

main().catch(error => {
    process.stderr.write(`${error && error.stack || error}\n`);
    process.exitCode = 1;
});
