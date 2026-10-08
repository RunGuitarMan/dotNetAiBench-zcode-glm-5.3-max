#!/usr/bin/env bash
# Applies EF migrations M1..M4 to PostgreSQL 17 (T01: empty DB and step-by-step transitions).
set -euo pipefail
cd "$(dirname "$0")/.."
export Motiva__PostgresConnectionString="${Motiva__PostgresConnectionString:-${MOTIVA_PG:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}}"
dotnet tool restore >/dev/null
# A clean checkout has no build outputs: build before applying migrations (T01/T10).
dotnet restore src/Motiva.Infrastructure/Motiva.Infrastructure.csproj >/dev/null
dotnet build src/Motiva.Infrastructure/Motiva.Infrastructure.csproj --nologo -v q
dotnet ef database update --project src/Motiva.Infrastructure --no-build
