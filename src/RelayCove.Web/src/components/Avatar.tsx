import { Bot } from 'lucide-react';
import { useEffect, useState } from 'react';
import { isPublicRealmAvatarUrl } from '../api/realmMedia';
import type { PersonSummary } from '../models/ui';
import { useRealmImage, useRealmMediaPolicy } from './RealmMedia';

interface AvatarProps {
    label: string;
    initials: string;
    tone: PersonSummary['tone'];
    size?: 'small' | 'medium' | 'large';
    avatarUrl?: string;
    isBot?: boolean;
}

export function Avatar({ label, initials, tone, size = 'medium', avatarUrl, isBot = false }: AvatarProps) {
    const mediaPolicy = useRealmMediaPolicy();
    const publicAvatarUrl = isPublicRealmAvatarUrl(avatarUrl, mediaPolicy.realm) ? avatarUrl : undefined;
    const protectedAvatarUrl = !publicAvatarUrl && (mediaPolicy.allowCrossOriginLoader || isSameOrigin(avatarUrl))
        ? avatarUrl
        : undefined;
    const image = useRealmImage(protectedAvatarUrl, 'avatar');
    const [failedObjectUrl, setFailedObjectUrl] = useState<string>();
    const imageUrl = publicAvatarUrl ?? image.objectUrl;

    useEffect(() => {
        if (failedObjectUrl && failedObjectUrl !== imageUrl) {
            setFailedObjectUrl(undefined);
        }
    }, [failedObjectUrl, imageUrl]);

    const showImage = (publicAvatarUrl !== undefined || image.status === 'loaded')
        && imageUrl !== undefined
        && imageUrl !== failedObjectUrl;
    return (
        <span
            className={`avatar avatar-${tone} avatar-${size}`}
            role="img"
            aria-label={label}
            data-image-status={avatarUrl
                ? publicAvatarUrl
                    ? 'direct'
                    : protectedAvatarUrl
                        ? image.status
                        : 'unavailable'
                : undefined}
        >
            {showImage ? (
                <img
                    src={imageUrl}
                    alt=""
                    decoding="async"
                    draggable="false"
                    referrerPolicy="no-referrer"
                    onError={() => setFailedObjectUrl(imageUrl)}
                />
            ) : isBot ? <Bot aria-hidden="true" /> : initials}
        </span>
    );
}

function isSameOrigin(sourceUrl: string | undefined): boolean {
    if (!sourceUrl || !globalThis.location?.origin) {
        return false;
    }
    try {
        return new URL(sourceUrl).origin === globalThis.location.origin;
    } catch {
        return false;
    }
}
