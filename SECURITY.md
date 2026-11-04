# Security policy

## Supported versions

DotNetQuery is pre-1.0. Security fixes are released for the latest published minor version; once 1.0 ships, the
latest minor of each supported major receives them.

| Version | Supported |
| --- | --- |
| Latest published release | Yes |
| Older releases | No - upgrade to the latest release |

## Reporting a vulnerability

Please **do not** open a public issue. Report it privately through GitHub's private vulnerability reporting:
[open a security advisory](https://github.com/msynk/dotnet-query/security/advisories/new).

Include what you can of: the affected package and version, the impact, steps or code to reproduce, and any known
mitigation. You can expect an acknowledgement within 5 business days and an assessment (accepted or declined, with
the reasoning) within 14 days. Accepted reports are fixed in a patch release, with an advisory crediting you unless
you prefer otherwise.

## Scope notes

Some behaviors are by design and documented; they are not vulnerabilities on their own:

- Development tools (`QueryDevToolsPanel`, `QueryDevToolsServer`, `QueryDevTools.ExportJson`) expose query keys,
  statuses and error messages. Keep them out of production (`Enabled`, `ShowData`, loopback-only server with an
  optional access token).
- Data persisted by `QueryPersistenceManager` or queued by `PersistentMutationQueue` is stored as written; wrap the
  persister in `ProtectedQueryPersister` to encrypt it.
- `QuerySynchronizer` transports carry serialized query data to every participant; synchronize only what all of them
  may see.
- A singleton `IQueryClient` in a multi-user server shares one cache between users; use the scoped registration
  (`AddDotNetQueryBlazor`, or `AddDotNetQuery(ServiceLifetime.Scoped)`).
