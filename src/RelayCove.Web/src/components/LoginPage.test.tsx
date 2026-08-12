import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DEFAULT_REALM } from '../api/realm';
import { LoginPage } from './LoginPage';

describe('LoginPage', () => {
    it('defaults to the target Realm and remember-login enabled', () => {
        render(<LoginPage login={vi.fn()} onAuthenticated={vi.fn()} />);

        expect(screen.getByRole('textbox', { name: 'Realm' })).toHaveValue(DEFAULT_REALM);
        expect(screen.getByRole('checkbox', { name: '记住登录（默认）' })).toBeChecked();
        expect(screen.getByText(/正式 Web 客户端/u)).toBeVisible();
        expect(screen.queryByText(/只建立安全登录/u)).not.toBeInTheDocument();
    });

    it('hands credentials to the auth boundary without rendering the returned API key', async () => {
        const login = vi.fn(async () => ({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            apiKey: 'fake-api-key-not-for-ui',
            userId: 7,
            fullName: 'Ada Lovelace',
            remember: true,
        }));
        const onAuthenticated = vi.fn();
        render(<LoginPage login={login} onAuthenticated={onAuthenticated} />);

        fireEvent.change(screen.getByRole('textbox', { name: 'Realm' }), { target: { value: 'https://chat.example.test' } });
        fireEvent.change(screen.getByRole('textbox', { name: '邮箱' }), { target: { value: 'ada@example.test' } });
        fireEvent.change(screen.getByLabelText('密码', { exact: true }), { target: { value: 'fake-password' } });
        fireEvent.click(screen.getByRole('button', { name: '登录' }));

        await waitFor(() => expect(onAuthenticated).toHaveBeenCalledOnce());
        expect(login).toHaveBeenCalledWith({
            realm: 'https://chat.example.test',
            email: 'ada@example.test',
            password: 'fake-password',
            remember: true,
        });
        expect(screen.queryByText('fake-api-key-not-for-ui')).not.toBeInTheDocument();
    });
});
