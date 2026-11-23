-- Ejecutar en la base de datos del servicio antes de desplegar el ejecutable.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'TicketBai', N'IntentosEnvio') IS NULL
    ALTER TABLE TicketBai ADD IntentosEnvio int NOT NULL
        CONSTRAINT DF_TicketBai_IntentosEnvio DEFAULT (0);

IF COL_LENGTH(N'TicketBai', N'UltimoErrorEnvio') IS NULL
    ALTER TABLE TicketBai ADD UltimoErrorEnvio nvarchar(max) NULL;

IF COL_LENGTH(N'TicketBai', N'AvisoEnvioPendiente') IS NULL
    ALTER TABLE TicketBai ADD AvisoEnvioPendiente bit NOT NULL
        CONSTRAINT DF_TicketBai_AvisoEnvioPendiente DEFAULT (0);

COMMIT TRANSACTION;
