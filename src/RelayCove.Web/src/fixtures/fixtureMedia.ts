import type { RealmImageLoader } from '../components/RealmMedia';

export const fixtureImageLoader: RealmImageLoader = async (sourceUrl, _kind, signal) => {
    if (signal.aborted) {
        throw new DOMException('Aborted', 'AbortError');
    }
    const name = new URL(sourceUrl).pathname.split('/').at(-1) ?? 'RelayCove';
    const label = decodeURIComponent(name).replace(/\.[^.]+$/u, '').slice(0, 24);
    const isPreview = sourceUrl.includes('/user_uploads/');
    const svg = isPreview ? teamPreviewSvg() : avatarSvg(label);
    return new Blob([svg], { type: 'image/svg+xml' });
};

function avatarSvg(label: string): string {
    const initials = [...label].slice(0, 2).join('').toLocaleUpperCase();
    return `<svg xmlns="http://www.w3.org/2000/svg" width="160" height="160" viewBox="0 0 160 160">
        <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#4db4ff"/><stop offset="1" stop-color="#6750a4"/></linearGradient></defs>
        <rect width="160" height="160" rx="42" fill="url(#g)"/>
        <text x="80" y="96" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="52" font-weight="650" fill="white">${escapeXml(initials)}</text>
    </svg>`;
}

function teamPreviewSvg(): string {
    return `<svg xmlns="http://www.w3.org/2000/svg" width="960" height="600" viewBox="0 0 960 600">
        <rect width="960" height="600" fill="#202125"/>
        <rect x="70" y="70" width="390" height="210" rx="30" fill="#2f9bff"/>
        <rect x="500" y="70" width="390" height="210" rx="30" fill="#43846f"/>
        <rect x="70" y="320" width="390" height="210" rx="30" fill="#d69b60"/>
        <rect x="500" y="320" width="390" height="210" rx="30" fill="#8268a9"/>
        <text x="480" y="305" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="34" font-weight="650" fill="white">RelayCove Team</text>
    </svg>`;
}

function escapeXml(value: string): string {
    return value.replace(/[&<>"']/gu, (character) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;',
    })[character]!);
}
