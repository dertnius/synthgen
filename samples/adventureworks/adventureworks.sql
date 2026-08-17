-- AdventureWorks-compatible schema subset (structure follows Microsoft's public
-- AdventureWorks sample database). Eleven tables across four schemas, exercising:
-- cross-schema FKs, composite PKs with and without IDENTITY, computed columns,
-- NEWID()/GETDATE() defaults, ROWGUIDCOL, money/nchar/tinyint/date types, reserved-word
-- column names, and CHECK constraints. Dependency order: top to bottom.

CREATE TABLE [Person].[Person](
    [PersonID] [int] IDENTITY(1,1) NOT NULL,
    [FirstName] [nvarchar](50) NOT NULL,
    [LastName] [nvarchar](50) NOT NULL,
    CONSTRAINT [PK_Person_PersonID] PRIMARY KEY CLUSTERED ([PersonID])
);

CREATE TABLE [Production].[ProductCategory](
    [ProductCategoryID] [int] IDENTITY(1,1) NOT NULL,
    [Name] [nvarchar](50) NOT NULL,
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_ProductCategory_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_ProductCategory_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_ProductCategory_ProductCategoryID] PRIMARY KEY CLUSTERED ([ProductCategoryID]),
    CONSTRAINT [AK_ProductCategory_Name] UNIQUE ([Name]),
    CONSTRAINT [AK_ProductCategory_rowguid] UNIQUE ([rowguid])
);

CREATE TABLE [Production].[ProductSubcategory](
    [ProductSubcategoryID] [int] IDENTITY(1,1) NOT NULL,
    [ProductCategoryID] [int] NOT NULL,
    [Name] [nvarchar](50) NOT NULL,
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_ProductSubcategory_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_ProductSubcategory_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_ProductSubcategory_ProductSubcategoryID] PRIMARY KEY CLUSTERED ([ProductSubcategoryID]),
    CONSTRAINT [AK_ProductSubcategory_Name] UNIQUE ([Name]),
    CONSTRAINT [FK_ProductSubcategory_ProductCategory_ProductCategoryID] FOREIGN KEY ([ProductCategoryID])
        REFERENCES [Production].[ProductCategory] ([ProductCategoryID])
);

CREATE TABLE [Production].[Product](
    [ProductID] [int] IDENTITY(1,1) NOT NULL,
    [Name] [nvarchar](50) NOT NULL,
    [ProductNumber] [nvarchar](25) NOT NULL,
    [MakeFlag] [bit] NOT NULL CONSTRAINT [DF_Product_MakeFlag] DEFAULT (1),
    [FinishedGoodsFlag] [bit] NOT NULL CONSTRAINT [DF_Product_FinishedGoodsFlag] DEFAULT (1),
    [Color] [nvarchar](15) NULL,
    [SafetyStockLevel] [smallint] NOT NULL,
    [ReorderPoint] [smallint] NOT NULL,
    [StandardCost] [money] NOT NULL,
    [ListPrice] [money] NOT NULL,
    [Size] [nvarchar](5) NULL,
    [Weight] [decimal](8, 2) NULL,
    [DaysToManufacture] [int] NOT NULL,
    [ProductLine] [nchar](2) NULL,
    [Class] [nchar](2) NULL,
    [Style] [nchar](2) NULL,
    [ProductSubcategoryID] [int] NULL,
    [SellStartDate] [datetime] NOT NULL,
    [SellEndDate] [datetime] NULL,
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_Product_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_Product_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_Product_ProductID] PRIMARY KEY CLUSTERED ([ProductID]),
    CONSTRAINT [AK_Product_Name] UNIQUE ([Name]),
    CONSTRAINT [AK_Product_ProductNumber] UNIQUE ([ProductNumber]),
    CONSTRAINT [FK_Product_ProductSubcategory_ProductSubcategoryID] FOREIGN KEY ([ProductSubcategoryID])
        REFERENCES [Production].[ProductSubcategory] ([ProductSubcategoryID]),
    CONSTRAINT [CK_Product_SafetyStockLevel] CHECK ([SafetyStockLevel] > 0),
    CONSTRAINT [CK_Product_ReorderPoint] CHECK ([ReorderPoint] > 0),
    CONSTRAINT [CK_Product_StandardCost] CHECK ([StandardCost] >= 0.00),
    CONSTRAINT [CK_Product_ListPrice] CHECK ([ListPrice] >= 0.00),
    CONSTRAINT [CK_Product_DaysToManufacture] CHECK ([DaysToManufacture] >= 0),
    CONSTRAINT [CK_Product_ProductLine] CHECK ([ProductLine] IN ('R', 'M', 'T', 'S') OR [ProductLine] IS NULL),
    CONSTRAINT [CK_Product_SellEndDate] CHECK ([SellEndDate] >= [SellStartDate] OR [SellEndDate] IS NULL)
);

