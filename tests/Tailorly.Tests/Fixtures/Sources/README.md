# Source fixtures

Real responses from each source, captured on **2026-10-25** with
`User-Agent: TailorlyRemote/1.0`, then trimmed to the first few items (field values unchanged). The adapter tests
parse these files, so a provider's format change shows up as a failing test when the fixtures are refreshed.

| File | Request |
|---|---|
| `himalayas/search_country_in_excl_ww_p1.json` | `GET https://himalayas.app/jobs/api/search?country=IN&exclude_worldwide=true&sort=recent&page=1` |
| `himalayas/search_worldwide_p1.json` | `GET …/jobs/api/search?worldwide=true&sort=recent&page=1` |
| `himalayas/search_country_in_p1.json`, `_p2.json` | `GET …/jobs/api/search?country=IN&sort=recent&page=1` and `page=2` (includes worldwide jobs; first 3 are pinned/featured) |
| `himalayas/browse_cursor.json` | `GET https://himalayas.app/jobs/api?limit=20` (browse feed with `nextCursor`; not used by the adapter) |
| `himalayas/search_invalid_country.json` | the 400 body for `country=ZZZZ` |
| `remoteok/api.json` | `GET https://remoteok.com/api` (legal notice + 11 jobs, one of them a "..." teaser) |
| `greenhouse/gitlab_jobs.json` | `GET https://boards-api.greenhouse.io/v1/boards/gitlab/jobs` |
| `greenhouse/gitlab_job_detail.json` | `GET …/v1/boards/gitlab/jobs/{id}` (first job of the list) |
| `greenhouse/gitlab_board.json` | `GET …/v1/boards/gitlab` |
| `greenhouse/board_not_found.json`, `job_not_found.json` | the 404 bodies for an unknown board / job |
| `lever/palantir_postings.json`, `site_not_found.json` | `GET https://api.lever.co/v0/postings/palantir?mode=json&limit=3`; 404 body |
| `ashby/ashby_job_board.json`, `board_not_found.txt` | `GET https://api.ashbyhq.com/posting-api/job-board/Ashby?includeCompensation=true`; 404 body |
| `jobicy/jobs.json` | `GET https://jobicy.com/api/jobs?count=5` (adapter not built; kept for M7) |
| `wwr/remote-jobs.rss` | `GET https://weworkremotely.com/remote-jobs.rss` (adapter not built; kept for M7) |

**Privacy:** Remote OK appends an anti-spam note to every description whose tag is the requester's IP address in
base64. The capture's tag was replaced with `192.0.2.1` (RFC 5737 documentation address); the adapter strips the note.

Refresh: re-run the requests above, trim to a handful of items, scrub the Remote OK tag, and re-run the adapter tests.
