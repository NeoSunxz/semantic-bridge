# Phase 0 spike (throwaway)

Probes MSAL, Power BI REST, ADOMD (token and `app:` forms), INFO.*, TMSCHEMA and TOM
against a real tenant. Not part of the final solution. Prints no secrets or tokens.

Env vars: SBRIDGE_TENANT_ID, SBRIDGE_CLIENT_ID, SBRIDGE_CLIENT_SECRET,
SBRIDGE_WORKSPACE (name or ID), SBRIDGE_DATASET, optional SBRIDGE_CLOUD=china|global (default china).

Run: `./run.sh` (dotnet run), or `./publish.sh` (single-file linux-x64, then runs it;
`EXTRACT=1 ./publish.sh` adds IncludeNativeLibrariesForSelfExtract). `--self-test` checks the scrubber.
Missing env vars: names are listed and the exit code is 2.
