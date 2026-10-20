-- =====================================================================================================
-- Purchases sync — SAP B1 side (Molas_Lubes_LTD on SQL Server)
--
-- 1. Index on ODOO_WEBHOOK_QUEUE for the two workers' polling queries.
-- 2. SBO_SP_TransactionNotification: queue Purchase Orders (22), Goods Receipt POs (20),
--    A/P Invoices (18) and A/P Credit Memos (19) into the EXISTING [dbo].[ODOO_WEBHOOK_QUEUE] table.
--
-- Rules (spec v4, section 4.2):
--   a. New rows fill ObjectType, TransType, DocEntry, DocNum, CardCode; delivery columns stay NULL.
--   b. EventType is always set explicitly: PO_UPSERT, GRPO_LINK, AP_INVOICE_CREATE, AP_INVOICE_CANCEL,
--      AP_CREDIT_CREATE, AP_CREDIT_CANCEL. Delivery rows keep 'webhook'.
--   c. DEPLOY ORDER: the middleware build that filters the delivery worker on ObjectType = '15'
--      (WebhookQueueProcessor) must be running BEFORE this script is applied.
--   d. Dedupe: skip the insert only when a row with the same ObjectType + DocEntry + TransType is
--      still 'pending'.
--   e. Non-clustered index on (Status, EventType, ObjectType, DocEntry).
--   f. One IF block per object type, each with its own TRY/CATCH writing a 'failed' row. @error is
--      never set by the new blocks; a SAP transaction is never blocked.
--
-- The object-15 delivery block is reproduced VERBATIM from the procedure as exported on 2026-10-20
-- and is not modified. Run in SSMS against Molas_Lubes_LTD. Safe to re-run.
-- =====================================================================================================

USE [Molas_Lubes_LTD];
GO

-- -----------------------------------------------------------------------------------------------------
-- 0. Safety: EventType column (the middleware adds it on first use; make sure it is there)
-- -----------------------------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[ODOO_WEBHOOK_QUEUE]') AND name = 'EventType'
)
    ALTER TABLE [dbo].[ODOO_WEBHOOK_QUEUE]
        ADD [EventType] NVARCHAR(50) NOT NULL
            CONSTRAINT DF_ODOO_WEBHOOK_QUEUE_EventType DEFAULT 'webhook';
GO

-- -----------------------------------------------------------------------------------------------------
-- 1. Index for the polling queries (rule e)
-- -----------------------------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_ODOO_WEBHOOK_QUEUE_Status_EventType_ObjectType_DocEntry'
      AND object_id = OBJECT_ID(N'[dbo].[ODOO_WEBHOOK_QUEUE]')
)
    CREATE NONCLUSTERED INDEX [IX_ODOO_WEBHOOK_QUEUE_Status_EventType_ObjectType_DocEntry]
        ON [dbo].[ODOO_WEBHOOK_QUEUE] ([Status], [EventType], [ObjectType], [DocEntry])
        INCLUDE ([TransType], [RetryCount], [CreatedAt]);
GO

-- -----------------------------------------------------------------------------------------------------
-- 2. Transaction notification procedure
-- -----------------------------------------------------------------------------------------------------
ALTER PROC [dbo].[SBO_SP_TransactionNotification]
    @object_type nvarchar(30),                  -- SBO Object Type
    @transaction_type nchar(1),                 -- [A]dd, [U]pdate, [D]elete, [C]ancel, C[L]ose
    @num_of_cols_in_key int,
    @list_of_key_cols_tab_del nvarchar(255),
    @list_of_cols_val_tab_del nvarchar(255)
