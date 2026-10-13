import assert from 'node:assert/strict'
import { describe, test } from 'node:test'
import {
  appendRef,
  buildSetup,
  checkExpression,
  columnRef,
  describeRule,
  formatAmount,
  formatBytes,
  isValidAlias,
  newRule,
  parseExpression,
  renameAliasInExpression,
  ruleProblem,
  setupProblem,
  type DraftFile,
  type RuleDraft,
} from './reconciliation.ts'

const trades: DraftFile = {
  id: 't', alias: 'trades', fileName: '1_internal_trades.csv', rowCount: 60,
  headers: ['TradeId', 'GrossCashAmount', 'Commission', 'Tax', 'Net Amount (INR)'],
}
const custodian: DraftFile = {
  id: 'c', alias: 'custodian', fileName: '2_custodian_settlements.csv', rowCount: 59,
  headers: ['ClientTradeRef', 'CashAmount'],
}
const files = [trades, custodian]

function rule(over: Partial<RuleDraft> = {}): RuleDraft {
  return {
    key: 'k', name: 'Cash', leftAlias: 'trades', rightAlias: 'custodian',
    leftKeyColumn: 'TradeId', rightKeyColumn: 'ClientTradeRef',
    leftExpression: 'trades.GrossCashAmount - trades.Commission - trades.Tax', operator: 'Equal',
    rightExpression: 'custodian.CashAmount', tolerance: '0', toleranceMode: 'Absolute', ...over,
  }
}

describe('parseExpression', () => {
  for (const ok of ['1 + 2 * 3', '(1 + 2) * 3', '-5 + 2', '- -4', '.5 + 0.25', 'a.X', 'a.[Net Amount] * 2', 'a.X+b.Y/2', '  1+1  '])
    test(`accepts ${JSON.stringify(ok)}`, () => assert.equal(parseExpression(ok).error, null))

  for (const bad of ['', '   ', '1 +', '(1 + 2', '1 + 2)', '1 2', 'amount', 'a.', 'a.[Open', '1.2.3', '1 $ 2', 'a.X ** 2', 'System.Exit(1)', 'a.X; DROP'])
    test(`rejects ${JSON.stringify(bad)}`, () => assert.notEqual(parseExpression(bad).error, null))

  test('collects every column reference, with bracketed names unwrapped', () => {
    const { refs } = parseExpression('a.Gross - a.[Net Amount] + b.Net')
    assert.deepEqual(refs, [
      { alias: 'a', column: 'Gross' },
      { alias: 'a', column: 'Net Amount' },
      { alias: 'b', column: 'Net' },
    ])
  })

  test('explains a bare column name', () => {
    assert.match(parseExpression('amount + 1').error ?? '', /amount\.ColumnName/)
  })
})

describe('checkExpression', () => {
  test('accepts real columns of the rule\'s files, ignoring case', () => {
    assert.equal(checkExpression('TRADES.grosscashamount - trades.Tax', ['trades', 'custodian'], files), null)
  })

  test('accepts a column whose name has spaces and symbols when bracketed', () => {
    assert.equal(checkExpression('trades.[Net Amount (INR)]', ['trades'], files), null)
  })

  test('flags a column that is not in the file and lists the real ones', () => {
    const error = checkExpression('trades.Gros', ['trades'], files)
    assert.match(error ?? '', /'Gros' is not a column of trades/)
    assert.match(error ?? '', /GrossCashAmount/)
  })

  test('flags a file the rule does not use', () => {
    assert.match(checkExpression('other.X', ['trades', 'custodian'], files) ?? '', /only uses 'trades' and 'custodian'/)
  })

  test('passes syntax errors through', () => {
    assert.match(checkExpression('trades.Tax +', ['trades'], files) ?? '', /ends too early/)
  })
})

