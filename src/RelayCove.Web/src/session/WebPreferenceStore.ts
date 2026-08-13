import type { Theme } from '../models/ui';

export interface WebPreferences {
    theme: Theme;
    fontSize: number;
    listWidth: number;
    detailsDefault: boolean;
    channelsCollapsed: boolean;
    directsCollapsed: boolean;
}

const STORAGE_KEY = 'relaycove.web.preferences.v1';

export const defaultWebPreferences: WebPreferences = {
    theme: 'light',
    fontSize: 14,
    listWidth: 310,
    detailsDefault: false,
    channelsCollapsed: false,
    directsCollapsed: false,
};

export function readWebPreferences(storage: Storage = window.localStorage): WebPreferences {
    try {
        const serialized = storage.getItem(STORAGE_KEY);
        if (!serialized) {
            return defaultWebPreferences;
        }
        const value = JSON.parse(serialized) as Partial<WebPreferences> & { version?: unknown };
        if (
            value.version !== 1
            || (value.theme !== 'light' && value.theme !== 'dark')
            || typeof value.fontSize !== 'number'
            || typeof value.listWidth !== 'number'
            || typeof value.detailsDefault !== 'boolean'
        ) {
            storage.removeItem(STORAGE_KEY);
            return defaultWebPreferences;
        }
        return {
            theme: value.theme,
            fontSize: clamp(Math.round(value.fontSize), 13, 16),
            listWidth: clamp(Math.round(value.listWidth / 10) * 10, 270, 370),
            detailsDefault: value.detailsDefault,
            channelsCollapsed: typeof value.channelsCollapsed === 'boolean' ? value.channelsCollapsed : false,
            directsCollapsed: typeof value.directsCollapsed === 'boolean' ? value.directsCollapsed : false,
        };
    } catch {
        return defaultWebPreferences;
    }
}

export function writeWebPreferences(
    preferences: WebPreferences,
    storage: Storage = window.localStorage,
): void {
    try {
        storage.setItem(STORAGE_KEY, JSON.stringify({ version: 1, ...preferences }));
    } catch {
        // Appearance persistence is best effort and never blocks the chat client.
    }
}

function clamp(value: number, minimum: number, maximum: number): number {
    return Math.max(minimum, Math.min(maximum, value));
}
