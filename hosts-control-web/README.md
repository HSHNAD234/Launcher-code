# HSHONAD Hosts Control Web

An admin website for maintaining the approved host-domain registry in PostgreSQL. This site stores configuration data only; it does not change the Windows Hosts file or redirect network traffic. The Launcher can read the public configuration endpoint for display and diagnostics.

Deployment instructions will be added with the service files. Use a persistent PostgreSQL database and keep administrator credentials in environment variables, never in source control.
