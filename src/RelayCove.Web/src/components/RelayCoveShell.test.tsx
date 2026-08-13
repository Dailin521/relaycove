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

describe('RelayCoveShell attachment send lifecycle', () => {
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
        const onUploadAttachment = vi.fn((_file: File, signal: AbortSignal) => {
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
                onUploadAttachment={onUploadAttachment}
            />,
        );

        const file = new File(['safe-image'], 'draft.png', { type: 'image/png' });
        const input = container.querySelector<HTMLInputElement>('.composer-file-input');
        expect(input).not.toBeNull();
        fireEvent.change(input!, { target: { files: [file] } });
        expect(container.querySelector('.composer-attachment-draft img')).not.toBeNull();
        fireEvent.click(screen.getByRole('button', { name: '发送消息' }));
        await waitFor(() => expect(onUploadAttachment).toHaveBeenCalledOnce());

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

    it('uploads multiple ordinary files in order and sends one message containing every link', async () => {
        const uploadOrder: string[] = [];
        const onUploadAttachment = vi.fn(async (file: File) => {
            uploadOrder.push(file.name);
            return {
                url: `https://chat.example.test/user_uploads/1/${file.name}`,
                filename: file.name,
            };
        });
        const onSendMessage = vi.fn(async () => undefined);
        const { container } = render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={workspace}
                presentation={presentation}
                onLogout={() => undefined}
                onSendMessage={onSendMessage}
                onUploadAttachment={onUploadAttachment}
            />,
        );
        const files = [
            new File(['zip'], 'build.zip', { type: 'application/zip' }),
            new File(['pdf'], 'notes.pdf', { type: 'application/pdf' }),
        ];
        fireEvent.change(container.querySelector<HTMLInputElement>('.composer-file-input')!, {
            target: { files },
        });

        expect(screen.getByText('build.zip')).toBeVisible();
        expect(screen.getByText('notes.pdf')).toBeVisible();
        fireEvent.click(screen.getByRole('button', { name: '发送消息' }));

        await waitFor(() => expect(onSendMessage).toHaveBeenCalledOnce());
        expect(uploadOrder).toEqual(['build.zip', 'notes.pdf']);
        expect(onSendMessage).toHaveBeenCalledWith(conversationId, [
            '[build.zip](https://chat.example.test/user_uploads/1/build.zip)',
            '[notes.pdf](https://chat.example.test/user_uploads/1/notes.pdf)',
        ].join('\n'));
    });

    it('accepts a dragged non-image file through the same attachment draft path', () => {
        const { container } = render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={workspace}
                presentation={presentation}
                onLogout={() => undefined}
                onSendMessage={async () => undefined}
                onUploadAttachment={async (file) => ({
                    url: `https://chat.example.test/user_uploads/1/${file.name}`,
                    filename: file.name,
                })}
            />,
        );
        const composer = container.querySelector<HTMLElement>('.composer')!;
        fireEvent.drop(composer, {
            dataTransfer: { types: ['text/uri-list'], files: [], dropEffect: 'none' },
        });
        expect(screen.getByText('只支持拖入本地文件，不能导入网页链接或 HTML。')).toBeVisible();
        const transfer = {
            types: ['Files'],
            files: [new File(['docx'], 'plan.docx', {
                type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
            })],
            dropEffect: 'none',
        };

        fireEvent.dragEnter(composer, { dataTransfer: transfer });
        expect(screen.getByText('松开即可添加附件')).toBeVisible();
        fireEvent.drop(composer, { dataTransfer: transfer });
        expect(screen.getByText('plan.docx')).toBeVisible();
    });
});