CREATE TABLE [Sales].[SalesTerritory](
    [TerritoryID] [int] IDENTITY(1,1) NOT NULL,
    [Name] [nvarchar](50) NOT NULL,
    [CountryRegionCode] [nvarchar](3) NOT NULL,
    [Group] [nvarchar](50) NOT NULL,
    [SalesYTD] [money] NOT NULL CONSTRAINT [DF_SalesTerritory_SalesYTD] DEFAULT (0.00),
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_SalesTerritory_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_SalesTerritory_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_SalesTerritory_TerritoryID] PRIMARY KEY CLUSTERED ([TerritoryID]),
    CONSTRAINT [AK_SalesTerritory_Name] UNIQUE ([Name]),
    CONSTRAINT [CK_SalesTerritory_SalesYTD] CHECK ([SalesYTD] >= 0.00)
);

CREATE TABLE [Sales].[Customer](
    [CustomerID] [int] IDENTITY(1,1) NOT NULL,
    [PersonID] [int] NULL,
    [StoreID] [int] NULL,
    [TerritoryID] [int] NULL,
    [AccountNumber] AS (ISNULL('AW' + CONVERT([varchar](8), [CustomerID]), '')),
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_Customer_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_Customer_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_Customer_CustomerID] PRIMARY KEY CLUSTERED ([CustomerID]),
    CONSTRAINT [FK_Customer_SalesTerritory_TerritoryID] FOREIGN KEY ([TerritoryID])
        REFERENCES [Sales].[SalesTerritory] ([TerritoryID])
);

-- Name is nullable in this subset (AdventureWorks proper says NOT NULL) so the D-C3
-- currency fixture can seed the NULL-name gap CUR-001 repairs.
CREATE TABLE [Sales].[Currency](
    [CurrencyCode] [nchar](3) NOT NULL,
    [Name] [nvarchar](50) NULL,
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_Currency_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_Currency_CurrencyCode] PRIMARY KEY CLUSTERED ([CurrencyCode])
);

CREATE TABLE [Sales].[SalesOrderHeader](
    [SalesOrderID] [int] IDENTITY(1,1) NOT NULL,
    [RevisionNumber] [tinyint] NOT NULL CONSTRAINT [DF_SalesOrderHeader_RevisionNumber] DEFAULT (0),
    [OrderDate] [datetime] NOT NULL CONSTRAINT [DF_SalesOrderHeader_OrderDate] DEFAULT (GETDATE()),
    [DueDate] [datetime] NOT NULL,
    [ShipDate] [datetime] NULL,
    [Status] [tinyint] NOT NULL CONSTRAINT [DF_SalesOrderHeader_Status] DEFAULT (1),
    [OnlineOrderFlag] [bit] NOT NULL CONSTRAINT [DF_SalesOrderHeader_OnlineOrderFlag] DEFAULT (1),
    [CustomerID] [int] NOT NULL,
    [TerritoryID] [int] NULL,
    [SubTotal] [money] NOT NULL CONSTRAINT [DF_SalesOrderHeader_SubTotal] DEFAULT (0.00),
    [TaxAmt] [money] NOT NULL CONSTRAINT [DF_SalesOrderHeader_TaxAmt] DEFAULT (0.00),
    [Freight] [money] NOT NULL CONSTRAINT [DF_SalesOrderHeader_Freight] DEFAULT (0.00),
    [TotalDue] AS (ISNULL([SubTotal] + [TaxAmt] + [Freight], 0)),
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_SalesOrderHeader_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_SalesOrderHeader_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_SalesOrderHeader_SalesOrderID] PRIMARY KEY CLUSTERED ([SalesOrderID]),
    CONSTRAINT [AK_SalesOrderHeader_rowguid] UNIQUE ([rowguid]),
    CONSTRAINT [FK_SalesOrderHeader_Customer_CustomerID] FOREIGN KEY ([CustomerID])
        REFERENCES [Sales].[Customer] ([CustomerID]),
    CONSTRAINT [FK_SalesOrderHeader_SalesTerritory_TerritoryID] FOREIGN KEY ([TerritoryID])
        REFERENCES [Sales].[SalesTerritory] ([TerritoryID]),
    CONSTRAINT [CK_SalesOrderHeader_Status] CHECK ([Status] >= 0 AND [Status] <= 8),
    CONSTRAINT [CK_SalesOrderHeader_DueDate] CHECK ([DueDate] >= [OrderDate]),
    CONSTRAINT [CK_SalesOrderHeader_ShipDate] CHECK ([ShipDate] >= [OrderDate] OR [ShipDate] IS NULL),
    CONSTRAINT [CK_SalesOrderHeader_SubTotal] CHECK ([SubTotal] >= 0.00),
    CONSTRAINT [CK_SalesOrderHeader_TaxAmt] CHECK ([TaxAmt] >= 0.00),
    CONSTRAINT [CK_SalesOrderHeader_Freight] CHECK ([Freight] >= 0.00)
);

