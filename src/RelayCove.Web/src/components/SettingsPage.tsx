import { Bell, Database, LogOut, MonitorCog, Palette, RotateCcw, UserRound, X } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { SessionSummary, Theme } from '../models/ui';

interface SettingsPageProps {
    session: SessionSummary;
    theme: Theme;
    fontSize: number;
    listWidth: number;
    detailsDefault: boolean;
    onThemeChange(theme: Theme): void;
    onFontSizeChange(value: number): void;
    onListWidthChange(value: number): void;
    onDetailsDefaultChange(value: boolean): void;
    onLogout(): void;
}

export function SettingsPage(props: SettingsPageProps) {
    const [confirmLogout, setConfirmLogout] = useState(false);
    const [activeTab, setActiveTab] = useState<'appearance' | 'general' | 'notifications' | 'storage' | 'account'>('appearance');
    const logoutTriggerRef = useRef<HTMLButtonElement>(null);
    const logoutDialogRef = useRef<HTMLElement>(null);
    const logoutCloseRef = useRef<HTMLButtonElement>(null);
    const closeLogout = useCallback(() => {
        setConfirmLogout(false);
        window.requestAnimationFrame(() => logoutTriggerRef.current?.focus());
    }, []);

    useEffect(() => {
        if (!confirmLogout) {
            return;
        }
        logoutCloseRef.current?.focus();
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape') {
                event.preventDefault();
                closeLogout();
                return;
            }
            if (event.key !== 'Tab') {
                return;
            }
            const items = [...(logoutDialogRef.current?.querySelectorAll<HTMLElement>('button') ?? [])];
            if (items.length === 0) {
                return;
            }
            const first = items[0]!;
            const last = items.at(-1)!;
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        }
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [closeLogout, confirmLogout]);

    return (
        <main className="settings-page" aria-labelledby="settings-title">
            <aside className="settings-sidebar" aria-label="设置分类">
                <h2 id="settings-title">设置</h2>
                <button className={activeTab === 'appearance' ? 'is-active' : ''} type="button" onClick={() => setActiveTab('appearance')}><Palette aria-hidden="true" />外观</button>
                <button className={activeTab === 'general' ? 'is-active' : ''} type="button" onClick={() => setActiveTab('general')}><MonitorCog aria-hidden="true" />通用</button>
                <button className={activeTab === 'notifications' ? 'is-active' : ''} type="button" onClick={() => setActiveTab('notifications')}><Bell aria-hidden="true" />通知</button>
                <button className={activeTab === 'storage' ? 'is-active' : ''} type="button" onClick={() => setActiveTab('storage')}><Database aria-hidden="true" />存储</button>
                <button className={activeTab === 'account' ? 'is-active' : ''} type="button" onClick={() => setActiveTab('account')}><UserRound aria-hidden="true" />账户</button>
            </aside>
            <div className="settings-content">
                {activeTab === 'appearance' && (
                    <>
                        <header>
                            <div>
                                <p className="eyebrow">APPEARANCE</p>
                                <h1>外观与布局</h1>
                                <p>这些是设备偏好，不进入 Zulip 业务状态。</p>
                            </div>
                            <button
                                className="secondary-button"
                                type="button"
                                onClick={() => {
                                    props.onThemeChange('light');
                                    props.onFontSizeChange(14);
                                    props.onListWidthChange(310);
                                    props.onDetailsDefaultChange(false);
                                }}
                            >
                                <RotateCcw aria-hidden="true" />恢复默认
                            </button>
                        </header>
                        <section className="settings-card">
                            <div className="setting-row">
                                <span><strong>主题</strong><small>立即预览浅色或深色外观</small></span>
                                <div className="theme-segment" role="group" aria-label="主题">
                                    <button className={props.theme === 'light' ? 'is-selected' : ''} type="button" aria-pressed={props.theme === 'light'} onClick={() => props.onThemeChange('light')}>浅色</button>
                                    <button className={props.theme === 'dark' ? 'is-selected' : ''} type="button" aria-pressed={props.theme === 'dark'} onClick={() => props.onThemeChange('dark')}>深色</button>
                                </div>
                            </div>
                            <label className="setting-row">
                                <span><strong>正文字号</strong><small>{props.fontSize} px</small></span>
                                <input type="range" min="13" max="16" step="1" value={props.fontSize} onChange={(event) => props.onFontSizeChange(Number(event.target.value))} />
                            </label>
                            <label className="setting-row">
                                <span><strong>会话栏宽度</strong><small>{props.listWidth} px</small></span>
                                <input type="range" min="270" max="370" step="10" value={props.listWidth} onChange={(event) => props.onListWidthChange(Number(event.target.value))} />
                            </label>
                            <label className="setting-row">
                                <span><strong>默认显示详情</strong><small>1024×768 下仍默认收起</small></span>
                                <input type="checkbox" checked={props.detailsDefault} onChange={(event) => props.onDetailsDefaultChange(event.target.checked)} />
                            </label>
                        </section>
                    </>
                )}
                {activeTab === 'general' && <CapabilitySettings eyebrow="GENERAL" title="通用" text="RelayCove.Web 直接连接当前 Zulip Realm。事件队列、游标和消息投影只保存在本页会话内。" />}
                {activeTab === 'notifications' && <CapabilitySettings eyebrow="NOTIFICATIONS" title="通知" text="浏览器通知尚未进入已验证能力门；当前不会伪造通知成功状态。" />}
                {activeTab === 'storage' && <CapabilitySettings eyebrow="STORAGE" title="本地存储" text="当前只持久保存经你选择的登录凭据和外观偏好；消息、事件队列、游标与 outbox 不写入浏览器持久缓存。" />}
                {activeTab === 'account' && (
                    <>
                        <header>
                            <div><p className="eyebrow">ACCOUNT</p><h1>账户</h1><p>账户事实来自当前 Zulip Realm。</p></div>
                        </header>
                        <section className="settings-card account-card">
                            <h2>当前账户</h2>
                            <dl>
                                <div><dt>Realm</dt><dd>{props.session.realm}</dd></div>
                                <div><dt>邮箱</dt><dd>{props.session.email}</dd></div>
                                <div><dt>API Key</dt><dd>已隐藏，永不显示或写入 URL</dd></div>
                            </dl>
                            <button ref={logoutTriggerRef} className="danger-button" type="button" onClick={() => setConfirmLogout(true)}>
                                <LogOut aria-hidden="true" />注销并清除本地凭据
                            </button>
                        </section>
                    </>
                )}
            </div>
            {confirmLogout && (
                <div className="dialog-backdrop" role="presentation" onPointerDown={(event) => { if (event.target === event.currentTarget) closeLogout(); }}>
                    <section ref={logoutDialogRef} className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="logout-dialog-title">
                        <button ref={logoutCloseRef} className="dialog-close" type="button" aria-label="关闭注销确认" onClick={closeLogout}><X aria-hidden="true" /></button>
                        <h2 id="logout-dialog-title">确认注销？</h2>
                        <p>这会清除此浏览器中的 Realm、邮箱和 API Key。不会删除 Zulip 消息。</p>
                        <div>
                            <button className="secondary-button" type="button" onClick={closeLogout}>取消</button>
                            <button className="danger-button" type="button" onClick={props.onLogout}>确认注销</button>
                        </div>
                    </section>
                </div>
            )}
        </main>
    );
}

function CapabilitySettings({ eyebrow, title, text }: { eyebrow: string; title: string; text: string }) {
    return (
        <>
            <header><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p>{text}</p></div></header>
            <section className="settings-card account-card"><p>{text}</p></section>
        </>
    );
}
