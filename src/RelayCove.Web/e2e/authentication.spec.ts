import { expect, test, type Page } from '@playwright/test';
import { collectBrowserIssues } from './consoleGuard';

interface CapturedRequest {
    url: string;
    method: string;
    body: string | null;
    authorization?: string;
}

async function installFakeRealm(page: Page) {
    const requests: CapturedRequest[] = [];
    let eventId = 41;
    let sentCount = 0;
    let uploadCount = 0;
    const sentBodies: string[] = [];
    const imageBytes = Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAKAAAABkCAYAAAABtjuPAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAQUSURBVHhe7dsxTuNQGMRxLrRn2ItwAO5BibR7CKCi2oqShgIhLoBEhQQlDQfwyoRAmHzP9mSCRJz/J/265xdpNHq2Ezj49bfrsLnf//4gcKCBwqOBwkMBQxooPBQwpIHCQwFDGig8FDCkgcJDAUMaKDwUMKSBwkMBQxooPBQwpIHCQwFDGig8FDCkgcJDAUMaKDwUMKSBwkMBQxooPBQwpIHCQwFDGig8FDCkgcJDAUMaKDwUMKSBwkMBQxooPBQwpIHCQwFDGig8FDCkgcJDAUMaKDwUMKSBwkMBQxooPBQwpIHCQwFDGig8FDCkgcJDAUMaKDx7U8Drbgvz0nWHsq8GOt1pd/GqH7Ayr7fd0do180MBN5zj93010DFH9y+61ejc3K3vMxcUMJjzM6eAl92NbuDMTE9EChjOSRHqmqvb7kkv3GRmWEIKGM7T/elaqF9sq3zLeb5c/4wdttcFfLxdX1e61CtXZvBUGnnR6F66iyu9Zvw5cU7PhBSwWFs667pH3eBt6hL1hoo0enIOlXew9LuFAhZrW87LPrUK2H7pGC/f2B6tz9w9FLBY23L8oDv00yjDXbnYPr1ap+j0Ev9sFLBY2+KcgCfPum4xfnFWTkGzvLuAAhZrS80XkYfiq5jW81td1n1GAYu16vBWr1yZ8muR1rNbVdb9ttcF3MaUJ1rru78Z3kJTFDCY68vGT3EUcDIKuOH05ev31UDfUMDJKKA5+tyogb6hgJNRwInT/+WL7tnTQN9QwMn2uoB6mvXq7/oWU63XQBd4C56KAhZr61883ufh61oNdGGb3wO+71V+3bP7KGCxtjf1JNRAl7b2S0hxO7f3+MEoYLF26JrlLJ8JNdAP3/xb8FxORApYrP3Q/Pnt8x+UNNBPredA4wQrTr/lzOVvAilgsXbV0PNgf70Guqp5ek0qYbvAc3qZoYDFWlVdu5zhl4qhEvVTv5S0nh+XM17e3UEBi7Vrhm7FY890A7fRjWbs83YMBSzWVoZuxaPPY1sr4XxuvUsUsFjbUu2xmCnFGLsdj82Uz9g9FLBY2zR0K574tcjQi0lr5vTMpyhgsXZIdCv+YuxEnOeJp/amgN9FA4WHAoY0UHgoYEgDhYcChjRQeChgSAOFhwKGNFB4KGBIA4WHAoY0UHgoYEgDhYcChjRQeChgSAOFhwKGNFB4KGBIA4WHAoY0UHgoYEgDhYcChjRQeChgSAOFhwKGNFB4KGBIA4WHAoY0UHgoYEgDhYcChjRQeChgSAOFhwKGNFB4KGBIA4WHAoY0UHgoYEgDhYcChjRQeP4DqQ4k3gSxbR4AAAAASUVORK5CYII=',
        'base64',
    );
    await page.route(/https:\/\/chat\.example\.test\/(?:avatar|user_avatars|user_uploads)\/.*/u, async (route) => {
        const request = route.request();
        requests.push({
            url: request.url(),
            method: request.method(),
            body: request.postData(),
            authorization: request.headers().authorization,
        });
        if (request.method() === 'OPTIONS') {
            await route.fulfill({
                status: 204,
                headers: {
                    'Access-Control-Allow-Origin': 'http://127.0.0.1:4173',
                    'Access-Control-Allow-Headers': 'Authorization',
                    'Access-Control-Allow-Methods': 'GET, OPTIONS',
                },
            });
            return;
        }
        await route.fulfill({
            status: 200,
            contentType: 'image/png',
            headers: {
                'Access-Control-Allow-Origin': 'http://127.0.0.1:4173',
                'Content-Length': String(imageBytes.byteLength),
            },
            body: imageBytes,
        });
    });
    await page.route('https://chat.example.test/api/v1/**', async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        requests.push({
            url: request.url(),
            method: request.method(),
            body: request.postData(),
            authorization: request.headers().authorization,
        });
        const path = url.pathname.replace('/api/v1/', '');
        const fulfill = (value: unknown) => route.fulfill({
            status: 200,
            contentType: 'application/json',
            headers: { 'Access-Control-Allow-Origin': 'http://127.0.0.1:4173' },
            body: JSON.stringify(value),
        });
        if (path === 'server_settings') {
            await fulfill({
                zulip_version: '12.1',
                zulip_feature_level: 500,
                is_incompatible: false,
                email_auth_enabled: true,
            });
            return;
        }
        if (path === 'fetch_api_key') {
            await fulfill({
                api_key: 'fake-api-key-for-browser-test',
                email: 'ada@example.test',
                user_id: 7,
            });
            return;
        }
        if (path === 'users/me') {
            await fulfill({
                user_id: 7,
                full_name: 'Ada Lovelace',
                email: 'user7@internal.example.test',
                is_active: true,
                avatar_url: '/user_avatars/7/avatar.png',
            });
            return;
        }
        if (path === 'register') {
            await fulfill({
                queue_id: `fake-queue-${requests.length}`,
                last_event_id: eventId,
                event_queue_longpoll_timeout_seconds: 60,
                max_message_length: 10_000,
                max_topic_length: 60,
                max_file_upload_size_mib: 25,
                subscriptions: [{ stream_id: 11, name: 'engineering', is_archived: false }],
                realm_users: [
                    { user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test', is_active: true, avatar_url: '/user_avatars/7/avatar.png' },
                    { user_id: 9, full_name: 'Grace Hopper', email: 'grace@example.test', is_active: true, avatar_url: '/user_avatars/9/avatar.png' },
                    { user_id: 10, full_name: 'Alan Turing', email: 'alan@example.test', is_active: true, is_bot: true },
                ],
                recent_private_conversations: [{ user_ids: [7, 9] }],
                unread_msgs: {
                    count: 2,
                    old_unreads_missing: false,
                    streams: [{ stream_id: 11, topic: 'Web client', unread_message_ids: [201] }],
                    pms: [{ other_user_id: 9, unread_message_ids: [101] }],
                    huddles: [],
                },
            });
            return;
        }
        if (path === 'users/me/11/topics') {
            await fulfill({ topics: [{ name: 'Web client', max_id: 201 }] });
            return;
        }
        if (path === 'user_uploads' && request.method() === 'POST') {
            uploadCount += 1;
            await fulfill({
                result: 'success',
                msg: '',
                url: '/user_uploads/1/a/upload.png',
                filename: 'upload.png',
            });
            return;
        }
        if (path.startsWith('user_uploads/') && request.method() === 'GET') {
            await fulfill({
                result: 'success',
                msg: '',
                url: '/user_uploads/temporary/fake-dashboard-token',
            });
            return;
        }
        if (path === 'events' && request.method() === 'GET') {
            await new Promise((resolve) => setTimeout(resolve, 750));
            eventId += 1;
            await fulfill({ events: [{ id: eventId, type: 'heartbeat' }] });
            return;
        }
        if (path === 'events' && request.method() === 'DELETE') {
            await fulfill({ result: 'success', msg: '' });
            return;
        }
        if (path === 'messages/flags/narrow') {
            await fulfill({ result: 'success', msg: '' });
            return;
        }
        if (path === 'messages' && request.method() === 'POST') {
            sentCount += 1;
            sentBodies.push(request.postData() ?? '');
            await fulfill({ result: 'success', msg: '', id: 501 });
            return;
        }
        if (path === 'messages' && request.method() === 'GET') {
            const narrow = JSON.parse(url.searchParams.get('narrow') ?? '[]') as Array<{ operator: string; operand?: unknown }>;
            const isChannel = narrow.some((term) => term.operator === 'channel');
            const dmIds = (narrow.find((term) => term.operator === 'dm')?.operand as number[] | undefined) ?? [9];
            const recipientNames: Record<number, string> = { 7: 'Ada Lovelace', 9: 'Grace Hopper', 10: 'Alan Turing' };
            const recipients = [...new Set([7, ...dmIds])].map((id) => ({ id, full_name: recipientNames[id] ?? `User ${id}` }));
            const anchor = url.searchParams.get('anchor');
            if (anchor === '501') {
                await fulfill({
                    found_oldest: false,
                    found_newest: true,
                    messages: [{
                        id: 501,
                        type: isChannel ? 'stream' : 'private',
                        stream_id: isChannel ? 11 : undefined,
                        subject: isChannel ? 'Web client' : undefined,
                        display_recipient: isChannel ? 'engineering' : recipients,
                        sender_id: 7,
                        sender_full_name: 'Ada Lovelace',
                        content: 'formal send from fake HTTP',
                        timestamp: 1_786_500_120,
                        flags: ['read'],
                    }],
                });
                return;
            }
            await fulfill({
                found_oldest: true,
                found_newest: true,
                messages: isChannel ? [{
                    id: 201,
                    type: 'stream',
                    stream_id: 11,
                    subject: 'Web client',
                    sender_id: 9,
                    sender_full_name: 'Grace Hopper',
                    avatar_url: '/user_avatars/9/avatar.png',
                    content: '频道历史来自 fake Zulip HTTP\n![dashboard.png](/user_uploads/a/dashboard.png)',
                    timestamp: 1_786_500_060,
                    flags: [],
                }] : [{
                    id: 101,
                    type: 'private',
                    display_recipient: recipients,
                    sender_id: 9,
                    sender_full_name: 'Grace Hopper',
                    avatar_url: '/user_avatars/9/avatar.png',
                    content: '私信历史来自 fake Zulip HTTP',
                    timestamp: 1_786_500_000,
                    flags: [],
                }],
            });
            return;
        }
        await route.fulfill({ status: 404, body: '' });
    });
    return {
        requests,
        imageBytes,
        get sentCount() { return sentCount; },
        get uploadCount() { return uploadCount; },
        sentBodies,
    };
}

