# Scheduled runs

## Purpose

How to make `run-open-prs.ps1` fire automatically on a schedule so every active PR in your Azure DevOps project gets reviewed without you running the script by hand. Covers the free, hosted options and points at the existing local-Windows path for completeness.

## Audience

Operators who already have `run.ps1 -PrUrl <url>` working on their workstation and want the same review to run unattended (twice a day, on every push, in CI on a cron, etc.).

## Why not Cloudflare Workers

Quick no, before the alternatives:

| Constraint | Cloudflare Workers | What this bot needs |
| --- | --- | --- |
| Runtime | V8/JS, no subprocess | Must run Docker / podman, Pi (npm CLI), and Python |
| CPU time (free) | ~10–30 s max | `PI_TIMEOUT_SECS` default is 600 s; full review easily 2–5 min per PR |
| Disk | None (edge read-only) | Artifact tree under `artifacts/pr-<id>/runs/<run_id>/` |
| Memory | 128 MB | `pi --session` is fine; Python + ripgrep + the loaded repo will exceed this |
| Secrets | Encrypted vars, but no Docker token store | Pulls repo branches via git over ADO REST; bearer token must reach `git/ops.py`'s `GIT_ASKPASS_SCRIPT` |

Same disqualifiers apply to Deno Deploy, Vercel Functions, Netlify Functions, and most other Function-as-a-Service platforms. The pipeline needs a real container/VM and a filesystem.

## Hosting options compared

| Option | Free tier | Runs Docker? | Effort | Best for |
| --- | --- | --- | --- | --- |
| **GitHub Actions `schedule:`** (recommended) | 2 000 min/mo private repos; unlimited public | Yes (`ubuntu-latest` runner) | Low — drop one YAML | This repo; you already use Azure Pipelines |
| **Oracle Cloud Always Free ARM** | 4 OCPU + 24 GB RAM, $0/month, indefinite | Yes, your own VM | Medium — needs account + credit card (no charge) and an SSH session to set up cron | 24/7 scheduler with persisted state, no minute cap |
| **Fly.io free Machines** | 3 shared VMs | Yes | Medium — `fly.toml` + a scheduled machine | Lightweight Docker hosts, but cold starts can delay small runs |
| **GitLab CI scheduled pipelines** | 400 min/mo | Yes | Low — same shape as GHA | Already on GitLab |
| **Local Windows Task Scheduler** (existing path) | Free, runs on your box | Yes | Zero — `setup-open-prs-schedule.ps1` already does it | Laptop or always-on workstation |

The rest of this doc is the GitHub Actions recipe. The Windows and Oracle paths already exist (one as a script in this repo, the other is standard Linux setup); stubs at the end.

## GitHub Actions (recommended)

A scheduled workflow on `ubuntu-latest` that runs the same PowerShell wrappers you use locally. Uses the GitHub-hosted container runner, so no VM to babysit. Same 95% coverage gate does not apply — this workflow never runs the test suite.

### What you need

- A GitHub repository that contains this codebase (push it, or mirror the relevant subset).
- Repository-level **Actions secrets** for the env vars the bot otherwise reads from `.env`:

  | Secret | Notes |
  | --- | --- |
  | `ADO_ORG` | ADO org short name |
  | `ADO_PROJECT` | ADO project name |
  | `ADO_REPO_ID` | repo id or name |
  | `ADO_PAT` | PAT with **Pull Request Contribute** scope. Passed to `run-open-prs-scheduled.ps1` as `-AdoToken`. |
  | `OPENAI_API_KEY` | Required by the `pi` CLI; not read directly by the Python package. |
  | `PI_MODEL` | optional override; defaults match `azure-pipelines-pr-review.yml` |

  Precedence ends up the same as on the host: **CLI flag > secret > default**.

### File to add

Path: `.github/workflows/open-prs.yml`

```yaml
name: open-prs

on:
  schedule:
    # Twice a day, 09:30 and 15:00 UTC. Adjust to your timezone.
    - cron: '30 9,15 * * *'
  workflow_dispatch: # manual trigger from the Actions tab

permissions:
  contents: read

jobs:
  review:
    runs-on: ubuntu-latest
    timeout-minutes: 90   # hard ceiling for one review run
    env:
      ADO_ORG:           ${{ secrets.ADO_ORG }}
      ADO_PROJECT:       ${{ secrets.ADO_PROJECT }}
      ADO_REPO_ID:       ${{ secrets.ADO_REPO_ID }}
      OPENAI_API_KEY:    ${{ secrets.OPENAI_API_KEY }}
      PI_MODEL:          ${{ secrets.PI_MODEL }}
      REVIEW_LANGUAGE:   English
      FAIL_ON:           none
      VOTE_WAITING_ON:   none
    steps:
      - uses: actions/checkout@v4

      - name: Build reviewer image
        run: ./build.ps1 -PiVersion 0.79.1

      - name: Run scheduled review
        run: |
          ./run-open-prs-scheduled.ps1 \
            -AdoToken "$ADO_PAT" \
            -LogDirectory logs/open-prs
        env:
          ADO_PAT: ${{ secrets.ADO_PAT }}

      - name: Upload logs on failure
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: open-prs-logs
          path: logs/open-prs/
          if-no-files-found: ignore
```

Notes on choices made above:

