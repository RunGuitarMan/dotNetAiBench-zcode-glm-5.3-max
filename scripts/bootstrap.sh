#!/usr/bin/env bash
# Trusted bootstrap: companies + active administrators via application use cases (T03).
set -euo pipefail
cd "$(dirname "$0")/.."
export Motiva__PostgresConnectionString="${Motiva__PostgresConnectionString:-${MOTIVA_PG:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}}"
# Build first: clean checkouts have no binaries (T01/T10).
dotnet restore tools/Motiva.Bootstrapper/Motiva.Bootstrapper.csproj >/dev/null
dotnet build tools/Motiva.Bootstrapper/Motiva.Bootstrapper.csproj --nologo -v q
dotnet run --project tools/Motiva.Bootstrapper --no-build -- "$@"
