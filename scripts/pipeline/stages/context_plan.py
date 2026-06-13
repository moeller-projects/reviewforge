from infrastructure.pi.prompts import stage_instruction

def run(cfg, ctx):
    text = stage_instruction('context planning', cfg, ctx.artifacts.metadata, ctx.files_text, ctx.wi_context, ctx.thread_context, ctx.paths()) + ctx.state.diff_text
    ctx.pi.run_json(cfg.context_plan_prompt_path, text, ctx.artifacts.plan, 'context planning')
