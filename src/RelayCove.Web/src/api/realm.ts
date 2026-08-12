import { ZulipWebError } from './errors';

export const DEFAULT_REALM = 'https://hklight.2000521.xyz';

export function normalizeRealm(value: string): string {
    let parsed: URL;
    try {
        parsed = new URL(value.trim());
    } catch {
        throw new ZulipWebError('invalid_realm');
    }

    if (
        parsed.protocol !== 'https:'
        || parsed.username.length > 0
        || parsed.password.length > 0
        || parsed.search.length > 0
        || parsed.hash.length > 0
        || (parsed.pathname !== '' && parsed.pathname !== '/')
    ) {
        throw new ZulipWebError('invalid_realm');
    }

    return parsed.origin;
}

export function apiUrl(realm: string, endpoint: string): string {
    if (!/^[a-z0-9_/-]+$/i.test(endpoint) || endpoint.startsWith('/')) {
        throw new ZulipWebError('invalid_response');
    }

    return `${normalizeRealm(realm)}/api/v1/${endpoint}`;
}
