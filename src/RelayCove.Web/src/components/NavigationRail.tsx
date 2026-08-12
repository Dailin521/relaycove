import { Bookmark, MessageCircle, Settings, Users } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { NavigationSection, PersonSummary } from '../models/ui';
import { Avatar } from './Avatar';

interface NavigationRailProps {
    currentUser: PersonSummary;
    activeSection: NavigationSection;
    totalUnread: number;
    onSelect(section: NavigationSection): void;
}

const navigationItems: Array<{
    id: NavigationSection;
    label: string;
    icon: typeof MessageCircle;
    capabilityUnavailable?: boolean;
}> = [
    { id: 'messages', label: '消息', icon: MessageCircle },
    { id: 'contacts', label: '联系人', icon: Users },
    { id: 'saved', label: '已保存', icon: Bookmark, capabilityUnavailable: true },
];

export function NavigationRail({ currentUser, activeSection, totalUnread, onSelect }: NavigationRailProps) {
    const [accountOpen, setAccountOpen] = useState(false);
    const accountTriggerRef = useRef<HTMLButtonElement>(null);
    const accountMenuRef = useRef<HTMLElement>(null);
    const closeAccount = useCallback((restoreFocus = true) => {
        setAccountOpen(false);
        if (restoreFocus) {
            window.requestAnimationFrame(() => accountTriggerRef.current?.focus());
        }
    }, []);

    useEffect(() => {
        if (!accountOpen) {
            return;
        }
        accountMenuRef.current?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus();
        function handlePointerDown(event: PointerEvent) {
            const target = event.target as Node;
            if (!accountMenuRef.current?.contains(target) && !accountTriggerRef.current?.contains(target)) {
                closeAccount(false);
            }
        }
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape') {
                event.preventDefault();
                closeAccount();
                return;
            }
            if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
                return;
            }
            const items = [...(accountMenuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
            if (items.length === 0) {
                return;
            }
            event.preventDefault();
            const currentIndex = items.indexOf(document.activeElement as HTMLElement);
            const nextIndex = event.key === 'Home'
                ? 0
                : event.key === 'End'
                    ? items.length - 1
                    : event.key === 'ArrowDown'
                        ? (currentIndex + 1 + items.length) % items.length
                        : (currentIndex - 1 + items.length) % items.length;
            items[nextIndex]?.focus();
        }
        window.addEventListener('pointerdown', handlePointerDown, true);
        window.addEventListener('keydown', handleKeyDown);
        return () => {
            window.removeEventListener('pointerdown', handlePointerDown, true);
            window.removeEventListener('keydown', handleKeyDown);
        };
    }, [accountOpen, closeAccount]);

    return (
        <nav className="navigation-rail" aria-label="主导航">
            <button
                ref={accountTriggerRef}
                className="account-avatar-button"
                type="button"
                aria-label={`当前用户：${currentUser.name}`}
                aria-expanded={accountOpen}
                aria-haspopup="menu"
                onClick={() => setAccountOpen((value) => !value)}
            >
                <Avatar
                    label={currentUser.name}
                    initials={currentUser.initials}
                    tone={currentUser.tone}
                    avatarUrl={currentUser.avatarUrl}
                    isBot={currentUser.isBot}
                />
            </button>
            {accountOpen && (
                <section ref={accountMenuRef} className="account-popover" role="menu" aria-label="账户菜单">
                    <strong>{currentUser.name}</strong>
                    <small>已连接 RelayCove Web</small>
                    <button type="button" role="menuitem" onClick={() => { closeAccount(false); onSelect('settings'); }}>打开设置</button>
                    <button type="button" role="menuitem" onClick={() => { closeAccount(false); onSelect('settings'); }}>账户与注销</button>
                </section>
            )}
            <div className="rail-main-items">
                {navigationItems.map(({ id, label, icon: Icon, capabilityUnavailable }) => (
                    <button
                        key={id}
                        type="button"
                        className={activeSection === id ? 'rail-item is-active' : 'rail-item'}
                        aria-current={activeSection === id ? 'page' : undefined}
                        aria-label={capabilityUnavailable ? `${label}，能力尚未启用` : label}
                        onClick={() => onSelect(id)}
                    >
                        <span className="rail-icon-wrap">
                            <Icon aria-hidden="true" />
                            {id === 'messages' && totalUnread > 0 && (
                                <b aria-label={`${totalUnread} 条未读消息`}>
                                    {totalUnread > 99 ? '99+' : totalUnread}
                                </b>
                            )}
                        </span>
                        <span>{label}</span>
                    </button>
                ))}
            </div>
            <button
                type="button"
                className={activeSection === 'settings' ? 'rail-item rail-settings is-active' : 'rail-item rail-settings'}
                aria-current={activeSection === 'settings' ? 'page' : undefined}
                aria-label="设置"
                onClick={() => onSelect('settings')}
            >
                <Settings aria-hidden="true" />
                <span>设置</span>
            </button>
        </nav>
    );
}