describe('column references', () => {
  test('plain names stay plain, others are bracketed', () => {
    assert.equal(columnRef('trades', 'Tax'), 'trades.Tax')
    assert.equal(columnRef('trades', 'Net Amount (INR)'), 'trades.[Net Amount (INR)]')
    assert.equal(columnRef('trades', 'Net-Amount'), 'trades.[Net-Amount]')
  })

  test('a bracketed reference parses back to the original column', () => {
    const { refs, error } = parseExpression(columnRef('t', 'Net Amount (INR)'))
    assert.equal(error, null)
    assert.equal(refs[0].column, 'Net Amount (INR)')
  })

  test('appendRef separates with a space', () => {
    assert.equal(appendRef('', 'a.X'), 'a.X')
    assert.equal(appendRef('a.X -', 'a.Y'), 'a.X - a.Y')
    assert.equal(appendRef('a.X ', 'a.Y'), 'a.X a.Y')
  })
})

describe('renameAliasInExpression', () => {
  test('renames every reference to the alias', () => {
    assert.equal(renameAliasInExpression('old.A - old.B + other.C', 'old', 'fresh'), 'fresh.A - fresh.B + other.C')
  })

  test('does not touch longer aliases, columns or bracketed names that contain the text', () => {
    assert.equal(renameAliasInExpression('myold.A + x.[old.B] + x.old', 'old', 'fresh'), 'myold.A + x.[old.B] + x.old')
  })

  test('is case-insensitive and works at the start and after operators or parentheses', () => {
    assert.equal(renameAliasInExpression('OLD.A*(old.B)', 'old', 'n'), 'n.A*(n.B)')
  })
})

describe('ruleProblem', () => {
  test('a complete rule is fine', () => assert.equal(ruleProblem(rule(), files), null))

  test('needs a name', () => assert.match(ruleProblem(rule({ name: ' ' }), files) ?? '', /name/))

  test('needs both files to exist', () => {
    assert.match(ruleProblem(rule({ leftAlias: 'nope' }), files) ?? '', /left file/)
    assert.match(ruleProblem(rule({ rightAlias: 'nope' }), files) ?? '', /right file/)
  })

  test('two files need a key column in each, and it must exist', () => {
    assert.match(ruleProblem(rule({ rightKeyColumn: '' }), files) ?? '', /key column in each file/)
    assert.match(ruleProblem(rule({ leftKeyColumn: 'Nope' }), files) ?? '', /'Nope' is not in trades/)
  })

  test('a within-file rule needs no key, but a chosen key must exist', () => {
    const within = rule({ rightAlias: 'trades', leftKeyColumn: '', rightKeyColumn: '', leftExpression: 'trades.Tax', rightExpression: 'trades.Commission' })
    assert.equal(ruleProblem(within, files), null)
    assert.match(ruleProblem({ ...within, leftKeyColumn: 'Nope' }, files) ?? '', /not in trades/)
  })

  test('a within-file rule cannot reach into another file', () => {
    const within = rule({ rightAlias: 'trades', leftKeyColumn: '', rightKeyColumn: '', rightExpression: 'custodian.CashAmount' })
    assert.match(ruleProblem(within, files) ?? '', /Right side/)
  })

  test('says which side is wrong', () => {
    assert.match(ruleProblem(rule({ leftExpression: 'trades.Nope' }), files) ?? '', /^Left side:/)
    assert.match(ruleProblem(rule({ rightExpression: '' }), files) ?? '', /^Right side:/)
  })

  test('tolerance must be sensible', () => {
    assert.match(ruleProblem(rule({ tolerance: '-1' }), files) ?? '', /zero or more/)
    assert.match(ruleProblem(rule({ tolerance: 'abc' }), files) ?? '', /zero or more/)
    assert.match(ruleProblem(rule({ tolerance: '101', toleranceMode: 'Percent' }), files) ?? '', /more than 100/)
    assert.equal(ruleProblem(rule({ tolerance: '100', toleranceMode: 'Percent' }), files), null)
  })

  test('tolerance is ignored for inequalities', () =>
    assert.equal(ruleProblem(rule({ operator: 'GreaterThan', tolerance: '-5' }), files), null))
})

