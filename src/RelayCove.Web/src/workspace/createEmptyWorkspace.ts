import type { SessionSummary, WorkspaceViewState } from '../models/ui';

export function createEmptyWorkspace(session: SessionSummary): WorkspaceViewState {
    const host = new URL(session.realm).host;
    return {
        workspaceName: host,
        currentUser: {
            id: 'current-user',
            name: session.email.split('@')[0] || session.email,
            initials: (session.email[0] || 'R').toUpperCase(),
            tone: 'blue',
        },
        channels: [],
        directs: [],
        conversations: {},
    };
}
