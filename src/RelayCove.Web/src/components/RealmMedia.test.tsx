import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Avatar } from './Avatar';
import {
    MAX_CONCURRENT_REALM_MEDIA_LOADS,
    RealmMediaProvider,
    useRealmImage,
} from './RealmMedia';

function Status({ sourceUrl }: { sourceUrl: string }) {
    const image = useRealmImage(sourceUrl, 'avatar');
    return <span data-testid={sourceUrl}>{image.status}</span>;
}

describe('RealmMediaProvider', () => {
    it('starts at most four media downloads and advances the queue as one settles', async () => {
        const pending: Array<(blob: Blob) => void> = [];
        const signals: AbortSignal[] = [];
        const loader = vi.fn((_sourceUrl: string, _kind: 'avatar' | 'upload', signal: AbortSignal) => {
            signals.push(signal);
            return new Promise<Blob>((resolve) => pending.push(resolve));
        });
        const sources = Array.from({ length: 7 }, (_, index) => `https://chat.example.test/avatar/${index}`);
        const { unmount } = render(
            <RealmMediaProvider loader={loader}>
                {sources.map((sourceUrl) => <Status key={sourceUrl} sourceUrl={sourceUrl} />)}
            </RealmMediaProvider>,
        );

        await waitFor(() => expect(loader).toHaveBeenCalledTimes(MAX_CONCURRENT_REALM_MEDIA_LOADS));
        pending[0]!(new Blob(['avatar'], { type: 'image/png' }));
        await waitFor(() => expect(loader).toHaveBeenCalledTimes(MAX_CONCURRENT_REALM_MEDIA_LOADS + 1));

        unmount();
        expect(signals.slice(1).every((signal) => signal.aborted)).toBe(true);
    });

    it('falls back to stable initials when a protected avatar cannot be loaded', async () => {
        const loader = vi.fn(async () => { throw new Error('unauthorized'); });
        render(
            <RealmMediaProvider loader={loader}>
                <Avatar
                    label="Grace Hopper"
                    initials="GH"
                    tone="blue"
                    avatarUrl="https://chat.example.test/avatar/9"
                />
            </RealmMediaProvider>,
        );

        await waitFor(() => expect(screen.getByRole('img', { name: 'Grace Hopper' })).toHaveAttribute('data-image-status', 'error'));
        expect(screen.getByRole('img', { name: 'Grace Hopper' })).toHaveTextContent('GH');
    });
});
