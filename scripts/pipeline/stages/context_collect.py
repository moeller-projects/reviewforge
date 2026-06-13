from __future__ import annotations
import subprocess
from infrastructure.artifacts.builder import read_json, write_json


def run(cfg, ctx):
    print('[review] collecting deterministic context from context plan', file=__import__('sys').stderr)
    plan = read_json(ctx.artifacts.plan)
    result = {'files': [], 'tests': [], 'searches': []}
    max_lines = int(__import__('os').getenv('CONTEXT_FILE_MAX_LINES', '260'))
    max_matches = int(__import__('os').getenv('CONTEXT_SEARCH_MAX_MATCHES', '40'))
    def safe(path):
        from pathlib import Path
        if not path:
            return None
        try:
            resolved = (ctx.state.repo_dir / path).resolve()
            if not resolved.is_relative_to(ctx.state.repo_dir.resolve()):
                return None
        except (ValueError, OSError):
            return None
        return resolved if resolved.is_file() else None
    for item in plan.get('files_to_read', []):
        if not isinstance(item, dict):
            continue
        p = safe(str(item.get('path', '')))
        if p:
            lines = p.read_text(errors='replace').splitlines()
            result['files'].append({
                'path': str(p.relative_to(ctx.state.repo_dir)),
                'reason': item.get('reason', ''),
                'truncated': len(lines) > max_lines,
                'content': '\n'.join(lines[:max_lines]),
            })
    for hint in plan.get('tests_to_inspect', []):
        p = safe(str(hint))
        if p:
            result['tests'].append({
                'path': str(p.relative_to(ctx.state.repo_dir)),
                'content': '\n'.join(p.read_text(errors='replace').splitlines()[:max_lines]),
            })
    for item in plan.get('searches_to_run', []):
        if not isinstance(item, dict) or not item.get('query'):
            continue
        cp = subprocess.run(
            ['rg', '-n', '--fixed-strings', '--glob', '!/.git/**', '--glob', '!node_modules/**', '--glob', '!artifacts/**', '--', str(item['query']), '.'],
            cwd=str(ctx.state.repo_dir),
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
        result['searches'].append({
            'query': item['query'],
            'reason': item.get('reason', ''),
            'matches': '\n'.join(cp.stdout.decode(errors='replace').splitlines()[:max_matches]),
        })
    write_json(ctx.artifacts.collected, result)
