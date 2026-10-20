import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
// in cascade order: general first, then components and pages, which build on it
import './styles/base.css'
import './styles/layout.css'
import './styles/controls.css'
import './styles/table.css'
import './styles/cards.css'
import './styles/overlay.css'
import './styles/CodeEditor.css'
import './styles/container.css'
import './styles/LogView.css'
import './styles/registries.css'
import './styles/network.css'
import './styles/charts.css'
import './styles/users.css'
import App from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
)
