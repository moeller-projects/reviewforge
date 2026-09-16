from __future__ import annotations
import json

from types import SimpleNamespace

from reviewforge.pipeline.crg import architecture as architecture_module
from reviewforge.pipeline.crg import flows as flows_module
from reviewforge.pipeline.crg import snapshots


def test_crg_stage_import_boundary():
    from reviewforge.pipeline.crg import EnrichWithCrgStage as package_stage
    from reviewforge.pipeline.stages import EnrichWithCrgStage as registered_stage

    assert package_stage is registered_stage


def test_snapshot_diff_is_sorted_capped_and_tracks_changed_nodes():
    base = {"nodes": {"b": {"kind": "Function"}, "a": {"kind": "Function"}}, "edges": {"a->b:CALLS": 1}}
    source = {"nodes": {"a": {"kind": "Class"}, "c": {"kind": "Function"}}, "edges": {"c->a:CALLS": 1}}
    result = snapshots.diff_snapshots(base, source)
    assert result["added_nodes"] == ["c"]
    assert result["removed_nodes"] == ["b"]
    assert result["changed_nodes"] == ["a"]
    assert result["added_edges"] == ["c->a:CALLS"]
    assert result["removed_edges"] == ["a->b:CALLS"]


def test_api_surface_requires_surviving_source_callers():
    base = {"nodes": {"pkg.api": {"file": "pkg/api.py"}}, "edges": {}}
    source = {"nodes": {}, "edges": {}}
    assert snapshots.api_surface(base, source, ["pkg/api.py"])["breaking_candidates"] == []


def test_api_surface_marks_changed_symbols_with_incoming_calls():
    base = {"nodes": {"pkg.api": {"kind": "Function"}}, "edges": {}}
    source = {
        "nodes": {"pkg.api": {"kind": "Class"}, "pkg.caller": {"kind": "Function"}},
        "edges": {"pkg.caller->pkg.api:CALLS": 1},
    }
    result = snapshots.api_surface(base, source, [])
    assert result["breaking_candidates"] == [{
        "symbol": "pkg.api",
        "reason": "removed or changed node with surviving incoming call edges",
        "caller_count": 1,
    }]


def test_flows_unwraps_package_result_and_sorts_top_fifteen(monkeypatch):
    flows = [
        {"entry_point": f"entry.{index:02d}", "criticality": index / 100, "files": ["/repo/src/a.py"]}
        for index in range(20)
    ]
    monkeypatch.setattr(
        "code_review_graph.flows.get_affected_flows",
        lambda *_args: {"affected_flows": flows, "total": len(flows)},
    )
    monkeypatch.setattr("code_review_graph.flows.compute_criticality", lambda flow, _adj: flow["criticality"])
    result = flows_module.flows(SimpleNamespace(load_flow_adjacency=lambda: object()), ["src/a.py"])
    assert len(result["top"]) == 15
    assert [item["entry_point"] for item in result["top"]] == [f"entry.{i:02d}" for i in range(19, 4, -1)]


def test_architecture_counts_changed_nodes_and_callers(monkeypatch):
    nodes = [
        SimpleNamespace(qualified_name="changed", file_path="/repo/src/a.py"),
        SimpleNamespace(qualified_name="caller", file_path="/repo/src/b.py"),
    ]
    edges = [SimpleNamespace(source_qualified="caller", target_qualified="changed", kind="CALLS")]
    store = SimpleNamespace(
        get_all_community_ids=lambda: {"changed": 1, "caller": 2},
        get_all_nodes=lambda exclude_files=True: nodes,
        get_all_edges=lambda: edges,
    )
    monkeypatch.setattr("code_review_graph.analysis.find_hub_nodes", lambda *_args: [])
    monkeypatch.setattr("code_review_graph.analysis.find_bridge_nodes", lambda *_args: [])
    result = architecture_module.architecture(store, ["src/a.py"])
    assert result["communities_crossed"] == 2

