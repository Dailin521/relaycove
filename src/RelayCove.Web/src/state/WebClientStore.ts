import type { WebClientState } from '../domain/types';
import { initialWebClientState, webClientReducer, type WebClientAction } from './webClientReducer';

export class WebClientStore {
    private state: WebClientState = initialWebClientState;
    private readonly listeners = new Set<() => void>();

    public getSnapshot = (): WebClientState => this.state;

    public subscribe = (listener: () => void): (() => void) => {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    };

    public dispatch(action: WebClientAction): void {
        const next = webClientReducer(this.state, action);
        if (next === this.state) {
            return;
        }
        this.state = next;
        for (const listener of this.listeners) {
            listener();
        }
    }
}
