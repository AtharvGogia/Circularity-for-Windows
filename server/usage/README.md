# Circularity usage count

A Cloudflare Worker that counts how many people use Circularity each day. The app sends at most
one ping a day while **Settings → Features → Help count how many people use Circularity** is on:

```
POST /ping   {"id": "<random GUID>", "version": "x.y.z"}
```

The count is off unless the user turns it on. The Worker stores the UTC day, the ID and the version
in a D1 table, and nothing else: it never reads the IP address or any other header (Cloudflare sees
the address, as for any request). Rows older than 90 days are deleted every night.

`/ping` shares one rate limit across everyone, 60 a minute (the `PINGS` binding), so a flood of
made-up IDs can't use up D1's free daily writes. It isn't per address, so no IP is needed for it.
The app tries again a few hours later, so a real ping turned away is still counted.

```
GET /stats   {"today": 12, "last7": 40, "last30": 95, "versions": {"x.y.z": 10, ...}}
GET /badge   the "today" number in shields.io's endpoint format
```

Both are served from Cloudflare's cache for 5 minutes, so they don't query D1 on every request.
That needs the Worker on a custom domain: on `workers.dev` the Cache API does nothing, and every
request queries D1, which is fine at this size.

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
