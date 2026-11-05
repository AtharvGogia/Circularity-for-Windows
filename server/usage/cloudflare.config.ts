import { bindings, defineConfig, triggers } from "cf/config";
import * as entrypoint from "./src/index.ts" with { type: "cf-worker" };

export default defineConfig({
	worker: {
		name: "circularity-usage",
		compatibilityDate: "2026-11-01",
		entrypoint,
		env: {
			DB: bindings.d1({ id: "a6baab94-1071-44bf-a270-d07913f5d877", name: "circularity-usage" }),
			// One limit shared by every ping (not per address, so no IP is read): far above real use, which is
			// one ping per person per day, and well under D1's free daily writes, so a flood can't use them up.
			PINGS: bindings.rateLimit({ namespace: "1001", simple: { limit: 60, period: 60 } }),
		},
		// Once a day: forget pings older than 90 days.
		triggers: [triggers.scheduled({ schedule: "17 3 * * *" })],
	},
});
