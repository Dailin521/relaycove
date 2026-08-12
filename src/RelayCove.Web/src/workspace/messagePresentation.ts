import { isPreviewableImageName, resolveRealmMediaUrl } from '../api/realmMedia';
import type { MessageAttachment } from '../models/ui';

export interface MessagePresentation {
    body: string;
    attachments: MessageAttachment[];
}

const markdownLink = /!?\[([^\]\r\n]{1,256})\]\(([^)\r\n]{1,4096})\)/gu;
export const MAX_MESSAGE_IMAGE_PREVIEWS = 4;

export function presentMessageContent(content: string, realm: string): MessagePresentation {
    const attachments: MessageAttachment[] = [];
    const seen = new Set<string>();
    const body = content.replace(markdownLink, (match, rawName: string, rawUrl: string) => {
        const sourceUrl = resolveRealmMediaUrl(realm, rawUrl.replace(/^<|>$/gu, ''), 'upload');
        if (!sourceUrl) {
            return match;
        }

        const name = normalizeAttachmentName(rawName);
        const pathName = decodeURIComponent(new URL(sourceUrl).pathname.split('/').at(-1) ?? '');
        if (!isPreviewableImageName(name) && !isPreviewableImageName(pathName)) {
            return match;
        }
        if (!seen.has(sourceUrl) && attachments.length >= MAX_MESSAGE_IMAGE_PREVIEWS) {
            return match;
        }
        if (!seen.has(sourceUrl)) {
            seen.add(sourceUrl);
            attachments.push({ kind: 'image', name, sourceUrl });
        }
        return '';
    }).replace(/[ \t]+\n/gu, '\n').replace(/\n{3,}/gu, '\n\n').trim();

    return { body, attachments };
}

function normalizeAttachmentName(value: string): string {
    const normalized = value.replace(/\\([\\\[\]])/gu, '$1').trim();
    return normalized.slice(0, 256) || '图片';
}