test('runs the formal login, media, message actions, history, send, restore, and logout journey with fake HTTP', async ({ page, context }) => {
    const browserIssues = collectBrowserIssues(page);
    const fakeRealm = await installFakeRealm(page);
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: 'http://127.0.0.1:4173' });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: '连接你的 Zulip Realm' })).toBeVisible();
    await page.getByRole('textbox', { name: 'Realm', exact: true }).fill('https://chat.example.test');
    await page.getByRole('textbox', { name: '邮箱', exact: true }).fill('ada@example.test');
    await page.getByLabel('密码', { exact: true }).fill('fake-password-for-browser-test');
    await page.getByRole('button', { name: '登录' }).click();

    const accountTrigger = page.getByRole('button', { name: '当前用户：Ada Lovelace' });
    await accountTrigger.click();
    const accountMenu = page.getByRole('menu', { name: '账户菜单' });
    await expect(accountMenu).toBeVisible();
    const openSettings = accountMenu.getByRole('menuitem', { name: '打开设置' });
    const accountAndLogout = accountMenu.getByRole('menuitem', { name: '账户与注销' });
    await expect(openSettings).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(accountAndLogout).toBeFocused();
    await page.keyboard.press('Home');
    await expect(openSettings).toBeFocused();
    await page.keyboard.press('End');
    await expect(accountAndLogout).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('menu', { name: '账户菜单' })).toHaveCount(0);
    await expect(accountTrigger).toBeFocused();

    await expect(page.getByRole('button', { name: /^Grace Hopper/u })).toBeVisible();
    await expect(page.getByText('私信历史来自 fake Zulip HTTP', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: /Web client/ })).toBeVisible();
    await page.getByRole('button', { name: /Web client/ }).click();
    await expect(page.getByText('频道历史来自 fake Zulip HTTP', { exact: true })).toBeVisible();
    await expect(page.locator('.message-row .avatar img')).toBeVisible();

    const imageButton = page.getByRole('button', { name: '打开图片 dashboard.png' });
    await expect(imageButton).toBeVisible();
    await imageButton.click();
    const imageViewer = page.getByRole('dialog', { name: '图片预览：dashboard.png' });
    await expect(imageViewer).toBeVisible();
    await expect(imageViewer.getByRole('link', { name: '下载图片 dashboard.png' })).toHaveAttribute('href', /^blob:/u);
    await page.keyboard.press('Escape');
    await expect(imageViewer).toHaveCount(0);
    await expect(imageButton).toBeFocused();

    const channelMessage = page.locator('[data-message-id="201"]');
    await channelMessage.click({ button: 'right' });
    const messageMenu = page.getByRole('menu', { name: '消息 201 操作' });
    await expect(messageMenu).toBeVisible();
    await messageMenu.getByRole('menuitem', { name: '复制消息正文' }).click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain('频道历史来自 fake Zulip HTTP');
    await channelMessage.focus();
    await channelMessage.press('Shift+F10');
    const openInZulip = messageMenu.getByRole('menuitem', { name: '在 Zulip 中打开' });
    await expect(openInZulip).toHaveAttribute('href', 'https://chat.example.test/#narrow/near/201');
    await expect(openInZulip).toHaveAttribute('rel', 'noopener noreferrer');
    await messageMenu.getByRole('menuitem', { name: '复制消息链接' }).click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe('https://chat.example.test/#narrow/near/201');
    await channelMessage.focus();
    await channelMessage.press('Shift+F10');
    await messageMenu.getByRole('menuitem', { name: '回复到输入框' }).click();
    await expect(page.getByRole('textbox', { name: '消息正文' })).toHaveValue(/> Grace Hopper：/u);
    await expect(page.getByRole('textbox', { name: '消息正文' })).toBeFocused();
    await page.getByRole('textbox', { name: '消息正文' }).fill('');

    const fileInput = page.locator('.composer-file-input');
    await fileInput.setInputFiles({ name: 'unsafe.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg/>') });
    await expect(page.getByText('请选择 PNG、JPEG、WebP、GIF 或 AVIF 图片。')).toBeVisible();
    const uploadFile = {
        name: 'upload.png',
        mimeType: 'image/png',
        buffer: fakeRealm.imageBytes,
    };
    const imageCaption = page.getByRole('textbox', { name: '消息正文' });
    await imageCaption.fill('图片说明保留');
    await fileInput.setInputFiles(uploadFile);
    await expect(page.getByAltText('待发送图片预览')).toBeVisible();
    await expect(page.getByText('upload.png', { exact: true })).toBeVisible();
    await page.screenshot({ path: '../../artifacts/web/screenshots/composer-image-1440-light.png' });
    await page.getByRole('button', { name: '移除待发送图片' }).click();
    await expect(page.getByAltText('待发送图片预览')).toHaveCount(0);
    await expect(imageCaption).toHaveValue('图片说明保留');
    await fileInput.setInputFiles(uploadFile);
    await page.getByRole('button', { name: '发送消息' }).click();
    await expect(page.getByAltText('待发送图片预览')).toHaveCount(0);
    expect(fakeRealm.uploadCount).toBe(1);
    expect(fakeRealm.sentCount).toBe(1);
    const uploadedMessage = new URLSearchParams(fakeRealm.sentBodies[0]).get('content');
    expect(uploadedMessage).toBe('图片说明保留\n[upload.png](https://chat.example.test/user_uploads/1/a/upload.png)');

    await page.getByRole('button', { name: '新建会话' }).click();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: '新建会话' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: '新建会话' })).toBeFocused();
    await page.getByRole('button', { name: '新建会话' }).click();
    await page.getByLabel('新建会话').getByText('Grace Hopper', { exact: true }).click();
    await page.getByLabel('新建会话').getByText('Alan Turing', { exact: true }).click();
    await page.getByRole('button', { name: '打开私信' }).click();
    await expect(page.getByRole('heading', { name: 'Grace Hopper、Alan Turing' })).toBeVisible();
    await expect(page.getByText('私信历史来自 fake Zulip HTTP', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: /Web client/ }).click();

    const composer = page.getByRole('textbox', { name: '消息正文' });
    await composer.fill('formal send from fake HTTP');
    await composer.press('Control+Enter');
    await expect(page.getByText('formal send from fake HTTP', { exact: true }).last()).toBeVisible();
    expect(fakeRealm.sentCount).toBe(2);
    await expect(composer).toHaveValue('');
    await page.screenshot({ path: '../../artifacts/web/screenshots/formal-client-fake-1440-light.png' });

    const loginRequests = fakeRealm.requests.filter((request) => (
        request.url.endsWith('/server_settings') || request.url.endsWith('/fetch_api_key')
    ));
    expect(loginRequests[0]).toMatchObject({
        url: 'https://chat.example.test/api/v1/server_settings',
        method: 'GET',
    });
    expect(loginRequests[0].authorization).toBeUndefined();
    expect(loginRequests[1].url).toBe('https://chat.example.test/api/v1/fetch_api_key');
    expect(loginRequests[1].url).not.toContain('fake-password-for-browser-test');
    expect(loginRequests[1].authorization).toBeUndefined();
    expect(loginRequests[1].body).toContain('password=fake-password-for-browser-test');

    const authenticated = fakeRealm.requests.filter((request) => (
        request.url.includes('/users/me') || request.url.includes('/register')
    ));
    expect(authenticated.some((request) => request.authorization?.startsWith('Basic '))).toBe(true);
    expect(authenticated.map((request) => Buffer.from(
        request.authorization!.slice('Basic '.length),
        'base64',
    ).toString('utf8').split(':', 1)[0])).toEqual(
        authenticated.map(() => 'ada@example.test'),
    );
    expect(fakeRealm.requests.every((request) => !request.url.includes('fake-api-key-for-browser-test'))).toBe(true);
    const mediaRequests = fakeRealm.requests.filter((request) => (
        request.method === 'GET' && /\/(?:avatar|user_avatars|user_uploads)\//u.test(request.url)
    ));
    expect(mediaRequests.length).toBeGreaterThan(0);
    const temporaryUrlRequests = mediaRequests.filter((request) => request.url.includes('/api/v1/user_uploads/'));
    expect(temporaryUrlRequests.length).toBeGreaterThan(0);
    expect(temporaryUrlRequests.every((request) => request.authorization?.startsWith('Basic '))).toBe(true);
    expect(mediaRequests.filter((request) => /\/(?:avatar|user_avatars)\//u.test(request.url)).every((request) => request.authorization?.startsWith('Basic '))).toBe(true);
    expect(mediaRequests.filter((request) => request.url.includes('/user_uploads/temporary/')).every((request) => request.authorization === undefined)).toBe(true);

    const serializedCredential = await page.evaluate(() => localStorage.getItem('relaycove.web.session.v1'));
    expect(serializedCredential).toContain('fake-api-key-for-browser-test');
    expect(serializedCredential).toContain('Ada Lovelace');
    expect(serializedCredential).toContain('ada@example.test');
    expect(serializedCredential).not.toContain('user7@internal.example.test');
    expect(serializedCredential).not.toContain('fake-password-for-browser-test');

    await page.reload();
    await expect(page.getByRole('button', { name: /^Grace Hopper/u })).toBeVisible();
    await page.getByRole('button', { name: '设置' }).click();
    await page.getByRole('button', { name: '账户' }).click();
    await page.getByRole('button', { name: '注销并清除本地凭据' }).click();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: '确认注销？' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: '注销并清除本地凭据' })).toBeFocused();
    await page.getByRole('button', { name: '注销并清除本地凭据' }).click();
    await page.getByRole('button', { name: '确认注销' }).click();
    await expect(page.getByRole('heading', { name: '连接你的 Zulip Realm' })).toBeVisible();
    expect(await page.evaluate(() => ({
        localCredential: localStorage.getItem('relaycove.web.session.v1'),
        sessionCredential: sessionStorage.getItem('relaycove.web.session.v1'),
        localValues: Object.values(localStorage),
        sessionValues: Object.values(sessionStorage),
    }))).toEqual({
        localCredential: null,
        sessionCredential: null,
        localValues: [expect.not.stringContaining('fake-api-key-for-browser-test')],
        sessionValues: [],
    });
    expect(fakeRealm.requests.some((request) => (
        new URL(request.url).pathname.endsWith('/events') && request.method === 'DELETE'
    ))).toBe(true);
    browserIssues.assertClean();
});
