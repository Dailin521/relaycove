import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ShellPresentation, WorkspaceViewState } from '../models/ui';
import { RelayCoveShell } from './RelayCoveShell';

const conversationId = 'channel:7:release';

const workspace: WorkspaceViewState = {
    workspaceName: 'Test Realm',
    currentUser: {
        id: '1',
        name: 'Test User',
        initials: 'TU',
        tone: 'blue',
    },
    channels: [{
        id: conversationId,
        kind: 'channel',
        title: 'Release',
        subtitle: '# release',
        time: '10:00',
        unread: 0,
        channelName: 'release',
        topic: 'release',
        avatar: '#',
        tone: 'green',
    }],
    directs: [],
    conversations: {
        [conversationId]: {
            id: conversationId,
            kind: 'channel',
            title: 'Release',
            subtitle: '# release',
            time: '10:00',
            unread: 0,
            channelName: 'release',
            topic: 'release',
            avatar: '#',
            tone: 'green',
            messages: [],
        },
    },
    selectedConversationId: conversationId,
};

const presentation: ShellPresentation = {
    conversationSearchEnabled: false,
    emptySearchText: '没有结果',
    composerStatusText: '测试模式',
    sendEnabled: true,
};

describe('RelayCoveShell image send lifecycle', () => {
    it('aborts an upload and blocks a late adapter result before logout continues', async () => {
        const createObjectUrl = vi.fn(() => 'blob:relaycove-image-draft');
        const revokeObjectUrl = vi.fn();
        Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
        Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });

        let resolveUpload!: (value: { url: string; filename: string }) => void;
        const uploadResult = new Promise<{ url: string; filename: string }>((resolve) => {
            resolveUpload = resolve;
        });
        let uploadSignal: AbortSignal | undefined;
        const onUploadImage = vi.fn((_file: File, signal: AbortSignal) => {
            uploadSignal = signal;
            return uploadResult;
        });
        const onSendMessage = vi.fn(async () => undefined);
        const onLogout = vi.fn();
        const { container } = render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={workspace}
                presentation={presentation}
                onLogout={onLogout}
                onSendMessage={onSendMessage}
                onUploadImage={onUploadImage}
            />,
        );

        const file = new File(['safe-image'], 'draft.png', { type: 'image/png' });
        const input = container.querySelector<HTMLInputElement>('.composer-file-input');
        expect(input).not.toBeNull();
        fireEvent.change(input!, { target: { files: [file] } });
        await screen.findByAltText('待发送图片预览');
        fireEvent.click(screen.getByRole('button', { name: '发送消息' }));
        await waitFor(() => expect(onUploadImage).toHaveBeenCalledOnce());

        fireEvent.click(screen.getByRole('button', { name: '设置' }));
        fireEvent.click(screen.getByRole('button', { name: '账户' }));
        fireEvent.click(screen.getByRole('button', { name: '注销并清除本地凭据' }));
        fireEvent.click(screen.getByRole('button', { name: '确认注销' }));

        expect(uploadSignal?.aborted).toBe(true);
        expect(revokeObjectUrl).toHaveBeenCalledExactlyOnceWith('blob:relaycove-image-draft');
        expect(onLogout).toHaveBeenCalledOnce();

        await act(async () => {
            resolveUpload({
                url: 'https://chat.example.test/user_uploads/1/late/draft.png',
                filename: 'draft.png',
            });
            await uploadResult;
        });

        expect(onSendMessage).not.toHaveBeenCalled();
        expect(revokeObjectUrl).toHaveBeenCalledTimes(1);
    });
});
