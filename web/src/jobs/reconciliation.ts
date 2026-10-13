import type { ComparisonOperator, InputFile, ReconciliationRule, ReconciliationSetup, ToleranceMode } from '../api'

/** Pure rule-builder logic (no React), so it can be tested on its own. The server remains the authority on validity. */

export const OPERATORS: { value: ComparisonOperator; symbol: string; label: string; words: string }[] = [
  { value: 'Equal', symbol: '==', label: 'equals', words: 'equal' },
  { value: 'NotEqual', symbol: '!=', label: 'does not equal', words: 'differ from' },
  { value: 'LessThan', symbol: '<', label: 'is less than', words: 'be less than' },
  { value: 'LessThanOrEqual', symbol: '<=', label: 'is at most', words: 'be at most' },
  { value: 'GreaterThan', symbol: '>', label: 'is greater than', words: 'be greater than' },
  { value: 'GreaterThanOrEqual', symbol: '>=', label: 'is at least', words: 'be at least' },
]

export function supportsTolerance(op: ComparisonOperator): boolean {
  return op === 'Equal' || op === 'NotEqual'
}

const ALIAS_PATTERN = /^[A-Za-z][A-Za-z0-9_]{0,29}$/

export function isValidAlias(alias: string): boolean {
  return ALIAS_PATTERN.test(alias.trim())
}

/** A rule as edited in the form: numbers are still text, a blank key means "not chosen". */
export interface RuleDraft {
  key: string
  name: string
  leftAlias: string
  rightAlias: string
  leftKeyColumn: string
  rightKeyColumn: string
  leftExpression: string
  operator: ComparisonOperator
  rightExpression: string
  tolerance: string
  toleranceMode: ToleranceMode
}

export interface DraftFile {
  id: string
  alias: string
  fileName: string
  headers: string[]
  rowCount: number
}

export function toDraftFile(f: InputFile): DraftFile {
  return { id: f.id, alias: f.alias, fileName: f.fileName, headers: f.headers, rowCount: f.rowCount }
}

export function newRule(files: DraftFile[], index: number): RuleDraft {
  const left = files[0]
  const right = files[1] ?? files[0]
  return {
    key: crypto.randomUUID(),
    name: `Rule ${index + 1}`,
    leftAlias: left?.alias ?? '',
    rightAlias: right?.alias ?? '',
    leftKeyColumn: '',
    rightKeyColumn: '',
    leftExpression: '',
    operator: 'Equal',
    rightExpression: '',
    tolerance: '0',
    toleranceMode: 'Absolute',
  }
}

// ---- column references ----

/** "Net Amount" needs brackets, "Amount" does not: file.[Net Amount] versus file.Amount. */
export function columnRef(alias: string, column: string): string {
  return /^[A-Za-z0-9_]+$/.test(column) ? `${alias}.${column}` : `${alias}.[${column}]`
}

/** Appends a column reference to an expression, separated by a space. */
export function appendRef(expression: string, ref: string): string {
  const trimmed = expression.trimEnd()
  return trimmed === '' ? ref : `${trimmed} ${ref}`
}

/**
 * Renames a file alias inside an expression. Only a whole alias followed by a dot counts, so column names that
 * merely contain the same text (file1.[old.x], myold.Col) are left alone.
 */
