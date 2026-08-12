import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ZulipWebError } from '../api/errors';
import { ZulipApiClient } from '../api/ZulipApiClient';
import { WebAuthService } from './WebAuthService';
import { WebCredentialStore } from './WebCredentialStore';

describe('WebAuthService', () => {
    beforeEach(() => {
        localStorage.clear();
        sessionStorage.clear();
    });

    it('probes before fetching a key and never persists the password', async () => {
        const urls: string[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            const url = input instanceof Request ? input.url : String(input);
            urls.push(url);
            if (url.endsWith('/server_settings')) {
                return new Response(JSON.stringify({
                    zulip_version: '12.1',
                    zulip_feature_level: 500,
                    is_incompatible: false,
                    email_auth_enabled: true,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            if (url.endsWith('/fetch_api_key')) {
                return new Response(JSON.stringify({
                    api_key: 'api-key-secret',
                    email: 'ada@example.test',
                    user_id: 7,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            return new Response(JSON.stringify({
                user_id: 7,
                full_name: 'Ada Lovelace',
                email: 'ada@example.test',
                is_active: true,
            }), { status: 200, headers: { 'Content-Type': 'application/json' } });
        });
        const service = new WebAuthService(
            new ZulipApiClient(transport),
            new WebCredentialStore(),
        );

        const session = await service.login({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            password: 'password-secret',
            remember: true,
        });

        expect(urls).toEqual([
            'https://chat.example.test/api/v1/server_settings',
            'https://chat.example.test/api/v1/fetch_api_key',
            'https://chat.example.test/api/v1/users/me',
        ]);
        expect(session.apiKey).toBe('api-key-secret');
        expect(localStorage.key(0)).not.toBeNull();
        expect(localStorage.getItem(localStorage.key(0)!)).not.toContain('password-secret');
    });

    it('rejects an incompatible Realm before sending credentials', async () => {
        const transport = vi.fn(async () => new Response(JSON.stringify({
            zulip_version: '11.0',
            zulip_feature_level: 499,
            is_incompatible: false,
            email_auth_enabled: true,
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
        const service = new WebAuthService(new ZulipApiClient(transport));

        await expect(service.login({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            password: 'password-secret',
            remember: true,
        })).rejects.toMatchObject({ code: 'realm_incompatible' } satisfies Partial<ZulipWebError>);
        expect(transport).toHaveBeenCalledOnce();
    });

    it('does not persist credentials when users/me disagrees with fetch_api_key identity', async () => {
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            const url = input instanceof Request ? input.url : String(input);
            if (url.endsWith('/server_settings')) {
                return new Response(JSON.stringify({
                    zulip_version: '12.1',
                    zulip_feature_level: 500,
                    email_auth_enabled: true,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            if (url.endsWith('/fetch_api_key')) {
                return new Response(JSON.stringify({
                    api_key: 'api-key-secret',
                    email: 'ada@example.test',
                    user_id: 7,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            return new Response(JSON.stringify({
                user_id: 8,
                full_name: 'Different User',
                email: 'different@example.test',
            }), { status: 200, headers: { 'Content-Type': 'application/json' } });
        });
        const service = new WebAuthService(new ZulipApiClient(transport), new WebCredentialStore());

        await expect(service.login({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            password: 'password-secret',
            remember: true,
        })).rejects.toMatchObject({ code: 'invalid_response' });

        expect(localStorage.length).toBe(0);
        expect(sessionStorage.length).toBe(0);
    });

    it('keeps the fetch_api_key authentication email when users/me exposes a different address', async () => {
        const authorizationEmails: string[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            const request = input instanceof Request ? input : new Request(input);
            const url = request.url;
            if (url.endsWith('/server_settings')) {
                return new Response(JSON.stringify({
                    zulip_version: '12.1',
                    zulip_feature_level: 500,
                    email_auth_enabled: true,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            if (url.endsWith('/fetch_api_key')) {
                return new Response(JSON.stringify({
                    api_key: 'api-key-secret',
                    email: 'login@example.test',
                    user_id: 9,
                }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            const authorization = request.headers.get('Authorization');
            authorizationEmails.push(atob(authorization!.slice('Basic '.length)).split(':', 1)[0]);
            return new Response(JSON.stringify({
                user_id: 9,
                full_name: 'Account Nine',
                email: 'user9@internal.example.test',
                is_active: true,
            }), { status: 200, headers: { 'Content-Type': 'application/json' } });
        });
        const store = new WebCredentialStore();
        const service = new WebAuthService(new ZulipApiClient(transport), store);

        const session = await service.login({
            realm: 'https://chat.example.test',
            email: 'login@example.test',
            password: 'password-secret',
            remember: true,
        });

        expect(authorizationEmails).toEqual(['login@example.test']);
        expect(session.email).toBe('login@example.test');
        expect(store.restore()?.email).toBe('login@example.test');
    });
});
