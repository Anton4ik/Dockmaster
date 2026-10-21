import EditorModule from 'react-simple-code-editor'
import Prism from 'prismjs'
import 'prismjs/components/prism-yaml'
import 'prismjs/components/prism-properties'
import 'prismjs/components/prism-json'
import 'prismjs/components/prism-ini'
import 'prismjs/components/prism-toml'
import 'prismjs/components/prism-nginx'
import type { CodeLanguage } from '../lib/languages'
import { useStoredState } from '../lib/hooks'

// the package ships CommonJS: depending on the bundler interop the component sits on .default
const Editor = ((EditorModule as unknown as { default?: typeof EditorModule }).default ?? EditorModule) as typeof EditorModule

const escape = (code: string) => code.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

const BOOL = { parse: (s: string) => s !== 'false', stringify: String }

/**
 * Textarea with syntax colours (Prism under a transparent textarea): yaml for compose, key=value for .env, and the
 * usual config formats for a project's files. `textareaId` lets a <label htmlFor> name it. Long lines wrap, or not
 * (one line each, scrolled sideways) — the viewer's choice, remembered for every editor. `highlight` replaces the
 * language's colours (the compose file marks its service names).
 */
export default function CodeEditor({ value, onChange, language, minHeight, textareaId, highlight }: {
  value: string
  onChange: (v: string) => void
  language: CodeLanguage
  minHeight?: number | string
  textareaId?: string
  highlight?: (code: string) => string
}) {
  const [wrap, setWrap] = useStoredState('dm.editor.wrap', true, BOOL)
  return (
    <>
    <div className="code-editor-bar">
      <label className="check-inline"><input type="checkbox" checked={wrap} onChange={e => setWrap(e.target.checked)} /> wrap lines</label>
    </div>
    <div className={'code-editor' + (wrap ? '' : ' nowrap')} style={{ minHeight }}>
      <Editor
        value={value}
        onValueChange={onChange}
        highlight={highlight ?? (code => language === 'plain' ? escape(code) : Prism.highlight(code, Prism.languages[language], language))}
        padding={12}
        tabSize={2}
        insertSpaces
        textareaId={textareaId}
        textareaClassName="code-editor-textarea"
        preClassName="code-editor-pre"
        style={{ fontFamily: 'var(--mono)', fontSize: 12.5, lineHeight: 1.45, minHeight: 'inherit' }}
      />
    </div>
    </>
  )
}
