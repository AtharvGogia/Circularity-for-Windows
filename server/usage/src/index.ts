// Circularity's daily-active count. The app sends at most one ping a day:
//   POST /ping  {"id": "<random GUID>", "version": "x.y.z"}
// The day comes from the server's clock (UTC). The request's IP address and headers are never read
// or stored (Cloudflare itself sees the address, as with any request). Rows are deleted after 90 days.
//   GET /stats  {today, yesterday, last7, last30, versions}
//   GET /badge  daily users, in shields.io's endpoint format: the busier of yesterday and today so far
import { env } from "cloudflare:workers";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/;
const MAX_BODY = 200;
const KEEP_DAYS = 90;

/** UTC date as yyyy-MM-dd, `daysAgo` days back. */
function day(daysAgo = 0): string {
	return new Date(Date.now() - daysAgo * 86_400_000).toISOString().slice(0, 10);
}

function json(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: {
			"content-type": "application/json",
			"cache-control": "public, max-age=300",
			"access-control-allow-origin": "*",
		},
	});
}

async function ping(request: Request): Promise<Response> {
	const text = await request.text();
	if (text.length > MAX_BODY) return new Response("Too large", { status: 413 });
	let body: unknown;
	try {
		body = JSON.parse(text);
	} catch {
		return new Response("Bad JSON", { status: 400 });
	}
	const { id, version } = (body ?? {}) as { id?: unknown; version?: unknown };
	if (typeof id !== "string" || !GUID.test(id) || typeof version !== "string" || !VERSION.test(version))
		return new Response("Bad ping", { status: 400 });

	// One shared key, checked after the body is valid so junk doesn't use it up: a flood of made-up IDs
	// is slowed down without looking at who sends them. The app tries again a few hours later, so a real
	// ping turned away here still gets counted.
	const { success } = await env.PINGS.limit({ key: "ping" });
	if (!success) return new Response("Too many pings", { status: 429 });

	await env.DB.prepare("INSERT OR IGNORE INTO pings (day, id, version) VALUES (?, ?, ?)")
		.bind(day(), id.toLowerCase(), version)
		.run();
	return new Response(null, { status: 204 });
}

async function distinctOn(daysAgo: number): Promise<number> {
	const row = await env.DB.prepare("SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day = ?")
		.bind(day(daysAgo))
		.first<{ n: number }>();
	return row?.n ?? 0;
}

async function distinctSince(daysAgo: number): Promise<number> {
	const row = await env.DB.prepare("SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?")
		.bind(day(daysAgo))
		.first<{ n: number }>();
	return row?.n ?? 0;
}

async function stats() {
	const [today, yesterday, last7, last30] = await Promise.all([
		distinctSince(0),
		distinctOn(1),
		distinctSince(6),
		distinctSince(29),
	]);
	const { results } = await env.DB.prepare(
		"SELECT version, COUNT(*) AS n FROM pings WHERE day = ? GROUP BY version ORDER BY n DESC",
	)
		.bind(day())
		.all<{ version: string; n: number }>();
	const versions = Object.fromEntries(results.map((r) => [r.version, r.n]));
	return { today, yesterday, last7, last30, versions };
}

/**
 * Serves GETs from Cloudflare's cache for 5 minutes, so the badge and stats don't query D1 every time.
 * (On workers.dev the Cache API does nothing; it takes effect on a custom domain.)
 */
async function cached(request: Request, ctx: ExecutionContext, make: () => Promise<Response>): Promise<Response> {
	const cache = caches.default;
	const hit = await cache.match(request);
	if (hit) return hit;
	const response = await make();
	ctx.waitUntil(cache.put(request, response.clone()));
	return response;
}

export default {
	async fetch(request, _env, ctx) {
		const { pathname } = new URL(request.url);
		if (pathname === "/ping")
			return request.method === "POST" ? ping(request) : new Response("Use POST", { status: 405 });
		if (request.method !== "GET") return new Response("Method not allowed", { status: 405 });
		if (pathname === "/stats") return cached(request, ctx, async () => json(await stats()));
		if (pathname === "/badge")
			return cached(request, ctx, async () => {
				// Today alone starts again from 0 at midnight UTC and fills up through the day, so the
				// badge shows yesterday's whole day until today has more.
				const { today, yesterday } = await stats();
				const users = Math.max(today, yesterday);
				return json({ schemaVersion: 1, label: "daily users", message: String(users), color: "8B5CF6" });
			});
		return new Response("Not found", { status: 404 });
	},

	async scheduled() {
		await env.DB.prepare("DELETE FROM pings WHERE day < ?").bind(day(KEEP_DAYS)).run();
	},
} satisfies ExportedHandler;
