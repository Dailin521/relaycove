import { Bot } from 'lucide-react';
import { useEffect, useState } from 'react';
import type { PersonSummary } from '../models/ui';
import { useRealmImage } from './RealmMedia';

interface AvatarProps {
    label: string;
    initials: string;
    tone: PersonSummary['tone'];
    size?: 'small' | 'medium' | 'large';
    avatarUrl?: string;
    isBot?: boolean;
}

export function Avatar({ label, initials, tone, size = 'medium', avatarUrl, isBot = false }: AvatarProps) {
    const image = useRealmImage(avatarUrl, 'avatar');
    const [failedObjectUrl, setFailedObjectUrl] = useState<string>();

    useEffect(() => {
        if (failedObjectUrl && failedObjectUrl !== image.objectUrl) {
            setFailedObjectUrl(undefined);
        }
    }, [failedObjectUrl, image.objectUrl]);

    const showImage = image.status === 'loaded'
        && image.objectUrl !== undefined
        && image.objectUrl !== failedObjectUrl;
    return (
        <span
            className={`avatar avatar-${tone} avatar-${size}`}
            role="img"
            aria-label={label}
            data-image-status={avatarUrl ? image.status : undefined}
        >
            {showImage ? (
                <img
                    src={image.objectUrl}
                    alt=""
                    decoding="async"
                    draggable="false"
                    onError={() => setFailedObjectUrl(image.objectUrl)}
                />
            ) : isBot ? <Bot aria-hidden="true" /> : initials}
        </span>
    );
}
