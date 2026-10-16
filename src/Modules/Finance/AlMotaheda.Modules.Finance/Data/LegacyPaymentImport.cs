namespace AlMotaheda.Modules.Finance.Data;

/// <summary>
/// Copies every payment recorded before Finance existed into Finance, once: an imported payment request (status Paid,
/// or Reversed when the payment was voided) with its lines and history, and its ledger entries (with reversing
/// entries for voided payments). After it, the ledger holds all the money ever paid, and every void has a request
/// to reverse.
/// </summary>
/// <remarks>
/// <para>Run by the <c>ImportExistingPayments</c> migration. Idempotent (every step skips what is already there), so
/// a database test runs it again on hand-made legacy rows.</para>
/// <para>The one place Finance reads other modules' tables, and only here, at migration time: the source tables
/// are in the Projects, Workforce, Subcontractors and Fleet schemas, which migrate before Finance.</para>
/// <para>Equipment payments have no idempotency key of their own; a deterministic one is made from the cost (or the
/// voided payment row), so running the import twice can't copy them twice.</para>
/// </remarks>
internal static class LegacyPaymentImport
{
    public const string Sql = """
        -- 1. Supplier payments.
        INSERT INTO [finance].[PaymentRequest]
            ([SourceType], [PayeeType], [PayeeId], [ProjectId], [Purpose], [Amount], [Method], [RequestedDate], [Reference], [Notes],
             [Payload], [IdempotencyKey], [Status], [Origin], [CreatedAt], [CreatedBy], [PaidOn], [ConfirmedAt], [ConfirmedBy],
             [SourcePaymentId], [ClosedAt], [ClosedBy], [CloseReason])
        SELECT 'SupplierPayment', 'Supplier', p.[SupplierId], p.[ProjectId], 'SupplierInvoices', p.[Amount], p.[Method], p.[PaymentDate],
               p.[Reference], p.[Notes], NULL, p.[IdempotencyKey], CASE WHEN p.[VoidedAt] IS NULL THEN 'Paid' ELSE 'Reversed' END, 'Imported',
               p.[CreatedAt], p.[CreatedBy], p.[PaymentDate], p.[CreatedAt], p.[CreatedBy], p.[Id], p.[VoidedAt], p.[VoidedBy], p.[VoidReason]
        FROM [projects].[SupplierPayment] p
        WHERE NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequest] r WHERE r.[IdempotencyKey] = p.[IdempotencyKey]);

        INSERT INTO [finance].[PaymentRequestLine] ([RequestId], [RecordType], [RecordId], [ProjectId], [Amount])
        SELECT r.[Id], 'ProjectExpense', a.[ExpenseId], p.[ProjectId], a.[Amount]
        FROM [finance].[PaymentRequest] r
        JOIN [projects].[SupplierPayment] p ON p.[Id] = r.[SourcePaymentId]
        JOIN [projects].[SupplierPaymentAllocation] a ON a.[PaymentId] = p.[Id]
        WHERE r.[Origin] = 'Imported' AND r.[SourceType] = 'SupplierPayment'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestLine] l WHERE l.[RequestId] = r.[Id]);

        -- 2. Worker payments. One project when every day paid is on it, otherwise none (the lines say which).
        INSERT INTO [finance].[PaymentRequest]
            ([SourceType], [PayeeType], [PayeeId], [ProjectId], [Purpose], [Amount], [Method], [RequestedDate], [Reference], [Notes],
             [Payload], [IdempotencyKey], [Status], [Origin], [CreatedAt], [CreatedBy], [PaidOn], [ConfirmedAt], [ConfirmedBy],
             [SourcePaymentId], [ClosedAt], [ClosedBy], [CloseReason])
        SELECT 'WorkerPayment', 'Worker', p.[WorkerId],
               (SELECT CASE WHEN COUNT(DISTINCT s.[ProjectId]) = 1 THEN MIN(s.[ProjectId]) END
                FROM [workforce].[WorkerPaymentAllocation] a
                JOIN [workforce].[AttendanceLine] line ON line.[Id] = a.[LineId]
                JOIN [workforce].[DailySheet] s ON s.[Id] = line.[SheetId]
                WHERE a.[PaymentId] = p.[Id]),
               'WorkerWages', p.[Amount], p.[Method], p.[PaymentDate], p.[Reference], p.[Notes], NULL, p.[IdempotencyKey],
               CASE WHEN p.[VoidedAt] IS NULL THEN 'Paid' ELSE 'Reversed' END, 'Imported',
               p.[CreatedAt], p.[CreatedBy], p.[PaymentDate], p.[CreatedAt], p.[CreatedBy], p.[Id], p.[VoidedAt], p.[VoidedBy], p.[VoidReason]
        FROM [workforce].[WorkerPayment] p
        WHERE NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequest] r WHERE r.[IdempotencyKey] = p.[IdempotencyKey]);

        INSERT INTO [finance].[PaymentRequestLine] ([RequestId], [RecordType], [RecordId], [ProjectId], [Amount])
        SELECT r.[Id], 'AttendanceLine', a.[LineId], s.[ProjectId], a.[Amount]
        FROM [finance].[PaymentRequest] r
        JOIN [workforce].[WorkerPaymentAllocation] a ON a.[PaymentId] = r.[SourcePaymentId]
        JOIN [workforce].[AttendanceLine] line ON line.[Id] = a.[LineId]
        JOIN [workforce].[DailySheet] s ON s.[Id] = line.[SheetId]
        WHERE r.[Origin] = 'Imported' AND r.[SourceType] = 'WorkerPayment'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestLine] l WHERE l.[RequestId] = r.[Id]);

        -- 3. Subcontractor payments: one line for the contract; the payment's own purpose is kept as the payload.
        INSERT INTO [finance].[PaymentRequest]
            ([SourceType], [PayeeType], [PayeeId], [ProjectId], [Purpose], [Amount], [Method], [RequestedDate], [Reference], [Notes],
             [Payload], [IdempotencyKey], [Status], [Origin], [CreatedAt], [CreatedBy], [PaidOn], [ConfirmedAt], [ConfirmedBy],
             [SourcePaymentId], [ClosedAt], [ClosedBy], [CloseReason])
        SELECT 'SubcontractPayment', 'Subcontractor', c.[SubcontractorId], c.[ProjectId],
               CASE WHEN p.[Purpose] = 'Advance' THEN 'SubcontractAdvance' ELSE 'SubcontractWork' END,
               p.[Amount], p.[Method], p.[PaymentDate], p.[Reference], p.[Notes], p.[Purpose], p.[IdempotencyKey],
               CASE WHEN p.[VoidedAt] IS NULL THEN 'Paid' ELSE 'Reversed' END, 'Imported',
               p.[CreatedAt], p.[CreatedBy], p.[PaymentDate], p.[CreatedAt], p.[CreatedBy], p.[Id], p.[VoidedAt], p.[VoidedBy], p.[VoidReason]
        FROM [sub].[SubPayment] p
        JOIN [sub].[SubContract] c ON c.[Id] = p.[ContractId]
        WHERE NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequest] r WHERE r.[IdempotencyKey] = p.[IdempotencyKey]);

        INSERT INTO [finance].[PaymentRequestLine] ([RequestId], [RecordType], [RecordId], [ProjectId], [Amount])
        SELECT r.[Id], 'SubContract', p.[ContractId], r.[ProjectId], r.[Amount]
        FROM [finance].[PaymentRequest] r
        JOIN [sub].[SubPayment] p ON p.[Id] = r.[SourcePaymentId]
        WHERE r.[Origin] = 'Imported' AND r.[SourceType] = 'SubcontractPayment'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestLine] l WHERE l.[RequestId] = r.[Id]);

        -- 4. Equipment costs: every voided payment (history table), then the current payment of each paid cost.
        INSERT INTO [finance].[PaymentRequest]
            ([SourceType], [PayeeType], [PayeeId], [ProjectId], [Purpose], [Amount], [Method], [RequestedDate], [Reference], [Notes],
             [Payload], [IdempotencyKey], [Status], [Origin], [CreatedAt], [CreatedBy], [PaidOn], [ConfirmedAt], [ConfirmedBy],
             [SourcePaymentId], [ClosedAt], [ClosedBy], [CloseReason])
        SELECT 'EquipmentCostPayment', 'Supplier', c.[SupplierId], c.[ProjectId], 'EquipmentCost', c.[Amount], v.[PaymentMethod], v.[PaidOn],
               v.[PaymentReference], NULL, NULL,
               CAST(SUBSTRING(HASHBYTES('SHA2_256', CONCAT('legacy-equipment-voided-payment-', v.[Id])), 1, 16) AS uniqueidentifier),
               'Reversed', 'Imported', v.[PaymentRecordedAt], v.[PaymentRecordedBy], v.[PaidOn], v.[PaymentRecordedAt], v.[PaymentRecordedBy],
               c.[Id], v.[VoidedAt], v.[VoidedBy], v.[VoidReason]
        FROM [fleet].[VoidedEquipmentCostPayment] v
        JOIN [fleet].[EquipmentCost] c ON c.[Id] = v.[CostId]
        WHERE NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequest] r
                          WHERE r.[IdempotencyKey] = CAST(SUBSTRING(HASHBYTES('SHA2_256', CONCAT('legacy-equipment-voided-payment-', v.[Id])), 1, 16) AS uniqueidentifier));

        INSERT INTO [finance].[PaymentRequest]
            ([SourceType], [PayeeType], [PayeeId], [ProjectId], [Purpose], [Amount], [Method], [RequestedDate], [Reference], [Notes],
             [Payload], [IdempotencyKey], [Status], [Origin], [CreatedAt], [CreatedBy], [PaidOn], [ConfirmedAt], [ConfirmedBy],
             [SourcePaymentId], [ClosedAt], [ClosedBy], [CloseReason])
        SELECT 'EquipmentCostPayment', 'Supplier', c.[SupplierId], c.[ProjectId], 'EquipmentCost', c.[Amount], c.[PaymentMethod], c.[PaidOn],
               c.[PaymentReference], NULL, NULL,
               CAST(SUBSTRING(HASHBYTES('SHA2_256', CONCAT('legacy-equipment-payment-', c.[Id])), 1, 16) AS uniqueidentifier),
               'Paid', 'Imported', c.[PaymentRecordedAt], c.[PaymentRecordedBy], c.[PaidOn], c.[PaymentRecordedAt], c.[PaymentRecordedBy],
               c.[Id], NULL, NULL, NULL
        FROM [fleet].[EquipmentCost] c
        WHERE c.[PaidOn] IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequest] r
                          WHERE r.[SourceType] = 'EquipmentCostPayment' AND r.[SourcePaymentId] = c.[Id] AND r.[Status] = 'Paid');

        INSERT INTO [finance].[PaymentRequestLine] ([RequestId], [RecordType], [RecordId], [ProjectId], [Amount])
        SELECT r.[Id], 'EquipmentCost', r.[SourcePaymentId], r.[ProjectId], r.[Amount]
        FROM [finance].[PaymentRequest] r
        WHERE r.[Origin] = 'Imported' AND r.[SourceType] = 'EquipmentCostPayment'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestLine] l WHERE l.[RequestId] = r.[Id]);

        -- 5. History: imported as paid, and reversed when voided.
        INSERT INTO [finance].[PaymentRequestEvent] ([RequestId], [Action], [FromStatus], [ToStatus], [ActorUserId], [At], [Note])
        SELECT r.[Id], 'Import', NULL, 'Paid', r.[CreatedBy], r.[CreatedAt], 'Recorded before payment requests existed'
        FROM [finance].[PaymentRequest] r
        WHERE r.[Origin] = 'Imported'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestEvent] e WHERE e.[RequestId] = r.[Id] AND e.[Action] = 'Import');

        INSERT INTO [finance].[PaymentRequestEvent] ([RequestId], [Action], [FromStatus], [ToStatus], [ActorUserId], [At], [Note])
        SELECT r.[Id], 'Reverse', 'Paid', 'Reversed', r.[ClosedBy], r.[ClosedAt], r.[CloseReason]
        FROM [finance].[PaymentRequest] r
        WHERE r.[Origin] = 'Imported' AND r.[Status] = 'Reversed'
          AND NOT EXISTS (SELECT 1 FROM [finance].[PaymentRequestEvent] e WHERE e.[RequestId] = r.[Id] AND e.[Action] = 'Reverse');

        -- 6. Ledger: one entry per project of each imported payment (money out is negative), and what no line covers
        --    on the request's own project.
        INSERT INTO [finance].[LedgerEntry]
            ([EntryDate], [Account], [Amount], [CounterpartyType], [CounterpartyId], [ProjectId], [Purpose], [PaymentRequestId],
             [SourceType], [SourcePaymentId], [ReversesEntryId], [CreatedAt], [CreatedBy])
        SELECT r.[PaidOn], CASE WHEN r.[Method] = 'Cash' THEN 'Cash' ELSE 'Bank' END, -part.[Amount], r.[PayeeType], r.[PayeeId],
               part.[ProjectId], r.[Purpose], r.[Id], r.[SourceType], r.[SourcePaymentId], NULL, r.[ConfirmedAt], r.[ConfirmedBy]
        FROM [finance].[PaymentRequest] r
        CROSS APPLY (
            SELECT l.[ProjectId], SUM(l.[Amount]) AS [Amount]
            FROM [finance].[PaymentRequestLine] l
            WHERE l.[RequestId] = r.[Id]
            GROUP BY l.[ProjectId]
            UNION ALL
            SELECT r.[ProjectId], r.[Amount] - covered.[Amount]
            FROM (SELECT ISNULL(SUM(l.[Amount]), 0) AS [Amount] FROM [finance].[PaymentRequestLine] l WHERE l.[RequestId] = r.[Id]) covered
            WHERE r.[Amount] > covered.[Amount]) part
        WHERE r.[Origin] = 'Imported'
          AND NOT EXISTS (SELECT 1 FROM [finance].[LedgerEntry] e WHERE e.[PaymentRequestId] = r.[Id]);

        INSERT INTO [finance].[LedgerEntry]
            ([EntryDate], [Account], [Amount], [CounterpartyType], [CounterpartyId], [ProjectId], [Purpose], [PaymentRequestId],
             [SourceType], [SourcePaymentId], [ReversesEntryId], [CreatedAt], [CreatedBy])
        SELECT CAST(r.[ClosedAt] AS date), e.[Account], -e.[Amount], e.[CounterpartyType], e.[CounterpartyId], e.[ProjectId], e.[Purpose],
               e.[PaymentRequestId], e.[SourceType], e.[SourcePaymentId], e.[Id], r.[ClosedAt], r.[ClosedBy]
        FROM [finance].[LedgerEntry] e
        JOIN [finance].[PaymentRequest] r ON r.[Id] = e.[PaymentRequestId]
        WHERE r.[Origin] = 'Imported' AND r.[Status] = 'Reversed' AND e.[ReversesEntryId] IS NULL
          AND NOT EXISTS (SELECT 1 FROM [finance].[LedgerEntry] reversal WHERE reversal.[ReversesEntryId] = e.[Id]);
        """;

    /// <summary>Removes what the import added (the migration's Down). Requests made in Finance are left alone.</summary>
    public const string UndoSql = """
        DELETE e FROM [finance].[LedgerEntry] e
        JOIN [finance].[PaymentRequest] r ON r.[Id] = e.[PaymentRequestId]
        WHERE r.[Origin] = 'Imported' AND e.[ReversesEntryId] IS NOT NULL;

        DELETE e FROM [finance].[LedgerEntry] e
        JOIN [finance].[PaymentRequest] r ON r.[Id] = e.[PaymentRequestId]
        WHERE r.[Origin] = 'Imported';

        DELETE e FROM [finance].[PaymentRequestEvent] e
        JOIN [finance].[PaymentRequest] r ON r.[Id] = e.[RequestId]
        WHERE r.[Origin] = 'Imported';

        DELETE l FROM [finance].[PaymentRequestLine] l
        JOIN [finance].[PaymentRequest] r ON r.[Id] = l.[RequestId]
        WHERE r.[Origin] = 'Imported';

        DELETE FROM [finance].[PaymentRequest] WHERE [Origin] = 'Imported';
        """;
}
