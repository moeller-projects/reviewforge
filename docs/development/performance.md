# Performance

**Purpose:** record implemented performance controls. **Audience:** maintainers. **Mode:** explanation.

The implementation limits diff size with `MAX_DIFF_BYTES`, can chunk review work at `CHUNK_TRIGGER_DIFF_BYTES`, caps collected file lines and search matches, and bounds context workers with `COLLECT_CONTEXT_WORKERS`. Pi session reuse avoids resending full context across compatible calls. Multi-stage metrics expose token and duration costs.

The repository does not define a separate performance budget or benchmark command. Measure with `ReviewMetrics` and controlled runs before adding complexity.
