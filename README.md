# eof-bot

A self-hosted chatbot that answers employees' questions **only from your own documents**, with section-level citations. Built with ASP.NET Core (.NET 8) and [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF).

Documents are Markdown files (for example Word documents converted to `.md`). The bot puts all of them into the agent's instructions, answers with a citation such as `(Nguồn: leave-policy.md § 1.2)`, and says so when the documents do not cover a question. It also ships an admin area for editing documents and prompts, running a benchmark, grading real chats with an LLM judge, and tracking token usage and quotas.

> The default prompt, judge prompts and web UI are in **Vietnamese**, because the project was built for Vietnamese documents. The prompts can be edited from the admin page; the UI strings live in `wwwroot/`.

## Features

- **Grounded answers with citations**: answers only from the loaded documents, cites `file § section`, and refuses when the documents are silent. The chat UI turns citations into clickable numbers that open the cited section.
- **Follow-up questions**: conversation history is kept per session (MAF `AgentSession`, stored in SQLite).
- **Images in chat**: paste, drag and drop or attach screenshots; they are compressed in the browser and sent to the model.
- **Microsoft Entra ID sign-in** (single tenant), admin list by email, and immediate lockout of accounts disabled in Entra (via Microsoft Graph). Sign-in can be turned off for trusted networks.
- **Admin area** (`/admin`): manage documents, prompt versions (with a test chat on drafts), benchmark questions and test runs.
- **Evaluation**: an LLM judge grades answers on 8 criteria (understanding, context, grounding, completeness, correct refusal, citation, conciseness, compliance), both for the benchmark and for a daily random sample of real chats.
- **Usage and quotas**: tokens per user/source, a per-user daily question limit, and (with CLIProxyAPI) the remaining quota of the upstream AI accounts.
- **DevUI** (MAF preview) in Development at `/devui`.

## Architecture

```
Browser (wwwroot: plain HTML/JS/CSS, SSE)          Admin (/admin)
        │  POST /api/chat/stream                         │  /api/admin/*
        ▼                                                ▼
ASP.NET Core minimal API (Program.cs, Auth.cs, Admin*.cs)
        │
        ├─ DocStore         loads data/docs/**/*.md, hashes them, wraps each in <document path="...">
        ├─ DocAgentFactory  MAF AIAgent: instructions = active prompt + <documents>…</documents>
        ├─ ChatService      streaming run, saves the session + Q&A log
        ├─ SessionJudge / EvalSampler / BenchRunner   8-criteria LLM judge, daily sampling, benchmark
        ├─ UsageDb / ProxyQuota                       token usage, user quotas, upstream account quotas
        └─ SQLite (data/app.db)  sessions, qa_logs, images, prompts, benchmark, evals, quotas
        │
        ▼
OpenAI-compatible Chat Completions endpoint (OPENAI_BASE_URL), e.g. CLIProxyAPI
```

There is no RAG: all documents (up to roughly 200 pages) go into the instructions, placed first so the provider's prompt cache can reuse them. Documents are read at startup; restart the app after changing them outside the admin page.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- An OpenAI-compatible Chat Completions endpoint, an API key and a model id. The quota card in the admin "Token" tab only works with [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI).
- For sign-in: a Microsoft Entra ID app registration (see below). Not needed with `Auth__Enabled=false`.
- Optional: Docker for container deployment.

## Setup

```bash
git clone https://github.com/hatrieukim/eof-bot.git
cd eof-bot
cp .env.example .env        # then fill in the values
mkdir -p data/docs          # put your *.md documents here
```

