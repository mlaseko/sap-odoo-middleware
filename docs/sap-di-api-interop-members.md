# SAP B1 DI API: member names on our server

The real property names of the SAPbobsCOM objects this middleware uses, read from the interop library the
production build makes (`src/SapOdooMiddleware/obj/x64/Release/net8.0/Interop.SAPbobsCOM.dll`, DI API 10.0) on
4–5 November 2026. Check here before writing DI code, instead of guessing.

## Why this file exists

The project builds with `EmbedInteropTypes=true` (see `SapOdooMiddleware.csproj`). The compiler then copies into
the build **only the members the C# code names directly**. A member reached by name through reflection is not in
the build, even when SAP has it. This cost three failed pushes on the first landed cost document (PRs #357–#360):

- `LandedCost_CostLine.amount` is **lower-case**, so a reflection lookup for "Amount" found nothing.
- Without `DocumentCurrency`, a landed cost document took the EUR currency of its GRPO and read the empty
  `AmountFC`, so SAP refused it with -5002 "Both expenditure and custom expenses are missing".

**Rule:** set DI members directly by the names below (`line.amount = …`). Use the late-bound helpers
(`TrySetInteropProperty`, `TrySetInteropEnum`) only for members that differ between DI API versions.

## How to read the names again

In PowerShell on the server:

```powershell
$asm = [Reflection.Assembly]::LoadFrom("C:\Users\Administrator\Documents\GitHub\sap-odoo-middleware\src\SapOdooMiddleware\obj\x64\Release\net8.0\Interop.SAPbobsCOM.dll")
$asm.GetTypes() | Where-Object { $_.Name -in 'ILandedCost_CostLine' } | ForEach-Object { "== $($_.FullName)"; $_.GetProperties() | ForEach-Object { "  $($_.Name) : $($_.PropertyType.Name)" } }
```

Ask for the `I…` interface: the plain name (e.g. `LandedCost_CostLine`) is an empty interface that inherits from it.

## Landed costs (`LandedCostsService`, OIPF)

### ILandedCost (header)

| Member | Type |
|---|---|
| DocEntry | Int32 |
| LandedCostNumber | Int32 |
| PostingDate | DateTime |
| DueDate | DateTime |
| VendorCode / VendorName | String |
| Broker / BrokerName | String |
| ClosedDocument | LandedCostDocStatusEnum |
| FileNumber | String |
| Remarks | String |
| Reference | String |
| **DocumentCurrency** | String |
| DocumentRate | Double |
| ProjectedCustoms / ActualCustoms | Double |
| Tax1 / Tax2 | Double |
| BeforeTax / Total / TotalFreightCharges | Double |
| ProjectedCustomsFC / Tax1FC / Tax2FC / BeforeTaxFC / TotalFC / TotalFreightChargesFC / ActualCustomsFC | Double |
| Series | Int32 |
| CustomsAffectsInventory | BoYesNoEnum |
| AmountToBalance / AmountToBalanceFC | Double |
| BillofLadingNumber | String |
| TransportType | Int32 |
| TransactionNumber | Int32 |
| JournalRemarks | String |
| LandedCost_ItemLines | LandedCost_ItemLines |
| LandedCost_CostLines | LandedCost_CostLines |
| AttachmentEntry | Int32 |
| UserFields | Fields |

There is **no** `DocumentDate` or `TaxDate` on this build.

### ILandedCost_CostLine

| Member | Type |
|---|---|
| DocEntry | Int32 |
| LandedCostCode | String |
| AllocationBy | LandedCostAllocationByEnum |
| **amount** (lower-case) | Double — local currency |
| AmountFC | Double — document currency |
| Factor | Double |
| CostType | LCCostTypeEnum |
| IncludeForCustoms | BoYesNoEnum |
| OpenAmount / OpenAmountFC | Double |
| Broker / BrokerName | String |
| CostCategory | LandedCostCostCategoryEnum |
| UserFields | Fields |

## Marketing documents (`Documents`: PO, GRPO, A/P invoice, drafts)

Only the members relevant to purchasing were listed.

### IDocuments

| Member | Type |
|---|---|
| DocDate / DocDueDate / TaxDate | DateTime |
| CardCode | String |
| NumAtCard | String |
| DocCurrency | String |
| DocRate | Double |
| DocTotal / DocTotalFc / DocTotalSys | Double |
| Comments | String |
| **DocObjectCode** | BoObjectTypes — set on a draft (oDrafts) to the target type, e.g. oPurchaseInvoices |
| DocObjectCodeEx | String |
| DiscountPercent | Double |
| TotalDiscount / TotalDiscountFC / TotalDiscountSC | Double |
| MaximumCashDiscount | BoYesNoEnum |
| CashDiscountDateOffset | Int32 |
| **Expenses** | DocumentsAdditionalExpenses — freight and other additional expenses |
| IntrastatExpenses | DocumentsAdditionalIntrastatExpenses |
| BaseAmount / BaseAmountFC / BaseAmountSC | Double |
| UseCorrectionVATGroup | BoYesNoEnum |
| PriceMode | PriceModeDocumentEnum |
| BaseType / BaseEntry | Int32 |

### IDocument_Lines

| Member | Type |
|---|---|
| Quantity | Double |
| Price | Double — after discount |
| **UnitPrice** | Double — before discount |
| PriceAfterVAT | Double |
| GrossPrice | Double |
| **DiscountPercent** | Double — 100 on a free line |
| UseBaseUnits | BoYesNoEnum |
| VatGroup / TaxCode / RecognizedTaxCode | String |
| **BaseType / BaseEntry / BaseLine** | Int32 — copy from a base document (20 = GRPO) |
| ActualBaseEntry / ActualBaseLine | Int32 |
| PickQuantity / PackageQuantity / RequiredQuantity / RetirementQuantity | Double |
| InventoryQuantity / RemainingOpenInventoryQuantity / RemainingOpenQuantity / BaseOpenQuantity | Double |
| ChangeInventoryQuantityIndependently / ConsiderQuantity | BoYesNoEnum |
| LineTotal | Double |
| Expenses | Document_LinesAdditionalExpenses |
| DistributeExpense | BoYesNoEnum |
| LastBuyInmPrice / LastBuyDistributeSum / LastBuyDistributeSumFc / LastBuyDistributeSumSc | Double |
| StockInmPrice / StockDistributesum / StockDistributesumForeign / StockDistributesumSystem | Double |
| GrossBuyPrice | Double |
| GrossBase | Int32 |
| GrossProfitTotalBasePrice | Double |
| ExpenseType | String |
| ExpenseOperationType | BoExpenseOperationTypeEnum |

### IDocumentsAdditionalExpenses (document freight, OEXD codes)

| Member | Type |
|---|---|
| **ExpenseCode** | Int32 — OEXD.ExpnsCode |
| LineTotal | Double — local currency |
| **LineTotalFC** | Double — document currency |
| LineTotalSys | Double |
| DistributionMethod | BoAdEpnsDistribMethods |
| VatGroup / TaxCode | String |
| BaseDocEntry / BaseDocLine / BaseDocType / BaseDocumentReference | Int32 |
| LastPurchasePrice | BoYesNoEnum |
| DistributionRule … DistributionRule5 | String |

## Freight definitions (OEXD) on Molas_Lubes_LTD

| Freight | Expense account | Stock | Used for |
|---|---|---|---|
| Liqui Moly freight | 5011131 Cogs – Freight inwards | ticked | Freight on Liqui Moly invoices (into item cost) |
| Liqui Moly display/marketing | 6011523 | unticked | Charged display and marketing items, not stock |

Their codes (`SELECT ExpnsCode, ExpnsName, ExpnsAcct FROM OEXD`) go into the Odoo SAP Purchases settings.
