# One-shot schema deployment (and optional demo seed) using the sqlcmd shipped with SQL Server tools.
FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
COPY . /database
RUN chmod +x /database/deploy.sh
USER mssql
ENV SQLCMD=/opt/mssql-tools18/bin/sqlcmd
ENTRYPOINT ["/database/deploy.sh"]
