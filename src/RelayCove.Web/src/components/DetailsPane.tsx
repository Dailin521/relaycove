import { ChevronRight, LockKeyhole, Pin, Users, X } from 'lucide-react';
import { useEffect } from 'react';
import type { ConversationDetail } from '../models/ui';

interface DetailsPaneProps {
    conversation?: ConversationDetail;
    onClose(): void;
}

export function DetailsPane({ conversation, onClose }: DetailsPaneProps) {
    useEffect(() => {
        function handleKeyDown(event: globalThis.KeyboardEvent) {
            if (event.key === 'Escape') {
                onClose();
            }
        }
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [onClose]);

    if (!conversation) {
        return null;
    }

    return (
        <aside className="details-pane" aria-label="会话详情">
            <header>
                <strong>{conversation.kind === 'channel' ? '频道详情' : '会话详情'}</strong>
                <button className="icon-button" type="button" aria-label="关闭会话详情" onClick={onClose}>
                    <X aria-hidden="true" />
                </button>
            </header>
            <section>
                <span className="detail-label">当前会话</span>
                <strong>{conversation.kind === 'channel' ? `# ${conversation.channelName}` : conversation.title}</strong>
                <p>{conversation.kind === 'channel' ? `话题：${conversation.topic}` : 'Zulip 私信会话'}</p>
            </section>
            <section className="unavailable-capability">
                <Users aria-hidden="true" />
                <div>
                    <strong>成员与共同频道暂不可用</strong>
                    <p>不会从 Realm 用户列表推测频道成员关系。</p>
                </div>
            </section>
            <section className="detail-actions">
                <button type="button" aria-disabled="true"><Pin aria-hidden="true" /><span>固定会话</span><ChevronRight aria-hidden="true" /></button>
                <button type="button" aria-disabled="true"><LockKeyhole aria-hidden="true" /><span>权限能力待接入</span><ChevronRight aria-hidden="true" /></button>
            </section>
        </aside>
    );
}
