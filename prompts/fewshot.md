---
version: 1.0
description: The plain prompt plus two solved examples from the dev pool (Template25_Instance126, Template28_Instance115).
---
You extract structured data from one invoice. The user message is the text of the invoice. Reply with a single JSON object that matches the response schema.

Fields:
- vendor: the party that issued the invoice (seller). `name` as printed; `address` split into `street` (all lines before the city line, joined with ", "), `city`, `region` (state or province code), `postalCode`, `country` (ISO 3166-1 alpha-2).
- customer: the bill-to party, same shape as vendor.
- invoiceNumber: the invoice or credit note number, without its label.
- invoiceDate, dueDate: ISO 8601 dates (yyyy-MM-dd).
- currency: ISO 4217 code of the amounts. `$` means USD.
- subtotal, discount, tax, total: numbers without currency symbols or thousands separators. `total` is the amount payable, whatever its label (Total, Balance due, Amount due). `discount` is a positive number even when printed with a minus sign. Keep the minus sign on other amounts if it is printed (credit notes).
- lineItems: one entry per printed item row with `description`, `quantity`, `unitPrice`, `amount`. Null if no item rows are printed.

Rules:
- Extract values exactly as printed. Do not recompute, reconcile or round totals, subtotals, tax or discount.
- Tax is the sum of all printed tax lines.
- If a field is not printed, return null.
- Customer is the bill-to party; ignore ship-to.

Examples:

Invoice text:
```
Bill to:Pamela Harris
0999 Antonio Rapids Apt. 810
Hubbardstad, TN 60201 US
Tel:+(563)518-3900
Email:mirandagray@example.com
Site:http://garcia.info/
Kelly-Moss
Address:6030 Willie Shores Suite 354
Guerraton, TX 60058 US
Email:meghan88@example.net
INVOICE ID 4411-929
Due Date : 31-Aug-2020
Invoice Date: 27-Jan-2012
SUB_TOTAL : 600.02  USD
GST(5%) : 30.00
GST(12%) : 72.00
GST(18%) : 108.00
GST(20%) : 120.00
GST(1%) : 6.00
TOTAL : 616.52 USD
```
Answer:
```json
{"vendor":{"name":"Kelly-Moss","address":{"street":"6030 Willie Shores Suite 354","city":"Guerraton","region":"TX","postalCode":"60058","country":"US"}},"customer":{"name":"Pamela Harris","address":{"street":"0999 Antonio Rapids Apt. 810","city":"Hubbardstad","region":"TN","postalCode":"60201","country":"US"}},"invoiceNumber":"4411-929","invoiceDate":"2012-01-27","dueDate":"2020-08-31","currency":"USD","subtotal":600.02,"discount":null,"tax":336.00,"total":616.52,"lineItems":null}
```

Invoice text:
```
TAX INVOICE
Date: 14-May-1996
Bill to:Priscilla Tran
030 Diaz Circle Apt. 174
Smithchester, TN 17946 US
Tel:+(244)963-2685
Email:ryanjones@example.net
Site:https://www.thomas.com/
Bank Name           Central Bank of USA
Branch Name          Raf CAMP
Bank Account Number  13580158
Bank Swift Code      SBININBB250
SUB_TOTAL : 1691.46  $
TAX:VAT (3.79%):  64.14 $
TOTAL : 1698.26 $
Address:86143 Ruth Isle Apt. 608
Ashleyshire, TN 12593 US
```
Answer:
```json
{"vendor":{"name":null,"address":{"street":"86143 Ruth Isle Apt. 608","city":"Ashleyshire","region":"TN","postalCode":"12593","country":"US"}},"customer":{"name":"Priscilla Tran","address":{"street":"030 Diaz Circle Apt. 174","city":"Smithchester","region":"TN","postalCode":"17946","country":"US"}},"invoiceNumber":null,"invoiceDate":"1996-05-14","dueDate":null,"currency":"USD","subtotal":1691.46,"discount":null,"tax":64.14,"total":1698.26,"lineItems":null}
```
