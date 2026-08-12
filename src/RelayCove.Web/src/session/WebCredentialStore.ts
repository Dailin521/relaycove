import { normalizeRealm } from '../api/realm';
import type { WebSession } from '../api/types';

interface StoredSessionV1 {
    version: 1;
    realm: string;
    email: string;
    apiKey: string;
    userId: number;
    fullName: string;
    remember: boolean;
}

const STORAGE_KEY = 'relaycove.web.session.v1';

export class WebCredentialStore {
    private readonly persistentStorage: Storage;
    private readonly transientStorage: Storage;

    public constructor(
        persistentStorage: Storage = window.localStorage,
        transientStorage: Storage = window.sessionStorage,
    ) {
        this.persistentStorage = persistentStorage;
        this.transientStorage = transientStorage;
    }

    public restore(): WebSession | null {
        const persistent = this.read(this.persistentStorage);
        if (persistent) {
            return persistent;
        }

        return this.read(this.transientStorage);
    }

    public save(session: WebSession): void {
        this.clear();
        const stored: StoredSessionV1 = {
            version: 1,
            realm: normalizeRealm(session.realm),
            email: session.email.trim(),
            apiKey: session.apiKey,
            userId: session.userId,
            fullName: session.fullName,
            remember: session.remember,
        };
        const storage = session.remember ? this.persistentStorage : this.transientStorage;
        storage.setItem(STORAGE_KEY, JSON.stringify(stored));
    }

    public clear(): void {
        this.persistentStorage.removeItem(STORAGE_KEY);
        this.transientStorage.removeItem(STORAGE_KEY);
    }

    private read(storage: Storage): WebSession | null {
        const serialized = storage.getItem(STORAGE_KEY);
        if (!serialized) {
            return null;
        }

        try {
            const candidate = JSON.parse(serialized) as Partial<StoredSessionV1>;
            if (
                candidate.version !== 1
                || typeof candidate.realm !== 'string'
                || typeof candidate.email !== 'string'
                || candidate.email.length === 0
                || typeof candidate.apiKey !== 'string'
                || candidate.apiKey.length === 0
                || typeof candidate.remember !== 'boolean'
                || typeof candidate.userId !== 'number'
                || candidate.userId <= 0
                || typeof candidate.fullName !== 'string'
                || candidate.fullName.length === 0
            ) {
                storage.removeItem(STORAGE_KEY);
                return null;
            }

            return {
                realm: normalizeRealm(candidate.realm),
                email: candidate.email,
                apiKey: candidate.apiKey,
                userId: candidate.userId,
                fullName: candidate.fullName,
                remember: candidate.remember,
            };
        } catch {
            storage.removeItem(STORAGE_KEY);
            return null;
        }
    }
}