def test_feature_failures_are_isolated_in_enrichment_stage(monkeypatch, tmp_path):
    from dataclasses import replace

    from test_context_disclosure import _cfg
    from reviewforge.artifacts import manager
    from reviewforge.pipeline.stage import StageContext
    from reviewforge.pipeline.crg import EnrichWithCrgStage

    cfg = replace(
        _cfg(tmp_path),
        crg_enabled=True,
        graph_api_diff=True,
        graph_flows=True,
        graph_arch=True,
    )
    artifacts = manager.create(cfg)
    repo_dir = tmp_path / "repo"
    repo_dir.mkdir()
    state = SimpleNamespace(
        repo_dir=repo_dir,
        files=["src/a.py"],
        range_spec="",
        base_commit="base",
        cleanup_paths=[],
    )

    class Store:
        pass

    monkeypatch.setattr("code_review_graph.graph.GraphStore", lambda _path: Store())
    monkeypatch.setattr("code_review_graph.incremental.full_build", lambda *_args, **_kwargs: {"nodes": 1})
    monkeypatch.setattr(
        "code_review_graph.changes.analyze_changes",
        lambda *_args, **_kwargs: {
            "summary": "",
            "risk_score": 0,
            "changed_functions": [],
            "affected_flows": [],
            "test_gaps": [],
            "review_priorities": [],
        },
    )
    feature_impl = {
        "api_surface": lambda *_args: {"status": "ok"},
        "flows": lambda *_args: {"status": "ok"},
        "architecture": lambda *_args: {"status": "ok"},
    }
    feature_modules = {
        "api_surface": snapshots,
        "flows": flows_module,
        "architecture": architecture_module,
    }
    for feature in ("api_surface", "flows", "architecture"):
        monkeypatch.setattr(feature_modules[feature], feature, feature_impl[feature])
    monkeypatch.setattr("code_review_graph.changes.parse_git_diff_ranges", lambda *_args: {})
    monkeypatch.setattr(snapshots, "snapshot", lambda _store: {"nodes": {}, "edges": {}})
    monkeypatch.setattr(snapshots, "build_base_snapshot", lambda *_args: {"nodes": {}, "edges": {}})

    for feature in ("api_surface", "flows", "architecture"):
        for name, implementation in feature_impl.items():
            monkeypatch.setattr(feature_modules[name], name, implementation)
        monkeypatch.setattr(
            feature_modules[feature],
            feature,
            lambda *_args, _feature=feature: (_ for _ in ()).throw(RuntimeError(_feature)),
        )
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=SimpleNamespace())
        result = EnrichWithCrgStage().run(ctx)
        assert result["crg_status"] == "ok"
        graph_context = json.loads(artifacts.graph_context.read_text())
        assert graph_context[feature]["status"] == "degraded"
        for other in ("api_surface", "flows", "architecture"):
            if other != feature:
                assert graph_context[other]["status"] == "ok"
