-- Legacy workspace.db fixture (L1 oldest).
-- Only the six model tables; Scans has no ManifestJson/CheckpointJson; no additive tables.
-- Built from the DDL WorkspaceDb produced before the migration baseline (EnsureCreated plus the
-- guarded additive statements). Plain SQL on purpose: no binary database files, no EF.
-- The file carries no migration history table, like every field file written by that code.

CREATE TABLE "Designs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Designs" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Data" TEXT NOT NULL,
    "SavedAt" TEXT NOT NULL,
    "SyncedAt" TEXT NULL
);
CREATE TABLE "Scans" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Scans" PRIMARY KEY,
    "Scope" TEXT NOT NULL,
    "ScopeType" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "StartedAt" TEXT NOT NULL,
    "FinishedAt" TEXT NULL,
    "ContentHash" TEXT NULL,
    "ResourceCount" INTEGER NOT NULL,
    "Error" TEXT NULL
);
CREATE TABLE "ResourceSnapshots" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ResourceSnapshots" PRIMARY KEY AUTOINCREMENT,
    "ResourceId" TEXT NOT NULL,
    "ResourceType" TEXT NOT NULL,
    "ScanId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Location" TEXT NULL,
    "ResourceGroup" TEXT NULL,
    "SubscriptionId" TEXT NULL,
    "TagsJson" TEXT NULL,
    "PropertiesJson" TEXT NULL,
    "TakenAt" TEXT NOT NULL,
    CONSTRAINT "FK_ResourceSnapshots_Scans_ScanId" FOREIGN KEY ("ScanId") REFERENCES "Scans" ("Id") ON DELETE CASCADE
);
CREATE TABLE "CostCache" (
    "Key" TEXT NOT NULL CONSTRAINT "PK_CostCache" PRIMARY KEY,
    "Json" TEXT NOT NULL,
    "FetchedAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NOT NULL
);
CREATE TABLE "ExecutionPlans" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ExecutionPlans" PRIMARY KEY,
    "DesignName" TEXT NOT NULL,
    "Json" TEXT NOT NULL,
    "PlanHash" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ApprovedBy" TEXT NULL,
    "ApprovedAt" TEXT NULL,
    "ExecutedAt" TEXT NULL
);
CREATE TABLE "AuditRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditRecords" PRIMARY KEY AUTOINCREMENT,
    "Timestamp" TEXT NOT NULL,
    "Actor" TEXT NOT NULL,
    "Action" TEXT NOT NULL,
    "Target" TEXT NOT NULL,
    "PlanHash" TEXT NULL,
    "DetailJson" TEXT NULL
);
CREATE INDEX "IX_AuditRecords_Timestamp" ON "AuditRecords" ("Timestamp");
CREATE INDEX "IX_CostCache_ExpiresAt" ON "CostCache" ("ExpiresAt");
CREATE UNIQUE INDEX "IX_Designs_Name" ON "Designs" ("Name");
CREATE INDEX "IX_Designs_SyncedAt" ON "Designs" ("SyncedAt");
CREATE INDEX "IX_ExecutionPlans_PlanHash" ON "ExecutionPlans" ("PlanHash");
CREATE INDEX "IX_ExecutionPlans_Status" ON "ExecutionPlans" ("Status");
CREATE INDEX "IX_ResourceSnapshots_ResourceId_TakenAt" ON "ResourceSnapshots" ("ResourceId", "TakenAt");
CREATE INDEX "IX_ResourceSnapshots_ScanId_ResourceId" ON "ResourceSnapshots" ("ScanId", "ResourceId");
CREATE INDEX "IX_Scans_Scope_StartedAt" ON "Scans" ("Scope", "StartedAt");

-- Seeded rows, parents before children.
INSERT INTO "Designs" ("Id","Name","Data","SavedAt","SyncedAt") VALUES
  ('d-alpha', 'alpha', '{"nodes":1}', '2026-03-01 10:11:01.1234567+00:00', '2026-03-05 10:11:05.1234567+00:00'),
  ('d-beta', 'beta', '{"nodes":2}', '2026-03-02 10:11:02.1234567+00:00', NULL),
  ('d-gamma', 'gamma', '{"nodes":3}', '2026-03-03 10:11:03.1234567+00:00', '2026-03-06 10:11:06.1234567+00:00');