CREATE TABLE [Sales].[SalesOrderDetail](
    [SalesOrderID] [int] NOT NULL,
    [SalesOrderDetailID] [int] IDENTITY(1,1) NOT NULL,
    [CarrierTrackingNumber] [nvarchar](25) NULL,
    [OrderQty] [smallint] NOT NULL,
    [ProductID] [int] NOT NULL,
    [UnitPrice] [money] NOT NULL,
    [UnitPriceDiscount] [money] NOT NULL CONSTRAINT [DF_SalesOrderDetail_UnitPriceDiscount] DEFAULT (0.0),
    [LineTotal] AS (ISNULL([UnitPrice] * (1.0 - [UnitPriceDiscount]) * [OrderQty], 0.0)),
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_SalesOrderDetail_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_SalesOrderDetail_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_SalesOrderDetail_SalesOrderID_SalesOrderDetailID]
        PRIMARY KEY CLUSTERED ([SalesOrderID], [SalesOrderDetailID]),
    CONSTRAINT [FK_SalesOrderDetail_SalesOrderHeader_SalesOrderID] FOREIGN KEY ([SalesOrderID])
        REFERENCES [Sales].[SalesOrderHeader] ([SalesOrderID]),
    CONSTRAINT [FK_SalesOrderDetail_Product_ProductID] FOREIGN KEY ([ProductID])
        REFERENCES [Production].[Product] ([ProductID]),
    CONSTRAINT [CK_SalesOrderDetail_OrderQty] CHECK ([OrderQty] > 0),
    CONSTRAINT [CK_SalesOrderDetail_UnitPrice] CHECK ([UnitPrice] >= 0.00),
    CONSTRAINT [CK_SalesOrderDetail_UnitPriceDiscount] CHECK ([UnitPriceDiscount] >= 0.00)
);

-- HumanResources subset. Employee keys off the Person rows above; this subset's person
-- key is [PersonID], so the FK below crosses the AdventureWorks naming seam rather than
-- renaming the employee column the rest of the HR schema references.
CREATE TABLE [HumanResources].[Employee](
    [BusinessEntityID] [int] NOT NULL,
    [NationalIDNumber] [nvarchar](15) NOT NULL,
    [LoginID] [nvarchar](256) NOT NULL,
    [JobTitle] [nvarchar](50) NOT NULL,
    [HireDate] [date] NOT NULL,
    [SalariedFlag] [bit] NOT NULL CONSTRAINT [DF_Employee_SalariedFlag] DEFAULT (1),
    [CurrentFlag] [bit] NOT NULL CONSTRAINT [DF_Employee_CurrentFlag] DEFAULT (1),
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_Employee_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_Employee_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_Employee_BusinessEntityID] PRIMARY KEY CLUSTERED ([BusinessEntityID]),
    CONSTRAINT [AK_Employee_NationalIDNumber] UNIQUE ([NationalIDNumber]),
    CONSTRAINT [AK_Employee_rowguid] UNIQUE ([rowguid]),
    CONSTRAINT [FK_Employee_Person_BusinessEntityID] FOREIGN KEY ([BusinessEntityID])
        REFERENCES [Person].[Person] ([PersonID])
);

-- Compensation records. Two firsts for this subset: a composite PK with no IDENTITY
-- member (both halves come from rules), and columns that in production would hold real
-- tax and bank identifiers — the reason this table is generated and never copied.
CREATE TABLE [HumanResources].[EmployeeFinancials](
    [BusinessEntityID] [int] NOT NULL,
    [EffectiveDate] [date] NOT NULL,
    [BaseSalary] [money] NOT NULL,
    [BonusTarget] [money] NOT NULL CONSTRAINT [DF_EmployeeFinancials_BonusTarget] DEFAULT (0),
    [CurrencyCode] [nchar](3) NOT NULL,
    [PayFrequency] [tinyint] NOT NULL,      -- 1 = monthly, 2 = biweekly
    [TaxID] [nvarchar](20) NULL,
    [IBAN] [nvarchar](34) NULL,
    [BankName] [nvarchar](60) NULL,
    [rowguid] [uniqueidentifier] ROWGUIDCOL NOT NULL CONSTRAINT [DF_EmployeeFinancials_rowguid] DEFAULT (NEWID()),
    [ModifiedDate] [datetime] NOT NULL CONSTRAINT [DF_EmployeeFinancials_ModifiedDate] DEFAULT (GETDATE()),
    CONSTRAINT [PK_EmployeeFinancials_BusinessEntityID_EffectiveDate]
        PRIMARY KEY CLUSTERED ([BusinessEntityID], [EffectiveDate]),
    CONSTRAINT [FK_EmployeeFinancials_Employee_BusinessEntityID] FOREIGN KEY ([BusinessEntityID])
        REFERENCES [HumanResources].[Employee] ([BusinessEntityID]),
    CONSTRAINT [FK_EmployeeFinancials_Currency_CurrencyCode] FOREIGN KEY ([CurrencyCode])
        REFERENCES [Sales].[Currency] ([CurrencyCode]),
    CONSTRAINT [CK_EmployeeFinancials_BaseSalary] CHECK ([BaseSalary] > 0),
    CONSTRAINT [CK_EmployeeFinancials_BonusTarget] CHECK ([BonusTarget] >= 0),
    CONSTRAINT [CK_EmployeeFinancials_PayFrequency] CHECK ([PayFrequency] IN (1, 2))
);
