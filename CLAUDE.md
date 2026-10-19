# eof-bot — a chatbot that answers from Markdown documents (Microsoft Agent Framework, .NET)

A learning project for Microsoft Agent Framework (MAF). The goal is to **learn**, not to ship a product fast.

## Working with the user

- Personal settings (reply language, etc.) may be in `CLAUDE.local.md` (gitignored).
- Few code comments: only keep comments that explain a non-obvious reason. Explanations go in the chat reply.

## Settled architecture (v1: a single user trying out the capabilities)

- Documents: **Markdown** files (`*.md`, converted from Word since 2026-10-02), under 200 pages in total, changed occasionally, dropped into `data/docs/` by the user. Citations rely on the `#`/`##`/`###` headings in the files.
- ALL documents go into the instructions (at the start, stable → prompt cache); no RAG yet. Measure tokens first, then decide whether to move to RAG (if so: local ONNX embeddings, because CLIProxyAPI does not support embeddings).
- No images in the documents.
- Not in the documents → say so. Follow-up questions work (AgentSession).
- UI: DevUI (MAF preview, Development only) + a hand-written web UI at `/` (SSE).
- SQLite stores sessions (`SerializeSessionAsync`) + a Q&A log (asker, question, answer, citations, document hash, model, tokens, latency) for later evaluation. No like/dislike buttons.
- Hot-reloading documents was skipped: changing documents needs an app restart.
- Develop on the Mac first, then deploy to a Docker host (`deploy.sh`).
- Microsoft sign-in (Entra ID, company accounts only, single tenant) — `Auth.cs`, see "Sign-in".
- Later: admin roles via App roles, backups, opening it to all employees.

## Environment

