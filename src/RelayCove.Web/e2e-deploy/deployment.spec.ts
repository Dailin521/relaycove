import { expect, test } from '@playwright/test';
import { collectBrowserIssues } from '../e2e/consoleGuard';

test('serves the production application from the fixed subpath', async ({ page, request }) => {
    const browserIssues = collectBrowserIssues(page);
    const response = await page.goto('./');

    expect(response?.status()).toBe(200);
    expect(response?.headers()['cache-control']).toBe('no-cache, must-revalidate');
    expect(response?.headers()['content-security-policy']).toContain("frame-ancestors 'none'");
    expect(response?.headers()['x-content-type-options']).toBe('nosniff');
    await expect(page).toHaveURL('http://127.0.0.1:4174/relaycove-web/');
    await page.waitForLoadState('networkidle');
    browserIssues.assertClean();
    await expect(page.getByRole('heading', { name: '连接你的 Zulip Realm' })).toBeVisible();

    const moduleSource = await page.locator('script[type="module"]').getAttribute('src');
    expect(moduleSource).toMatch(/^\/relaycove-web\/assets\/index-[\w-]+\.js$/);
    const faviconSource = await page.locator('link[rel="icon"]').getAttribute('href');
    expect(faviconSource).toBe('/relaycove-web/relaycove.svg');

    const missingAsset = await request.get('http://127.0.0.1:4174/relaycove-web/assets/not-a-real-build-asset.js');
    expect(missingAsset.status()).toBe(404);

    const moduleResponse = await request.get(`http://127.0.0.1:4174${moduleSource}`);
    expect(moduleResponse.status()).toBe(200);
    expect(moduleResponse.headers()['cache-control']).toBe('public, max-age=31536000, immutable');

    const faviconResponse = await request.get(`http://127.0.0.1:4174${faviconSource}`);
    expect(faviconResponse.status()).toBe(200);
    expect(faviconResponse.headers()['content-type']).toContain('image/svg+xml');

    const clientRoute = await request.get('http://127.0.0.1:4174/relaycove-web/settings');
    expect(clientRoute.status()).toBe(200);
    expect(clientRoute.headers()['content-type']).toContain('text/html');

    const outsidePrefix = await request.get('http://127.0.0.1:4174/');
    expect(outsidePrefix.status()).toBe(404);
    browserIssues.assertClean();
});
