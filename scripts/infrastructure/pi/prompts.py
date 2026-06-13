from __future__ import annotations
from pathlib import Path
from typing import Any
from config import Config


def system_prompt(cfg: Config) -> str:
    return cfg.review_prompt_path.read_text() + "\n\n---\n" + f'LANGUAGE: Write every "title", "message", "summary", and "suggestion" value in {cfg.review_language}. Do NOT translate file paths, identifiers, or code.\n---\n\n' + cfg.standards_path.read_text()


def stage_instruction(stage: str, cfg: Config, metadata: Path, files_text: str, wi: Any, threads: Any, paths: dict[str, Path]) -> str:
    parts = [f"{stage} stage for Azure DevOps PR #{cfg.pr_id}. Return only the JSON object requested by the system prompt.\n", "Repository/project metadata:", metadata.read_text(), "\nChanged files:", files_text, f"\nLinked work items:\n{__import__('json').dumps(wi, ensure_ascii=False)}", f"\nExisting PR comments:\n{__import__('json').dumps(threads, ensure_ascii=False)}"]
    for label, key in [("Intent reconstruction", "intent"), ("Context collection plan", "plan"), ("Runner-collected context", "collected"), ("Context digest", "digest"), ("Candidate findings", "candidate"), ("Verified findings", "verified")]:
        path = paths[key]
        if path.exists() and path.stat().st_size:
            parts += [f"\n{label}:", path.read_text()]
    return "\n".join(parts) + "\nUnified diff follows on stdin.\n"


def review_instruction(cfg: Config, files_text: str, state: Any, wi: Any, wi_comments: Any, threads: Any, intent: Path, digest: Path, chunk_label: str = "", truncated: bool = False) -> str:
    parts = ["Review the unified diff provided on stdin.", "The PR range is merge-base(target, source)..source.", f"Target branch: {state.target_branch}", f"Source branch: {state.source_branch}", f"Target commit: {state.target_commit}", f"Source commit: {state.source_commit}", f"Merge-base: {state.base_commit}\n", "Changed files:", files_text, ""]
    if chunk_label:
        parts += ["---", "LARGE DIFF CHUNK", f"This review covers {chunk_label} of a large PR split by file to preserve context. Review ONLY the files listed in this chunk. Do NOT infer missing implementation, missing work-item coverage, or other findings from files that are not present in this chunk.\n"]
    if wi:
        parts += ["---", "LINKED WORK ITEMS", "The following work items are linked to this PR. Verify that the changes fulfill each work item's description and acceptance criteria. If a requirement is not addressed by the diff, create a finding with severity at least \"major\", file=null, line=null.\n"]
        for item in wi:
            parts.append(f"Work Item #{item.get('id')} [{item.get('type')}] {item.get('title')} (State: {item.get('state')})\n  Description: {item.get('description')}\n  Acceptance Criteria: {item.get('acceptanceCriteria')}")
    if wi_comments:
        parts.append("WORK ITEM COMMENTS (respect these as additional context for requirements)")
        for group in wi_comments:
            parts.append(f"Work Item #{group.get('workItemId')} comments:")
            for comment in group.get("comments", []): parts.append(f"  [{comment.get('author')}] {str(comment.get('text',''))[:500]}")
    if threads:
        parts += ["---", "EXISTING PR COMMENTS", "The following comments already exist on this PR. Do NOT create a finding that covers the same issue already raised in these comments.\n"]
        for thread in threads:
            loc = f"{thread.get('filePath')}:{thread.get('line')}" if thread.get("filePath") else "(general)"
            parts.append(f"[{thread.get('author')}] {loc}: {str(thread.get('firstComment',''))[:300]}")
    if intent.exists(): parts += ["---", "PR INTENT RECONSTRUCTION", intent.read_text()]
    if digest.exists(): parts += ["---", "CONTEXT DIGEST", digest.read_text(), "Use this digest as evidence. If a candidate issue is plausibly intentional according to this context, do not report it."]
    if truncated: parts.append("NOTE: The diff was truncated due to size. Review only what is present and mention truncation in the summary.")
    return "\n".join(parts) + "\nReturn ONLY the JSON object defined in your instructions.\n"
