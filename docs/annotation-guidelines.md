# Annotation guidelines

How ground truth in `evals/annotations.jsonl` is produced. The FATURA rules are implemented in
[`FaturaConverter`](../src/InvoiceEvals.Core/Fatura/FaturaConverter.cs); keep the two in sync.

## General

- A field is `null` when it is **not printed** on the document. Extractors are expected to return `null` too;
  a value where ground truth is `null` counts as a false positive.
- `lineItems: null` means **not annotated** (see "Known limitations"), not "no line items". Line-item metrics skip such documents.
- Ground truth is **what is printed**, not what is arithmetically correct. FATURA amounts and dates are random
  (see below); an extractor must not "fix" them.

## Field mapping (FATURA → `InvoiceDto`)

FATURA annotates each field as a labelled text span (e.g. `"TOTAL : 734.33 EUR"`). Labels are stripped.

| `InvoiceDto` field | FATURA key(s) | Rule |
|---|---|---|
| `vendor.name` | `SELLER_NAME` | Trimmed text. `null` on the 16 templates without a `SELLER_NAME` span (e.g. logo only). |
| `vendor.address` | `SELLER_ADDRESS` | `Address:` prefix stripped, parsed as below. |
| `customer` | `BILL_TO`, else `BUYER` | First line is the name, then the address. `SEND_TO` (ship-to) is ignored. |
| `invoiceNumber` | `NUMBER` | Last whitespace-separated token (`Invoice number INV/72-97/395` → `INV/72-97/395`). |
| `invoiceDate` | `DATE` | See Dates. |
| `dueDate` | `DUE_DATE` | See Dates. |
| `currency` | trailing token of `TOTAL`, `SUB_TOTAL`, `AMOUNT_DUE` | See Currency. |
| `subtotal` | `SUB_TOTAL` | See Amounts. |
| `discount` | `DISCOUNT` | Positive magnitude: `DISCOUNT(1.85%): (-) 13.42` → `13.42`. |
| `tax` | `TAX` + every `GST(n%)` key | **Sum of all printed tax lines.** Template 25 prints five GST lines; `tax` is their sum. |
| `total` | `TOTAL`, else `AMOUNT_DUE` | Amount payable, whatever its label (`TOTAL`, `BALANCE DUE`, `DUE_AMOUNT`). |
| `lineItems` | — | Always `null` for FATURA. |

Not mapped: `TITLE`, `PO_NUMBER`, `GSTIN*` (tax IDs), `PAYMENT_DETAILS`, `NOTE`, `CONDITIONS`, `TOTAL_WORDS`, contact lines (Tel/Email/Site), `LOGO`, `TABLE`, `OTHER`.

## Normalization

- **Dates** → ISO 8601 `yyyy-MM-dd` (`DateOnly`). FATURA prints `d-MMM-yyyy` with English month abbreviations.
- **Amounts** → JSON number (`decimal`), invariant culture, `.` as decimal separator, printed scale preserved (`725.30`).
  The last number in the span is the amount, so percentages in `TAX:VAT (3.88%): 28.18` are skipped.
- **Currency** → ISO 4217. `$` and `USD` → `USD`; `EUR` → `EUR`. `null` if no amount carries a currency.
- **Addresses** → `street` (all lines before the city line, joined with `, `), then `city`, `region` (2-letter state code),
  `postalCode` (string, leading zeros kept), `country` (ISO 3166-1 alpha-2) parsed from `City, ST 12345 US`.

## Rejected annotations

The converter rejects an annotation rather than guessing:

- `DATE` whose text is a due date (`Due Date : …`): FATURA labelling defect, 2 of 10,000 documents.
- Conflicting currencies between amount fields (none observed).
- Any span that does not match the expected shape (none observed).

`evals download` prints every rejection; rejected documents are excluded from sampling.

## Known limitations of FATURA as ground truth

Measured over all 10,000 annotations:

- **No line items.** `TABLE` is annotated as a bounding box only. Some templates print line amounts that sum to the
  subtotal, so a hand-annotated subset is possible later.
- **Amounts do not reconcile.** `subtotal − discount + tax = total` holds for 8 of 5,597 documents that print both
  subtotal and total. Line-item currency symbols can differ from the totals currency.
- **Dates are random.** The due date precedes the invoice date in 2,885 of 5,798 documents that print both.
- **Low vendor diversity.** Seller name and address are fixed per template: 34 distinct vendor names in total.
- **US-only addresses**, three currencies (USD, EUR, `$`), English only.

The synthetic edge cases (credit notes, multi-currency, multi-page, discounts) are meant to cover what FATURA cannot.

## Dev pool and golden set split

`evals download` holds out 5 of the 50 layouts (ranked first by `SHA-256("<seed>:holdout:<layout>")`; with seed 42:
Template2, Template25, Template28, Template29, Template48). From those it samples 20 documents into `evals/dev/`;
the golden set (`evals/annotations.jsonl`, 150 documents) is sampled from the other 45 layouts. Prompt writing and
few-shot examples use the dev pool only. Template25, the one layout with five GST lines, is therefore dev-only;
the synthetic set covers that case.

## Model input (text layers)

Each FATURA document gets two text layers next to its image, written by `evals download`:

- `<name>.txt` ("text" input mode, default): the text of every labelled span, ordered top to bottom then left to
  right by bounding box ([`FaturaText.Spans`](../src/InvoiceEvals.Core/Fatura/FaturaText.cs)). Exact, but the item
  table is missing (FATURA annotates it only as a box), so line amounts cannot be confused with totals. This makes
  FATURA easier than a real invoice.
- `<name>.ocr.txt` ("ocr" input mode): FATURA's full-page OCR text (the `OTHER` key), noise included. Ground truth
  stays the printed value. **The OCR text never contains the vendor name**: in all 103 golden documents that print
  one, the name is absent and template text (e.g. `SK DIGITAL`) appears instead. Vendor-name scores in ocr mode
  measure the dataset, not the model.
