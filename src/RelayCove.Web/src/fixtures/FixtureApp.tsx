import { RelayCoveShell } from '../components/RelayCoveShell';
import { chatFixture } from './chatFixture';
import { fixtureImageLoader } from './fixtureMedia';

export function FixtureApp() {
    return (
        <RelayCoveShell
            session={{ realm: 'https://fixture.invalid', email: 'fixture@relaycove.invalid' }}
            workspace={chatFixture}
            loadRealmImage={fixtureImageLoader}
            allowCrossOriginMediaLoader
            presentation={{
                conversationSearchEnabled: true,
                dataSourceNotice: '本地演示数据 · 不连接 Zulip',
                emptySearchText: '没有匹配的本地演示会话',
                composerStatusText: '演示草稿只保存在当前页面',
                sendEnabled: false,
            }}
            onLogout={() => {
                window.history.replaceState({}, '', window.location.pathname);
                window.location.reload();
            }}
        />
    );
}
