# Sample reconciliation files

Synthetic data for the file-reconciliation feature (September 2026 equity trades settled through a custodian). Regenerable and deterministic; no real data.

| File | Rows | Columns | What it represents |
|---|---|---|---|
| `1_internal_trades.csv` | 60 | 13 | Internal trade blotter (front/middle office books) |
| `2_custodian_settlements.csv` | 59 | 9 | Custodian's settlement statement |
| `3_gl_cash_postings.csv` | 60 | 10 | General-ledger postings to the custody cash account |

## Columns

**1_internal_trades** - `TradeId`, `TradeDate`, `SettlementDate`, `Counterparty`, `ISIN`, `Currency` (INR/USD/EUR), `Side` (BUY/SELL), `Quantity`, `Price`, `GrossCashAmount` (signed: BUY negative, SELL positive), `Commission`, `Tax` (STT/stamp duty), `TradeStatus`.

**2_custodian_settlements** - `SettlementRef`, `ClientTradeRef` (= TradeId), `SettlementDate`, `ISIN`, `Currency`, `SettledQuantity`, `CashAmount` (signed net cash), `SettlementStatus` (SETTLED/FAILED), `Custodian`.

**3_gl_cash_postings** - `JournalId`, `PostingDate`, `TradeId`, `AccountCode`, `AccountName`, `Currency`, `DebitAmount`, `CreditAmount`, `CostCenter`, `Narrative`.

Net cash of a trade = `GrossCashAmount - Commission - Tax`. Note the join column has a different name in each file (`TradeId`, `ClientTradeRef`, `TradeId`), on purpose.

## Example rules and what they should find

1. **Trade cash vs custodian** (join `1.TradeId = 2.ClientTradeRef`): `1.GrossCashAmount - 1.Commission - 1.Tax == 2.CashAmount`
2. **Quantity vs custodian**: `1.Quantity == 2.SettledQuantity`
3. **Custodian vs ledger** (join `2.ClientTradeRef = 3.TradeId`): `2.CashAmount == 3.DebitAmount - 3.CreditAmount`

## Expected results (verified against the files with an independent script)

Tolerance 0 unless stated. With tolerance 0.05 the penny differences (marked *) pass.

**Rule 1** (left = internal trades, right = custodian)

| Outcome | Trades |
|---|---|
| Cash differs | T-100003 (0.01)*, T-100044 (0.02)*, T-100012 (0.50), T-100019 (250.00), T-100026 (1,200.00) |
| No record in custodian file | T-100007, T-100031, T-100052 |
| No record in internal file | T-900001, T-900002 |
| Everything else | passes (52 records: 60 - 3 unmatched - 5 differing) |

**Rule 2** (same join): quantity differs for T-100015 (-10) and T-100038 (+20); the same 3 + 2 unmatched records as Rule 1.

**Rule 3** (left = custodian, right = ledger)

| Outcome | Trades |
|---|---|
| Amount differs | T-100003 (0.01)*, T-100044 (0.02)*, T-100050 (0.01)*, T-100005 (0.50), T-100012 (0.50), T-100017 (75.25), T-100019 (250.00), T-100026 (1,200.00), T-100029 (3,000.00) |
| No ledger posting | T-100010, T-100041, T-900001, T-900002 |
| Duplicate key in ledger (posted twice) | T-100020 |
| No record in custodian file | T-100007, T-100031, T-100052, T-800001 |

(The ledger was built from the internal books, so it agrees with Rule 1 wherever the custodian does not.)

## Planted problems that numeric rules cannot see

These are text or date columns. The first version of the feature compares numbers only, so these are listed as a reminder of what a later "text equals" rule type would catch:

- T-100022, T-100047: `SettlementStatus` is FAILED.
- T-100034: custodian settlement date is one day late.
- T-100036: ledger currency differs from the trade's currency.
