import { describe, expect, it, vi } from 'vitest';
import { ZulipWebError } from './errors';
import { ZulipApiClient } from './ZulipApiClient';

describe('ZulipApiClient', () => {
    it('invokes the browser fetch function without an illegal receiver', async () => {
        const browserFetch = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
            zulip_version: '12.1',
            zulip_feature_level: 500,
            is_incompatible: false,
            email_auth_enabled: true,
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
        vi.stubGlobal('fetch', browserFetch);
        const client = new ZulipApiClient();

        await client.getServerSettings('https://chat.example.test');

        expect(browserFetch).toHaveBeenCalledOnce();
        vi.unstubAllGlobals();
    });

    it('probes server settings without credentials', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
            zulip_version: '12.1',
            zulip_feature_level: 500,
            is_incompatible: false,
            email_auth_enabled: true,
            future_field: 'ignored',
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
        const client = new ZulipApiClient(transport);

        const result = await client.getServerSettings('https://chat.example.test');

        expect(result).toEqual({
            zulipVersion: '12.1',
            zulipFeatureLevel: 500,
            isIncompatible: false,
            emailAuthenticationEnabled: true,
        });
        expect(transport).toHaveBeenCalledOnce();
        const [url, init] = transport.mock.calls[0];
        expect(url).toBe('https://chat.example.test/api/v1/server_settings');
        expect(init?.method).toBe('GET');
        expect(new Headers(init?.headers).has('Authorization')).toBe(false);
        expect(init?.redirect).toBe('error');
    });

    it('posts the password only in the fetch_api_key form body', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
            api_key: 'api-key-secret',
            email: 'ada@example.test',
            user_id: 7,
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
        const client = new ZulipApiClient(transport);

        const credential = await client.fetchApiKey(
            'https://chat.example.test',
            'ada@example.test',
            'password-secret',
        );

        expect(credential.apiKey).toBe('api-key-secret');
        const [url, init] = transport.mock.calls[0];
        expect(url).toBe('https://chat.example.test/api/v1/fetch_api_key');
        expect(String(url)).not.toContain('password-secret');
        expect(String(init?.body)).toContain('password=password-secret');
        expect(new Headers(init?.headers).has('Authorization')).toBe(false);
        expect(init?.cache).toBe('no-store');
        expect(init?.credentials).toBe('omit');
        expect(init?.redirect).toBe('error');
        expect(init?.referrerPolicy).toBe('no-referrer');
    });

    it('creates Basic authentication without putting the API key in the URL', () => {
        const client = new ZulipApiClient();

        const request = client.createAuthenticatedRequest({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            apiKey: 'api-key-secret',
        }, 'users/me');

        expect(request.url).toBe('https://chat.example.test/api/v1/users/me');
        expect(request.url).not.toContain('api-key-secret');
        expect(request.headers.get('Authorization'))
            .toBe(`Basic ${btoa('ada@example.test:api-key-secret')}`);
        expect(request.redirect).toBe('error');
    });

    it('does not let callers weaken authenticated request transport defaults', () => {
        const client = new ZulipApiClient();

        const request = client.createAuthenticatedRequest({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            apiKey: 'api-key-secret',
        }, 'users/me', {
            cache: 'force-cache',
            credentials: 'include',
            redirect: 'follow',
            referrerPolicy: 'unsafe-url',
        });

        expect(request.cache).toBe('no-store');
        expect(request.credentials).toBe('omit');
        expect(request.redirect).toBe('error');
        expect(request.referrerPolicy).toBe('no-referrer');
    });

    it('never copies a server response body into an authentication error', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(
            JSON.stringify({ msg: 'password-secret api-key-secret' }),
            { status: 401, headers: { 'Content-Type': 'application/json' } },
        ));
        const client = new ZulipApiClient(transport);

        let thrown: unknown;
        try {
            await client.fetchApiKey(
                'https://chat.example.test',
                'ada@example.test',
                'password-secret',
            );
        } catch (error) {
            thrown = error;
        }

        expect(thrown).toBeInstanceOf(ZulipWebError);
        expect(String(thrown)).not.toContain('password-secret');
        expect(String(thrown)).not.toContain('api-key-secret');
    });
});
