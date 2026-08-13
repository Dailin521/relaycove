import { ChevronRight, Pin, Plus, Search } from 'lucide-react';
import { KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ConversationSummary, NavigationSection, NewConversationRequest, PersonSummary } from '../models/ui';
import { Avatar } from './Avatar';

interface ConversationPaneProps {
    section: NavigationSection;
    channels: ConversationSummary[];
    directs: ConversationSummary[];
    contacts: PersonSummary[];
    currentUser: PersonSummary;
    subscribedChannels: Array<{ channelId: number; name: string }>;
    selectedId?: string;
    searchEnabled: boolean;
    searchTitle?: string;
    dataSourceNotice?: string;
    emptySearchText: string;
    onSelect(conversationId: string): void;
    onCreateConversation?(request: NewConversationRequest): void;
    channelsCollapsed: boolean;
    directsCollapsed: boolean;
    onChannelsCollapsedChange(collapsed: boolean): void;
    onDirectsCollapsedChange(collapsed: boolean): void;
}

function ConversationRow({
    conversation,
    selected,
    onSelect,
}: {
    conversation: ConversationSummary;
    selected: boolean;
    onSelect(): void;
}) {
    return (
        <button
            className={selected ? 'conversation-row is-selected' : 'conversation-row'}
            type="button"
            aria-current={selected ? 'true' : undefined}
            onClick={onSelect}
        >
            <Avatar
                label={conversation.title}
                initials={conversation.avatar}
                tone={conversation.tone}
                size="large"
                avatarUrl={conversation.avatarUrl}
                isBot={conversation.isBot}
            />
            <span className="conversation-copy">
                <strong>{conversation.title}</strong>
                <small>{conversation.subtitle}</small>
            </span>
            <span className="conversation-meta">
                <time>{conversation.time}</time>
                {conversation.unread > 0 && (
                    <b aria-label={`${conversation.unread} 条未读消息`}>
                        {conversation.unread > 99 ? '99+' : conversation.unread}
                    </b>
                )}
                {conversation.pinned && <Pin aria-label="已置顶" />}
                {conversation.online && <i className="online-dot" aria-label="在线" />}
            </span>
        </button>
    );
}