- .NET 8 SDK, ASP.NET Core, solution `EofBot.sln`, project at the repository root.
- Packages: `Microsoft.Agents.AI.OpenAI` 1.23.0, `Microsoft.Agents.AI.DevUI` (preview), `Microsoft.Data.Sqlite` + `Dapper`, `DotNetEnv`, `Microsoft.Identity.Web`.
- Run: `dotnet run` → http://localhost:5086
- Documents: `DocsPath` in config, default `data/docs` (real documents; `data/` is gitignored together with the DBs), also for `dotnet run` (Development, changed 2026-10-03). To use the sample docs: `DocsPath=samples/docs dotnet run`.
- Mock data: 4 files `samples/docs/*.md` for a fictional company "Kỳ Lân Xanh", with numbers deliberately different from real law and made-up names (Chuồn Chuồn, Cú Mèo, bà Trần Thị Mây...) to catch the bot answering from general knowledge. Questions + answers: `samples/cau-hoi-test.md` (deliberately OUTSIDE `samples/docs`).
- `DocStore` (`DocStore.cs`): loads every `*.md` (including subfolders), sorted by path, normalizes `\r\n`, wraps each file in `<document path="...">`, hash = first 12 chars of SHA-256. `GET /api/status` shows a summary, `GET /docs` shows the exact text block that goes into the prompt.
- `DocAgentFactory`: creates an `AIAgent` with instructions = 9 rules + `<documents>` with all documents; caches the agent by document hash. `GET /ask?q=...` asks one question without history (testing only).
- DevUI: `Microsoft.Agents.AI.DevUI` 1.23.0-preview (pulls in Hosting preview + Hosting.OpenAI **alpha**). Needs all of: `builder.AddAIAgent("eof-bot", factory)`, `AddOpenAIResponses/Conversations` + `Map...`, `builder.AddDevUI()` (missing → crash in `DevUIAuthFilter`; the official sample lacks this line) + `MapDevUI()`; DevUI only runs in Development. Open http://localhost:5086/devui. Conversations live in RAM (lost on restart). The Traces tab says "not available" (no OpenTelemetry yet). Calling `/v1/responses` by hand needs `metadata.entity_id = "eof-bot"`.
- The agent registered via `AddAIAgent` is created once at startup → hot-reloading documents would need a change (probably a `DelegatingAIAgent` calling `DocAgentFactory.For(Current)`).
- Token-measuring middleware and FileSystemWatcher were **skipped by decision** (2026-10-02): changing documents needs a restart; tokens are still recorded in `qa_logs`.
- SQLite: `ChatDb.cs` (Microsoft.Data.Sqlite + Dapper, plain SQL, columns aliased `AS PascalCase` to map onto records), file `DbPath` = `data/app.db`, `CREATE TABLE IF NOT EXISTS` at startup. Tables `sessions` (serialized MAF session = chat history only, NOT the instructions/documents) and `qa_logs` (question, answer, citations extracted with the regex "(Nguồn: ...)", doc_hash, model, tokens, latency).
- `ChatService.cs`: `OpenAsync` (create/load a session, null if the sessionId does not exist) then `RunAsync` (always `RunStreamingAsync`, collecting updates with `ToAgentResponse()` to get Text + Usage; the session + log are saved only at the end; if the client disconnects midway nothing is saved).
- API: `POST /api/chat {sessionId?, userName, message}` (single response, for curl testing; unknown sessionId → 404), `POST /api/chat/stream` (same body, SSE: `delta {text}` → `done {sessionId, answer, token, latencyMs}` or `error {message}`), `GET /api/sessions?user=`, `GET /api/sessions/{id}/messages`. Chats through DevUI do NOT go through these (not saved, not logged).
- Web UI: `wwwroot/` plain HTML + JS + CSS, no build step. `index.html`, `app.js`, `style.css`; `lib/` vendors marked 18.0.14 (Markdown → HTML) + DOMPurify 3.4.16 (sanitizes model output) so there is no CDN dependency. The user name comes from `/api/me` (sign-in cookie). SSE is read with `fetch` + manual `\n\n` splitting (EventSource is GET-only). Enter sends, ignored while `isComposing` (Vietnamese IME).
- Design "Warm & calm" (settled 2026-10-04): inspired by claude.ai but does NOT copy Anthropic's logo/fonts/brand colors. `wwwroot/theme.css` = shared color/font variables (warm paper background, warm greys, one brick-red accent `--accent`), Literata serif for greeting/headings/answers, Be Vietnam Pro sans for the UI. Fonts are self-hosted in `wwwroot/fonts/` (vietnamese/latin/latin-ext subsets only); `/fonts` is served BEFORE the auth layer so `/login` can use it; `theme.css` is still behind sign-in, so `login.html` keeps inline CSS.
- Chat page: welcome screen (greeting by time of day + given name = last word of `name`, 4 suggestions that only prefill the input, never auto-send), "X of Y questions left today" from `GET /api/me/quota` (hidden for admins). `cite.js` turns "(Nguồn: ...)" into numbers [1] [2] (same parsing as `Citations.ParseSources`, expanding ranges like 7.4–7.6); clicking a number opens a source panel via `GET /api/docs/section?file=&section=` (`DocSections.cs`, uses `Citations.LoadSections`, cached by document hash; any signed-in user can view). Section not found → says the bot may have cited a wrong section number.
- Admin: sidebar grouped into Content / Quality / Operations. Test runs: pick a run from a select; stat boxes compare with the latest finished run on the same set + same scale; an 8-criteria matrix (✓/✗/–) + a Σ row of failed questions; filters All / Failed / Error / Changed since previous run; detail panel on the right. Token: 3 cards (account quota, today, 30 days) + chart + per-user table.
- Hand-written SSE (`Sse.cs`) because .NET 8 has no `TypedResults.ServerSentEvents`. Once the 200 header is sent, errors must be reported with an `error` event.
- Gotcha: the DevUI package mistakenly ships `staticwebassets/index.html` at `/` → build error "Conflicting assets ... index.html". The real DevUI runs from resources embedded in the dll, so `EofBot.csproj` removes that file (`<StaticWebAsset Remove=...>`). Re-check when upgrading DevUI.
- Verified: streaming through CLIProxyAPI still returns token usage at the end (no extra configuration).
- Prompt cache: measured 2026-10-04 (prompt ~45k tokens, back-to-back requests): `cached_tokens` > 0 in 3/6 Gemini turns and 1/6 Codex turns → caching works but unevenly. Earlier measurements (prompt ~1.8k) were all 0.
- Prompt lesson: the "I don't know" rule must come LAST and be the last resort; placed early, the bot gives up instead of combining information from 2 documents (question 6 went from 0–1/3 to 5/5). After editing the prompt always re-run questions 9 and 10 to make sure the bot does not start making things up.

