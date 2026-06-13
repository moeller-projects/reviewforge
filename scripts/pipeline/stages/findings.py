from infrastructure.pi.prompts import review_instruction
from infrastructure.git.chunker import build_chunks
from infrastructure.artifacts.builder import read_json, write_json


def run(cfg, ctx):
    ctx.artifacts.system_prompt.write_text(ctx.system_prompt)

    def one(diff, files_text, out, label='', truncated=False):
        text = review_instruction(
            cfg, files_text, ctx.state, ctx.wi_context, ctx.wi_comments_context,
            ctx.thread_context, ctx.artifacts.intent, ctx.artifacts.digest, label, truncated,
        ) + diff
        ctx.pi.run_json(ctx.artifacts.system_prompt, text, out, 'reviewer')

    diff_bytes = len(ctx.state.diff_text.encode())
    if cfg.disable_chunk_review or diff_bytes <= cfg.chunk_trigger_diff_bytes:
        if cfg.disable_chunk_review and diff_bytes > cfg.chunk_trigger_diff_bytes:
            print('[review] DISABLE_CHUNK_REVIEW is enabled; reviewing large diff in a single pass', file=__import__('sys').stderr)
        one(ctx.state.diff_text, ctx.files_text, ctx.artifacts.candidate)
        return

    print('[review] diff exceeds chunk trigger; splitting review into file-based chunks', file=__import__('sys').stderr)
    chunks, truncated_any = build_chunks(ctx.state, cfg.max_diff_bytes)
    findings_list = []
    summaries = []
    seen = set()
    for i, ch in enumerate(chunks, 1):
        out = ctx.artifact_tmp / f'chunk-{i}.json'
        one(ch.diff_text, ch.files_text, out, f'chunk {i}/{len(chunks)}', ch.truncated)
        doc = read_json(out)
        summaries.append(doc.get('summary', ''))
        for f in doc.get('findings', []):
            key = (
                f.get('file') or '',
                f.get('line') or 0,
                f.get('severity') or '',
                f.get('title') or '',
                f.get('message') or '',
            )
            if key not in seen:
                seen.add(key)
                findings_list.append(f)
    write_json(
        ctx.artifacts.candidate,
        {
            'summary': (
                f"Reviewed {len(ctx.state.files)} changed file(s) across {len(chunks)} diff chunk(s)"
                + ('; oversized file diffs were truncated. ' if truncated_any else '. ')
                + ' '.join(s for s in summaries if s)
            ),
            'findings': findings_list,
        },
    )
