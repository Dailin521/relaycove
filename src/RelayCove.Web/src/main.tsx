import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from '@relaycove/runtime-app';
import './styles/tokens.css';
import './styles/global.css';
import './styles/app.css';

const root = document.getElementById('root');
if (!root) {
    throw new Error('RelayCove root element is missing.');
}

createRoot(root).render(
    <StrictMode>
        <App />
    </StrictMode>,
);
