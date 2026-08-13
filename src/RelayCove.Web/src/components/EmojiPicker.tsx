import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

interface EmojiPickerProps {
    trigger: HTMLElement;
    onSelect(choice: EmojiChoice): void;
    onClose(restoreFocus?: boolean): void;
    title?: string;
    description?: string;
    ariaLabel?: string;
}

export interface EmojiChoice {
    emoji: string;
    label: string;
    emojiName: string;
    emojiCode: string;
    reactionType: 'unicode_emoji';
}

export const emojiChoices: readonly EmojiChoice[] = [
    ['😀', '开心', 'grinning', '1f600'], ['😄', '大笑', 'smile', '1f604'],
    ['😂', '笑哭', 'joy', '1f602'], ['🥰', '喜爱', 'smiling_face_with_3_hearts', '1f970'],
    ['😍', '喜欢', 'heart_eyes', '1f60d'], ['🤔', '思考', 'thinking', '1f914'],
    ['👍', '赞', '+1', '1f44d'], ['👎', '不赞同', '-1', '1f44e'],
    ['👏', '鼓掌', 'clap', '1f44f'], ['🙌', '庆祝', 'raised_hands', '1f64c'],
    ['🎉', '派对', 'tada', '1f389'], ['❤️', '爱心', 'heart', '2764'],
    ['🔥', '火热', 'fire', '1f525'], ['✅', '完成', 'check', '2705'],
    ['👀', '关注', 'eyes', '1f440'], ['😭', '大哭', 'sob', '1f62d'],
    ['😅', '汗颜', 'sweat_smile', '1f605'], ['😮', '惊讶', 'open_mouth', '1f62e'],
    ['🙏', '感谢', 'pray', '1f64f'], ['💪', '加油', 'muscle', '1f4aa'],
    ['🚀', '起飞', 'rocket', '1f680'], ['💡', '想法', 'bulb', '1f4a1'],
    ['🎯', '目标', 'dart', '1f3af'], ['✨', '闪亮', 'sparkles', '2728'],
].map(([emoji, label, emojiName, emojiCode]) => ({
    emoji,
    label,
    emojiName,
    emojiCode,
    reactionType: 'unicode_emoji' as const,
}));

export function EmojiPicker({
    trigger,
    onSelect,
    onClose,
    title = '表情',
    description = '选择后插入输入框',
    ariaLabel = '选择表情',
}: EmojiPickerProps) {
    const pickerRef = useRef<HTMLDivElement>(null);
    const [position, setPosition] = useState({ left: 8, top: 8 });

    useLayoutEffect(() => {
        const picker = pickerRef.current;
        if (!picker) {
            return;
        }
        const triggerBounds = trigger.getBoundingClientRect();
        const pickerBounds = picker.getBoundingClientRect();
        setPosition({
            left: Math.max(8, Math.min(triggerBounds.left, window.innerWidth - pickerBounds.width - 8)),
            top: Math.max(8, triggerBounds.top - pickerBounds.height - 8),
        });
        picker.querySelector<HTMLButtonElement>('button')?.focus();
    }, [trigger]);

    useEffect(() => {
        function handlePointerDown(event: PointerEvent) {
            const target = event.target as Node;
            if (!pickerRef.current?.contains(target) && !trigger.contains(target)) {
                onClose(false);
            }
        }
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape') {
                event.preventDefault();
                onClose();
            }
        }
        window.addEventListener('pointerdown', handlePointerDown, true);
        window.addEventListener('keydown', handleKeyDown);
        return () => {
            window.removeEventListener('pointerdown', handlePointerDown, true);
            window.removeEventListener('keydown', handleKeyDown);
        };
    }, [onClose, trigger]);

    function moveFocus(event: React.KeyboardEvent<HTMLDivElement>) {
        const buttons = [...(pickerRef.current?.querySelectorAll<HTMLButtonElement>('button') ?? [])];
        const current = buttons.indexOf(document.activeElement as HTMLButtonElement);
        const moves: Record<string, number> = {
            ArrowLeft: -1,
            ArrowRight: 1,
            ArrowUp: -6,
            ArrowDown: 6,
        };
        if (event.key === 'Home') {
            event.preventDefault();
            buttons[0]?.focus();
        } else if (event.key === 'End') {
            event.preventDefault();
            buttons.at(-1)?.focus();
        } else if (moves[event.key] !== undefined && buttons.length > 0) {
            event.preventDefault();
            const next = Math.max(0, Math.min(buttons.length - 1, current + moves[event.key]!));
            buttons[next]?.focus();
        }
    }

    const theme = document.querySelector('.relaycove-app')?.getAttribute('data-theme') ?? 'light';
    return createPortal(
        <div
            ref={pickerRef}
            className="emoji-picker"
            data-theme={theme}
            role="dialog"
            aria-label={ariaLabel}
            style={position}
            onKeyDown={moveFocus}
        >
            <header>
                <strong>{title}</strong>
                <span>{description}</span>
            </header>
            <div className="emoji-grid">
                {emojiChoices.map((choice) => (
                    <button
                        key={choice.emoji}
                        type="button"
                        aria-label={`${choice.label} ${choice.emoji}`}
                        title={choice.label}
                        onClick={() => onSelect(choice)}
                    >
                        {choice.emoji}
                    </button>
                ))}
            </div>
        </div>,
        document.body,
    );
}
