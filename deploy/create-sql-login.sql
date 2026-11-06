/*
    Create the SQL Server login ScreenConnect uses in web.config.

    Run once as a sysadmin, for example:

        sqlcmd -S .\SQLEXPRESS -E -i create-sql-login.sql

    Change the password first. The login does not need a server role: it only has
    to own the two application databases. If you want sqlite2mssql.exe
    --create-database to create the databases, uncomment the dbcreator grant.

    The script is idempotent, and handles the case where the login already owns a
    database (in which case it maps to dbo and needs no explicit role grant).
*/

USE [master];
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'CWControl')
    CREATE LOGIN [CWControl]
        WITH PASSWORD = N'CHANGE-ME',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
GO
/* Only needed if sqlite2mssql.exe should create the databases itself. */
-- ALTER SERVER ROLE [dbcreator] ADD MEMBER [CWControl];
GO

/* ---------- session database ---------- */
IF DB_ID(N'ScreenConnect') IS NULL
    CREATE DATABASE [ScreenConnect];
GO
USE [ScreenConnect];
GO
DECLARE @sid varbinary(85) = SUSER_SID(N'CWControl');
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE sid = @sid)
    CREATE USER [CWControl] FOR LOGIN [CWControl];
DECLARE @user sysname = (SELECT name FROM sys.database_principals WHERE sid = @sid);
IF @user IS NOT NULL AND @user <> N'dbo'
   AND NOT EXISTS (SELECT 1
                   FROM sys.database_role_members rm
                   JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
                   JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
                   WHERE r.name = N'db_owner' AND m.name = @user)
BEGIN
    DECLARE @cmd nvarchar(max) = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@user) + N';';
    EXEC sp_executesql @cmd;
END
GO

/* ---------- security database (full SQL Server layout only) ---------- */
IF DB_ID(N'ScreenConnect_Security') IS NULL
    CREATE DATABASE [ScreenConnect_Security];
GO
USE [ScreenConnect_Security];
GO
DECLARE @sid varbinary(85) = SUSER_SID(N'CWControl');
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE sid = @sid)
    CREATE USER [CWControl] FOR LOGIN [CWControl];
DECLARE @user sysname = (SELECT name FROM sys.database_principals WHERE sid = @sid);
IF @user IS NOT NULL AND @user <> N'dbo'
   AND NOT EXISTS (SELECT 1
                   FROM sys.database_role_members rm
                   JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
                   JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
                   WHERE r.name = N'db_owner' AND m.name = @user)
BEGIN
    DECLARE @cmd nvarchar(max) = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@user) + N';';
    EXEC sp_executesql @cmd;
END
GO
