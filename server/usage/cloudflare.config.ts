import { bindings, defineConfig, triggers } from "cf/config";
import * as entrypoint from "./src/index.ts" with { type: "cf-worker" };

export default defineConfig({
	worker: {
		name: "circularity-usage",
		compatibilityDate: "2026-11-01",
		entrypoint,
		env: {
			DB: bindings.d1({ id: "a6baab94-1071-44bf-a270-d07913f5d877", name: "circularity-usage" }),
		},
		// Once a day: forget pings older than 90 days.
		triggers: [triggers.scheduled({ schedule: "17 3 * * *" })],
	},
});
