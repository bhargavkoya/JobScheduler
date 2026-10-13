import { useState, type ChangeEvent } from 'react'
import { uploadJobFiles } from '../api'
import { useAuth } from '../auth/AuthContext'
import {
  appendRef,
  columnRef,
  describeRule,
  isSingleFile,
  isValidAlias,
  newRule,
  OPERATORS,
  renameAliasInExpression,
  ruleProblem,
  supportsTolerance,
  toDraftFile,
  type DraftFile,
  type RuleDraft,
} from './reconciliation'

const input = 'mt-1 w-full rounded border border-slate-300 px-2 py-1.5 text-sm text-slate-900'

interface Props {
  files: DraftFile[]
  rules: RuleDraft[]
  onChange: (files: DraftFile[], rules: RuleDraft[]) => void
}

/** Step 1: upload CSVs (columns are read from their headers). Step 2: define record-by-record rules over those columns. */
export function ReconciliationSetup({ files, rules, onChange }: Props) {
  const { token } = useAuth()
  const [previews, setPreviews] = useState<Record<string, string[][]>>({})
  const [openPreview, setOpenPreview] = useState<string | null>(null)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function onPick(e: ChangeEvent<HTMLInputElement>) {
    const picked = Array.from(e.target.files ?? [])
    e.target.value = '' // let the same file be picked again later
    if (picked.length === 0) return
    setError(null)
    setUploading(true)
    try {
      const uploaded = await uploadJobFiles(picked, token)
      const taken = new Set(files.map((f) => f.alias.toLowerCase()))
      const added = uploaded.map((u) => {
        // The server makes aliases unique within one upload; keep them unique across uploads too.
        let alias = u.alias
        for (let n = 2; taken.has(alias.toLowerCase()); n++) alias = `${u.alias}_${n}`
        taken.add(alias.toLowerCase())
        return { ...toDraftFile(u), alias }
      })
      setPreviews((p) => ({ ...p, ...Object.fromEntries(uploaded.map((u) => [u.id, u.preview])) }))
      const all = [...files, ...added]
      onChange(all, rules.length === 0 && all.length > 0 ? [newRule(all, 0)] : rules)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed')
    } finally {
      setUploading(false)
    }
  }

  /** Renames a file everywhere it is used: in the rules' file choices and inside their expressions. */
  function renameFile(id: string, to: string) {
    const from = files.find((f) => f.id === id)?.alias ?? ''
    onChange(
      files.map((f) => (f.id === id ? { ...f, alias: to } : f)),
      rules.map((r) => ({
        ...r,
        leftAlias: r.leftAlias.toLowerCase() === from.toLowerCase() ? to : r.leftAlias,
        rightAlias: r.rightAlias.toLowerCase() === from.toLowerCase() ? to : r.rightAlias,
        leftExpression: renameAliasInExpression(r.leftExpression, from, to),
        rightExpression: renameAliasInExpression(r.rightExpression, from, to),
      })),
    )
  }

  function removeFile(id: string) {
    const alias = files.find((f) => f.id === id)?.alias.toLowerCase()
    const remainingFiles = files.filter((f) => f.id !== id)
    // A rule that used the file cannot survive without it.
    const remainingRules = rules.filter((r) => r.leftAlias.toLowerCase() !== alias && r.rightAlias.toLowerCase() !== alias)
    onChange(remainingFiles, remainingRules)
  }

  function updateRule(key: string, patch: Partial<RuleDraft>) {
    onChange(files, rules.map((r) => (r.key === key ? { ...r, ...patch } : r)))
  }

  return (
    <div className="space-y-4 rounded-lg border border-indigo-100 bg-indigo-50/40 p-3">
      <section className="space-y-2">
        <h4 className="text-sm font-medium text-slate-800">1. Upload the CSV files to compare</h4>
        <p className="text-xs text-slate-500">
          The first row must be the column names. Columns are read from your files, so any layout works (up to 5 MB and 50,000 rows each).
        </p>
        <label className="inline-block cursor-pointer rounded border border-slate-300 bg-white px-3 py-1.5 text-sm hover:bg-slate-50">
          {uploading ? 'Uploading…' : files.length === 0 ? 'Choose CSV files' : 'Add more files'}
          <input type="file" accept=".csv,text/csv" multiple disabled={uploading} onChange={onPick} className="sr-only" />
        </label>
        {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

        <ul className="space-y-2">
          {files.map((f) => (
            <li key={f.id} className="rounded border border-slate-200 bg-white p-2 text-sm">
              <div className="flex flex-wrap items-center gap-2">
                <AliasInput alias={f.alias} others={files.filter((x) => x.id !== f.id).map((x) => x.alias)} onCommit={(a) => renameFile(f.id, a)} />
                <span className="text-slate-700">{f.fileName}</span>
                <span className="text-xs text-slate-500">
                  {f.rowCount.toLocaleString()} rows · {f.headers.length} columns
                </span>
                <button type="button" onClick={() => setOpenPreview(openPreview === f.id ? null : f.id)} className="ml-auto text-xs text-indigo-700 hover:underline">
                  {openPreview === f.id ? 'Hide preview' : 'Preview'}
                </button>
                <button type="button" onClick={() => removeFile(f.id)} className="text-xs text-red-700 hover:underline">
                  Remove
                </button>
              </div>
              <div className="mt-1 flex flex-wrap gap-1">
                {f.headers.map((h) => (
                  <code key={h} className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-700">
                    {h}
                  </code>
                ))}
              </div>
              {openPreview === f.id && previews[f.id] && (
                <div className="mt-2 overflow-x-auto">
                  <table className="text-xs">
                    <thead>
                      <tr>
                        {f.headers.map((h) => (
                          <th key={h} className="whitespace-nowrap border-b border-slate-200 px-2 py-1 text-left font-medium text-slate-600">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {previews[f.id].map((row, i) => (
                        <tr key={i}>
                          {row.map((cell, j) => (
                            <td key={j} className="whitespace-nowrap border-b border-slate-100 px-2 py-1 text-slate-700">{cell}</td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </li>
          ))}
        </ul>
      </section>

      <section className="space-y-2">
        <h4 className="text-sm font-medium text-slate-800">2. Define the rules</h4>
        <p className="text-xs text-slate-500">
          Each rule is checked for every record. Write each side as arithmetic over columns, for example{' '}
          <code className="rounded bg-white px-1">trades.Gross - trades.Fee</code> equals <code className="rounded bg-white px-1">custodian.Net</code>.
          Use <code className="rounded bg-white px-1">file.[Column Name]</code> when a column name has spaces.
        </p>

        {rules.map((r, i) => (
          <RuleCard
            key={r.key}
            rule={r}
            files={files}
            onChange={(patch) => updateRule(r.key, patch)}
            onRemove={() => onChange(files, rules.filter((x) => x.key !== r.key))}
            canRemove={rules.length > 1}
            index={i}
          />
        ))}

        <button
          type="button"
          disabled={files.length === 0}
          onClick={() => onChange(files, [...rules, newRule(files, rules.length)])}
          className="rounded border border-slate-300 bg-white px-3 py-1.5 text-sm hover:bg-slate-50 disabled:opacity-50"
        >
          Add rule
        </button>
      </section>
    </div>
  )
}

/** The short name rules use for a file. Edited freely, applied on blur once it is valid and unique. */
function AliasInput({ alias, others, onCommit }: { alias: string; others: string[]; onCommit: (alias: string) => void }) {
  const [text, setText] = useState(alias)
  const [problem, setProblem] = useState<string | null>(null)

  function commit() {
    const next = text.trim()
    if (next === alias) return setProblem(null)
    if (!isValidAlias(next)) {
      setProblem('Start with a letter; then letters, digits or _ (max 30)')
      return setText(alias)
    }
    if (others.some((o) => o.toLowerCase() === next.toLowerCase())) {
      setProblem('Another file already uses that name')
      return setText(alias)
    }
    setProblem(null)
    onCommit(next)
  }

  return (
    <span className="inline-flex flex-col">
      <input
        aria-label={`Name used in rules for ${alias}`}
        title="The name rules use for this file"
        value={text}
        onChange={(e) => {
          setText(e.target.value)
          setProblem(null)
        }}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            commit()
          }
        }}
        className="w-40 rounded border border-indigo-200 bg-indigo-50 px-2 py-0.5 font-mono text-xs text-indigo-900"
      />
      {problem && <span className="text-xs text-red-700">{problem}</span>}
    </span>
  )
}

function RuleCard({
  rule,
  files,
  onChange,
  onRemove,
  canRemove,
  index,
}: {
  rule: RuleDraft
  files: DraftFile[]
  onChange: (patch: Partial<RuleDraft>) => void
  onRemove: () => void
  canRemove: boolean
  index: number
}) {
  const left = files.find((f) => f.alias.toLowerCase() === rule.leftAlias.toLowerCase())
  const right = files.find((f) => f.alias.toLowerCase() === rule.rightAlias.toLowerCase())
  const single = isSingleFile(rule)
  const usable = single ? (left ? [left] : []) : [left, right].filter((f): f is DraftFile => !!f)
  const problem = ruleProblem(rule, files)

  function changeFile(side: 'left' | 'right', alias: string) {
    // Key columns belong to a file, so choosing another file clears that side's key.
    onChange(side === 'left' ? { leftAlias: alias, leftKeyColumn: '' } : { rightAlias: alias, rightKeyColumn: '' })
  }

  return (
    <div className="space-y-2 rounded border border-slate-200 bg-white p-3">
      <div className="flex items-end gap-2">
        <label className="block flex-1 text-xs text-slate-600">
          Rule name
          <input value={rule.name} onChange={(e) => onChange({ name: e.target.value })} className={input} aria-label={`Rule ${index + 1} name`} />
        </label>
        {canRemove && (
          <button type="button" onClick={onRemove} className="rounded border border-red-200 px-2 py-1.5 text-xs text-red-700 hover:bg-red-50">
            Remove rule
          </button>
        )}
      </div>

      <div className="grid gap-2 sm:grid-cols-2">
        <label className="block text-xs text-slate-600">
          Left file
          <select value={rule.leftAlias} onChange={(e) => changeFile('left', e.target.value)} className={input}>
            {files.map((f) => (
              <option key={f.id} value={f.alias}>{f.alias}</option>
            ))}
          </select>
        </label>
        <label className="block text-xs text-slate-600">
          Right file {single && <span className="text-slate-400">(same file: compares columns of each row)</span>}
          <select value={rule.rightAlias} onChange={(e) => changeFile('right', e.target.value)} className={input}>
            {files.map((f) => (
              <option key={f.id} value={f.alias}>{f.alias}</option>
            ))}
          </select>
        </label>
      </div>

      {single ? (
        <label className="block text-xs text-slate-600">
          Identify each row by (optional)
          <select value={rule.leftKeyColumn} onChange={(e) => onChange({ leftKeyColumn: e.target.value })} className={input}>
            <option value="">Row number</option>
            {left?.headers.map((h) => (
              <option key={h} value={h}>{h}</option>
            ))}
          </select>
        </label>
      ) : (
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="block text-xs text-slate-600">
            Match records on: column in {rule.leftAlias}
            <select value={rule.leftKeyColumn} onChange={(e) => onChange({ leftKeyColumn: e.target.value })} className={input}>
              <option value="">Choose a column…</option>
              {left?.headers.map((h) => (
                <option key={h} value={h}>{h}</option>
              ))}
            </select>
          </label>
          <label className="block text-xs text-slate-600">
            …equal to column in {rule.rightAlias}
            <select value={rule.rightKeyColumn} onChange={(e) => onChange({ rightKeyColumn: e.target.value })} className={input}>
              <option value="">Choose a column…</option>
              {right?.headers.map((h) => (
                <option key={h} value={h}>{h}</option>
              ))}
            </select>
          </label>
        </div>
      )}

      <div className="grid gap-2 lg:grid-cols-[1fr_auto_1fr]">
        <ExpressionField label="Left side" value={rule.leftExpression} files={usable} onChange={(v) => onChange({ leftExpression: v })} />
        <label className="block text-xs text-slate-600">
          Must
          <select value={rule.operator} onChange={(e) => onChange({ operator: e.target.value as RuleDraft['operator'] })} className={input}>
            {OPERATORS.map((o) => (
              <option key={o.value} value={o.value}>{o.symbol}  {o.label}</option>
            ))}
          </select>
        </label>
        <ExpressionField label="Right side" value={rule.rightExpression} files={usable} onChange={(v) => onChange({ rightExpression: v })} />
      </div>

      {supportsTolerance(rule.operator) && (
        <div className="flex items-end gap-2">
          <label className="block text-xs text-slate-600">
            Tolerance
            <input
              type="number"
              min={0}
              step="any"
              value={rule.tolerance}
              onChange={(e) => onChange({ tolerance: e.target.value })}
              className={input + ' w-28'}
            />
          </label>
          <select
            aria-label="Tolerance type"
            value={rule.toleranceMode}
            onChange={(e) => onChange({ toleranceMode: e.target.value as RuleDraft['toleranceMode'] })}
            className={input + ' w-44'}
          >
            <option value="Absolute">amount (difference up to)</option>
            <option value="Percent">% of the right side</option>
          </select>
        </div>
      )}

      {problem ? (
        <p role="alert" className="rounded bg-amber-50 px-2 py-1 text-xs text-amber-800">{problem}</p>
      ) : (
        <p className="rounded bg-emerald-50 px-2 py-1 text-xs text-emerald-800">{describeRule(rule)}</p>
      )}
    </div>
  )
}

function ExpressionField({
  label,
  value,
  files,
  onChange,
}: {
  label: string
  value: string
  files: DraftFile[]
  onChange: (value: string) => void
}) {
  return (
    <div className="block text-xs text-slate-600">
      <label>
        {label}
        <input
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="e.g. a.Gross - a.Fee"
          spellCheck={false}
          className={input + ' font-mono'}
        />
      </label>
      <select
        aria-label={`Insert a column into the ${label.toLowerCase()}`}
        value=""
        onChange={(e) => e.target.value && onChange(appendRef(value, e.target.value))}
        className="mt-1 w-full rounded border border-slate-200 bg-slate-50 px-2 py-1 text-xs text-slate-600"
      >
        <option value="">Insert a column…</option>
        {files.map((f) => (
          <optgroup key={f.id} label={`${f.alias} (${f.fileName})`}>
            {f.headers.map((h) => (
              <option key={h} value={columnRef(f.alias, h)}>{h}</option>
            ))}
          </optgroup>
        ))}
      </select>
    </div>
  )
}