To try it without your own documents, use the fictional sample documents in `samples/docs` (see [Run](#run)). `samples/cau-hoi-test.md` lists test questions with expected answers.

### Entra ID app registration

1. In Microsoft Entra ID, create an app registration (single tenant).
2. Add a **Web** redirect URI `https://<your-host>/signin-oidc` (Entra only accepts `https://` or `http://localhost`, e.g. `http://localhost:5086/signin-oidc` for development).
3. Create a client secret.
4. To lock out disabled accounts immediately, grant the **Application** permission `Microsoft Graph / User.Read.All` and give admin consent. Without it, the check is skipped (everyone is let through).
5. Put the tenant ID, client ID and secret into `.env`.

## Configuration

Secrets and per-machine settings go in `.env` (loaded at startup, never committed). Any ASP.NET Core setting can also be set as an environment variable (`Section__Key`).

| Setting | Where | Description |
|---|---|---|
| `OPENAI_BASE_URL` | `.env` | OpenAI-compatible endpoint, e.g. `http://127.0.0.1:8317/v1` (required) |
| `OPENAI_API_KEY` | `.env` | API key for that endpoint (required) |
| `OPENAI_MODEL` | `.env` | Model id used by the bot and the judge (required) |
| `CLIPROXY_MANAGEMENT_KEY` | `.env` | CLIProxyAPI management key for the quota card (optional) |
| `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__ClientSecret` | `.env` | Entra ID app registration |
| `Admins` | `appsettings.json` or `Admins__0=…` | Sign-in emails of admins (case-insensitive) |
| `Auth__Enabled` | `.env` | `false` disables sign-in: each browser is an anonymous guest and **everyone is an admin**. Use only on a trusted network |
| `DocsPath` | config/env | Documents folder (default `data/docs`) |
| `DbPath` | config/env | SQLite file (default `data/app.db`) |
| `BenchPath` | config/env | Legacy Markdown benchmark table imported once into an empty DB (default `data/bench/cau-hoi-benchmark.md`) |
| `EvalPerDay` | config/env | Real chat sessions sampled for grading per day (default 5, `0` = off) |
| `BenchParallel` | config/env | Benchmark question groups run in parallel (default 3) |

Documents: Markdown files with numbered headings (`## 1.`, `### 1.2`) work best, because citations and the source panel refer to section numbers.

## Run

```bash
dotnet run                          # http://localhost:5086, documents from data/docs
DocsPath=samples/docs dotnet run    # use the fictional sample documents
```

- Chat: http://localhost:5086
- Admin: http://localhost:5086/admin (admins only)
- DevUI: http://localhost:5086/devui (Development only; it exposes the full instructions)

### Docker

```bash
docker compose up -d --build        # http://<host>:5086, data/ is mounted from the host
```

`docker-compose.yml` publishes port 5086 on all interfaces; restrict it (e.g. `127.0.0.1:5086:8080`) or put it behind an HTTPS reverse proxy, especially when sign-in is disabled.

To deploy to a remote Docker host over SSH (rsync + `docker compose`):

```bash
DEPLOY_HOST=user@server DEPLOY_DIR=eof-bot ./deploy.sh
```

The script never copies `.env` or `data/`; create them on the host.

## Tests

There is no unit test project yet. Quality is measured with the built-in benchmark and judge:

- `dotnet build` must pass.
- Admin → "Bộ benchmark": add questions with expected answers and sources; Admin → "Chạy test": run the Quick / Medium / Full set against a prompt version. Each question is graded on the 8 criteria and compared with the previous run.
- The sample documents in `samples/docs` with `samples/cau-hoi-test.md` are a small manual test set designed to catch answers made up from general knowledge.

Note that a full benchmark run sends every document to the model twice per question (bot + judge), so it is token-heavy.

## Project layout

```
Program.cs            app setup, chat/session/image endpoints
Auth.cs, AnonymousAuth.cs, AccountCheck.cs   sign-in, admin policy, Graph account check
Doc*.cs               document loading, agent factory, section lookup
Chat*.cs              chat service, SQLite storage, image parsing
Admin*.cs             admin APIs (docs, prompts, benchmark, tests, evals, usage)
Bench/, Eval/         benchmark runner, citations, judge, daily sampler
ProxyQuota.cs, ProviderErrors.cs, UsageDb.cs  quotas and provider error handling
wwwroot/              web UI and admin UI (no build step)
samples/              fictional sample documents and test questions
```

## Third-party assets

- [marked](https://github.com/markedjs/marked) (MIT) and [DOMPurify](https://github.com/cure53/DOMPurify) (Apache-2.0 / MPL-2.0) are vendored in `wwwroot/lib/`.
- [Be Vietnam Pro](https://fonts.google.com/specimen/Be+Vietnam+Pro) and [Literata](https://fonts.google.com/specimen/Literata) fonts (SIL Open Font License 1.1) are self-hosted in `wwwroot/fonts/`.

## License

[MIT](LICENSE) © 2026 hatrieukim
