import { beforeEach, describe, expect, it } from 'vitest';
import { defaultWebPreferences, readWebPreferences, writeWebPreferences } from './WebPreferenceStore';

describe('WebPreferenceStore', () => {
    beforeEach(() => localStorage.clear());

    it('persists only non-sensitive appearance preferences', () => {
        writeWebPreferences({ theme: 'dark', fontSize: 16, listWidth: 350, detailsDefault: true });

        expect(readWebPreferences()).toEqual({ theme: 'dark', fontSize: 16, listWidth: 350, detailsDefault: true });
        expect(localStorage.getItem('relaycove.web.preferences.v1')).not.toContain('apiKey');
    });

    it('removes malformed preference state and uses defaults', () => {
        localStorage.setItem('relaycove.web.preferences.v1', '{"version":1,"theme":"system"}');

        expect(readWebPreferences()).toEqual(defaultWebPreferences);
        expect(localStorage.getItem('relaycove.web.preferences.v1')).toBeNull();
    });
});
