import { beforeEach, describe, expect, it } from 'vitest';
import type { WebSession } from '../api/types';
import { WebCredentialStore } from './WebCredentialStore';

const remembered: WebSession = {
    realm: 'https://chat.example.test',
    email: 'ada@example.test',
    apiKey: 'api-key-secret',
    userId: 7,
    fullName: 'Ada Lovelace',
    remember: true,
};

describe('WebCredentialStore', () => {
    beforeEach(() => {
        localStorage.clear();
        sessionStorage.clear();
    });

    it('persists remembered credentials only in browser local storage', () => {
        const store = new WebCredentialStore();

        store.save(remembered);

        expect(localStorage.length).toBe(1);
        expect(sessionStorage.length).toBe(0);
        expect(store.restore()).toEqual(remembered);
    });

    it('uses session storage when remember login is disabled', () => {
        const store = new WebCredentialStore();

        store.save({ ...remembered, remember: false });

        expect(localStorage.length).toBe(0);
        expect(sessionStorage.length).toBe(1);
        expect(store.restore()).toEqual({ ...remembered, remember: false });
    });

    it('clears all browser credentials on logout', () => {
        const store = new WebCredentialStore();
        store.save(remembered);

        store.clear();

        expect(localStorage.length).toBe(0);
        expect(sessionStorage.length).toBe(0);
        expect(store.restore()).toBeNull();
    });

    it('removes malformed credential state instead of returning it', () => {
        localStorage.setItem('relaycove.web.session.v1', JSON.stringify({
            version: 1,
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
        }));
        const store = new WebCredentialStore();

        expect(store.restore()).toBeNull();
        expect(localStorage.length).toBe(0);
    });
});