AS
BEGIN
    -- Return values
    DECLARE @error  int                         -- Result (0 for no error)
    DECLARE @error_message nvarchar(200)        -- Error string to be displayed
    SELECT @error = 0
    SELECT @error_message = N'Ok'

    --------------------------------------------------------------------------------------------------
    --  EXISTING CODE (preserved as-is)
    --------------------------------------------------------------------------------------------------
    /*
    declare @No varchar(20)
    IF @object_type = '17' AND @transaction_type in ('U','A')
    BEGIN
        Set @No = ( select Count(*) from ORDR T0 INNER JOIN OCRD T1 ON T0.CardCode = T1.CardCode
                    where (T1.[Balance]*-1) < T0.[DocTotal] and T1.[GroupCode] IN ('100')
                    AND T0.[DocEntry] = @list_of_cols_val_tab_del )
        if (@No > 0)
        begin
            SET @error = 42090
            SET @error_message = N'BK- Customer does not have paid enough to proceed with the sales order'
        end
    END
    */

    --------------------------------------------------------------------------------------------------
    --  ODOO DELIVERY WEBHOOK: Queue delivery confirmation when ODLN is added
    --  Object Type 15 = Delivery Note (ODLN/DLN1)
    --  Transaction Type 'A' = Add (new delivery posted)
    --------------------------------------------------------------------------------------------------
    IF @object_type = '15' AND @transaction_type = 'A'
    BEGIN
        BEGIN TRY
            DECLARE @delivery_docentry  INT;
            DECLARE @delivery_docnum    INT;
            DECLARE @delivery_date      DATETIME;
            DECLARE @card_code          NVARCHAR(15);
            DECLARE @base_so_docentry   INT;
            DECLARE @odoo_so_id         NVARCHAR(50);
            DECLARE @odoo_delivery_id   NVARCHAR(50);
            DECLARE @already_queued     INT;

            -- Parse the DocEntry from the key value SAP passes in
            SET @delivery_docentry = CAST(@list_of_cols_val_tab_del AS INT);

            -- Get header info from the Delivery Note (ODLN)
            SELECT
                @delivery_docnum = T0.[DocNum],
                @delivery_date   = T0.[DocDate],
                @card_code       = T0.[CardCode]
            FROM [dbo].[ODLN] T0
            WHERE T0.[DocEntry] = @delivery_docentry;

            -- Find the base Sales Order DocEntry from the first delivery line
            -- BaseType = 17 means the line was copied from a Sales Order (ORDR)
            SELECT TOP 1
                @base_so_docentry = T0.[BaseEntry]
            FROM [dbo].[DLN1] T0
            WHERE T0.[DocEntry] = @delivery_docentry
              AND T0.[BaseType] = 17;

            -- Read the Odoo UDFs from the base Sales Order
            IF @base_so_docentry IS NOT NULL
            BEGIN
                SELECT
                    @odoo_so_id       = T0.[U_Odoo_SO_ID],
                    @odoo_delivery_id = T0.[U_Odoo_Delivery_ID]
                FROM [dbo].[ORDR] T0
                WHERE T0.[DocEntry] = @base_so_docentry;
            END

            -- Only queue if this delivery came from an Odoo-originated sales order
            -- (i.e., U_Odoo_SO_ID is populated)
            IF @odoo_so_id IS NOT NULL AND LEN(LTRIM(RTRIM(@odoo_so_id))) > 0
            BEGIN
                -- Prevent duplicate queue entries for the same delivery
                SELECT @already_queued = COUNT(*)
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                WHERE [DocEntry] = @delivery_docentry
                  AND [ObjectType] = '15';

                IF @already_queued = 0
                BEGIN
                    INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    (
                        [ObjectType], [TransType], [DocEntry], [DocNum],
                        [OdooSoId], [OdooDeliveryId], [BaseSODocEntry],
                        [DeliveryDate], [CardCode]
                    )
                    VALUES
                    (
                        '15',                               -- Delivery Note
                        'A',                                -- Add
                        @delivery_docentry,                 -- ODLN.DocEntry
                        @delivery_docnum,                   -- ODLN.DocNum
                        LTRIM(RTRIM(@odoo_so_id)),          -- ORDR.U_Odoo_SO_ID
                        LTRIM(RTRIM(@odoo_delivery_id)),    -- ORDR.U_Odoo_Delivery_ID
                        @base_so_docentry,                  -- ORDR.DocEntry
                        @delivery_date,                     -- ODLN.DocDate
                        @card_code                          -- ODLN.CardCode
                    );
                END
            END
        END TRY
        BEGIN CATCH
            -- NEVER block the SAP transaction due to webhook queue errors
            -- Log the error silently — the delivery still goes through in SAP
            -- You can check this with: SELECT * FROM ODOO_WEBHOOK_QUEUE WHERE Status = 'failed'
            DECLARE @catch_msg NVARCHAR(500) = ERROR_MESSAGE();
            -- Optional: insert a failed record for visibility
            BEGIN TRY
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [Status], [ErrorMessage])
                VALUES
                    ('15', 'A', ISNULL(@delivery_docentry, 0), 'failed',
                     'SP_TransactionNotification error: ' + @catch_msg);
            END TRY
            BEGIN CATCH
                -- Absolute last resort — swallow everything so SAP is never blocked
            END CATCH
        END CATCH
    END

    --------------------------------------------------------------------------------------------------
    --  PURCHASES SYNC (SAP → Odoo): queue PO / GRPO / A/P Invoice / A/P Credit Memo events
    --  Spec: docs/molas_purchases_integration_spec.md §4.2 (middleware repo).
    --  Shared variables for the four blocks below (prefixed @p_ to stay clear of the delivery block).
    --------------------------------------------------------------------------------------------------
    DECLARE @p_docentry  INT;
    DECLARE @p_docnum    INT;
    DECLARE @p_cardcode  NVARCHAR(15);
    DECLARE @p_canceled  CHAR(1);
    DECLARE @p_event     NVARCHAR(50);
    DECLARE @p_pending   INT;
    DECLARE @p_catch     NVARCHAR(500);

    --------------------------------------------------------------------------------------------------
    --  Object Type 22 = Purchase Order (OPOR) — A, U, L, C → PO_UPSERT
    --------------------------------------------------------------------------------------------------
    IF @object_type = '22' AND @transaction_type IN ('A', 'U', 'L', 'C')
    BEGIN
        BEGIN TRY
            SET @p_docentry = CAST(@list_of_cols_val_tab_del AS INT);
            SET @p_event    = N'PO_UPSERT';
            SELECT @p_docnum = T0.[DocNum], @p_cardcode = T0.[CardCode]
            FROM [dbo].[OPOR] T0 WHERE T0.[DocEntry] = @p_docentry;

            SELECT @p_pending = COUNT(*)
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            WHERE [ObjectType] = '22' AND [DocEntry] = @p_docentry
              AND [TransType] = @transaction_type AND [Status] = 'pending';

            IF @p_pending = 0
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [EventType])
                VALUES
                    ('22', @transaction_type, @p_docentry, @p_docnum, @p_cardcode, @p_event);
        END TRY
        BEGIN CATCH
            SET @p_catch = ERROR_MESSAGE();
            BEGIN TRY
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [Status], [ErrorMessage], [EventType])
                VALUES
                    ('22', @transaction_type, ISNULL(@p_docentry, 0), 'failed',
                     'SP_TransactionNotification error: ' + @p_catch, N'PO_UPSERT');
            END TRY
            BEGIN CATCH
                -- swallow: SAP is never blocked
            END CATCH
        END CATCH
    END

    --------------------------------------------------------------------------------------------------
    --  Object Type 20 = Goods Receipt PO (OPDN) — A, U → GRPO_LINK
    --------------------------------------------------------------------------------------------------
    IF @object_type = '20' AND @transaction_type IN ('A', 'U')
    BEGIN
        BEGIN TRY
            SET @p_docentry = CAST(@list_of_cols_val_tab_del AS INT);
            SET @p_event    = N'GRPO_LINK';
            SELECT @p_docnum = T0.[DocNum], @p_cardcode = T0.[CardCode], @p_canceled = T0.[CANCELED]
            FROM [dbo].[OPDN] T0 WHERE T0.[DocEntry] = @p_docentry;

            -- A cancellation document (CANCELED = 'C') is itself added with 'A'; it never links.
            IF ISNULL(@p_canceled, 'N') <> 'C'
            BEGIN
                SELECT @p_pending = COUNT(*)
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                WHERE [ObjectType] = '20' AND [DocEntry] = @p_docentry
                  AND [TransType] = @transaction_type AND [Status] = 'pending';

                IF @p_pending = 0
                    INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                        ([ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [EventType])
                    VALUES
                        ('20', @transaction_type, @p_docentry, @p_docnum, @p_cardcode, @p_event);
            END
        END TRY
        BEGIN CATCH
            SET @p_catch = ERROR_MESSAGE();
            BEGIN TRY
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [Status], [ErrorMessage], [EventType])
                VALUES
                    ('20', @transaction_type, ISNULL(@p_docentry, 0), 'failed',
                     'SP_TransactionNotification error: ' + @p_catch, N'GRPO_LINK');
            END TRY
            BEGIN CATCH
            END CATCH
        END CATCH
    END

    --------------------------------------------------------------------------------------------------
    --  Object Type 18 = A/P Invoice (OPCH) — A → AP_INVOICE_CREATE, C → AP_INVOICE_CANCEL
    --------------------------------------------------------------------------------------------------
    IF @object_type = '18' AND @transaction_type IN ('A', 'C')
    BEGIN
        BEGIN TRY
            SET @p_docentry = CAST(@list_of_cols_val_tab_del AS INT);
            SET @p_event    = CASE @transaction_type WHEN 'A' THEN N'AP_INVOICE_CREATE' ELSE N'AP_INVOICE_CANCEL' END;
            SELECT @p_docnum = T0.[DocNum], @p_cardcode = T0.[CardCode], @p_canceled = T0.[CANCELED]
            FROM [dbo].[OPCH] T0 WHERE T0.[DocEntry] = @p_docentry;

            -- On 'A', skip the cancellation document itself (CANCELED = 'C'); the original document's
            -- 'C' event is what drives the Odoo reversal.
            IF NOT (@transaction_type = 'A' AND ISNULL(@p_canceled, 'N') = 'C')
            BEGIN
                SELECT @p_pending = COUNT(*)
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                WHERE [ObjectType] = '18' AND [DocEntry] = @p_docentry
                  AND [TransType] = @transaction_type AND [Status] = 'pending';

                IF @p_pending = 0
                    INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                        ([ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [EventType])
                    VALUES
                        ('18', @transaction_type, @p_docentry, @p_docnum, @p_cardcode, @p_event);
            END
        END TRY
        BEGIN CATCH
            SET @p_catch = ERROR_MESSAGE();
            BEGIN TRY
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [Status], [ErrorMessage], [EventType])
                VALUES
                    ('18', @transaction_type, ISNULL(@p_docentry, 0), 'failed',
                     'SP_TransactionNotification error: ' + @p_catch,
                     CASE @transaction_type WHEN 'A' THEN N'AP_INVOICE_CREATE' ELSE N'AP_INVOICE_CANCEL' END);
            END TRY
            BEGIN CATCH
            END CATCH
        END CATCH
    END

    --------------------------------------------------------------------------------------------------
    --  Object Type 19 = A/P Credit Memo (ORPC) — A → AP_CREDIT_CREATE, C → AP_CREDIT_CANCEL
    --------------------------------------------------------------------------------------------------
    IF @object_type = '19' AND @transaction_type IN ('A', 'C')
    BEGIN
        BEGIN TRY
            SET @p_docentry = CAST(@list_of_cols_val_tab_del AS INT);
            SET @p_event    = CASE @transaction_type WHEN 'A' THEN N'AP_CREDIT_CREATE' ELSE N'AP_CREDIT_CANCEL' END;
            SELECT @p_docnum = T0.[DocNum], @p_cardcode = T0.[CardCode], @p_canceled = T0.[CANCELED]
            FROM [dbo].[ORPC] T0 WHERE T0.[DocEntry] = @p_docentry;

            IF NOT (@transaction_type = 'A' AND ISNULL(@p_canceled, 'N') = 'C')
            BEGIN
                SELECT @p_pending = COUNT(*)
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                WHERE [ObjectType] = '19' AND [DocEntry] = @p_docentry
                  AND [TransType] = @transaction_type AND [Status] = 'pending';

                IF @p_pending = 0
                    INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                        ([ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [EventType])
                    VALUES
                        ('19', @transaction_type, @p_docentry, @p_docnum, @p_cardcode, @p_event);
            END
        END TRY
        BEGIN CATCH
            SET @p_catch = ERROR_MESSAGE();
            BEGIN TRY
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([ObjectType], [TransType], [DocEntry], [Status], [ErrorMessage], [EventType])
                VALUES
                    ('19', @transaction_type, ISNULL(@p_docentry, 0), 'failed',
                     'SP_TransactionNotification error: ' + @p_catch,
                     CASE @transaction_type WHEN 'A' THEN N'AP_CREDIT_CREATE' ELSE N'AP_CREDIT_CANCEL' END);
            END TRY
            BEGIN CATCH
            END CATCH
        END CATCH
    END

    --------------------------------------------------------------------------------------------------
    -- Select the return values
    SELECT @error, @error_message
END
GO

-- -----------------------------------------------------------------------------------------------------
-- Verification (run after deploying; nothing below changes data)
-- -----------------------------------------------------------------------------------------------------
-- SELECT TOP 20 Id, ObjectType, TransType, DocEntry, DocNum, CardCode, Status, EventType, ErrorMessage, CreatedAt
-- FROM dbo.ODOO_WEBHOOK_QUEUE WHERE EventType <> 'webhook' ORDER BY Id DESC;
