#!/usr/bin/env bash
# Demonstrates that the architecture gate fails on a COMPILABLE planted violation (T3-03):
# an endpoint referencing the DbContext-based store must break the architecture test group.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
# Two complementary gates (stage-2 §1.2 L01):
# 1) compile-time: BannedApiAnalyzers reject banned symbols with RS0030 (the violation cannot compile);
# 2) test-time: NetArchTest fails on a compilable namespace-level violation (no banned symbol used).
set -uo pipefail
export DOTNET_CLI_UI_LANGUAGE=en-US
mkdir -p artifacts

# --- Gate 1: compile-time ban (uses the banned type directly)
TMP1="src/Motiva.Api/Endpoints/PlantedBanned.cs"
cat > "$TMP1" <<'CS'
// TEMPORARY planted compile-time violation (removed after the run).
using Motiva.Infrastructure.Persistence;

namespace Motiva.Api.Endpoints;

internal static class PlantedBanned
{
    public static int Count(MotivaDbContext db) => db.Employees.Count();
}
CS
trap 'rm -f "$TMP1" "$TMP2"' EXIT
if dotnet build src/Motiva.Api/Motiva.Api.csproj --nologo -v q > artifacts/arch-violation.log 2>&1; then
  echo "ARCH GATE FAILURE: compile-time ban did not reject the banned usage"; exit 1
fi
grep -q "RS0030" artifacts/arch-violation.log || { echo "ARCH GATE FAILURE: build failed without RS0030"; exit 1; }
rm -f "$TMP1"
echo "compile-time ban works: RS0030 rejects DbContext usage in an endpoint"

# --- Gate 2: NetArchTest on a compilable namespace violation (no banned symbol, dependency only)
TMP2="src/Motiva.Api/Endpoints/PlantedNamespace.cs"
cat > "$TMP2" <<'CS'
// TEMPORARY planted namespace violation (removed after the run): no banned symbol is named,
// the endpoint references the infrastructure namespace and compiles cleanly.
using Motiva.Infrastructure.Persistence;

namespace Motiva.Api.Endpoints;

internal static class PlantedNamespace
{
    public static string Describe(EmployeeRow row) => row.GetType().Name;
}
CS
if ! dotnet build src/Motiva.Api/Motiva.Api.csproj --nologo -v q >> artifacts/arch-violation.log 2>&1; then
  echo "ARCH GATE FAILURE: the namespace violation did not compile (see artifacts/arch-violation.log)"; exit 1
fi
if dotnet test tests/Motiva.Tests.Architecture --no-restore --nologo >> artifacts/arch-violation.log 2>&1; then
  echo "ARCH GATE FAILURE: planted namespace violation did NOT fail the tests"; exit 1
fi
grep -qE "Failed|failed" artifacts/arch-violation.log || { echo "ARCH GATE FAILURE: test run failed without assertions"; exit 1; }
echo "Arch gate works: compile-time ban (RS0030) + NetArchTest both reject planted violations (artifacts/arch-violation.log)"