def test_base_snapshot_cache_is_keyed_by_tool_version(monkeypatch, tmp_path):
    import subprocess
    from dataclasses import replace

    from test_context_disclosure import _cfg

    repo = tmp_path / "repo"
    repo.mkdir()
    subprocess.run(["git", "init", "-q", "-b", "main"], cwd=repo, check=True)
    subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=repo, check=True)
    subprocess.run(["git", "config", "user.name", "Test"], cwd=repo, check=True)
    (repo / "a.py").write_text("x = 1\n", encoding="utf-8")
    subprocess.run(["git", "add", "a.py"], cwd=repo, check=True)
    subprocess.run(["git", "commit", "-qm", "base"], cwd=repo, check=True)
    base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()

    cfg = replace(_cfg(tmp_path), crg_cache_dir=None)
    warm_db = tmp_path / "artifacts" / "crg-cache" / "repo" / "crg-2.3.7" / "crg.db"
    warm_db.parent.mkdir(parents=True)
    warm_db.write_bytes(b"warm-source-graph")
    warm_bytes = warm_db.read_bytes()
    warm_mtime = warm_db.stat().st_mtime_ns
    state = SimpleNamespace(repo_dir=repo, base_commit=base, cleanup_paths=[])
    ctx = SimpleNamespace(cfg=cfg, state=state)
    builds = []

    class Store:
        pass

    monkeypatch.setattr("code_review_graph.graph.GraphStore", lambda _path: Store())
    monkeypatch.setattr(
        "code_review_graph.incremental.full_build",
        lambda *_args, **_kwargs: builds.append(True) or {"nodes": 1},
    )
    monkeypatch.setattr(snapshots, "snapshot", lambda _store: {"nodes": {}, "edges": {}})

    snapshots.build_base_snapshot(ctx, "2.3.7")
    cache = tmp_path / "artifacts" / "crg-cache" / "repo" / "base-snapshots" / "crg-2.3.7" / f"{base}.json"
    first_bytes = cache.read_bytes()
    first_mtime = cache.stat().st_mtime_ns
    snapshots.build_base_snapshot(ctx, "2.3.7")
    assert cache.exists()
    assert cache.read_bytes() == first_bytes
    assert cache.stat().st_mtime_ns == first_mtime
    assert len(builds) == 1
    snapshots.build_base_snapshot(ctx, "2.3.8")
    cache_v2 = tmp_path / "artifacts" / "crg-cache" / "repo" / "base-snapshots" / "crg-2.3.8" / f"{base}.json"
    assert cache_v2.exists()
    snapshots.build_base_snapshot(ctx, "2.3.8")
    assert len(builds) == 2
    assert warm_db.read_bytes() == warm_bytes
    assert warm_db.stat().st_mtime_ns == warm_mtime
    assert len(state.cleanup_paths) == 4
    assert all(not path.exists() for path in state.cleanup_paths)
def test_wave_two_off_preserves_phase_one_shape_and_instruction(tmp_path):
    from dataclasses import replace

    from test_context_disclosure import _cfg
    from reviewforge.artifacts import manager
    from reviewforge.pipeline.stage import StageContext
    from reviewforge.reasoning.prefix import _build_single_pi_prefix

    cfg = replace(_cfg(tmp_path), crg_enabled=True)
    artifacts = manager.create(cfg)
    base = {
        "status": "ok",
        "tool_version": "2.3.7",
        "build": {"mode": "incremental", "duration_ms": 1},
        "summary": "",
        "risk_score": 0.0,
        "changed_functions": [],
        "affected_flows": [],
        "test_gaps": [],
        "impacted_files": [],
        "review_priorities": [],
    }
    state = SimpleNamespace(repo_dir=None, files=[], range_spec="", cleanup_paths=[])
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=SimpleNamespace())
    ctx.extras["graph_context"] = dict(base)
    ctx.extras["crg_analysis"] = dict(base)
    instruction = _build_single_pi_prefix(ctx)
    assert set(ctx.extras["graph_context"]) == set(base)
    assert "Deterministic API-surface changes:" not in instruction
    assert "Critical flows reached by this change:" not in instruction
    assert "Architecture facts:" not in instruction