describe('setupProblem', () => {
  test('a good setup has no problem', () => assert.equal(setupProblem(files, [rule()]), null))
  test('needs files and rules', () => {
    assert.match(setupProblem([], [rule()]) ?? '', /Upload at least one/)
    assert.match(setupProblem(files, []) ?? '', /at least one rule/)
  })
  test('aliases must be valid and unique', () => {
    assert.match(setupProblem([{ ...trades, alias: '1bad' }, custodian], [rule()]) ?? '', /not a valid file name/)
    assert.match(setupProblem([trades, { ...custodian, alias: 'TRADES' }], [rule()]) ?? '', /Two files/)
  })
  test('rule names must be unique and each rule valid', () => {
    assert.match(setupProblem(files, [rule(), rule({ key: 'k2', name: 'cash' })]) ?? '', /Two rules/)
    assert.match(setupProblem(files, [rule({ leftExpression: 'x' })]) ?? '', /^Cash: Left side/)
  })
})

describe('buildSetup', () => {
  test('maps files by alias and rules to the API shape', () => {
    const setup = buildSetup(files, [rule({ tolerance: '0.05', name: ' Cash ' })])
    assert.deepEqual(setup.files, [{ fileId: 't', alias: 'trades' }, { fileId: 'c', alias: 'custodian' }])
    assert.deepEqual(setup.rules[0], {
      name: 'Cash', leftAlias: 'trades', rightAlias: 'custodian', leftKeyColumn: 'TradeId', rightKeyColumn: 'ClientTradeRef',
      leftExpression: 'trades.GrossCashAmount - trades.Commission - trades.Tax', operator: 'Equal',
      rightExpression: 'custodian.CashAmount', tolerance: 0.05, toleranceMode: 'Absolute',
    })
  })

  test('a within-file rule sends no right key, and a blank left key becomes null', () => {
    const r = buildSetup(files, [rule({ rightAlias: 'trades', leftKeyColumn: '', rightKeyColumn: 'ignored' })]).rules[0]
    assert.equal(r.leftKeyColumn, null)
    assert.equal(r.rightKeyColumn, null)
  })

  test('tolerance is zero for operators that do not use it', () =>
    assert.equal(buildSetup(files, [rule({ operator: 'LessThan', tolerance: '9' })]).rules[0].tolerance, 0))

  test('a blank tolerance means zero', () =>
    assert.equal(buildSetup(files, [rule({ tolerance: '' })]).rules[0].tolerance, 0))
})

describe('helpers', () => {
  test('aliases', () => {
    for (const ok of ['a', 'internal_trades', 'File2']) assert.equal(isValidAlias(ok), true, ok)
    for (const bad of ['', '1a', 'has space', 'a.b', 'x'.repeat(31)]) assert.equal(isValidAlias(bad), false, bad)
  })

  test('newRule starts from the first two files, or one file twice', () => {
    assert.deepEqual([newRule(files, 0).leftAlias, newRule(files, 0).rightAlias], ['trades', 'custodian'])
    assert.deepEqual([newRule([trades], 1).leftAlias, newRule([trades], 1).rightAlias], ['trades', 'trades'])
    assert.equal(newRule([trades], 1).name, 'Rule 2')
  })

  test('describeRule reads like a sentence', () => {
    assert.equal(
      describeRule(rule({ tolerance: '0.05' })),
      'For every record matched on trades.TradeId = custodian.ClientTradeRef: trades.GrossCashAmount - trades.Commission - trades.Tax must equal custodian.CashAmount (tolerance 0.05).',
    )
    assert.match(describeRule(rule({ rightAlias: 'trades', operator: 'GreaterThan' })), /^For every row: .* must be greater than /)
    assert.match(describeRule(rule({ tolerance: '2', toleranceMode: 'Percent' })), /tolerance 2%/)
  })

  test('amounts and sizes', () => {
    assert.equal(formatAmount(null), '—')
    assert.equal(formatAmount(1200), '1,200.00')
    assert.equal(formatAmount(-355220.8), '-3,55,220.80')
    assert.equal(formatBytes(512), '512 B')
    assert.equal(formatBytes(1536), '1.5 KB')
  })
})
