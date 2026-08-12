import {
    createContext,
    type ReactNode,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
} from 'react';
import type { RealmMediaKind } from '../api/realmMedia';

export type RealmImageLoader = (
    sourceUrl: string,
    kind: RealmMediaKind,
    signal: AbortSignal,
) => Promise<Blob>;

interface MediaEntry {
    controller: AbortController;
    promise: Promise<string>;
    objectUrl?: string;
    byteSize?: number;
    references: number;
}

interface PendingLoad {
    controller: AbortController;
    run(): void;
    reject(error: unknown): void;
}

export const MAX_CONCURRENT_REALM_MEDIA_LOADS = 4;
export const MAX_CACHED_REALM_MEDIA_BYTES = 64 * 1024 * 1024;

interface RealmMediaCache {
    acquire(sourceUrl: string, kind: RealmMediaKind): Promise<string>;
    release(sourceUrl: string, kind: RealmMediaKind): void;
}

const RealmMediaContext = createContext<RealmMediaCache | undefined>(undefined);

export function RealmMediaProvider({
    children,
    loader,
}: {
    children: ReactNode;
    loader?: RealmImageLoader;
}) {
    const entries = useRef(new Map<string, MediaEntry>());
    const activeLoads = useRef(0);
    const pendingLoads = useRef<PendingLoad[]>([]);
    const cachedBytes = useRef(0);
    const cache = useMemo<RealmMediaCache | undefined>(() => {
        if (!loader) {
            return undefined;
        }
        const keyFor = (sourceUrl: string, kind: RealmMediaKind) => `${kind}:${sourceUrl}`;
        const startNext = () => {
            while (activeLoads.current < MAX_CONCURRENT_REALM_MEDIA_LOADS && pendingLoads.current.length > 0) {
                const pending = pendingLoads.current.shift()!;
                if (pending.controller.signal.aborted) {
                    pending.reject(new DOMException('Aborted', 'AbortError'));
                    continue;
                }
                activeLoads.current += 1;
                pending.run();
            }
        };
        const load = (sourceUrl: string, kind: RealmMediaKind, controller: AbortController) => (
            new Promise<Blob>((resolve, reject) => {
                const pending: PendingLoad = {
                    controller,
                    reject,
                    run: () => {
                        void loader(sourceUrl, kind, controller.signal).then(resolve, reject).finally(() => {
                            activeLoads.current = Math.max(0, activeLoads.current - 1);
                            startNext();
                        });
                    },
                };
                const abortQueued = () => {
                    const index = pendingLoads.current.indexOf(pending);
                    if (index >= 0) {
                        pendingLoads.current.splice(index, 1);
                        reject(new DOMException('Aborted', 'AbortError'));
                    }
                };
                controller.signal.addEventListener('abort', abortQueued, { once: true });
                pendingLoads.current.push(pending);
                startNext();
            })
        );
        return {
            acquire(sourceUrl, kind) {
                const key = keyFor(sourceUrl, kind);
                const existing = entries.current.get(key);
                if (existing) {
                    existing.references += 1;
                    return existing.promise;
                }

                const controller = new AbortController();
                const entry: MediaEntry = {
                    controller,
                    references: 1,
                    promise: Promise.resolve(''),
                };
                entry.promise = load(sourceUrl, kind, controller).then((blob) => {
                    if (controller.signal.aborted || entries.current.get(key) !== entry) {
                        throw new DOMException('Aborted', 'AbortError');
                    }
                    if (cachedBytes.current + blob.size > MAX_CACHED_REALM_MEDIA_BYTES) {
                        throw new Error('Realm media cache budget exceeded.');
                    }
                    const objectUrl = URL.createObjectURL(blob);
                    entry.objectUrl = objectUrl;
                    entry.byteSize = blob.size;
                    cachedBytes.current += blob.size;
                    return objectUrl;
                }).catch((error: unknown) => {
                    if (entries.current.get(key) === entry) {
                        entries.current.delete(key);
                    }
                    throw error;
                });
                entries.current.set(key, entry);
                return entry.promise;
            },
            release(sourceUrl, kind) {
                const key = keyFor(sourceUrl, kind);
                const entry = entries.current.get(key);
                if (!entry) {
                    return;
                }
                entry.references = Math.max(0, entry.references - 1);
                if (entry.references > 0) {
                    return;
                }
                entries.current.delete(key);
                entry.controller.abort();
                if (entry.objectUrl) {
                    URL.revokeObjectURL(entry.objectUrl);
                }
                cachedBytes.current = Math.max(0, cachedBytes.current - (entry.byteSize ?? 0));
            },
        };
    }, [loader]);

    useEffect(() => () => {
        for (const entry of entries.current.values()) {
            entry.controller.abort();
            if (entry.objectUrl) {
                URL.revokeObjectURL(entry.objectUrl);
            }
        }
        entries.current.clear();
        for (const pending of pendingLoads.current.splice(0)) {
            pending.controller.abort();
            pending.reject(new DOMException('Aborted', 'AbortError'));
        }
        cachedBytes.current = 0;
    }, [loader]);

    return <RealmMediaContext.Provider value={cache}>{children}</RealmMediaContext.Provider>;
}

export function useRealmImage(sourceUrl: string | undefined, kind: RealmMediaKind) {
    const cache = useContext(RealmMediaContext);
    const [retryVersion, setRetryVersion] = useState(0);
    const [state, setState] = useState<{
        sourceUrl?: string;
        objectUrl?: string;
        status: 'idle' | 'loading' | 'loaded' | 'error';
    }>({ status: 'idle' });

    useEffect(() => {
        if (!sourceUrl || !cache) {
            setState({ sourceUrl, status: 'idle' });
            return;
        }
        let active = true;
        setState({ sourceUrl, status: 'loading' });
        void cache.acquire(sourceUrl, kind).then((objectUrl) => {
            if (active) {
                setState({ sourceUrl, objectUrl, status: 'loaded' });
            }
        }).catch((error: unknown) => {
            if (active && !(error instanceof DOMException && error.name === 'AbortError')) {
                setState({ sourceUrl, status: 'error' });
            }
        });
        return () => {
            active = false;
            cache.release(sourceUrl, kind);
        };
    }, [cache, kind, retryVersion, sourceUrl]);

    return {
        objectUrl: state.sourceUrl === sourceUrl ? state.objectUrl : undefined,
        status: state.sourceUrl === sourceUrl ? state.status : 'loading',
        retry: useCallback(() => setRetryVersion((value) => value + 1), []),
    };
}
