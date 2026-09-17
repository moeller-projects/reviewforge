# Deterministic review boundaries

Review runs must be isolated, provenance-bearing, and unable to post findings outside the authoritative PR scope. The change makes Pi sessions run-scoped, records the selected range and scope outcome in `run-summary.json`, rejects contradictory ADO/local file sets, requires changed-line anchors for `single_pi`, and makes finding markers independent of line movement.

Non-goals: changing the canonical `ReviewResult` schema, renaming stable artifacts, or changing ADO posting marker syntax.
