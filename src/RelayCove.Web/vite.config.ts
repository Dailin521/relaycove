import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

export default defineConfig(({ command, mode, isPreview }) => {
    const fixtureRuntime = (command === 'serve' && !isPreview) || mode === 'e2e';
    const runtimeApp = fixtureRuntime ? './src/fixtures/FixtureRuntimeApp.tsx' : './src/App.tsx';
    const base = (command === 'build' || isPreview) && mode !== 'e2e' ? '/relaycove-web/' : '/';

    return {
        base,
        plugins: [react()],
        resolve: {
            alias: {
                '@relaycove/runtime-app': fileURLToPath(new URL(runtimeApp, import.meta.url)),
            },
        },
        build: {
            outDir: mode === 'e2e' ? 'dist-e2e' : 'dist',
            emptyOutDir: true,
            sourcemap: false,
        },
        server: {
            host: '127.0.0.1',
            strictPort: true,
        },
        preview: {
            host: '127.0.0.1',
            strictPort: true,
        },
        test: {
            environment: 'jsdom',
            include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
            setupFiles: ['./src/test/setup.ts'],
            restoreMocks: true,
            clearMocks: true,
        },
    };
});
