# Configuration files

Application-wide settings remain in `src/FlightDeals/appsettings.json`. Normal .NET configuration precedence, including environment overrides, applies to switches, quota, logging, connection strings, notification settings and digest settings.

The `FlightDeals` section references:

- `BaselineFile`: `config/baselines.json`
- `ProfileFiles`, in this exact order:
  1. `config/profiles/core.json`
  2. `config/profiles/historical-2027-08.json`
  3. `config/profiles/future-2027-10.json`
  4. `config/profiles/cyprus-2027-05.json`

Each file contains a complete JSON array. `FlightDealsConfiguration.Load` resolves paths relative to the content root, deserializes each document independently and concatenates profile arrays in the configured order. It validates the resulting `AppSettings` and registers it once at startup. Tokyo Premium remains the first/default profile. Scheduler priorities and tie-breaking remain unchanged.

`ProfileFiles` is deliberately atomic: the highest-priority configuration provider defining it supplies the entire list. Its indices must start at zero and be consecutive. A shorter list never inherits indices from an earlier provider. The data arrays themselves never enter the .NET configuration-provider merge system.

With no corresponding file reference, legacy inline `Profiles` (or the legacy single `Profile`) and `ManualBaselines` remain supported. Profile and baseline modes are chosen independently. Supplying both inline definitions and file references for the same data fails clearly, including an explicitly empty inline definition. Empty profile-file lists, missing/malformed files, null data entries, duplicate IDs and invalid settings fail startup. Existing profile and baseline validation remains authoritative.

Data files contain no credentials. Secrets continue to come from private environment configuration. Extracting these files changes neither database records nor search-profile serialization, notification scopes, accounting or delivery state. See [container configuration](containers.md) for staged server migration and rollback.