## Images in chat (2026-10-05)

Users paste (Ctrl+V), drag and drop, or click 📎 to send images, on the chat page and the admin test chat. User decisions: keep images in history, store them as BLOBs in `app.db`, the judge sees images.

- `wwwroot/image.js` (shared by both pages): `compressImage` scales the long edge down to 1568px (image tokens are per pixel, not per file size), prefers PNG for screenshots (keeps text sharp), PNG > 800KB becomes JPEG 0.85, transparent backgrounds are filled white; already-small images (≤ 1568px, ≤ 1MB) keep the original file. Tested in Chromium: Full HD → 1568×882 still shows 11px text legibly. `attachImages` = paste/drag-drop/button + preview; `imageStrip` + lightbox to view images in messages. Pasting when the clipboard has text (copied from Word/Excel) pastes the text and drops the image. Shared CSS is at the end of `theme.css`.
- Compression happens in the browser, not on the server: phone photos over 5MB must be shrunk before upload, and this avoids an image library (ImageSharp needs a commercial license, SkiaSharp needs native libs in Docker).
- API: `images` (array of data URLs) in the body of `/api/chat` and `/api/chat/stream`; image-only messages are allowed (session title `[ảnh]`). `ChatImages.Parse` checks ≤ 4 images, PNG/JPEG/WebP/GIF, ≤ 5MB each → 400, checked BEFORE the quota.
- `ChatService` sends a `ChatMessage` with `TextContent` + `DataContent`. The serialized MAF session keeps the images (base64 in `session_json`) → later turns resend them.
- Table `qa_images` (`qa_log_id`, `media_type`, `data` BLOB), written in the same transaction as `qa_logs`. `GET /api/sessions/{id}/messages` also returns `images` (URLs). `GET /api/images/{id}`: session owner or admin, cached `immutable`.
- Judge: `TurnRow.Images`; the transcript inserts images right after that turn's question; judge rule: details the bot repeats from an image do not count as outside the documents (criterion 3). The real-chat eval page shows each turn's images.
- Tested in a container (fake LLM, temp DB, Playwright). Tested with the real `gpt-6-luna` through CLIProxyAPI (2026-10-06, `dotnet run` on a DB copy): it read the error code on a screenshot correctly, remembered the image in a later turn without resending it, image-only messages worked, and the judge saw the images.

## Sign-in (Entra ID)

- `Microsoft.Identity.Web` 4.16, OpenID Connect authorization code flow (+PKCE, client secret) → app cookie (365 days, sliding). Config: `AzureAd` in `appsettings.json` + `AzureAd__TenantId/ClientId/ClientSecret` in `.env`.
- `FallbackPolicy` = sign-in required for EVERYTHING (including static files, admin, DevUI, `/docs`) because `UseAuthentication/UseAuthorization` come BEFORE `UseStaticFiles`. Only `/login` (page with a button, inline CSS) and `/auth/login` are anonymous.
- Not signed in: pages → 302 `/login`; `/api/*`, `/v1/*` → 401 (JS goes to `/login`). `/auth/logout` (POST) only clears the app cookie, it does not sign out of Microsoft; login uses `prompt=select_account` so users can switch accounts.
- `user_name` = `preferred_username` (email) lowercased, set by the server, ignoring what the client sends; exception: `admin-thử` from the admin page. Someone else's session → 404.
- Admin = email listed in `Admins` (array, `appsettings.json` or `Admins__0=...` env vars), case-insensitive, re-read on every request (edits apply immediately). Policy `Auth.AdminPolicy` is attached to each group/endpoint `/api/admin/*`, `/docs`, `/ask`, `/api/status`; static pages `/admin/*` are guarded by the `UseAdminPages` middleware (static files cannot carry a policy). Not admin → 403. Regular users sending `userName: admin-thử` or `promptId` are ignored.
- Disabling an account must revoke access immediately (user requirement, 2026-10-04): `AccountCheck.cs` calls Graph `GET /users/{oid}?$select=accountEnabled` with an app-only token (client credentials, `EnableTokenAcquisitionToCallDownstreamApi`) in `cookie.Events.OnValidatePrincipal`, cached 1 minute per user. Disabled or deleted (404) → cookie cleared. Graph errors → let through + log a warning (not cached). Needs the Application permission `User.Read.All` + admin consent in Entra; without it Graph returns 403 and everyone is let through.
- `GetAccessTokenForAppAsync` must pass `authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme` (otherwise IDW10503 because it guesses "Cookies").
- The cookie must have `IsPersistent = true` (set in the challenge properties), otherwise it is lost when the browser closes.
- Entra only accepts redirect URIs `https://...` or `http://localhost` → a server deployment needs HTTPS + a domain name before sign-in works.
- Disabling sign-in: `Auth__Enabled=false` (default true). `AnonymousAuth.cs` gives each browser a `khach-xxxxxxxx` id in the `eof_anon` cookie (own history + question quota), EVERYONE is an admin (user decision), `/login` → `/`, the web UI shows "Khách" (guest) and hides the sign-out button. Microsoft.Identity.Web/AccountCheck are not registered, so no `AzureAd__*` variables are needed. Only use on a trusted network.

