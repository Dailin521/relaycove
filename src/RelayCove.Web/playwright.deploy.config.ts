import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './e2e-deploy',
    outputDir: '../../artifacts/web/playwright/deploy-test-results',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    timeout: 30_000,
    expect: {
        timeout: 5_000,
    },
    reporter: [['line']],
    use: {
        baseURL: 'http://127.0.0.1:4174/relaycove-web/',
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
        command: 'npm run preview:deploy-test',
        url: 'http://127.0.0.1:4174/relaycove-web/',
        reuseExistingServer: false,
        timeout: 30_000,
        stdout: 'pipe',
        stderr: 'pipe',
    },
});
