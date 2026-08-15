-- pfandwerk fixture seed: dbo.Security with the defects the rules repair.
--
-- Gap counts are constants the P3a tests assert against, so changing a row here changes
-- a test expectation. Both are listed in docs/example-security-bathrooms.md.
--
--   SEC-001 (PropertyType = 'EFH' AND Bathrooms IS NULL)  -> 5 rows: 101-105
--           of those, 104 has Rooms NULL and is skipped, not patched -> 4 patched
--   SEC-002 (SecurityId IS NULL)                          -> 2 rows: 102, 104
--
-- Rows 106-108 are controls: they must come out of a run byte-identical. 108 in
-- particular is an EFH that already satisfies the derivation, so it proves the gap
-- predicate does not over-select.

INSERT INTO [dbo].[Security] (PropertyId, SecurityId, PropertyType, Rooms, Bathrooms) VALUES
    -- SEC-001 gaps. Expected derivation: ceil(Rooms/3), minimum 1.
    (101, N'DE0001234567', 'EFH',    3, NULL),   -- -> 1
    (102, NULL,            'EFH',    5, NULL),   -- -> 2   also a SEC-002 gap
    (103, N'DE0007654321', 'EFH',    7, NULL),   -- -> 3
    (104, NULL,            'EFH', NULL, NULL),   -- -> skipped: Rooms unusable
                                                 --    also a SEC-002 gap
    (105, N'DE0009999999', 'EFH',    9, NULL),   -- -> 3

    -- Controls: no rule matches these.
    (106, N'DE0001111111', 'MFH',   12,    4),   -- not EFH, so SEC-001 ignores it
    (107, N'DE0002222222', 'WHG',    2,    1),   -- not EFH
    (108, N'DE0003333333', 'EFH',    4,    2);   -- EFH, already correct: 4 rooms -> 2
