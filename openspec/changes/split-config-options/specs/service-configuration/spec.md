## MODIFIED Requirements

### Requirement: Service options are bound by cohesive configuration section

The service MUST bind review, workspace, persistence, Git, and host settings to distinct typed options sections named `Review`, `Workspace`, `Persistence`, `Git`, and `Host`, respectively, rather than binding them through `ReviewForgeServiceOptions` and the `ReviewForge` section. Existing setting defaults and validation behavior MUST remain unchanged. The obsolete `ReviewForge` configuration keys MUST NOT act as aliases for the new keys.

#### Scenario: New TOML sections configure their owning options

- **WHEN** the service loads `Review`, `Workspace`, `Persistence`, `Git`, and `Host` configuration sections
- **THEN** each owning service consumer receives the configured value through its corresponding typed options class

#### Scenario: Legacy section is not a fallback

- **WHEN** a setting is supplied only through the obsolete `ReviewForge` section
- **THEN** the service uses the new setting's default and does not bind the legacy value

#### Scenario: Invalid values still fail startup validation

- **WHEN** a value in one of the new sections violates a validation rule that applied to its former `ReviewForge` setting
- **THEN** options validation rejects the configuration at startup

### Requirement: Configuration precedence and secrets remain unchanged

The service MUST preserve TOML reload behavior and MUST allow environment variables and command-line arguments to override TOML values. ADO PATs and API keys MUST remain sourced only from their existing environment variables and MUST NOT bind from the new sections.

#### Scenario: Environment variable overrides TOML

- **WHEN** a TOML value and its corresponding environment-variable value are both configured
- **THEN** the environment-variable value is effective

#### Scenario: Secrets remain environment-only

- **WHEN** an ADO PAT or API key is present only in a TOML or other configuration provider
- **THEN** the service does not use that value as a credential