export function ConversationPane({
    section,
    channels,
    directs,
    contacts,
    currentUser,
    subscribedChannels,
    selectedId,
    searchEnabled,
    searchTitle,
    dataSourceNotice,
    emptySearchText,
    onSelect,
    onCreateConversation,
    channelsCollapsed,
    directsCollapsed,
    onChannelsCollapsedChange,
    onDirectsCollapsedChange,
}: ConversationPaneProps) {
    const [query, setQuery] = useState('');
    const [newConversationOpen, setNewConversationOpen] = useState(false);
    const [newConversationMode, setNewConversationMode] = useState<'dm' | 'channel'>('dm');
    const [selectedUsers, setSelectedUsers] = useState<number[]>([]);
    const [selectedChannelId, setSelectedChannelId] = useState<number>(subscribedChannels[0]?.channelId ?? 0);
    const [topic, setTopic] = useState('');
    const paneRef = useRef<HTMLElement>(null);
    const newConversationTriggerRef = useRef<HTMLButtonElement>(null);
    const newConversationPopoverRef = useRef<HTMLElement>(null);
    const normalizedQuery = query.trim().toLocaleLowerCase();
    const filter = (conversation: ConversationSummary) => normalizedQuery.length === 0
        || `${conversation.title} ${conversation.subtitle}`.toLocaleLowerCase().includes(normalizedQuery);
    const visibleChannels = useMemo(() => channels.filter(filter), [channels, normalizedQuery]);
    const visibleDirects = useMemo(() => directs.filter(filter), [directs, normalizedQuery]);
    const channelsHidden = channelsCollapsed && !(normalizedQuery && visibleChannels.length > 0);
    const directsHidden = directsCollapsed && !(normalizedQuery && visibleDirects.length > 0);

    useEffect(() => {
        if ((!selectedChannelId || !subscribedChannels.some((channel) => channel.channelId === selectedChannelId)) && subscribedChannels[0]) {
            setSelectedChannelId(subscribedChannels[0].channelId);
        }
    }, [selectedChannelId, subscribedChannels]);

    const closeNewConversation = useCallback((restoreFocus = true) => {
        setNewConversationOpen(false);
        if (restoreFocus) {
            window.requestAnimationFrame(() => newConversationTriggerRef.current?.focus());
        }
    }, []);

    useEffect(() => {
        if (!newConversationOpen) {
            return;
        }
        newConversationPopoverRef.current?.querySelector<HTMLButtonElement>('[role="tab"]')?.focus();
        function handlePointerDown(event: PointerEvent) {
            const target = event.target as Node;
            if (!newConversationPopoverRef.current?.contains(target) && !newConversationTriggerRef.current?.contains(target)) {
                closeNewConversation(false);
            }
        }
        function handleKeyDown(event: globalThis.KeyboardEvent) {
            if (event.key === 'Escape') {
                event.preventDefault();
                closeNewConversation();
            }
        }
        window.addEventListener('pointerdown', handlePointerDown, true);
        window.addEventListener('keydown', handleKeyDown);
        return () => {
            window.removeEventListener('pointerdown', handlePointerDown, true);
            window.removeEventListener('keydown', handleKeyDown);
        };
    }, [closeNewConversation, newConversationOpen]);

    function handleSearchKeyDown(event: KeyboardEvent<HTMLInputElement>) {
        if (event.key !== 'ArrowDown') {
            return;
        }
        const firstConversation = paneRef.current?.querySelector<HTMLButtonElement>('.conversation-list:not([hidden]) .conversation-row');
        if (firstConversation) {
            event.preventDefault();
            firstConversation.focus();
        }
    }

    if (section === 'contacts') {
        return (
            <aside className="conversation-pane capability-pane contacts-pane" aria-label="联系人">
                <div className="capability-heading">
                    <strong>联系人</strong>
                    <small>来自当前 Realm 的已知活跃用户</small>
                </div>
                <div className="contact-list">
                    {contacts.map((contact) => (
                        <button
                            key={contact.id}
                            type="button"
                            className="contact-row"
                            onClick={() => onCreateConversation?.({ kind: 'dm', userIds: [Number(contact.id)] })}
                        >
                            <Avatar
                                label={contact.name}
                                initials={contact.initials}
                                tone={contact.tone}
                                avatarUrl={contact.avatarUrl}
                                isBot={contact.isBot}
                            />
                            <span><strong>{contact.name}</strong><small>发起私信</small></span>
                        </button>
                    ))}
                    {contacts.length === 0 && <p className="no-results">当前没有可用联系人。</p>}
                </div>
            </aside>
        );
    }

    if (section !== 'messages') {
        return (
            <aside className="conversation-pane capability-pane" aria-label="已保存">
                <div className="capability-heading">
                    <strong>已保存</strong>
                    <small>正式数据能力将在独立 Slice 接入</small>
                </div>
                <div className="capability-empty">
                    <span aria-hidden="true">◇</span>
                    <p>当前不会用演示数据冒充 Zulip 权威结果。</p>
                </div>
            </aside>
        );
    }

    return (
        <aside ref={paneRef} className="conversation-pane" aria-label="会话列表">
            <div className="conversation-tools">
                <label className="conversation-search">
                    <Search aria-hidden="true" />
                    <input
                        type="search"
                        aria-label="搜索会话"
                        placeholder={searchTitle ?? '搜索会话、成员和消息'}
                        value={query}
                        disabled={!searchEnabled}
                        title={searchTitle}
                        onChange={(event) => setQuery(event.target.value)}
                        onKeyDown={handleSearchKeyDown}
                    />
                </label>
                <button
                    ref={newConversationTriggerRef}
                    className="new-conversation-button"
                    type="button"
                    aria-label={onCreateConversation ? '新建会话' : '新建会话，演示中不可用'}
                    aria-expanded={onCreateConversation ? newConversationOpen : undefined}
                    aria-haspopup={onCreateConversation ? 'dialog' : undefined}
                    aria-disabled={!onCreateConversation}
                    onClick={() => onCreateConversation && setNewConversationOpen((value) => !value)}
                >
                    <Plus aria-hidden="true" />
                </button>
            </div>
            {newConversationOpen && (
                <section ref={newConversationPopoverRef} className="new-conversation-popover" role="dialog" aria-label="新建会话">
                    <div className="new-conversation-tabs" role="tablist" aria-label="会话类型">
                        <button type="button" role="tab" aria-selected={newConversationMode === 'dm'} onClick={() => setNewConversationMode('dm')}>私信</button>
                        <button type="button" role="tab" aria-selected={newConversationMode === 'channel'} onClick={() => setNewConversationMode('channel')}>频道话题</button>
                    </div>
                    {newConversationMode === 'dm' ? (
                        <div className="new-conversation-form">
                            <label className="new-conversation-option">
                                <input
                                    type="checkbox"
                                    checked={selectedUsers.includes(Number(currentUser.id))}
                                    onChange={(event) => setSelectedUsers(event.target.checked ? [Number(currentUser.id)] : [])}
                                />
                                <span>{currentUser.name}（自己）</span>
                            </label>
                            {contacts.map((contact) => {
                                const userId = Number(contact.id);
                                return (
                                    <label className="new-conversation-option" key={contact.id}>
                                        <input
                                            type="checkbox"
                                            checked={selectedUsers.includes(userId)}
                                            onChange={(event) => setSelectedUsers((current) => event.target.checked
                                                ? [...current.filter((id) => id !== Number(currentUser.id)), userId]
                                                : current.filter((id) => id !== userId))}
                                        />
                                        <span>{contact.name}</span>
                                    </label>
                                );
                            })}
                            <button
                                type="button"
                                className="primary-button"
                                disabled={selectedUsers.length === 0}
                                onClick={() => {
                                    onCreateConversation?.({ kind: 'dm', userIds: selectedUsers });
                                    closeNewConversation();
                                    setSelectedUsers([]);
                                }}
                            >
                                打开私信
                            </button>
                        </div>
                    ) : (
                        <div className="new-conversation-form">
                            <label>频道
                                <select value={selectedChannelId} onChange={(event) => setSelectedChannelId(Number(event.target.value))}>
                                    {subscribedChannels.map((channel) => <option key={channel.channelId} value={channel.channelId}># {channel.name}</option>)}
                                </select>
                            </label>
                            <label>话题
                                <input value={topic} maxLength={200} onChange={(event) => setTopic(event.target.value)} placeholder="输入话题（允许空话题）" />
                            </label>
                            <button
                                type="button"
                                className="primary-button"
                                disabled={!selectedChannelId}
                                onClick={() => {
                                    onCreateConversation?.({ kind: 'channel', channelId: selectedChannelId, topic });
                                    closeNewConversation();
                                    setTopic('');
                                }}
                            >
                                打开话题
                            </button>
                        </div>
                    )}
                </section>
            )}
            {dataSourceNotice && <p className="data-source-notice">{dataSourceNotice}</p>}
            <div className="conversation-scroll">
                <section className="conversation-group" aria-labelledby="channels-heading">
                    <button
                        className="group-heading"
                        id="channels-heading"
                        type="button"
                        aria-expanded={!channelsHidden}
                        aria-controls="channels-list"
                        onClick={() => onChannelsCollapsedChange(!channelsCollapsed)}
                    >
                        <span><ChevronRight aria-hidden="true" />频道</span><small>{channels.length}</small>
                    </button>
                    <div className="conversation-list" id="channels-list" hidden={channelsHidden}>
                        {visibleChannels.map((conversation) => (
                            <ConversationRow
                                key={conversation.id}
                                conversation={conversation}
                                selected={conversation.id === selectedId}
                                onSelect={() => onSelect(conversation.id)}
                            />
                        ))}
                    </div>
                </section>
                <section className="conversation-group" aria-labelledby="directs-heading">
                    <button
                        className="group-heading"
                        id="directs-heading"
                        type="button"
                        aria-expanded={!directsHidden}
                        aria-controls="directs-list"
                        onClick={() => onDirectsCollapsedChange(!directsCollapsed)}
                    >
                        <span><ChevronRight aria-hidden="true" />私信</span><small>{directs.length} 个会话</small>
                    </button>
                    <div className="conversation-list" id="directs-list" hidden={directsHidden}>
                        {visibleDirects.map((conversation) => (
                            <ConversationRow
                                key={conversation.id}
                                conversation={conversation}
                                selected={conversation.id === selectedId}
                                onSelect={() => onSelect(conversation.id)}
                            />
                        ))}
                    </div>
                </section>
                {visibleChannels.length === 0 && visibleDirects.length === 0 && (
                    <p className="no-results">{emptySearchText}</p>
                )}
            </div>
        </aside>
    );
}
