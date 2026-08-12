export function createBasicAuthorization(email: string, apiKey: string): string {
    const bytes = new TextEncoder().encode(`${email}:${apiKey}`);
    let binary = '';
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return `Basic ${btoa(binary)}`;
}
