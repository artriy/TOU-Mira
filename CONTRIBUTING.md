When contributing to TOU: Mira, we implore you to make your PRs into the dev branch rather than the main branch.
Doing so will be FAR easier for everyone involved, as it means that updates are merged together properly

We will generally take some time to accept or deny Role/Modifier PRs, but that's just because we want to avoid throwing too much content at once while working away at much more needed bugfixes.
Another thing; don't try to port Sidemen-only roles into here, that's very silly to do.

## Draft mode verification

The headless regression suite links the production constraint solver, session state and startup feasibility rules without loading Unity. With .NET 8 or newer:

```shell
dotnet test tests/TownOfUs.Draft.Tests/TownOfUs.Draft.Tests.csproj --configuration Release
```

CI runs this suite after building the solution. Coverage includes shared and overlapping buckets, weighted quotas, role amounts, symmetric exclusions, an exhaustive small-draft oracle, hidden confirmations, stale sessions/offers, concurrent timers and target/pair feasibility.

For game integration, use matching Among Us, Reactor and MiraAPI builds on every client. Check small lobbies with role-list pools, hidden sidebar picks, concurrent conflicting choices, random picks, timeouts, exhausted shuffles, disconnects, host changes, recap cancellation and roster/settings changes during the native countdown. Compare final roles with host-accepted choices. Headless tests do not verify Unity rendering or multiplayer transport.