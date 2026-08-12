import { normalizeRealm } from './realm';

export type RealmMediaKind = 'avatar' | 'upload';

const allowedPrefixes: Readonly<Record<RealmMediaKind, readonly string[]>> = {
    avatar: ['/avatar/', '/user_avatars/', '/static/generated/avatars/'],
    upload: ['/user_uploads/'],
};

export function resolveRealmMediaUrl(
    realm: string,
    value: string | undefined,
    kind: RealmMediaKind,
): string | undefined {
    const candidate = value?.trim();
    if (!candidate || candidate.length > 4_096 || candidate.startsWith('//')) {
        return undefined;
    }

    try {
        const normalizedRealm = normalizeRealm(realm);
        const url = new URL(candidate, `${normalizedRealm}/`);
        if (
            url.protocol !== 'https:'
            || url.origin !== normalizedRealm
            || url.username.length > 0
            || url.password.length > 0
            || url.hash.length > 0
        ) {
            return undefined;
        }

        const decodedPath = decodeURIComponent(url.pathname);
        if (decodedPath.split('/').some((part) => part === '..')) {
            return undefined;
        }
        if (!allowedPrefixes[kind].some((prefix) => decodedPath.startsWith(prefix))) {
            return undefined;
        }

        return url.toString();
    } catch {
        return undefined;
    }
}

export function isPreviewableImageName(name: string): boolean {
    return /\.(?:avif|gif|jpe?g|png|webp)$/iu.test(name.trim());
}