describe('RelayCoveShell message interactions', () => {
    it('inserts an emoji at the current caret and restores the composer focus', async () => {
        render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={workspace}
                presentation={presentation}
                onLogout={() => undefined}
                onSendMessage={async () => undefined}
            />,
        );

        const textarea = screen.getByRole('textbox', { name: '消息正文' }) as HTMLTextAreaElement;
        fireEvent.change(textarea, { target: { value: '你好世界' } });
        textarea.focus();
        textarea.setSelectionRange(2, 2);
        fireEvent.click(screen.getByRole('button', { name: '插入表情' }));
        fireEvent.click(await screen.findByRole('button', { name: '赞 👍' }));

        expect(textarea).toHaveValue('你好👍世界');
        await waitFor(() => expect(textarea).toHaveFocus());
        expect(textarea.selectionStart).toBe(4);
    });

    it('quotes complete raw Markdown including image and file links', async () => {
        const quoteWorkspace: WorkspaceViewState = {
            ...workspace,
            conversations: {
                [conversationId]: {
                    ...workspace.conversations[conversationId],
                    messages: [{
                        id: '42',
                        sender: { id: '9', name: 'Grace Hopper', initials: 'GH', tone: 'green' },
                        sentAt: '10:00',
                        body: '正文',
                        rawContent: '正文\n![设计图](/user_uploads/1/design.png)\n[需求文档](/user_uploads/1/spec.pdf)',
                        permalink: 'https://chat.example.test/#narrow/near/42',
                    }],
                },
            },
        };
        render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={quoteWorkspace}
                presentation={presentation}
                onLogout={() => undefined}
                onSendMessage={async () => undefined}
            />,
        );

        fireEvent.click(screen.getByRole('button', { name: '引用 Grace Hopper 的消息' }));

        expect(screen.getByRole('textbox', { name: '消息正文' })).toHaveValue(
            '@_**Grace Hopper|9** [said](https://chat.example.test/#narrow/near/42):\n'
            + '```quote\n正文\n![设计图](/user_uploads/1/design.png)\n[需求文档](/user_uploads/1/spec.pdf)\n```\n\n',
        );
    });

    it('wires reaction, edit, delete, and star controls to formal message mutations', async () => {
        const messageWorkspace: WorkspaceViewState = {
            ...workspace,
            conversations: {
                [conversationId]: {
                    ...workspace.conversations[conversationId],
                    messages: [{
                        id: '101',
                        sender: workspace.currentUser,
                        sentAt: '10:00',
                        body: '原始正文',
                        rawContent: '原始正文',
                        own: true,
                        reactions: [],
                        isStarred: false,
                    }],
                },
            },
        };
        const onToggleReaction = vi.fn(async () => undefined);
        const onEditMessage = vi.fn(async () => undefined);
        const onDeleteMessage = vi.fn(async () => undefined);
        const onToggleStar = vi.fn(async () => undefined);
        render(
            <RelayCoveShell
                session={{ realm: 'https://chat.example.test', email: 'test@example.test' }}
                workspace={messageWorkspace}
                presentation={{ ...presentation, maxMessageLength: 10_000 }}
                onLogout={() => undefined}
                onSendMessage={async () => undefined}
                onToggleReaction={onToggleReaction}
                onEditMessage={onEditMessage}
                onDeleteMessage={onDeleteMessage}
                onToggleStar={onToggleStar}
            />,
        );

        fireEvent.click(screen.getByRole('button', { name: '收藏消息' }));
        await waitFor(() => expect(onToggleStar).toHaveBeenCalledWith('101', true));

        fireEvent.click(screen.getByRole('button', { name: '编辑 Test User 的消息' }));
        const editor = await screen.findByRole('textbox', { name: '编辑消息正文' });
        fireEvent.change(editor, { target: { value: '修改后的正文' } });
        fireEvent.click(screen.getByRole('button', { name: '保存修改' }));
        await waitFor(() => expect(onEditMessage).toHaveBeenCalledWith('101', '修改后的正文'));

        fireEvent.click(screen.getByRole('button', { name: /更多消息操作/ }));
        fireEvent.click(await screen.findByRole('menuitem', { name: '添加表情反应' }));
        fireEvent.click(await screen.findByRole('button', { name: '赞 👍' }));
        await waitFor(() => expect(onToggleReaction).toHaveBeenCalledWith('101', {
            emojiName: '+1',
            emojiCode: '1f44d',
            reactionType: 'unicode_emoji',
        }, true));

        fireEvent.click(screen.getByRole('button', { name: /更多消息操作/ }));
        fireEvent.click(await screen.findByRole('menuitem', { name: '删除消息' }));
        expect(await screen.findByText('消息将从 Zulip 删除，无法撤销。')).toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', { name: '确认删除' }));
        await waitFor(() => expect(onDeleteMessage).toHaveBeenCalledWith('101'));
    });
});
