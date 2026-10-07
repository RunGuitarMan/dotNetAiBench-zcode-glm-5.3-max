#!/usr/bin/env bash
# Trusted bootstrap: companies + active administrators via application use cases (T03).
set -euo pipefail
cd "$(dirname "$0")/.."
export Motiva__PostgresConnectionString="${MOTIVA_PG:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}"
dotnet run --project tools/Motiva.Bootstrapper --no-build -- "$@"
