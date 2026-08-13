import { describe, expect, it } from 'vitest';
import type { WebSession } from '../api/types';
import type { UserProfile } from '../domain/types';
import { initialWebClientState } from '../state/webClientReducer';
import { projectWebClient } from './projectWorkspace';

const session: WebSession = {
    realm: 'https://chat.example.test',
    email: 'ada@example.test',
    apiKey: 'test-api-key',
    userId: 7,
    fullName: 'Ada Lovelace',
    remember: true,
};

const currentUser: UserProfile = {
    userId: session.userId,
    fullName: session.fullName,
    email: session.email,
    isActive: true,
};

describe('projectWebClient', () => {
    it('always exposes the authenticated self-DM without fixture or server history', () => {
        const projected = projectWebClient(session, {
            ...initialWebClientState,
            currentUser,
        });

        expect(projected.workspace.directs).toContainEqual(expect.objectContaining({
            id: 'dm:self',
            kind: 'self',
            title: 'Ada Lovelace（自己）',
        }));
        expect(projected.workspace.conversations['dm:self']).toEqual(expect.objectContaining({
            id: 'dm:self',
            messages: [],
        }));
    });
});
