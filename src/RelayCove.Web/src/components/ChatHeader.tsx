import { ArrowLeft, Info, MoreHorizontal, Search } from 'lucide-react';
import type { ConversationDetail } from '../models/ui';

interface ChatHeaderProps {
    conversation?: ConversationDetail;
    detailsOpen: boolean;
    onBack(): void;
    onToggleDetails(): void;
}

export function ChatHeader({ conversation, detailsOpen, onBack, onToggleDetails }: ChatHeaderProps) {
    return (
        <header className="chat-header">
            <button className="mobile-back-button" type="button" aria-label="返回会话列表" onClick={onBack}>
                <ArrowLeft aria-hidden="true" />
            </button>
            <div className="chat-title">
                <h1>{conversation?.title ?? '选择一个会话'}</h1>
                <small>
                    {conversation?.kind === 'channel'
                        ? `# ${conversation.channelName} · ${conversation.topic}`
                        : conversation ? 'Zulip 私信' : '从左侧选择会话开始'}
                </small>
            </div>
            <button className="icon-button" type="button" aria-label="会话内搜索尚未启用" aria-disabled="true" title="会话内搜索将在后续能力门接入">
                <Search aria-hidden="true" />
            </button>
            <button
                className={detailsOpen ? 'icon-button is-pressed' : 'icon-button'}
                type="button"
                aria-label="会话详情"
                aria-expanded={detailsOpen}
                onClick={onToggleDetails}
                disabled={!conversation}
            >
                <Info aria-hidden="true" />
            </button>
            <button className="icon-button" type="button" aria-label="更多操作尚未启用" aria-disabled="true">
                <MoreHorizontal aria-hidden="true" />
            </button>
        </header>
    );
}
