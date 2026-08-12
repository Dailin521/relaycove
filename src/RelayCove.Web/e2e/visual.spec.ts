import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { collectBrowserIssues } from './consoleGuard';

const screenshotDirectory = resolve('../../artifacts/web/screenshots');

test.beforeAll(() => {
    mkdirSync(screenshotDirectory, { recursive: true });
});

async function expectNoHorizontalOverflow(page: import('@playwright/test').Page) {
    const hasOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
    expect(hasOverflow).toBe(false);
}

test('captures the 1440x900 light and dark shells with no console errors', async ({ page }) => {
    const browserIssues = collectBrowserIssues(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/?fixture=chat');

    await expect(page.getByRole('heading', { name: 'UI 设计讨论' })).toBeVisible();
    await expect(page.getByText('本地演示数据 · 不连接 Zulip')).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.screenshot({
        path: resolve(screenshotDirectory, 'desktop-1440-light.png'),
        animations: 'disabled',
    });

    await page.getByRole('button', { name: '切换到深色主题' }).click();
    await page.getByRole('button', { name: '会话详情' }).click();
    await expect(page.getByRole('complementary', { name: '会话详情' })).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.screenshot({
        path: resolve(screenshotDirectory, 'desktop-1440-dark.png'),
        animations: 'disabled',
    });
    browserIssues.assertClean();
});

test('captures the 1024x768 light shell without a horizontal scrollbar', async ({ page }) => {
    const browserIssues = collectBrowserIssues(page);
    await page.setViewportSize({ width: 1024, height: 768 });
    await page.goto('/?fixture=chat');

    await expect(page.getByRole('heading', { name: 'UI 设计讨论' })).toBeVisible();
    await expect(page.getByRole('complementary', { name: '会话详情' })).toHaveCount(0);
    await expectNoHorizontalOverflow(page);
    await page.screenshot({
        path: resolve(screenshotDirectory, 'desktop-1024-light.png'),
        animations: 'disabled',
    });
    browserIssues.assertClean();
});