## Evaluation with 8 criteria

Following Instacart's LACE write-up (2026-10-04): grade Pass / Fail / Not applicable on 8 criteria, direct judging only (no reflection/debate, user decision). Shared by the benchmark and real chats.

- 8 criteria: 1 Understood the question, 2 Used previous-turn context correctly, 3 Grounded in the documents, 4 Complete, 5 Correct refusal, 6 Cited the right section, 7 Concise without repetition, 8 Compliance. Score = passed / applicable × 100. Criterion 7 is subjective, only watch the trend.
- `Eval/SessionJudge.cs`: the model writes free-form notes (reading ALL documents, ~48k tokens per call), then a second call converts them to JSON. Criterion 6 is graded in code. The judge uses the same `OPENAI_MODEL` as the bot (no more `JUDGE_MODEL`).
- A social greeting answered with the fallback sentence does NOT count as a failure (user decision). The judge is a bit lenient on required points; the user chose to leave it.
- `doc_snapshots` (in `app.db`): stores the content of each document version by hash on every load, so a session is graded against the version the bot answered from.

### Real chats
- `EvalSampler` (BackgroundService): checks hourly; if there is no `auto` round today, randomly samples `EvalPerDay` (default 5, 0 = off) sessions that are: ungraded, idle ≥ 30 minutes, not `admin-thử`, using a single document version still in `doc_snapshots`. Graded sessions are not re-graded.
- Tables `evals` (column `source` = auto/manual) + `eval_scores`. API `/api/admin/evals`: `daily`, `failed`, `{id}`, `sessions/{id}` (manual grading), `sample?n=`, `try` (grade a hand-written conversation, not saved; add `qid` = benchmark mode).
- Admin tab "Eval chat thật": chart of the average score per chat day + list of failed sessions (click to see the conversation and reasons). No per-criterion failure rates (by decision).
- The judge was checked with 10 conversations with planted mistakes: caught 10/10.

### Benchmark
- Questions live in the `bench_questions` table (sets Quick / Medium / Full), edited in the "Bộ benchmark" tab and run in the "Chạy test" tab (`BenchRunner`, in-app, writes `test_runs`/`test_results` in `app.db`). `BenchPath` (default `data/bench/cau-hoi-benchmark.md`) is imported once while the table is empty.
- Judge benchmark mode: only the last question is graded (earlier questions in a follow-up group are context), with an expected answer; criterion 6 compares cited sections with the Sources column (`Citations.Check`); if the bot refuses, criterion 6 = not applicable.
- `test_runs.scale`: `grade4` = old runs graded on 4 levels (correct/partial/wrong/fabricated), `criteria8` = 8 criteria. The two scales are not comparable; old runs show "(cũ)".
- A full run costs ~14 million tokens with ~150 questions (bot ~48k + judge ~48k per question).

## Tokens and quotas (2026-10-04)

Two levels, by user decision: overall = % of the AI accounts' real quota; per user = questions per day (tokens are still shown to see the burn). Admin tab "Token".

