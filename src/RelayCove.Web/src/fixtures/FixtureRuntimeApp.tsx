import { App as ProductionApp } from '../App';
import { FixtureApp } from './FixtureApp';

export function App() {
    const fixtureRequested = new URLSearchParams(window.location.search).get('fixture') === 'chat';
    return fixtureRequested ? <FixtureApp /> : <ProductionApp />;
}
