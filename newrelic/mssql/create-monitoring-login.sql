-- Read-only New Relic monitoring login for Azure SQL Database.
-- Run as the server admin. Replace <PASSWORD>. Azure SQL Database has no VIEW SERVER STATE grant;
-- the built-in server roles ##MS_ServerStateReader## / ##MS_DefinitionReader## are the read-only equivalents.

-- 1) Connected to the master database:
CREATE LOGIN [newrelic] WITH PASSWORD = '<PASSWORD>';
CREATE USER [newrelic] FOR LOGIN [newrelic];
ALTER SERVER ROLE ##MS_ServerStateReader## ADD MEMBER [newrelic];
ALTER SERVER ROLE ##MS_DefinitionReader## ADD MEMBER [newrelic];

-- 2) Connected to the application database (SecureDocs):
CREATE USER [newrelic] FOR LOGIN [newrelic];
GRANT VIEW DATABASE STATE TO [newrelic];
GRANT VIEW DEFINITION TO [newrelic];
