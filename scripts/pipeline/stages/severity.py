from infrastructure.pi.prompts import stage_instruction

def run(cfg, ctx):
    print('[review] running severity calibration stage', file=__import__('sys').stderr)
    text = stage_instruction('severity calibration', cfg, ctx.artifacts.metadata, ctx.files_text, ctx.wi_context, ctx.thread_context, ctx.paths()) + ctx.state.diff_text
    ctx.pi.run_json(cfg.severity_prompt_path, text, ctx.artifacts.severity, 'severity calibration')
