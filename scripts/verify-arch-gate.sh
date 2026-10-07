#!/usr/bin/env bash
# Demonstrates that the architecture gate fails on a COMPILABLE planted violation (T3-03):
# an endpoint referencing the DbContext-based store must break the architecture test group.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
TMP="src/Motiva.Api/Endpoints/PlantedViolation.cs"
cat > "$TMP" <<'CS'
// TEMPORARY planted violation for the architecture gate demonstration (removed after the run).
using Motiva.Infrastructure.Persistence;

namespace Motiva.Api.Endpoints;

internal static class PlantedViolation
{
    public static int Query(MotivaDbContext db) => db.Employees.Count();
}
CS
trap 'rm -f "$TMP"' EXIT
export DOTNET_CLI_UI_LANGUAGE=en-US
if ! dotnet build src/Motiva.Api/Motiva.Api.csproj --nologo -v q > artifacts/arch-violation.log 2>&1; then
  echo "ARCH GATE FAILURE: the planted violation did not compile (see artifacts/arch-violation.log)"; exit 1
fi
if dotnet test tests/Motiva.Tests.Architecture --no-restore --nologo >> artifacts/arch-violation.log 2>&1; then
  echo "ARCH GATE FAILURE: planted violation did NOT fail the tests"; exit 1
fi
if ! grep -qE "Failed|failed" artifacts/arch-violation.log; then
  echo "ARCH GATE FAILURE: test run failed without test assertions — inspect artifacts/arch-violation.log"; exit 1
fi
echo "Arch gate works: compilable planted DbContext usage in an endpoint fails the group (artifacts/arch-violation.log)"
