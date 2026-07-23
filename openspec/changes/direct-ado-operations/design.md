## Context

ReviewForge's pipeline and legacy ADO helper CLI currently share workflow behavior across a process boundary. The direct operation module can host the workflow composition while the CLI remains a thin adapter.

## Goals

- Remove subprocess overhead from pipeline ADO stages.
- Keep CLI arguments, artifacts, markers, and error semantics compatible.
- Keep authenticated ADO access outside Pi subprocesses.

## Design

Expose `fetch_pr_context(cfg, out_dir)` and `post_findings(cfg, findings_path, out_path)` in `reviewforge.ado.operations`. These functions adapt the existing command logic to `Config` and `Path` inputs, returning parsed results or raising the existing domain errors. Pipeline stages call them directly. `reviewforge.ado.cli` delegates module execution to `operations.main()` and remains available for external scripts.

No new dependency, protocol, or artifact name is introduced.

## Risks and Mitigations

The main risk is accidental drift between CLI and pipeline behavior. Both paths use the same operation functions, and focused CLI, stage, full-suite, and coverage checks verify compatibility.