- Burn: `UsageDb.ListSinceAsync` sums tokens (input + output) from `qa_logs` (chat, per user), `test_results` (benchmark, judge tokens NOT included yet → really ~double), `evals`. API `GET /api/admin/usage?days=`. Days use fixed Vietnam time +7 (`AdminUsage.VnDate`, no tzdata needed in the container).
- Account quota (`ProxyQuota.cs`): CLIProxyAPI management API (`CLIPROXY_MANAGEMENT_KEY`, same host as `OPENAI_BASE_URL`). `auth-files` → `auth-files/models?name=` filters accounts serving `OPENAI_MODEL` → `api-call` (the proxy replaces `$TOKEN$` with the OAuth token) calls internal endpoints: Codex `chatgpt.com/backend-api/wham/usage` (monthly window on the free plan), Antigravity `loadCodeAssist` (plan) + `retrieveUserQuotaSummary` (5 hours + weekly, per group Gemini / Claude-GPT). Only percentages, no token counts. Cached 3 minutes (errors: 30 seconds); errors are only shown, never block chat. API `GET /api/admin/quota?refresh=true`.
- Summing several accounts (the proxy rotates them): only same provider + plan; an account that exhausted a long window counts 0 for shorter windows; disabled/erroring accounts count 0 but still add to capacity (N × 100%).
- Measured 2026-10-04 (reference only, NOT used for conversion): 1% of the monthly Codex free quota ≈ 150–190k tokens (3–4 questions); Gemini is noisy (the API alternates between 2 sets of numbers), quotas are cost-based so cached tokens are ~4× cheaper.
- User quota: table `quotas` (`user_name` = `*` is the default, 30 questions/day; other rows are overrides). Counts `qa_logs` rows since midnight Vietnam time → failed/interrupted questions are not counted. Enforced in `/api/chat` and `/api/chat/stream` before calling the LLM → 429 + a message (web UI shows a yellow box). Admins and `admin-thử` are exempt (`Auth.QuotaExempt`). Edited in the Token tab (`PUT /api/admin/quotas/default`, `.../users/{user}` with `questions: null` = back to default).
- Proxy out of quota: 429 `model_cooldown` + `Retry-After` (can be many hours). The OpenAI SDK retries 429 by default and WAITS for Retry-After → the user hangs; `ProxyRetryPolicy` does not retry 429 and waits at most 10 s per attempt. `ProviderErrors.Describe` turns 429/503 into readable messages (SSE `error` with `quota: true`).

## Deployment

- `DEPLOY_HOST=... ./deploy.sh` = rsync the code (except `.env`, `data/`, `bin`, `obj`) + `docker compose up -d --build --force-recreate` on the host (`~/$DEPLOY_DIR`, default `eof-bot`). Documents are not pushed: the host's `data/docs/` is the source of truth, edited at `/admin`. The app only reads documents at startup.
- `docker-compose.yml` binds `0.0.0.0:5086`. Without HTTPS, Entra sign-in cannot work, so such hosts run with `Auth__Enabled=false` — only on a trusted network.
- The container runs Production (no DevUI), `user: 1000:1000` so SQLite can write to `./data` mounted from the host.
- The host's `.env` is created by hand and never synced. Benchmark files with answers are not pushed to the host.
- The quota tab on the host needs that host's own `CLIPROXY_MANAGEMENT_KEY` in its `.env` (and remote management enabled in CLIProxyAPI when the container reaches it over the network).

## LLM via CLIProxyAPI

- `.env` (gitignored, never print or commit keys): `OPENAI_BASE_URL`, `OPENAI_API_KEY`, `OPENAI_MODEL`, `CLIPROXY_MANAGEMENT_KEY`. Template in `.env.example`.
- Local: `http://127.0.0.1:8317/v1` (Homebrew `cliproxyapi`). Any OpenAI-compatible endpoint works for chat; the quota tab is CLIProxyAPI-specific.
- Uses Chat Completions (`GetChatClient`). Current model `gpt-6-luna` (Codex; switched from `gemini-3.8-flash-high` via Antigravity on 2026-10-04), no `openai/` prefix. Changing the model = edit `.env` + restart; the Token tab follows.

## MAF 1.x API notes

- Create an agent: `chatClient.AsAIAgent(name:, instructions:, tools:)` — needs `using OpenAI.Chat;`.
- Sessions: `await agent.CreateSessionAsync()`; save/load: `SerializeSessionAsync` / `DeserializeSessionAsync`.
- Run: `agent.RunAsync(text, session)` / `agent.RunStreamingAsync(text, session)`.
