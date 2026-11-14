-- The SQL Server schema as releases before EF migrations left it, plus seed rows.
-- Batches are separated by a line holding only GO (SqlClient does not understand GO; the
-- test fixture splits on it).
--
-- AdsDesigns and AzureSubscriptions are what EnsureCreated made. The five other tables are the
-- old startup DDL: unnamed primary keys, an unnamed DEFAULT GETUTCDATE() on
-- AdsPlanApprovals.ApprovedAtUtc, and WITHOUT the four columns that startup used to ALTER in
-- (AdsPlanApprovals.RevertPlanJson, AdsPlanApplyEvents.TargetScope / RevertPlanHash /
-- RevertPlanJson) and the two ApplyId indexes. There is no migrations history table.
CREATE TABLE AdsDesigns (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdsDesigns PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    UserId uniqueidentifier NOT NULL,
    DesignData nvarchar(max) NOT NULL,
    LastModified datetime2 NOT NULL,
    CONSTRAINT ADS_CHECK_JSON CHECK (ISJSON(DesignData)=1));
GO
CREATE TABLE AzureSubscriptions (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AzureSubscriptions PRIMARY KEY,
    UserId uniqueidentifier NOT NULL,
    SubscriptionId uniqueidentifier NOT NULL,
    SubscriptionName nvarchar(64) NOT NULL,
    TenantId uniqueidentifier NOT NULL,
    ClientId uniqueidentifier NOT NULL,
    ClientSecret nvarchar(max) NOT NULL);
GO
CREATE TABLE AdsPlanApprovals (
    PlanHash nvarchar(71) NOT NULL PRIMARY KEY,
    PlanJson nvarchar(max) NOT NULL,
    Status nvarchar(20) NOT NULL,
    ApprovedBy nvarchar(320) NOT NULL,
    ApprovedByUserId nvarchar(64) NOT NULL,
    ApprovedAtUtc datetime2 NOT NULL DEFAULT GETUTCDATE());
GO
CREATE TABLE AdsPlanApplyEvents (
    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ApplyId uniqueidentifier NOT NULL,
    PlanHash nvarchar(71) NOT NULL,
    PlanJson nvarchar(max) NOT NULL,
    SubscriptionId nvarchar(64) NOT NULL,
    ResourceGroupName nvarchar(90) NOT NULL,
    DeploymentName nvarchar(64) NOT NULL,
    ApprovedBy nvarchar(320) NOT NULL,
    ApprovedByUserId nvarchar(64) NOT NULL,
    ApprovedAtUtc datetime2 NOT NULL,
    PreviewEvidence nvarchar(64) NULL,
    EventType nvarchar(16) NOT NULL,
    Detail nvarchar(1024) NULL,
    TimestampUtc datetime2 NOT NULL);
GO
CREATE TABLE AdsGuardrailPolicies (
    Id int NOT NULL PRIMARY KEY,
    Json nvarchar(max) NOT NULL,
    UpdatedBy nvarchar(320) NOT NULL,
    UpdatedByUserId nvarchar(64) NOT NULL,
    UpdatedAtUtc datetime2 NOT NULL);
GO
CREATE TABLE AdsDecomApprovals (
    ExecutionHash nvarchar(71) NOT NULL PRIMARY KEY,
    WindowId nvarchar(64) NOT NULL,
    PlanHash nvarchar(71) NOT NULL,
    SubscriptionId nvarchar(64) NOT NULL,
    ApprovedBy nvarchar(320) NOT NULL,
    ApprovedByUserId nvarchar(64) NOT NULL,
    ApprovedAtUtc datetime2 NOT NULL);
GO
CREATE TABLE AdsDecomWindowEvents (
    Id uniqueidentifier NOT NULL PRIMARY KEY,
    ApplyId uniqueidentifier NOT NULL,
    Event nvarchar(16) NOT NULL,
    WindowId nvarchar(64) NOT NULL,
    PlanHash nvarchar(71) NOT NULL,
    ExecutionHash nvarchar(71) NOT NULL,
    SubscriptionId nvarchar(64) NOT NULL,
    Principal nvarchar(320) NOT NULL,
    Detail nvarchar(1024) NULL,
    AtUtc datetime2 NOT NULL);
GO
INSERT INTO AdsDesigns (Name, UserId, DesignData, LastModified) VALUES
    (N'First design', '11111111-1111-1111-1111-111111111111', N'{"nodes":[],"links":[]}', '2026-01-01T10:00:00'),
    (N'Second design', '11111111-1111-1111-1111-111111111111', N'{"nodes":[{"id":"a"}],"links":[]}', '2026-01-02T10:00:00');
GO
INSERT INTO AzureSubscriptions (UserId, SubscriptionId, SubscriptionName, TenantId, ClientId, ClientSecret) VALUES
    ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', N'Sandbox',
     '33333333-3333-3333-3333-333333333333', '44444444-4444-4444-4444-444444444444', N'encrypted-secret-blob');
GO
INSERT INTO AdsPlanApprovals (PlanHash, PlanJson, Status, ApprovedBy, ApprovedByUserId) VALUES
    (N'sha256:aaaaaaaa', N'{"plan":1}', N'Approved', N'someone@example.com', N'user-1');
GO
INSERT INTO AdsPlanApplyEvents (ApplyId, PlanHash, PlanJson, SubscriptionId, ResourceGroupName, DeploymentName,
    ApprovedBy, ApprovedByUserId, ApprovedAtUtc, EventType, TimestampUtc) VALUES
    ('55555555-5555-5555-5555-555555555555', N'sha256:aaaaaaaa', N'{"plan":1}', N'sub-1', N'rg-1', N'dep-1',
     N'someone@example.com', N'user-1', '2026-01-03T10:00:00', N'Started', '2026-01-03T10:00:01');
GO
INSERT INTO AdsGuardrailPolicies (Id, Json, UpdatedBy, UpdatedByUserId, UpdatedAtUtc) VALUES
    (1, N'{}', N'someone@example.com', N'user-1', '2026-01-04T10:00:00');
GO
INSERT INTO AdsDecomApprovals (ExecutionHash, WindowId, PlanHash, SubscriptionId, ApprovedBy, ApprovedByUserId, ApprovedAtUtc) VALUES
    (N'sha256:bbbbbbbb', N'window-1', N'sha256:aaaaaaaa', N'sub-1', N'someone@example.com', N'user-1', '2026-01-05T10:00:00');
GO
INSERT INTO AdsDecomWindowEvents (Id, ApplyId, Event, WindowId, PlanHash, ExecutionHash, SubscriptionId, Principal, AtUtc) VALUES
    ('66666666-6666-6666-6666-666666666666', '55555555-5555-5555-5555-555555555555', N'Opened', N'window-1',
     N'sha256:aaaaaaaa', N'sha256:bbbbbbbb', N'sub-1', N'someone@example.com', '2026-01-05T10:00:01');
