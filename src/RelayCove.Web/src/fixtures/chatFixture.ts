import type { ConversationDetail, PersonSummary, WorkspaceViewState } from '../models/ui';

const lin: PersonSummary = { id: 'lin', name: '林远', initials: '林', tone: 'blue' };
const maya: PersonSummary = { id: 'maya', name: 'Maya Chen', initials: 'MC', tone: 'orange', avatarUrl: 'https://fixture.invalid/user_avatars/maya.svg' };
const alex: PersonSummary = { id: 'alex', name: 'Alex Wu', initials: 'AW', tone: 'green', avatarUrl: 'https://fixture.invalid/user_avatars/alex.svg' };
const daniel: PersonSummary = { id: 'daniel', name: 'Daniel Okafor', initials: 'DO', tone: 'slate', avatarUrl: 'https://fixture.invalid/user_avatars/daniel.svg' };
const sarah: PersonSummary = { id: 'sarah', name: 'Sarah Li', initials: 'SL', tone: 'violet', avatarUrl: 'https://fixture.invalid/user_avatars/sarah.svg' };

const uiDesign: ConversationDetail = {
    id: 'design',
    kind: 'channel',
    title: 'UI 设计讨论',
    subtitle: '# design · Maya：顶部控件需要收敛…',
    time: '刚刚',
    unread: 0,
    pinned: true,
    channelName: 'design',
    topic: 'UI 设计讨论',
    avatar: '#',
    tone: 'blue',
    messages: [
        {
            id: 'm1',
            sender: maya,
            sentAt: '09:28',
            body: '顶部按微信逻辑收敛，只保留置顶、最小化、最大化和关闭。',
        },
        {
            id: 'm2',
            sender: maya,
            sentAt: '09:29',
            body: '中栏不要再加额外工具区，只要搜索、新建，以及清楚区分的频道和私信列表。',
            reaction: '👍 3',
        },
        {
            id: 'm3',
            sender: lin,
            sentAt: '09:41',
            body: '可以。私信区直接列出当前工作区可靠获知的成员，频道仍保留话题上下文。',
            own: true,
            quote: {
                sender: 'Maya Chen',
                body: '只要搜索、新建，以及频道和私信列表',
            },
        },
        {
            id: 'm4',
            sender: alex,
            sentAt: '09:46',
            body: '我会让失败发送保持显式恢复，不做自动重试。',
            attachments: [{
                kind: 'image',
                name: 'relaycove-team-avatars.png',
                sourceUrl: 'https://fixture.invalid/user_uploads/relaycove-team-avatars.png',
            }],
        },
    ],
    dateLabel: '今天 09:28',
    unreadSeparatorAfter: 2,
    unreadSeparatorText: '4 条未读消息',
};

const windowsClient: ConversationDetail = {
    id: 'engineering',
    kind: 'channel',
    title: 'Windows 客户端',
    subtitle: '# engineering · Alex：构建已通过',
    time: '10:42',
    unread: 5,
    channelName: 'engineering',
    topic: 'Windows 客户端',
    avatar: '▣',
    tone: 'blue',
    messages: [
        { id: 'e1', sender: alex, sentAt: '10:39', body: '类型检查和生产构建已经纳入本地验证。' },
        { id: 'e2', sender: lin, sentAt: '10:42', body: '收到，浏览器测试继续只使用 mock HTTP。', own: true },
    ],
};

const product: ConversationDetail = {
    id: 'product', kind: 'channel', title: '产品路线图',
    subtitle: '# product · @你 Stage 22W 范围', time: '昨天', unread: 2,
    channelName: 'product', topic: '产品路线图', avatar: '⌘', tone: 'orange',
    messages: [{ id: 'p1', sender: sarah, sentAt: '17:40', body: 'Web 是正式产品，MAUI 在交互版本冻结后原生复刻。' }],
};

const release: ConversationDetail = {
    id: 'release', kind: 'channel', title: '版本发布',
    subtitle: '# release · Web Slice 1 检查清单', time: '周日', unread: 0,
    channelName: 'release', topic: '版本发布', avatar: '◇', tone: 'violet',
    messages: [{ id: 'r1', sender: daniel, sentAt: '18:22', body: '本轮不推送、不发布，也不触发真实消息写入。' }],
};

function direct(
    id: string,
    person: PersonSummary,
    body: string,
    time: string,
    unread = 0,
    online = false,
): ConversationDetail {
    return {
        id,
        kind: id === 'self' ? 'self' : 'direct',
        title: id === 'self' ? `${person.name}（自己）` : person.name,
        subtitle: body,
        time,
        unread,
        online,
        avatar: person.initials,
        tone: person.tone,
        avatarUrl: person.avatarUrl,
        isBot: person.isBot,
        messages: [{ id: `${id}-1`, sender: person, sentAt: time, body, own: id === 'self' }],
    };
}

const mayaDm = direct('maya', maya, '我把下一轮检查项整理好了', '09:56', 0, true);
const alexDm = direct('alex', alex, '发送状态那块可以开始联调', '09:31', 1);
const danielDm = direct('daniel', daniel, '今晚跑一轮 Windows 11 验收', '昨天');
const sarahDm = direct('sarah', sarah, '范围说明我补到文档里了', '昨天');
const selfDm = direct('self', lin, '备忘：逐个审查设置和失败状态', '周日');

const allConversations = [
    uiDesign,
    windowsClient,
    product,
    release,
    mayaDm,
    alexDm,
    danielDm,
    sarahDm,
    selfDm,
];

export const chatFixture: WorkspaceViewState = {
    workspaceName: 'Acme Workspace',
    currentUser: lin,
    channels: [uiDesign, windowsClient, product, release],
    directs: [mayaDm, alexDm, danielDm, sarahDm, selfDm],
    conversations: Object.fromEntries(allConversations.map((conversation) => [conversation.id, conversation])),
    selectedConversationId: uiDesign.id,
};
