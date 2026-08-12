import { ZulipWebError } from '../api/errors';
import { normalizeRealm } from '../api/realm';
import type { LoginRequest, WebSession } from '../api/types';
import { ZulipApiClient } from '../api/ZulipApiClient';
import { WebCredentialStore } from './WebCredentialStore';

const MINIMUM_ZULIP_FEATURE_LEVEL = 500;

export class WebAuthService {
    private readonly apiClient: ZulipApiClient;
    private readonly credentialStore: WebCredentialStore;

    public constructor(
        apiClient: ZulipApiClient = new ZulipApiClient(),
        credentialStore: WebCredentialStore = new WebCredentialStore(),
    ) {
        this.apiClient = apiClient;
        this.credentialStore = credentialStore;
    }

    public restore(): WebSession | null {
        return this.credentialStore.restore();
    }

    public async login(request: LoginRequest, signal?: AbortSignal): Promise<WebSession> {
        const realm = normalizeRealm(request.realm);
        const settings = await this.apiClient.getServerSettings(realm, signal);
        if (settings.isIncompatible || settings.zulipFeatureLevel < MINIMUM_ZULIP_FEATURE_LEVEL) {
            throw new ZulipWebError('realm_incompatible');
        }
        if (!settings.emailAuthenticationEnabled) {
            throw new ZulipWebError('email_auth_unavailable');
        }

        const credential = await this.apiClient.fetchApiKey(
            realm,
            request.email,
            request.password,
            signal,
        );
        const currentUser = await this.apiClient.getCurrentUser(credential, signal);
        if (credential.userId !== undefined && credential.userId !== currentUser.userId) {
            throw new ZulipWebError('invalid_response');
        }
        const session: WebSession = {
            ...credential,
            userId: currentUser.userId,
            fullName: currentUser.fullName,
            remember: request.remember,
        };
        this.credentialStore.save(session);
        return session;
    }

    public logout(): void {
        this.credentialStore.clear();
    }
}
