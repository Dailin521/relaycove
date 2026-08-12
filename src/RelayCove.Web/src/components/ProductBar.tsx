import { Maximize2, Minus, Moon, Pin, Square, Sun, X } from 'lucide-react';
import { useState } from 'react';
import type { Theme } from '../models/ui';

interface ProductBarProps {
    workspaceName: string;
    theme: Theme;
    onThemeChange(theme: Theme): void;
}

export function ProductBar({ workspaceName, theme, onThemeChange }: ProductBarProps) {
    const [pinned, setPinned] = useState(false);
    const [maximized, setMaximized] = useState(false);

    return (
        <header className="product-bar">
            <span className="product-mark" aria-hidden="true">R</span>
            <strong>RelayCove</strong>
            <button className="workspace-button" type="button" aria-label={`当前工作区：${workspaceName}`}>
                {workspaceName}
            </button>
            <span className="product-bar-spacer" />
            <button
                className="window-action theme-action"
                type="button"
                aria-label={theme === 'light' ? '切换到深色主题' : '切换到浅色主题'}
                onClick={() => onThemeChange(theme === 'light' ? 'dark' : 'light')}
            >
                {theme === 'light' ? <Moon aria-hidden="true" /> : <Sun aria-hidden="true" />}
            </button>
            <button
                className={pinned ? 'window-action is-pressed' : 'window-action'}
                type="button"
                aria-label="置顶视觉状态"
                aria-pressed={pinned}
                onClick={() => setPinned((value) => !value)}
                title="Web 中仅保留已确认的置顶状态外观"
            >
                <Pin aria-hidden="true" />
            </button>
            <button className="window-action" type="button" aria-label="最小化不可用于浏览器" title="由浏览器窗口管理" aria-disabled="true">
                <Minus aria-hidden="true" />
            </button>
            <button
                className={maximized ? 'window-action is-pressed' : 'window-action'}
                type="button"
                aria-label={maximized ? '还原视觉状态' : '最大化视觉状态'}
                aria-pressed={maximized}
                onClick={() => setMaximized((value) => !value)}
                title="Web 中仅保留窗口控制外观"
            >
                {maximized ? <Maximize2 aria-hidden="true" /> : <Square aria-hidden="true" />}
            </button>
            <button className="window-action close-action" type="button" aria-label="关闭不可用于浏览器" title="由浏览器窗口管理" aria-disabled="true">
                <X aria-hidden="true" />
            </button>
        </header>
    );
}
