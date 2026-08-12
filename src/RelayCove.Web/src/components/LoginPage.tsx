import { LockKeyhole, ShieldCheck } from 'lucide-react';
import { FormEvent, useState } from 'react';
import { toSafeLoginMessage } from '../api/errors';
import { DEFAULT_REALM } from '../api/realm';
import type { LoginRequest, WebSession } from '../api/types';

interface LoginPageProps {
    login(request: LoginRequest, signal?: AbortSignal): Promise<WebSession>;
    onAuthenticated(session: WebSession): void;
}

export function LoginPage({ login, onAuthenticated }: LoginPageProps) {
    const [realm, setRealm] = useState(DEFAULT_REALM);
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [remember, setRemember] = useState(true);
    const [submitting, setSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    async function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        setError(null);
        setSubmitting(true);
        try {
            const session = await login({ realm, email, password, remember });
            setPassword('');
            onAuthenticated(session);
        } catch (caught) {
            setError(toSafeLoginMessage(caught));
        } finally {
            setSubmitting(false);
        }
    }

    return (
        <main className="login-page">
            <section className="login-card" aria-labelledby="login-title">
                <div className="login-brand" aria-hidden="true">R</div>
                <p className="eyebrow">RELAYCOVE WEB</p>
                <h1 id="login-title">连接你的 Zulip Realm</h1>
                <p className="login-intro">RelayCove 正式 Web 客户端。登录后直接同步当前 Zulip Realm 的会话、消息、头像和图片。</p>
                <form onSubmit={handleSubmit}>
                    <label>
                        <span>Realm</span>
                        <input
                            name="realm"
                            type="url"
                            inputMode="url"
                            autoComplete="url"
                            value={realm}
                            onChange={(event) => setRealm(event.target.value)}
                            required
                        />
                    </label>
                    <label>
                        <span>邮箱</span>
                        <input
                            name="email"
                            type="email"
                            autoComplete="username"
                            value={email}
                            onChange={(event) => setEmail(event.target.value)}
                            required
                        />
                    </label>
                    <label>
                        <span>密码</span>
                        <input
                            name="password"
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            onChange={(event) => setPassword(event.target.value)}
                            required
                        />
                    </label>
                    <label className="remember-login">
                        <input
                            name="remember"
                            type="checkbox"
                            checked={remember}
                            onChange={(event) => setRemember(event.target.checked)}
                        />
                        <span>记住登录（默认）</span>
                    </label>
                    {error && <p className="login-error" role="alert">{error}</p>}
                    <button className="primary-button" type="submit" disabled={submitting}>
                        <LockKeyhole aria-hidden="true" />
                        {submitting ? '正在验证…' : '登录'}
                    </button>
                </form>
                <p className="credential-note">
                    <ShieldCheck aria-hidden="true" />
                    选择记住登录后，API Key 会保存在此浏览器本地；注销会清除本地凭据。
                </p>
            </section>
        </main>
    );
}
