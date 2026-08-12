import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './e2e',
    outputDir: '../../artifacts/web/playwright/test-results',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    timeout: 30_000,
    expect: {
        timeout: 5_000,
    },
    reporter: [
        ['line'],
        ['html', { outputFolder: '../../artifacts/web/playwright/report', open: 'never' }],
    ],
    use: {
        baseURL: 'http://127.0.0.1:4173',
        browserName: 'chromium',
        headless: true,
        locale: 'zh-CN',
        timezoneId: 'Asia/Shanghai',
        serviceWorkers: 'block',
        trace: 'off',
        video: 'off',
        screenshot: 'only-on-failure',
    },
    webServer: {
        command: 'npm run preview:e2e',
        url: 'http://127.0.0.1:4173',
        reuseExistingServer: false,
        timeout: 30_000,
        stdout: 'pipe',
        stderr: 'pipe',
    },
});