- **`ubuntu-latest` + PowerShell.** The wrappers are PowerShell, but GitHub-hosted Ubuntu runners ship with `pwsh`. No Windows runner needed; you stay on the cheaper tier.
- **`workflow_dispatch`** so you can fire the same workflow by hand from the Actions tab for the first run / debugging. No secrets different from schedule.
- **Secrets to env, not to `.env`.** Forwarding each value as a job-level `env:` entry matches the precedence rules already documented in `configuration.md`.
- **`if: failure()` log upload.** Without it, the `logs/open-prs/` directory is wiped when the runner tears down. Pin to 30 days if you want to keep history.

### Tunables

- **Cron expression** — `on.schedule[0].cron`. Format is standard 5-field cron in UTC. GitHub's docs recommend a wider window than you need because scheduled jobs may be delayed during heavy load (see Caveats).
- **`timeout-minutes`** — job-level ceiling. Bump to 180 for very large PR sets; default 90 covers a single PR or a small batch.
- **`VOTE_WAITING_ON`** — controls whether the bot casts the "waiting for author" vote at/above a given severity. Default is `none` (no vote); raise to `major` to escalate cleanly.
- **`--MaxPullRequests 5`** on `run-open-prs-scheduled.ps1` — caps batch size per run so a backlog does not blow the timeout. `0` (default) means "all currently active".

### Caveats

- **Skipped runs.** GitHub does not guarantee a scheduled run fires at the exact minute under heavy load. For "review twice a day", this is fine; for "review every 5 minutes", it is not.
- **Concurrency.** Add `concurrency:` if you do not want overlapping runs:

  ```yaml
  concurrency:
    group: open-prs
    cancel-in-progress: false
  ```

- **Branch reachability.** The Actions runner checks out `refs/heads/<default>` of your GitHub repo — that is fine because `run-open-prs-scheduled.ps1` re-resolves branches through the ADO REST API; nothing depends on the local checkout being the ADO repo.
- **Disk usage.** Logs grow. Either mount `actions/upload-artifact` (above) or push them somewhere cheap on success.
- **Cost ceiling.** Minutes accumulate. Add a monthly budget alert on the GitHub org / repo Settings → Billing.

## Oracle Cloud Always Free (alternative)

Real VM, $0/month for the smallest ARM shape (4 OCPU, 24 GB RAM), no minute cap. Setup in one sitting:

1. Sign up at <cloud.oracle.com/>. Card required, never charged.
2. Create a VM.Standard.A1.Flex instance on Oracle Linux 8 or Ubuntu 22.04 (ARM).
3. SSH in, install Docker, open the firewall on 22 only:

   ```bash
   sudo dnf config-manager --add-repo=https://download.docker.com/linux/centos/docker-ce.repo
   sudo dnf install -y docker-ce
   sudo systemctl enable --now docker
   sudo usermod -aG docker $USER
   ```

4. Clone this repo and create `.env` (chmod 600).
5. Test once with `./run-open-prs.ps1 -AdoToken "$(awk -F= '/^ADO_AUTH_TOKEN/ {print $2}' .env)"`.
6. Add a cron job:

   ```cron
   30 9,15 * * *  cd /home/ubuntu/reviewforge && ./run-open-prs-scheduled.ps1 -EnvFile /home/ubuntu/.env >> /home/ubuntu/logs/cron.log 2>&1
   ```

Trade-offs vs. GitHub Actions: persistent state and no minute cap, but you own the uptime, the OS patching, and the firewall.

## Fly.io (alternative)

Lightweight Docker host. `fly launch` to deploy a tiny image that clones the repo and runs the scheduler, then `fly machines update --schedule daily` or a `cron` add-on for the times. Free allowance covers 3 shared VMs; cold starts may add 10–30 s to small runs. See <fly.io/docs/> for current free-tier limits before committing.

## Local Windows Task Scheduler (existing path)

You already have it:

```powershell
# Register twice-daily runs at 09:30 and 15:00 local time, current user
./setup-open-prs-schedule.ps1
```

What it does and how to inspect / remove the job: see the script's header block and `Get-ScheduledTask -TaskName reviewforge-open-prs`. Override times with `-Times @( '09:00', '17:00' )`.

This requires the workstation to be awake at the trigger time. It does not work for a laptop you close at 17:00.

## Caveats common to all hosted options

- **No live TTY.** Disable any interactive prompts; `run-open-prs-scheduled.ps1` already defaults to `-Interactive:$false`.
- **Timezone.** Cron is UTC on GitHub Actions. The Windows scheduler uses local time. Oracle cron uses the VM's locale. Pick whichever mental model is easiest for your on-call rotation.
- **Secrets rotation.** Rotate `ADO_PAT` and `OPENAI_API_KEY` quarterly; the bot has no automatic reminder.
- **First run cost.** The Docker build step (`./build.ps1 -PiVersion 0.79.1`) fetches Pi via npm the first time. Subsequent runs reuse the layer cache.

## Where to look next

- [`azure-pipelines-pr-review.yml`](../../azure-pipelines-pr-review.yml) — sibling pipeline that reviews a single PR on PR creation.
- [`run-open-prs-scheduled.ps1`](../../run-open-prs-scheduled.ps1) — the wrapper all three hosted recipes above call.
- [`run-open-prs.ps1`](../../run-open-prs.ps1) — what the wrapper actually does.
- [`setup-open-prs-schedule.ps1`](../../setup-open-prs-schedule.ps1) — local-Windows equivalent.
- [`docs/reference/cli.md`](cli.md) — `reviewforge` subcommands and exit codes.
- [`docs/reference/configuration.md`](configuration.md) — full env var reference; precedence rules.
