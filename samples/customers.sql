-- Sample schema: a lookup table plus a customers table exercising most supported
-- DDL features (identity, PK, FK, unique, check, defaults, computed, rowversion).

CREATE TABLE [dbo].[Countries]
(
    [CountryCode]   CHAR(2)        NOT NULL CONSTRAINT PK_Countries PRIMARY KEY,
    [CountryName]   NVARCHAR(80)   NOT NULL
);

CREATE TABLE [dbo].[Customers]
(
    [CustomerId]    INT             IDENTITY(1,1) NOT NULL,
    [CustomerCode]  VARCHAR(20)     NOT NULL,
    [FirstName]     NVARCHAR(50)    NOT NULL,
    [LastName]      NVARCHAR(50)    NOT NULL,
    [Email]         NVARCHAR(120)   NOT NULL,
    [Phone]         VARCHAR(30)     NULL,
    [BirthDate]     DATE            NULL,
    [CreditLimit]   DECIMAL(12,2)   NOT NULL,
    [IsActive]      BIT             NOT NULL CONSTRAINT DF_Customers_IsActive DEFAULT (1),
    [Status]        VARCHAR(10)     NOT NULL,
    [CountryCode]   CHAR(2)         NOT NULL,
    [CreatedAt]     DATETIME2(3)    NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSUTCDATETIME()),
    [ExternalId]    UNIQUEIDENTIFIER NOT NULL,
    [Notes]         NVARCHAR(MAX)   NULL,
    [FullName]      AS ([FirstName] + N' ' + [LastName]),
    [RowVer]        ROWVERSION      NOT NULL,

    CONSTRAINT [PK_Customers] PRIMARY KEY ([CustomerId]),
    CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email]),
    CONSTRAINT [UQ_Customers_Code] UNIQUE ([CustomerCode]),
    CONSTRAINT [FK_Customers_Countries] FOREIGN KEY ([CountryCode])
        REFERENCES [dbo].[Countries] ([CountryCode]),
    CONSTRAINT [CK_Customers_Status] CHECK ([Status] IN ('Active', 'Inactive', 'Pending')),
    CONSTRAINT [CK_Customers_CreditLimit] CHECK ([CreditLimit] >= 0)
);
