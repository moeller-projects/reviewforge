from infrastructure.pi.prompts import stage_instruction
from infrastructure.artifacts.builder import read_json

def run(cfg, ctx):
    text = stage_instruction('intent reconstruction', cfg, ctx.artifacts.metadata, ctx.files_text, ctx.wi_context, ctx.thread_context, ctx.paths()) + ctx.state.diff_text
    ctx.pi.run_json(cfg.intent_prompt_path, text, ctx.artifacts.intent, 'intent reconstruction')
