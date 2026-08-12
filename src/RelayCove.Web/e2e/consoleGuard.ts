import type { Page } from '@playwright/test';

export interface BrowserIssueCollector {
    assertClean(): void;
}

export function collectBrowserIssues(page: Page): BrowserIssueCollector {
    const issues: string[] = [];
    page.on('console', (message) => {
        if (message.type() === 'error' || message.type() === 'warning') {
            issues.push(`console.${message.type()}: ${message.text()}`);
        }
    });
    page.on('pageerror', (error) => {
        issues.push(`pageerror: ${error.message}`);
    });
    page.on('requestfailed', (request) => {
        const failure = request.failure()?.errorText ?? '';
        const intentionalLongPollCancellation = request.method() === 'GET'
            && new URL(request.url()).pathname.endsWith('/api/v1/events')
            && failure.includes('ERR_ABORTED');
        if (intentionalLongPollCancellation) {
            return;
        }
        issues.push(`requestfailed: ${request.method()} ${request.url()} ${failure}`);
    });

    return {
        assertClean() {
            if (issues.length > 0) {
                throw new Error(`Browser issues detected:\n${issues.join('\n')}`);
            }
        },
    };
}