export function renameAliasInExpression(expression: string, from: string, to: string): string {
  return expression.replace(new RegExp(`(^|[^A-Za-z0-9_.\\[\\]])${escapeRegExp(from)}\\.`, 'gi'), `$1${to}.`)
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

// ---- expression checking (mirrors the server's grammar) ----

export interface ColumnReference {
  alias: string
  column: string
}

export interface ExpressionCheck {
  error: string | null
  refs: ColumnReference[]
}

/** Numbers, alias.Column or alias.[Column Name], + - * /, parentheses and unary minus. Nothing else. */
export function parseExpression(text: string): ExpressionCheck {
  const refs: ColumnReference[] = []
  let pos = 0

  class Bad extends Error {}
  const fail = (message: string, at = pos): never => {
    throw new Bad(`${message} (position ${at + 1})`)
  }
  const peek = () => (pos < text.length ? text[pos] : '')
  const skip = () => {
    while (pos < text.length && /\s/.test(text[pos])) pos++
  }

  function sum() {
    product()
    for (;;) {
      skip()
      if (peek() === '+' || peek() === '-') {
        pos++
        product()
      } else return
    }
  }

  function product() {
    unary()
    for (;;) {
      skip()
      if (peek() === '*' || peek() === '/') {
        pos++
        unary()
      } else return
    }
  }

  function unary() {
    skip()
    if (peek() === '-' || peek() === '+') {
      pos++
      unary()
    } else primary()
  }

  function primary() {
    skip()
    const c = peek()
    if (c === '(') {
      pos++
      sum()
      skip()
      if (peek() !== ')') fail('Missing closing parenthesis')
      pos++
    } else if (/[0-9.]/.test(c)) {
      const start = pos
      while (pos < text.length && /[0-9.]/.test(text[pos])) pos++
      if (!/^(\d+\.?\d*|\.\d+)$/.test(text.slice(start, pos))) fail(`'${text.slice(start, pos)}' is not a valid number`, start)
    } else if (/[A-Za-z_]/.test(c)) {
      const start = pos
      while (pos < text.length && /[A-Za-z0-9_]/.test(text[pos])) pos++
      const alias = text.slice(start, pos)
      if (peek() !== '.') fail(`'${alias}' must be written as ${alias}.ColumnName`, start)
      pos++
      let column: string
      if (peek() === '[') {
        const close = text.indexOf(']', pos + 1)
        if (close < 0) fail('Missing closing ] in a column name')
        column = text.slice(pos + 1, close).trim()
        pos = close + 1
      } else {
        const colStart = pos
        while (pos < text.length && /[A-Za-z0-9_]/.test(text[pos])) pos++
        column = text.slice(colStart, pos)
      }
      if (column === '') fail(`Missing column name after '${alias}.'`)
      refs.push({ alias, column })
    } else {
      fail(c === '' ? 'The expression ends too early' : `Unexpected '${c}'`)
    }
  }

  try {
    if (text.trim() === '') return { error: 'Enter an expression', refs }
    sum()
    skip()
    if (pos < text.length) fail(`Unexpected '${text[pos]}'`)
    return { error: null, refs }
  } catch (e) {
    if (e instanceof Bad) return { error: e.message, refs }
    throw e
  }
}

/** The first thing wrong with one side of a rule, or null. Columns are checked against the real file headers. */
export function checkExpression(
  text: string,
  allowedAliases: string[],
  files: Pick<DraftFile, 'alias' | 'headers'>[],
): string | null {
  const { error, refs } = parseExpression(text)
  if (error) return error
  for (const r of refs) {
    const alias = allowedAliases.find((a) => a.toLowerCase() === r.alias.toLowerCase())
    if (!alias) return `'${r.alias}.${r.column}': this rule only uses ${allowedAliases.map((a) => `'${a}'`).join(' and ')}`
    const file = files.find((f) => f.alias.toLowerCase() === alias.toLowerCase())
    if (!file?.headers.some((h) => h.toLowerCase() === r.column.toLowerCase()))
      return `'${r.column}' is not a column of ${alias}. Columns: ${file?.headers.join(', ') ?? '—'}`
  }
  return null
}

// ---- whole-rule checks and payload ----

export function isSingleFile(rule: Pick<RuleDraft, 'leftAlias' | 'rightAlias'>): boolean {
  return rule.leftAlias.toLowerCase() === rule.rightAlias.toLowerCase()
}

function hasColumn(files: DraftFile[], alias: string, column: string): boolean {
  return !!files.find((f) => f.alias.toLowerCase() === alias.toLowerCase())?.headers.some((h) => h.toLowerCase() === column.toLowerCase())
}

/** The first problem with a rule, or null when it is ready to save. */
export function ruleProblem(rule: RuleDraft, files: DraftFile[]): string | null {
  if (rule.name.trim() === '') return 'Give the rule a name'
  if (!files.some((f) => f.alias.toLowerCase() === rule.leftAlias.toLowerCase())) return 'Choose the left file'
  if (!files.some((f) => f.alias.toLowerCase() === rule.rightAlias.toLowerCase())) return 'Choose the right file'

  if (!isSingleFile(rule)) {
    if (rule.leftKeyColumn === '' || rule.rightKeyColumn === '') return 'Choose the key column in each file to match records on'
    if (!hasColumn(files, rule.leftAlias, rule.leftKeyColumn)) return `Key column '${rule.leftKeyColumn}' is not in ${rule.leftAlias}`
    if (!hasColumn(files, rule.rightAlias, rule.rightKeyColumn)) return `Key column '${rule.rightKeyColumn}' is not in ${rule.rightAlias}`
  } else if (rule.leftKeyColumn !== '' && !hasColumn(files, rule.leftAlias, rule.leftKeyColumn)) {
    return `Key column '${rule.leftKeyColumn}' is not in ${rule.leftAlias}`
  }

  const aliases = isSingleFile(rule) ? [rule.leftAlias] : [rule.leftAlias, rule.rightAlias]
  const left = checkExpression(rule.leftExpression, aliases, files)
  if (left) return `Left side: ${left}`
  const right = checkExpression(rule.rightExpression, aliases, files)
  if (right) return `Right side: ${right}`

  if (supportsTolerance(rule.operator)) {
    const t = Number(rule.tolerance === '' ? '0' : rule.tolerance)
    if (!Number.isFinite(t) || t < 0) return 'Tolerance must be zero or more'
    if (rule.toleranceMode === 'Percent' && t > 100) return 'A percentage tolerance cannot be more than 100'
  }
  return null
}

export function describeRule(rule: RuleDraft): string {
  const op = OPERATORS.find((o) => o.value === rule.operator)
  const join = isSingleFile(rule)
    ? 'For every row'
    : `For every record matched on ${rule.leftAlias}.${rule.leftKeyColumn || '…'} = ${rule.rightAlias}.${rule.rightKeyColumn || '…'}`
  const t = Number(rule.tolerance === '' ? '0' : rule.tolerance)
  const tolerance =
    supportsTolerance(rule.operator) && t > 0 ? ` (tolerance ${rule.tolerance}${rule.toleranceMode === 'Percent' ? '%' : ''})` : ''
  return `${join}: ${rule.leftExpression || '…'} must ${op?.words ?? ''} ${rule.rightExpression || '…'}${tolerance}.`
}

/** The request body for creating a job: attached files by alias, rules as the API expects them. */
export function buildSetup(files: DraftFile[], rules: RuleDraft[]): ReconciliationSetup {
  return {
    files: files.map((f) => ({ fileId: f.id, alias: f.alias.trim() })),
    rules: rules.map<ReconciliationRule>((r) => {
      const single = isSingleFile(r)
      return {
        name: r.name.trim(),
        leftAlias: r.leftAlias,
        rightAlias: r.rightAlias,
        leftKeyColumn: r.leftKeyColumn === '' ? null : r.leftKeyColumn,
        rightKeyColumn: single || r.rightKeyColumn === '' ? null : r.rightKeyColumn,
        leftExpression: r.leftExpression.trim(),
        operator: r.operator,
        rightExpression: r.rightExpression.trim(),
        tolerance: supportsTolerance(r.operator) ? Number(r.tolerance === '' ? '0' : r.tolerance) : 0,
        toleranceMode: r.toleranceMode,
      }
    }),
  }
}

/** Why the form cannot be submitted yet, or null. Checks aliases, then every rule. */
export function setupProblem(files: DraftFile[], rules: RuleDraft[]): string | null {
  if (files.length === 0) return 'Upload at least one CSV file'
  const seen = new Set<string>()
  for (const f of files) {
    if (!isValidAlias(f.alias)) return `'${f.alias}' is not a valid file name for rules: start with a letter, then letters, digits or underscores`
    if (seen.has(f.alias.toLowerCase())) return `Two files are called '${f.alias}'`
    seen.add(f.alias.toLowerCase())
  }
  if (rules.length === 0) return 'Add at least one rule'
  const names = new Set<string>()
  for (const r of rules) {
    const problem = ruleProblem(r, files)
    if (problem) return `${r.name || 'A rule'}: ${problem}`
    if (names.has(r.name.trim().toLowerCase())) return `Two rules are called '${r.name.trim()}'`
    names.add(r.name.trim().toLowerCase())
  }
  return null
}

/** Money-style display: thousands separators, at least two decimals, no float noise. */
export function formatAmount(value: number | null): string {
  if (value === null || value === undefined) return '—'
  return value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 10 })
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
