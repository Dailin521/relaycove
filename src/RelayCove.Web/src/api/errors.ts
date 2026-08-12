export type ZulipWebErrorCode =
    | 'invalid_realm'
    | 'realm_unavailable'
    | 'realm_incompatible'
    | 'email_auth_unavailable'
    | 'authentication_failed'
    | 'invalid_response'
    | 'unauthorized'
    | 'rate_limited'
    | 'queue_expired'
    | 'network'
    | 'request_timed_out'
    | 'rejected'
    | 'protocol';

const errorMessages: Record<ZulipWebErrorCode, string> = {
    invalid_realm: 'Realm 必须是仅包含 HTTPS origin 的地址。',
    realm_unavailable: '无法连接到该 Realm，请检查地址和网络。',
    realm_incompatible: '该 Realm 与当前 RelayCove Web 不兼容。',
    email_auth_unavailable: '该 Realm 未启用邮箱密码登录。',
    authentication_failed: '邮箱或密码不正确。',
    invalid_response: 'Realm 返回了无法识别的响应。',
    unauthorized: '登录已失效，请重新登录。',
    rate_limited: 'Realm 暂时限制了请求，请稍后重试。',
    queue_expired: '事件队列已过期。',
    network: '网络连接中断。',
    request_timed_out: '请求等待超时。',
    rejected: 'Realm 拒绝了该操作。',
    protocol: 'Realm 返回了当前客户端无法处理的数据。',
};

export class ZulipWebError extends Error {
    public readonly code: ZulipWebErrorCode;
    public readonly status?: number;
    public readonly retryAfterMs?: number;

    public constructor(code: ZulipWebErrorCode, status?: number, retryAfterMs?: number) {
        super(errorMessages[code]);
        this.name = 'ZulipWebError';
        this.code = code;
        this.status = status;
        this.retryAfterMs = retryAfterMs;
    }
}

export function toSafeLoginMessage(error: unknown): string {
    return error instanceof ZulipWebError
        ? error.message
        : errorMessages.realm_unavailable;
}