def test_wave_two_helpers_cover_snapshot_kinds_and_failure_branches(monkeypatch, tmp_path):
    from reviewforge.pipeline.crg.flows import _flow_kind
    from reviewforge.pipeline.crg.snapshots import snapshot

    monkeypatch.setattr("code_review_graph.graph_diff.take_snapshot", lambda store: {"store": store})
    assert snapshot("store") == {"store": "store"}
    monkeypatch.setattr("code_review_graph.graph_diff.take_snapshot", lambda store: {"edges": {"b", "a"}})
    assert list(snapshot("store")["edges"]) == ["a", "b"]

    base = {f"changed.{index}": {"v": 0} for index in range(51)}
    source = {f"changed.{index}": {"v": 1} for index in range(51)}
    source.update({"caller": {}, "unrelated": {}})
    edges = {f"caller->changed.{index}:CALLS": 1 for index in range(51)}
    edges.update({"bad-edge": 1, "bad->edge:CALLS": 1, "caller->unrelated:IMPORTS": 1})
    result = snapshots.api_surface({"nodes": base, "edges": {}}, {"nodes": source, "edges": edges}, [])

    assert _flow_kind({"entry_point": "test_example"}) == "test"
    assert _flow_kind({"entry_point": "serve", "decorators": "@app.get"}) == "http handler"
    assert _flow_kind({"entry_point": "cli.main"}) == "cli"
    assert _flow_kind({"entry_point": "nightly_job"}) == "job"
    node_store = SimpleNamespace(
        get_node=lambda _name: SimpleNamespace(
            file_path="/repo/api.py", is_test=False, extra={"decorators": "@route"}
        )
    )
    assert _flow_kind({"entry_point": "serve"}, node_store) == "http handler"
    monkeypatch.setattr(
        "code_review_graph.flows.get_affected_flows",
        lambda *_args: {"affected_flows": ["bad", {"entry_point": "cli.main", "files": ["/repo/a.py"], "criticality": 0.2}]},
    )
    monkeypatch.setattr(
        "code_review_graph.flows.trace_flows",
        lambda *_args: [{"entry_point": "cli.main", "files": ["/repo/a.py"], "criticality": 0.2}],
    )
    monkeypatch.setattr("code_review_graph.flows.compute_criticality", lambda *_args: (_ for _ in ()).throw(ValueError("bad")))
    flow_store = SimpleNamespace(load_flow_adjacency=lambda: object())
    assert flows_module.flows(flow_store, ["a.py"])["top"][0]["criticality"] == 0.2
    monkeypatch.setattr("code_review_graph.flows.get_affected_flows", lambda *_args: {"affected_flows": []})
    monkeypatch.setattr("code_review_graph.flows.trace_flows", lambda *_args: [{"files": ["other.py"]}])
    assert flows_module.flows(flow_store, ["a.py"])["top"] == []

    monkeypatch.setattr(
        "code_review_graph.analysis.find_hub_nodes",
        lambda *_args: [{"qualified_name": "changed", "file": "/repo/a.py"}],
    )
    monkeypatch.setattr(
        "code_review_graph.analysis.find_bridge_nodes",
        lambda *_args: [{"qualified_name": "bridge", "file": "/repo/b.py"}],
    )
    arch_store = SimpleNamespace(
        get_all_community_ids=lambda: {"changed": 1, "caller": 2},
        get_all_nodes=lambda exclude_files=True: [
            SimpleNamespace(qualified_name="changed", file_path="/repo/a.py")
        ],
        get_all_edges=lambda: [
            SimpleNamespace(source_qualified="caller", target_qualified="changed", kind="IMPORTS")
        ],
    )
    assert architecture_module.architecture(arch_store, ["a.py"])["hubs_touched"]

    from dataclasses import replace
    from test_context_disclosure import _cfg
    cfg = replace(_cfg(tmp_path), crg_cache_dir=tmp_path / "cache")
    bad_state = SimpleNamespace(repo_dir=tmp_path, base_commit="", cleanup_paths=[])
    try:
        snapshots.build_base_snapshot(SimpleNamespace(cfg=cfg, state=bad_state), "x")
    except ValueError:
        pass
    else:
        raise AssertionError("missing base commit must fail")
    cfg = replace(_cfg(tmp_path), crg_cache_dir=tmp_path / "cache2")
    state = SimpleNamespace(repo_dir=tmp_path, base_commit="base2", cleanup_paths=[])
    monkeypatch.setattr("code_review_graph.graph.GraphStore", lambda _path: SimpleNamespace())
    monkeypatch.setattr("code_review_graph.incremental.full_build", lambda *_args, **_kwargs: {})
    monkeypatch.setattr(snapshots, "snapshot", lambda _store: {"nodes": {}, "edges": {}})
    def run_git(_repo, *args, **kwargs):
        if args[:2] == ("worktree", "remove"):
            raise RuntimeError("cleanup")
        return ""
    monkeypatch.setattr("reviewforge.git.ops.run_git", run_git)
    snapshots.build_base_snapshot(SimpleNamespace(cfg=cfg, state=state), "x")
