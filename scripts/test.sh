#!/usr/bin/env bash
# Runs one test layer: unit|functional|http|persistence|architecture|e2e|all (T08).
set -euo pipefail
cd "$(dirname "$0")/.."
GROUP="${1:-all}"
export Motiva__PostgresConnectionString="${MOTIVA_PG:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}"
export Motiva__ValkeyEndpoint="${MOTIVA_VALKEY:-localhost:6380}"
export Motiva__S3ServiceUrl="${MOTIVA_S3:-http://localhost:9100}"
export Motiva__S3AccessKey="motiva"
export Motiva__S3SecretKey="motiva-stand-secret"
export Motiva__S3Bucket="motiva-exports"
export MOTIVA_TEST_SIGNING_KEY="${MOTIVA_TEST_SIGNING_KEY:-dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1hYmMtZGVmIQ==}"
run() {
  echo "== dotnet test $1"
  dotnet test "tests/$1" --no-restore --logger "console;verbosity=normal" --logger "trx;LogFileName=$1.trx" --results-directory ./artifacts/tests
}
case "$GROUP" in
  unit) run Motiva.Tests.Unit ;;
  architecture) run Motiva.Tests.Architecture ;;
  persistence) run Motiva.Tests.Persistence ;;
  functional) run Motiva.Tests.Functional ;;
  http) run Motiva.Tests.Http ;;
  e2e) bash scripts/e2e/run.sh ;;
  all)
    run Motiva.Tests.Unit
    run Motiva.Tests.Architecture
    run Motiva.Tests.Persistence
    run Motiva.Tests.Functional
    run Motiva.Tests.Http
    bash scripts/e2e/run.sh
    ;;
  *) echo "usage: $0 unit|architecture|persistence|functional|http|e2e|all"; exit 2 ;;
esac
