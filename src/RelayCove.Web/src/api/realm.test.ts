import { describe, expect, it } from 'vitest';
import { ZulipWebError } from './errors';
import { apiUrl, normalizeRealm } from './realm';

describe('normalizeRealm', () => {
    it('normalizes an HTTPS origin and removes the trailing slash', () => {
        expect(normalizeRealm(' https://Chat.Example.test/ ')).toBe('https://chat.example.test');
    });

    it.each([
        'http://chat.example.test',
        'https://user:secret@chat.example.test',
        'https://chat.example.test/path',
        'https://chat.example.test/?token=secret',
        'https://chat.example.test/#secret',
        'not a url',
    ])('rejects a non-origin Realm: %s', (value) => {
        expect(() => normalizeRealm(value)).toThrowError(ZulipWebError);
    });
});

describe('apiUrl', () => {
    it('builds a fixed API path without accepting a query string', () => {
        expect(apiUrl('https://chat.example.test', 'server_settings'))
            .toBe('https://chat.example.test/api/v1/server_settings');
        expect(() => apiUrl('https://chat.example.test', 'users/me?key=secret'))
            .toThrowError(ZulipWebError);
    });
});
