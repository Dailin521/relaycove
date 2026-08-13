import { Download, FileText, RotateCw } from 'lucide-react';
import type { MessageAttachment } from '../models/ui';
import { useRealmFileDownload } from './RealmMedia';

export function MessageFile({ attachment }: { attachment: MessageAttachment }) {
    const file = useRealmFileDownload(attachment.sourceUrl);
    return (
        <button
            className={file.error ? 'message-file is-error' : 'message-file'}
            type="button"
            disabled={file.loading}
            aria-label={file.loading
                ? `正在下载附件 ${attachment.name}`
                : file.error ? `重试下载附件 ${attachment.name}` : `下载附件 ${attachment.name}`}
            onClick={() => void file.download(attachment.name)}
        >
            <span className="message-file-icon"><FileText aria-hidden="true" /></span>
            <span>
                <strong>{attachment.name}</strong>
                <small>{file.loading ? '正在安全下载…' : file.error ? '下载失败，点击重试' : '附件 · 点击下载'}</small>
            </span>
            {file.error ? <RotateCw aria-hidden="true" /> : <Download aria-hidden="true" />}
        </button>
    );
}