INSERT INTO "Scans" ("Id","Scope","ScopeType","Status","StartedAt","FinishedAt","ContentHash","ResourceCount","Error") VALUES
  ('s-1', '/subscriptions/11111111-1111-1111-1111-111111111111', 'subscription', 'Completed', '2026-03-10 10:11:01.1234567+00:00', '2026-03-10 10:11:59.1234567+00:00', 'hash-1', 2, NULL),
  ('s-2', '/subscriptions/11111111-1111-1111-1111-111111111111', 'subscription', 'Partial', '2026-03-11 10:11:01.1234567+00:00', '2026-03-11 10:11:59.1234567+00:00', 'hash-2', 2, 'one group failed');
INSERT INTO "ResourceSnapshots" ("ResourceId","ResourceType","ScanId","Name","Location","ResourceGroup","SubscriptionId","TagsJson","PropertiesJson","TakenAt") VALUES
  ('/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-fx/providers/Microsoft.Network/virtualNetworks/vnet-a', 'microsoft.network/virtualnetworks', 's-1', 'vnet-a', 'westeurope', 'rg-fx', '11111111-1111-1111-1111-111111111111', '{"env":"fx"}', NULL, '2026-03-10 10:11:02.1234567+00:00'),
  ('/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-fx/providers/Microsoft.Storage/storageAccounts/stfx', 'microsoft.storage/storageaccounts', 's-1', 'stfx', 'westeurope', 'rg-fx', '11111111-1111-1111-1111-111111111111', NULL, '{"sku":"Standard_LRS"}', '2026-03-10 10:11:03.1234567+00:00'),
  ('/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-fx/providers/Microsoft.Network/virtualNetworks/vnet-a', 'microsoft.network/virtualnetworks', 's-2', 'vnet-a', 'westeurope', 'rg-fx', '11111111-1111-1111-1111-111111111111', '{"env":"fx"}', NULL, '2026-03-11 10:11:02.1234567+00:00'),
  ('/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-fx/providers/Microsoft.Storage/storageAccounts/stfx', 'microsoft.storage/storageaccounts', 's-2', 'stfx', 'westeurope', 'rg-fx', '11111111-1111-1111-1111-111111111111', NULL, NULL, '2026-03-11 10:11:03.1234567+00:00');
INSERT INTO "CostCache" ("Key","Json","FetchedAt","ExpiresAt") VALUES
  ('item-1|westeurope|Standard_LRS', '{"price":1.5}', '2026-03-12 10:11:01.1234567+00:00', '2026-03-13 10:11:01.1234567+00:00'),
  ('item-2|northeurope|Premium_LRS', '{"price":2.5}', '2026-03-12 10:11:02.1234567+00:00', '2026-03-13 10:11:02.1234567+00:00');
INSERT INTO "ExecutionPlans" ("Id","DesignName","Json","PlanHash","Status","CreatedAt","ApprovedBy","ApprovedAt","ExecutedAt") VALUES
  ('p-1', 'alpha', '{"steps":1}', 'planhash-1', 'Approved', '2026-03-14 10:11:01.1234567+00:00', 'fx-user', '2026-03-14 10:11:02.1234567+00:00', NULL),
  ('p-2', 'beta', '{"steps":2}', 'planhash-2', 'Draft', '2026-03-15 10:11:01.1234567+00:00', NULL, NULL, NULL);
INSERT INTO "AuditRecords" ("Timestamp","Actor","Action","Target","PlanHash","DetailJson") VALUES
  ('2026-03-16 10:11:01.1234567+00:00', 'fx-user', 'plan.approve', 'p-1', 'planhash-1', NULL),
  ('2026-03-16 10:11:02.1234567+00:00', 'fx-user', 'scan.start', 's-1', NULL, '{"scope":"sub"}'),
  ('2026-03-16 10:11:03.1234567+00:00', 'fx-user', 'scan.finish', 's-2', NULL, NULL);
