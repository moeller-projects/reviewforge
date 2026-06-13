from infrastructure.pi.prompts import stage_instruction
import shutil

def run(cfg, ctx):
    if not cfg.verify_findings:
        print('[review] VERIFY_FINDINGS=0; skipping verification stage', file=__import__('sys').stderr); shutil.copyfile(ctx.artifacts.candidate, ctx.artifacts.verified); return
    print('[review] running adversarial finding verification stage', file=__import__('sys').stderr)
    text = stage_instruction('finding verification', cfg, ctx.artifacts.metadata, ctx.files_text, ctx.wi_context, ctx.thread_context, ctx.paths()) + ctx.state.diff_text
    ctx.pi.run_json(cfg.verify_prompt_path, text, ctx.artifacts.verified, 'finding verification')
