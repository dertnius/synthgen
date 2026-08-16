-- pfandwerk fixture: dbo.Security
--
-- Worked end-to-end in docs/example-security-bathrooms.md. Two gap rules apply:
--   SEC-001  Bathrooms  derived   from Rooms, EFH rows only
--   SEC-002  SecurityId identity  ledger-backed, frozen forever
--
-- 'Security' is bracketed throughout: it is not a reserved T-SQL keyword, but it appears
-- in CREATE SECURITY POLICY and reads ambiguously unquoted.

CREATE TABLE [dbo].[Security]
(
    PropertyId    int            NOT NULL,

    -- The external reference downstream systems join on. Nullable in the fixture only
    -- because the upstream interface leaves it empty; SEC-002 fills it.
    SecurityId    nvarchar(24)   NULL,

    -- EFH = Einfamilienhaus (single-family house), MFH = Mehrfamilienhaus
    -- (multi-family), WHG = Wohnung (apartment). SEC-001 applies to EFH only.
    PropertyType  varchar(8)     NOT NULL,

    Rooms         int            NULL,

    -- NULL for EFH is the defect SEC-001 repairs. Left nullable at the database level
    -- because MFH and WHG rows legitimately arrive without it.
    Bathrooms     int            NULL,

    CONSTRAINT PK_Security PRIMARY KEY (PropertyId),
    CONSTRAINT CK_Security_PropertyType CHECK (PropertyType IN ('EFH', 'MFH', 'WHG')),
    CONSTRAINT CK_Security_Rooms CHECK (Rooms IS NULL OR Rooms >= 0),
    CONSTRAINT CK_Security_Bathrooms CHECK (Bathrooms IS NULL OR Bathrooms >= 1)
);

-- Enforced a second time by the database, as with the ledger's UQ_Ledger_Value: a
-- SecurityId collision is a data-integrity failure, not merely a planning miss.
CREATE UNIQUE INDEX UQ_Security_SecurityId
    ON [dbo].[Security] (SecurityId)
    WHERE SecurityId IS NOT NULL;
