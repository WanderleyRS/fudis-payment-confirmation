IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE TABLE [Orders] (
        [Id] int NOT NULL IDENTITY,
        [Status] nvarchar(32) NOT NULL,
        [DriverId] int NOT NULL,
        [DeliveryFee] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE TABLE [PaymentInbox] (
        [Id] int NOT NULL IDENTITY,
        [Provider] nvarchar(32) NOT NULL,
        [ProviderEventId] nvarchar(128) NOT NULL,
        [OrderId] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [ReceivedAt] datetimeoffset NOT NULL,
        [ProcessedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_PaymentInbox] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE TABLE [RestaurantNotificationOutbox] (
        [Id] int NOT NULL IDENTITY,
        [OrderId] int NOT NULL,
        [Type] nvarchar(64) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Attempts] int NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [DeliveredAt] datetimeoffset NULL,
        [ProcessingStartedAt] datetimeoffset NULL,
        [LastError] nvarchar(max) NULL,
        CONSTRAINT [PK_RestaurantNotificationOutbox] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE TABLE [DriverEarnings] (
        [Id] int NOT NULL IDENTITY,
        [OrderId] int NOT NULL,
        [DriverId] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_DriverEarnings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DriverEarnings_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DriverEarnings_OrderId] ON [DriverEarnings] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Orders_DriverId] ON [Orders] ([DriverId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentInbox_Provider_ProviderEventId] ON [PaymentInbox] ([Provider], [ProviderEventId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RestaurantNotificationOutbox_OrderId_Type] ON [RestaurantNotificationOutbox] ([OrderId], [Type]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RestaurantNotificationOutbox_Status_NextAttemptAt] ON [RestaurantNotificationOutbox] ([Status], [NextAttemptAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105192122_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261105192122_InitialCreate', N'8.0.11');
END;
GO

COMMIT;
GO

