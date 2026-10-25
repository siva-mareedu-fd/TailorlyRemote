# Tailorly Remote: project package

Your daily shortlist of remote roles, and the résumé to win them.

## What is in this package

### `docs/` (read in this order)
| # | File | Purpose |
|---|---|---|
| 1 | `PRD_TailorlyRemote.md` | **What** to build: scope, features F1–F15, rules, milestones, risks, source-terms review (Appendix D) |
| 2 | `SystemDesign_TailorlyRemote.md` | **How** it is structured: architecture, flows, job discovery paths, AI layer, DOCX engine, security, deployment |
| 3 | `DatabaseDesign_TailorlyRemote.md` | Authoritative PostgreSQL schema (DDL), constraints, seed data, key queries, retention |
| 4 | `ApiDesign_TailorlyRemote.md` | Authoritative HTTP contract: endpoints, types, error codes, rate limits |

Precedence: the PRD wins on *what*; the System Design wins on *how*; the Database and API designs are authoritative for the schema and the HTTP contract.

### `design/` (final UI prototypes, open in any browser)
| File | What it is |
|---|---|
| `tailorly-app.html` | **All app screens in one clickable prototype.** Sidebar links, or add a screen name to the URL: `#shortlist`, `#tailor`, `#tracker`, `#addjob`, `#resume`, `#preferences`, `#usage`, `#account`, `#admin`, `#onboarding`, `#system` |
| `tailorly-auth-B.html` | Sign-in and sign-up page in the final style (`#signin` or `#signup`) |

`design/explorations/` holds earlier options (blue palette, light shortlist variant, first Design B shortlist). They are kept for reference only.

Both final files use the same colour tokens (CSS variables at the top of each file), light by default with a dark-mode toggle. `#system` in the app shows every token and component.

### Published versions
- App prototype: https://claude.ai/artifact/4tG96dUJXB3AVjk7GLFSo3
- Sign-in / sign-up: https://claude.ai/artifact/VD4jtxQ5wz4VPG1JCW2BYM

## Using this with Claude Code
- `CLAUDE.md` (repo root) holds the standing instructions. Claude Code reads it automatically each session.
- `ClaudeCode_Prompts.md` has the kickoff prompt (M0), one prompt per milestone (M1 to M6), and review prompts (isolation, privacy, docs conformance, UI fidelity, AI quality).

Steps:
1. Put `CLAUDE.md` at the repo root, the four documents in `docs/`, and the two final design files in `design/`.
2. Start Claude Code in the repo and paste **Prompt 0** from `ClaudeCode_Prompts.md`. Review its plan, then say "go".
3. After each milestone, review the report, run the app, then start a fresh session for the next prompt.
4. External API details (URLs, field names, terms) are written from secondary sources. Claude Code must verify each against the provider's official documentation before building an adapter.

## Open items before launch
- Written reply from Himalayas on whether a per-user matching service is permitted (email already sent). Keep Himalayas disabled until confirmed.
- Adzuna is excluded from the MVP (no public pricing; commercial licence and display rules).
- Confirm the default assumptions listed in PRD section 17 (INR default currency, unclear-eligibility handling in the email digest, cover-letter tones, 600K / 1.4M token split).
- Legal review of the privacy notice (résumé text is processed by an AI provider).
- Formal colour-contrast check of the final palette.
- Collect about 10 real résumé files early to test format-preserving edits.
