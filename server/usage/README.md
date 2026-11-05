# Circularity usage count

A Cloudflare Worker that counts how many people use Circularity each day. The app sends at most
one ping a day while **Settings → Features → Help count how many people use Circularity** is on:

```
POST /ping   {"id": "<random GUID>", "version": "x.y.z"}
```

The Worker stores the UTC day, the ID and the version in a D1 table, and nothing else: it never
reads the IP address or any other header. Rows older than 90 days are deleted every night.

```
GET /stats   {"today": 12, "last7": 40, "last30": 95, "versions": {"x.y.z": 10, ...}}
GET /badge   the "today" number in shields.io's endpoint format
```

## Develop and deploy

Uses the [`cf` CLI](https://developers.cloudflare.com/cf/) (`npm install --global cf`).

```bash
npm install
cf d1 migrations apply <database-id> --local --persist-to .cloudflare/state
cf dev        # http://localhost:5173
cf deploy
```

New tables or columns go in a new file in `migrations/`, applied with
`cf d1 migrations apply <database-id>` (add `--local --persist-to .cloudflare/state` for `cf dev`).
