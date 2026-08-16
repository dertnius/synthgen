CREATE TABLE dbo.Security (
    PropertyId   INTEGER NOT NULL PRIMARY KEY,
    SecurityId   TEXT    NULL,
    PropertyType TEXT    NOT NULL CHECK (PropertyType IN ('EFH','MFH','WHG')),
    Rooms        INTEGER NULL CHECK (Rooms IS NULL OR Rooms >= 0),
    Bathrooms    INTEGER NULL CHECK (Bathrooms IS NULL OR Bathrooms >= 1)
);
CREATE UNIQUE INDEX dbo.UQ_Security_SecurityId ON Security (SecurityId) WHERE SecurityId IS NOT NULL;
INSERT INTO dbo.Security (PropertyId, SecurityId, PropertyType, Rooms, Bathrooms) VALUES
    (101, 'DE0001234567', 'EFH',    3, NULL),
    (102, NULL,           'EFH',    5, NULL),
    (103, 'DE0007654321', 'EFH',    7, NULL),
    (104, NULL,           'EFH', NULL, NULL),
    (105, 'DE0009999999', 'EFH',    9, NULL),
    (106, 'DE0001111111', 'MFH',   12,    4),
    (107, 'DE0002222222', 'WHG',    2,    1),
    (108, 'DE0003333333', 'EFH',    4,    2);
