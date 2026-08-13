import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import type { ConversationSummary } from '../models/ui';
import { ConversationPane } from './ConversationPane';

const channel: ConversationSummary = {
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
};

const direct: ConversationSummary = {
    id: 'dm:9',
    kind: 'direct',
    title: 'Grace Hopper',
    subtitle: '私信',
    time: '',
    unread: 0,
    avatar: 'GH',
    tone: 'green',
};

function Harness() {
    const [channelsCollapsed, setChannelsCollapsed] = useState(false);
    const [directsCollapsed, setDirectsCollapsed] = useState(false);
    return (
        <ConversationPane
            section="messages"
            channels={[channel]}
            directs={[direct]}
            contacts={[]}
            currentUser={{ id: '7', name: 'Ada', initials: 'A', tone: 'blue' }}
            subscribedChannels={[{ channelId: 5, name: 'engineering' }]}
            searchEnabled
            emptySearchText="没有结果"
            onSelect={() => undefined}
            channelsCollapsed={channelsCollapsed}
            directsCollapsed={directsCollapsed}
            onChannelsCollapsedChange={setChannelsCollapsed}
            onDirectsCollapsedChange={setDirectsCollapsed}
        />
    );
}

describe('ConversationPane groups', () => {
    it('collapses channels and direct messages independently with accessible buttons', () => {
        render(<Harness />);
        const channelsButton = screen.getByRole('button', { name: /频道1/u });
        const directsButton = screen.getByRole('button', { name: /私信1 个会话/u });
        expect(channelsButton).toHaveAttribute('aria-expanded', 'true');
        expect(directsButton).toHaveAttribute('aria-expanded', 'true');
        expect(screen.getByRole('button', { name: /Release/u })).toBeVisible();
        expect(screen.getByRole('button', { name: /Grace Hopper/u })).toBeVisible();

        fireEvent.click(channelsButton);
        expect(channelsButton).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('button', { name: /Release/u })).toBeNull();
        expect(screen.getByRole('button', { name: /Grace Hopper/u })).toBeVisible();

        fireEvent.click(directsButton);
        expect(directsButton).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('button', { name: /Grace Hopper/u })).toBeNull();
    });
});
