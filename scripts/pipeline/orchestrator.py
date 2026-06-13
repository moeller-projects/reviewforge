from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import json, os, shutil

from config import Config
from infrastructure.ado import client as ado
from infrastructure.artifacts import manager
from infrastructure.artifacts.builder import changed_files, read_json, write_json
from infrastructure.git import ops
from infrastructure.pi.runner import PiRunner
from infrastructure.pi.prompts import system_prompt
from pipeline.validation import validate_review_doc, validate_stage
from pipeline.stages import intent, context_plan, context_collect, context_digest, findings, verify, severity

@dataclass
class ReviewContext:
    state: ops.RepoState
    artifacts: manager.Artifacts
    pi: PiRunner
    files_text: str
    wi_context: list
    wi_comments_context: list
    thread_context: list
    system_prompt: str
    artifact_tmp: Path
    def paths(self) -> dict[str, Path]:
        return {'intent': self.artifacts.intent, 'plan': self.artifacts.plan, 'collected': self.artifacts.collected, 'digest': self.artifacts.digest, 'candidate': self.artifacts.candidate, 'verified': self.artifacts.verified}


def log(message: str) -> None:
    print(f"[review] {message}", file=__import__('sys').stderr)


def ensure_tools() -> None:
    for tool in ['git','pi','rg']:
        if not shutil.which(tool): raise SystemExit(f"[review][ERROR] {tool} is required")


def should_skip(cfg: Config, metadata: dict) -> dict | None:
    if cfg.force_review: return None
    if metadata.get('isDraft') is True: return {'summary':'Skipped: PR is a draft.', 'findings': []}
    if (metadata.get('status') or 'active') != 'active': return {'summary': f"Skipped: PR status is {metadata.get('status')}.", 'findings': []}
    if cfg.review_target_branches:
        allowed={x.strip().removeprefix('refs/heads/') for x in cfg.review_target_branches.split(',') if x.strip()}
        target=str(metadata.get('targetRefName') or '').removeprefix('refs/heads/')
        if allowed and target not in allowed: return {'summary': f'Skipped: target branch {target} is not in the review policy.', 'findings': []}
    return None


def run(cfg: Config) -> int:
    ensure_tools()
    os.environ['PI_SKIP_VERSION_CHECK'] = '1'
    os.environ['PI_TELEMETRY'] = '0'
    os.environ['PI_OFFLINE'] = '0'
    source, target = ado.resolve_branches(cfg)
    artifacts = manager.create(cfg)
    state = ops.prepare_repo(cfg, source, target)
    try:
        files_text='\n'.join(state.files) + ('\n' if state.files else '')
        diff_bytes=len(state.diff_text.encode())
        log(f'changed files: {len(state.files)}')
        log(f'diff size: {diff_bytes} bytes')
        (artifacts.dir / 'diff.patch').write_text(state.diff_text)
        log('fetching Azure DevOps PR context via Python helper')
        ado.call_helper(cfg, 'fetch-context', artifacts.dir)
        metadata = read_json(artifacts.metadata)
        metadata['git'] = {
            'baseCommit': state.base_commit,
            'sourceCommit': state.source_commit,
            'targetCommit': state.target_commit,
            'changedFiles': state.files,
            'rangeSpec': state.range_spec,
        }
        write_json(artifacts.metadata, metadata)
        (artifacts.dir / 'commits.txt').write_text(ops.run_git(state.repo_dir, 'log', '--oneline', state.range_spec))
        write_json(artifacts.dir / 'changed-files.json', changed_files(state.files))
        skipped = should_skip(cfg, metadata)
        if skipped:
            print(json.dumps(skipped, ensure_ascii=False))
            return 0
        wi = read_json(artifacts.dir / 'work-items.json') if cfg.include_work_items else []
        wi_comments = read_json(artifacts.dir / 'work-item-comments.json') if cfg.include_work_items else []
        threads = read_json(artifacts.dir / 'threads.json') if cfg.include_existing_comments else []
        log(f'loaded {len(wi)} linked work item(s) and {len(threads)} existing thread(s)')
        ctx = ReviewContext(
            state, artifacts, PiRunner(cfg), files_text, wi, wi_comments, threads,
            system_prompt(cfg), artifacts.dir / '.tmp',
        )
        ctx.artifact_tmp.mkdir(exist_ok=True)
        if diff_bytes == 0:
            doc = {'summary': 'No changes to review.', 'findings': []}
            write_json(artifacts.candidate, doc)
            write_json(artifacts.final, doc)
        else:
            log('running production review preflight stages')
            intent.run(cfg, ctx)
            validate_stage(read_json(artifacts.intent), 'intent reconstruction')
            context_plan.run(cfg, ctx)
            validate_stage(read_json(artifacts.plan), 'context planning')
            context_collect.run(cfg, ctx)
            context_digest.run(cfg, ctx)
            validate_stage(read_json(artifacts.digest), 'context digest')
            findings.run(cfg, ctx)
            validate_stage(read_json(artifacts.candidate), 'candidate findings')
            verify.run(cfg, ctx)
            validate_stage(read_json(artifacts.verified), 'finding verification')
            severity.run(cfg, ctx)
            validate_stage(read_json(artifacts.severity), 'severity calibration')
            write_json(artifacts.final, read_json(artifacts.severity))
        final = read_json(artifacts.final)
        validate_review_doc(final)
        if cfg.dry_run:
            log('DRY_RUN=1; printing findings JSON')
            print(json.dumps(final, ensure_ascii=False))
            return 0
        log(f'posting findings to PR #{cfg.pr_id} via Python ADO helper')
        ado.call_helper(cfg, 'post-findings', artifacts.dir, findings=artifacts.final)
        return 0
    finally:
        ops.cleanup(state)
