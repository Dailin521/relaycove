import { expect, test } from '@playwright/test';
import { collectBrowserIssues } from './consoleGuard';

test('supports keyboard navigation, composer sizing, and dismissing details', async ({ page }) => {
    const browserIssues = collectBrowserIssues(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/?fixture=chat');

    const search = page.getByRole('searchbox', { name: '搜索会话' });
    await search.focus();
    await search.press('ArrowDown');
    await expect(page.getByRole('button', { name: /UI 设计讨论/ })).toBeFocused();

    const resizer = page.getByRole('separator', { name: /输入区高度 112 像素/ });
    await resizer.focus();
    await resizer.press('ArrowUp');
    await expect(page.getByRole('separator', { name: /输入区高度 128 像素/ })).toHaveAttribute('aria-valuenow', '128');
    await page.getByRole('separator', { name: /输入区高度 128 像素/ }).press('End');
    await expect(page.getByRole('separator', { name: /输入区高度 300 像素/ })).toHaveAttribute('aria-valuenow', '300');

    await page.getByRole('button', { name: '会话详情' }).click();
    const details = page.getByRole('complementary', { name: '会话详情' });
    await expect(details).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(details).toHaveCount(0);
    browserIssues.assertClean();
});

test('switches between the conversation list and chat in a narrow viewport', async ({ page }) => {
    const browserIssues = collectBrowserIssues(page);
    await page.setViewportSize({ width: 640, height: 760 });
    await page.goto('/?fixture=chat');

    await expect(page.getByRole('button', { name: '返回会话列表' })).toBeVisible();
    await page.getByRole('button', { name: '返回会话列表' }).click();
    await expect(page.getByRole('complementary', { name: '会话列表' })).toBeVisible();
    const listHasOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
    expect(listHasOverflow).toBe(false);
    await page.getByRole('button', { name: /Windows 客户端/ }).click();
    await expect(page.getByRole('heading', { name: 'Windows 客户端' })).toBeVisible();
    await page.getByRole('button', { name: /更多消息操作/ }).first().click();
    await expect(page.getByRole('menu', { name: /消息 .* 操作/ })).toBeVisible();
    await page.getByRole('heading', { name: 'Windows 客户端' }).click();
    await expect(page.getByRole('menu', { name: /消息 .* 操作/ })).toHaveCount(0);
    const hasOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
    expect(hasOverflow).toBe(false);
    browserIssues.assertClean();
});

test('supports complete message actions, protected avatars, and image preview in the fixture', async ({ page, context }) => {
    const browserIssues = collectBrowserIssues(page);
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: 'http://127.0.0.1:4173' });
    await page.setViewportSize({ width: 1024, height: 768 });
    await page.goto('/?fixture=chat');

    await expect(page.locator('.message-row .avatar img').first()).toBeVisible();
    const message = page.locator('[data-message-id="m1"]');
    await message.click({ button: 'right' });
    const menu = page.getByRole('menu', { name: '消息 m1 操作' });
    await expect(menu).toBeVisible();
    await expect(menu.getByRole('menuitem', { name: '引用回复' })).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(menu.getByRole('menuitem', { name: '复制消息正文' })).toBeFocused();
    await page.screenshot({ path: '../../artifacts/web/screenshots/message-actions-1024-light.png' });
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(message).toBeFocused();

    await message.press('Shift+F10');
    await menu.getByRole('menuitem', { name: '复制消息正文' }).click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain('顶部按微信逻辑收敛');
    await message.press('Shift+F10');
    await menu.getByRole('menuitem', { name: '引用回复' }).click();
    const composer = page.getByRole('textbox', { name: '消息正文' });
    await expect(composer).toHaveValue(/^\*\*Maya Chen\*\* \[said\]\(#\):\r?\n```quote\r?\n/u);
    await expect(composer).toBeFocused();

    const imageButton = page.getByRole('button', { name: '打开图片 relaycove-team-avatars.png' });
    await expect(imageButton).toBeVisible();
    await imageButton.click();
    const viewer = page.getByRole('dialog', { name: '图片预览：relaycove-team-avatars.png' });
    await expect(viewer).toBeVisible();
    await expect(viewer.getByRole('link', { name: '下载图片 relaycove-team-avatars.png' })).toHaveAttribute('href', /^blob:/u);
    await page.screenshot({ path: '../../artifacts/web/screenshots/image-preview-1024-light.png' });
    await page.getByRole('button', { name: '关闭图片预览' }).click();
    await expect(viewer).toHaveCount(0);
    await expect(imageButton).toBeFocused();
    await imageButton.click();
    await page.locator('.image-viewer-backdrop').click({ position: { x: 5, y: 5 } });
    await expect(viewer).toHaveCount(0);
    await expect(imageButton).toBeFocused();

    const hasOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
    expect(hasOverflow).toBe(false);
    browserIssues.assertClean();
});
