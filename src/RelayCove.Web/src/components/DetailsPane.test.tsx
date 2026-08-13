import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ConversationDetail } from '../models/ui';
import { DetailsPane } from './DetailsPane';

const conversation: ConversationDetail = {
    id: 'channel:5:release',
    kind: 'channel',
    title: 'Release',
    subtitle: '# engineering',
    time: '',
    unread: 0,
    channelName: 'engineering',
    channelId: 5,
    topic: 'release',
    avatar: '#',
    tone: 'blue',
    messages: [],
};

describe('DetailsPane channel actions', () => {
    afterEach(() => vi.restoreAllMocks());

    it('requires confirmation and submits one channel unsubscribe', async () => {
        vi.spyOn(window, 'confirm').mockReturnValue(true);
        const unsubscribe = vi.fn(async () => undefined);
        const close = vi.fn();
        render(<DetailsPane conversation={conversation} onClose={close} onUnsubscribeChannel={unsubscribe} />);

        fireEvent.click(screen.getByRole('button', { name: '退出频道' }));

        await waitFor(() => expect(unsubscribe).toHaveBeenCalledExactlyOnceWith(5));
        expect(window.confirm).toHaveBeenCalledOnce();
        expect(close).toHaveBeenCalledOnce();
    });
});
