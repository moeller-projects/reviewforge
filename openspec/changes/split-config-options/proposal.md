# Proposal: Split service configuration into cohesive sections

## Why

`ReviewForgeServiceOptions` binds unrelated review, workspace, persistence, Git, and host settings through the `[ReviewForge]` section. Consumers receive a broad options type even when they need only one concern. This makes ownership unclear and gives the application configuration an unnecessary product/project-name root.

## What changes

Replace the `[ReviewForge]` section and `ReviewForgeServiceOptions` with five typed sections and option classes:

- `Review` / `ReviewOptions`
- `Workspace` / `WorkspaceOptions`
- `Persistence` / `PersistenceOptions`
- `Git` / `GitOptions`
- `Host` / `HostOptions`

Move settings by responsibility, preserve existing defaults and validation, update every in-repository consumer, test, and document, and remove the old section keys without compatibility aliases. Keep existing independent sections (`Reasoning`, `Ado`, `Resolve`, `AutoFix`, `VerifyFindings`, `Discovery`, `ApiDocs`, `Api`, and `RepoReadTools`) and root `Urls` unchanged. API keys and ADO PATs remain environment-only.

## Configuration migration

This is a deliberate configuration-contract change. Deployments using `ReviewForge:*` or `ReviewForge__*` must move to the new sections. No legacy-key fallback is provided.

## Non-goals

- Changing option defaults, validation rules, or runtime behavior.
- Renaming existing independent configuration sections.
- Changing TOML file names, loading order, or environment/command-line precedence.
- Binding secrets from TOML or other configuration providers.
