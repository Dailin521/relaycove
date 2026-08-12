export interface ServerSettings {
    zulipVersion: string;
    zulipFeatureLevel: number;
    isIncompatible: boolean;
    emailAuthenticationEnabled: boolean;
}

export interface ApiKeyCredential {
    realm: string;
    email: string;
    apiKey: string;
    userId?: number;
}

export interface LoginRequest {
    realm: string;
    email: string;
    password: string;
    remember: boolean;
}

export interface WebSession extends ApiKeyCredential {
    userId: number;
    fullName: string;
    remember: boolean;
}

export type FetchTransport = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
