import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, resolve, sep } from 'node:path';

const host = '127.0.0.1';
const port = 4174;
const basePath = '/relaycove-web/';
const root = resolve('dist');
const rootPrefix = `${root}${sep}`;
const securityHeaders = {
    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; connect-src 'self' https:; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=()',
    'Referrer-Policy': 'no-referrer',
    'X-Content-Type-Options': 'nosniff',
    'X-Frame-Options': 'DENY',
};
const contentTypes = {
    '.css': 'text/css; charset=utf-8',
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.png': 'image/png',
    '.svg': 'image/svg+xml',
    '.woff2': 'font/woff2',
};

function sendEmpty(response, status, extraHeaders = {}) {
    response.writeHead(status, { ...securityHeaders, ...extraHeaders });
    response.end();
}

async function sendFile(response, filePath, immutable = false) {
    const details = await stat(filePath);
    if (!details.isFile()) {
        sendEmpty(response, 404);
        return;
    }

    response.writeHead(200, {
        ...securityHeaders,
        'Cache-Control': immutable
            ? 'public, max-age=31536000, immutable'
            : 'no-cache, must-revalidate',
        'Content-Length': String(details.size),
        'Content-Type': contentTypes[extname(filePath)] ?? 'application/octet-stream',
    });
    createReadStream(filePath).pipe(response);
}

const server = createServer(async (request, response) => {
    try {
        const pathname = new URL(request.url ?? '/', `http://${host}:${port}`).pathname;
        if (pathname === basePath.slice(0, -1)) {
            sendEmpty(response, 308, { Location: basePath });
            return;
        }
        if (!pathname.startsWith(basePath)) {
            sendEmpty(response, 404);
            return;
        }

        const relativePath = decodeURIComponent(pathname.slice(basePath.length));
        const candidate = resolve(root, relativePath || 'index.html');
        if (candidate !== root && !candidate.startsWith(rootPrefix)) {
            sendEmpty(response, 404);
            return;
        }

        try {
            await sendFile(response, candidate, pathname.startsWith(`${basePath}assets/`));
        } catch (error) {
            if (error?.code !== 'ENOENT') {
                throw error;
            }
            if (pathname.startsWith(`${basePath}assets/`)) {
                sendEmpty(response, 404);
                return;
            }
            await sendFile(response, resolve(root, 'index.html'));
        }
    } catch {
        sendEmpty(response, 400);
    }
});

server.listen(port, host, () => {
    process.stdout.write(`RelayCove deploy preview: http://${host}:${port}${basePath}\n`);
});
